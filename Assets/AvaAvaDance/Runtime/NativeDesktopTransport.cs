using System;
using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaAvaDance
{
    internal static class DesktopCallbacks
    {
        // The host and Mono share a process, but own separate heaps. The native
        // functions borrow each buffer only for the duration of the call.
        internal static readonly bool Hosted = Environment.GetEnvironmentVariable("AVA_DESKTOP_PID") ==
            System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

        internal static IntPtr Address(string name)
        {
            if (Hosted && long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out long address)) return new IntPtr(address);
            return IntPtr.Zero;
        }

        internal static T Bind<T>(string name) where T : Delegate
        {
            var address = Address(name);
            if (address == IntPtr.Zero) throw new InvalidOperationException("Missing desktop callback " + name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }

    public sealed partial class DanceApp
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PollDesktop(IntPtr buffer, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PublishDesktop([In] byte[] bytes, int length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PresentDesktop([In] byte[] rgba, int width, int height,
            long document, long layout);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CaptureDesktop(IntPtr destination, int capacity, int maxDimension);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PrepareDesktopQuit();

        internal static bool NativeDesktopActive => DesktopCallbacks.Hosted;
        DesktopDelivery desktopDelivery;
        PollDesktop pollDesktop;
        PublishDesktop publishDesktop;
        PresentDesktop presentDesktop;
        Coroutine desktopLoop;
        byte[] desktopInput = new byte[4096];
        byte[] desktopPixels;
        float desktopWidth, desktopHeight, desktopScale = 1;
        long readbackLayout, readbackDocument;
        AsyncGPUReadbackRequest readback;
        RenderTexture readbackSource;
        Texture2D syncReadback;
        bool readbackFailed;
        string publishedSnapshot;
        long publishedDocument = -1;
        bool waitingForMacEdits, macEditsReady;

        bool DeferMacQuitUntilEditsArrive()
        {
            if (desktopDelivery == null || Application.platform != RuntimePlatform.OSXPlayer) return false;
            if (macEditsReady) { macEditsReady = false; return false; }
            if (!waitingForMacEdits)
            {
                waitingForMacEdits = true;
                DesktopCallbacks.Bind<PrepareDesktopQuit>("AVA_DESKTOP_PREPARE_QUIT")();
            }
            return true;
        }

        static string CaptureNativeWorkspace(int maxDimension)
        {
            // PNG encoders may add overhead for incompressible RGBA. The MCP
            // endpoint bounds maxDimension before allocating this buffer.
            var bytes = new byte[checked(maxDimension * maxDimension * 4 + 65536)];
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var capture = DesktopCallbacks.Bind<CaptureDesktop>("AVA_DESKTOP_CAPTURE");
                int count = capture(pin.AddrOfPinnedObject(), bytes.Length, maxDimension);
                if (count < 1 || count > bytes.Length) throw new InvalidOperationException("Desktop workspace capture failed.");
                return Convert.ToBase64String(bytes, 0, count);
            }
            finally { pin.Free(); }
        }

        void StartNativeDesktop()
        {
            if (!NativeDesktopActive) return;
            publishDesktop = DesktopCallbacks.Bind<PublishDesktop>("AVA_DESKTOP_PUBLISH");
            pollDesktop = DesktopCallbacks.Bind<PollDesktop>("AVA_DESKTOP_POLL");
            presentDesktop = DesktopCallbacks.Bind<PresentDesktop>("AVA_DESKTOP_FRAME");
            desktopDelivery = new DesktopDelivery();
            // A hidden parent HWND can suppress automatic camera rendering. Drive
            // this camera explicitly at the point where its result is consumed.
            previewCamera.enabled = false;
            AcquireBackground();
            desktopLoop = StartCoroutine(PumpNativeDesktop());
        }

        void StopNativeDesktop()
        {
            if (desktopDelivery == null) return;
            desktopDelivery.Close();
            if (desktopLoop != null) StopCoroutine(desktopLoop);
            if (readbackSource != null)
            {
                readback.WaitForCompletion();
                RenderTexture.ReleaseTemporary(readbackSource);
                readbackSource = null;
            }
            if (syncReadback != null) Destroy(syncReadback);
            ReleaseBackground();
            desktopDelivery = null;
        }

        string PollDesktopMessage()
        {
            while (true)
            {
                var pin = GCHandle.Alloc(desktopInput, GCHandleType.Pinned);
                int length;
                try { length = pollDesktop(pin.AddrOfPinnedObject(), desktopInput.Length); }
                finally { pin.Free(); }
                if (length == 0) return null;
                if (length > 0 && length <= desktopInput.Length) return Encoding.UTF8.GetString(desktopInput, 0, length);
                if (length >= -desktopInput.Length || length < -4194304)
                    throw new InvalidOperationException("Invalid desktop event size: " + length);
                desktopInput = new byte[-length];
            }
        }

        void PublishDesktopJson(string json)
        {
            var utf8 = Encoding.UTF8.GetBytes(json);
            publishDesktop(utf8, utf8.Length);
        }

        void ProcessDesktopMessage(JObject message)
        {
            switch ((string)message["type"])
            {
                case "resize":
                    float w = (float)message["width"], h = (float)message["height"];
                    if (!(w > 0 && h > 0 && w <= 32768 && h <= 32768)) return;
                    float scale = (float?)message["scale"] ?? 1;
                    if (!float.IsFinite(scale) || scale <= 0) return;
                    if (w != desktopWidth || h != desktopHeight || scale != desktopScale)
                    {
                        desktopWidth = w;
                        desktopHeight = h;
                        desktopScale = scale;
                        desktopDelivery.Resize();
                        ResizePreview();
                    }
                    return;
                case "close":
                    if (modal == null && importDialog == null) Application.Quit();
                    return;
                case "quitAfterEdits":
                    if (!waitingForMacEdits) return;
                    waitingForMacEdits = false;
                    if (modal == null && importDialog == null)
                    {
                        macEditsReady = true;
                        Application.Quit();
                    }
                    return;
            }
            if (desktopDelivery.AcceptInput((long?)message["document"], documentGeneration))
                NativeDesktopAction(message);
        }

        IEnumerator PumpNativeDesktop()
        {
            double nextSnapshot = 0;
            var endOfFrame = new WaitForEndOfFrame();
            while (desktopDelivery.Open)
            {
                yield return endOfFrame;
                try
                {
                    bool input = false;
                    for (int i = 0; i < 64; i++)
                    {
                        string message = PollDesktopMessage();
                        if (message == null) break;
                        ProcessDesktopMessage(JObject.Parse(message));
                        input = true;
                    }
                    if (desktopWidth <= 0 || desktopHeight <= 0) continue;
                    if (input || publishedDocument != documentGeneration || Time.realtimeSinceStartupAsDouble >= nextSnapshot)
                    {
                        var snapshot = NativeDesktopSnapshot(desktopWidth, desktopHeight);
                        snapshot["document"] = documentGeneration;
                        snapshot["layout"] = desktopDelivery.Layout;
                        string json = snapshot.ToString(Formatting.None);
                        if (json != publishedSnapshot)
                        {
                            PublishDesktopJson(json);
                            publishedSnapshot = json;
                            publishedDocument = documentGeneration;
                        }
                        nextSnapshot = Time.realtimeSinceStartupAsDouble + 1.0 / 15;
                    }
                    PresentDesktopPreview();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    PublishDesktopJson(new JObject { ["error"] = ex.Message }.ToString(Formatting.None));
                }
            }
        }

        void PresentDesktopPreview()
        {
            if (readbackSource != null)
            {
                if (!readback.done) return;
                try
                {
                    readbackFailed = readback.hasError;
                    if (!readbackFailed && desktopDelivery.AcceptFrame(readbackLayout, readbackDocument, documentGeneration)
                        && previewTexture != null && previewTexture.width == readback.width && previewTexture.height == readback.height)
                        SendDesktopPixels(readback.GetData<byte>(), readback.width, readback.height);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(readbackSource);
                    readbackSource = null;
                }
            }
            if (previewTexture == null || !previewTexture.IsCreated() || exporting) return;
            int w = previewTexture.width, h = previewTexture.height;
            previewCamera.Render();
            // Resolve MSAA and decouple pending GPU work from viewport resize/destruction.
            var resolved = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            try
            {
                bool async = !readbackFailed && SystemInfo.supportsAsyncGPUReadback;
                // Unity's RGBA readback and ReadPixels start at the texture's
                // bottom row. Both native image APIs consume top-down scanlines.
                Graphics.Blit(previewTexture, resolved, new Vector2(1, -1), new Vector2(0, 1));
                if (async)
                {
                    readback = AsyncGPUReadback.Request(resolved, 0, TextureFormat.RGBA32);
                    readbackSource = resolved;
                    readbackLayout = desktopDelivery.Layout;
                    readbackDocument = documentGeneration;
                    return;
                }
                if (syncReadback == null || syncReadback.width != w || syncReadback.height != h)
                {
                    if (syncReadback != null) Destroy(syncReadback);
                    syncReadback = new Texture2D(w, h, TextureFormat.RGBA32, false);
                }
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = resolved;
                    syncReadback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                }
                finally { RenderTexture.active = previous; }
                SendDesktopPixels(syncReadback.GetRawTextureData<byte>(), w, h);
            }
            finally { if (readbackSource != resolved) RenderTexture.ReleaseTemporary(resolved); }
        }

        void SendDesktopPixels(Unity.Collections.NativeArray<byte> data, int w, int h)
        {
            if (desktopPixels == null || desktopPixels.Length != data.Length) desktopPixels = new byte[data.Length];
            data.CopyTo(desktopPixels);
            presentDesktop(desktopPixels, w, h, documentGeneration, desktopDelivery.Layout);
        }
    }
}
