using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ava.Extensions;
using AvaAvaDance.Import;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        McpEndpoint mcp;
        bool mcpBackground;
        double mcpActiveUntil;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        delegate void TimerCallback(IntPtr window, uint message, UIntPtr id, uint time);
        [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint interval, TimerCallback callback);
        [DllImport("user32.dll")] static extern bool KillTimer(IntPtr window, UIntPtr id);
        TimerCallback mcpWake;
        UIntPtr mcpTimer;
#endif
        Exception mcpActionError;
        void ReportActionError(string prefix, Exception error)
        {
            status.text = prefix + error.Message;
            if (PackageFileDialog.AutomationChooser != null) mcpActionError = error;
        }
        readonly JsonSerializer mcpJson = JsonSerializer.Create(new JsonSerializerSettings {
            Converters = { new StringEnumConverter() }, MissingMemberHandling = MissingMemberHandling.Error
        });

        void StartMcp()
        {
            if (Environment.GetCommandLineArgs().Contains("-avaNoMcp")) return;
            try
            {
                mcp = new McpEndpoint(processId, Application.isEditor, Application.dataPath, McpDispatch);
#if UNITY_EDITOR
                UnityEditor.EditorApplication.update += WakePendingMcp;
#elif UNITY_STANDALONE_WIN
                // The Windows message loop still runs when an unfocused player suspends Update.
                // The timer runs on Unity's thread. Dispatch stays in Update, outside native dialogs.
                mcpWake = (_, __, ___, ____) => WakePendingMcp();
                mcpTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 200, mcpWake);
                if (mcpTimer == UIntPtr.Zero) throw new InvalidOperationException("Could not install MCP wake timer.");
#endif
            }
            catch (Exception ex) { StopMcp(); Debug.LogWarning("AvaAvaDance MCP: " + ex.Message); }
        }

        void WakePendingMcp() { if (mcp?.HasPending == true) WakeMcp(); }

        void WakeMcp()
        {
            if (!mcpBackground) { AcquireBackground(); mcpBackground = true; }
            mcpActiveUntil = Time.realtimeSinceStartupAsDouble + 10;
        }

        void PumpMcp()
        {
            if (mcp == null) return;
            mcp.Pump();
            if (mcpBackground && Time.realtimeSinceStartupAsDouble > mcpActiveUntil &&
                !mcp.HasPending && !importing && !loadingWave && !exporting && !playing)
            {
                mcpBackground = false;
                ReleaseBackground();
            }
        }

        void StopMcp()
        {
            if (mcp == null) return;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= WakePendingMcp;
#elif UNITY_STANDALONE_WIN
            if (mcpTimer != UIntPtr.Zero) KillTimer(IntPtr.Zero, mcpTimer);
            mcpTimer = UIntPtr.Zero;
#endif
            try { mcp.Dispose(); }
            catch (IOException ex) { Debug.LogWarning("AvaAvaDance MCP: " + ex.Message); }
            finally
            {
                mcp = null;
                if (mcpBackground) { mcpBackground = false; ReleaseBackground(); }
            }
        }

        JToken Json(object value) => value == null ? JValue.CreateNull() : JToken.FromObject(value, mcpJson);
        JToken Summary() => Json(new {
            document = documentGeneration, projectPath, frame, playing, modelMode,
            selectedOwner = modelMode ? actor.id : activeProp?.Track == selectedTrack ? activeProp?.Data.id : "scene",
            selectedTrack = selectedTrack?.Id, importing, loadingWave, exporting, exportError = movieError,
            dialog = modal?.Q<Label>("dialog-title")?.text ?? importDialog?.Q<Label>()?.text,
            status = status.text, undo = undo.Count, redo = redo.Count, profile = ribbonProfile ? "ribbon" : "classic"
        });

        JToken Page<T>(IEnumerable<T> items, JObject args)
        {
            int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 64;
            if (offset < 0 || limit < 1 || limit > 256) throw new ArgumentException("offset >= 0, limit 1..256");
            var page = items.Skip(offset).Take(limit + 1).ToList();
            return Json(new { items = page.Take(limit), nextOffset = page.Count > limit ? (int?)(offset + limit) : null });
        }

        JToken McpState(JObject args)
        {
            switch ((string)args["section"])
            {
                case "summary": return Summary();
                case "scene": return Json(SceneState());
                case "materials": return Page(EnumerateMaterials().Select(e => e.slot), args);
                case "rig": {
                    var model = drawOrder.FirstOrDefault(a => a.id == (string)args["owner"]);
                    if (model?.rigProfile == null) throw new ArgumentException("Choose a model with a rig from scene.");
                    return JObject.Parse(JsonUtility.ToJson(model.rigProfile));
                }
                case "project": return JObject.Parse(JsonUtility.ToJson(CaptureProject()));
                case "document": return Json(new { projectPath, document = documentGeneration, modified = ProjectChanged() });
                case "timeline": return Json(new { frame, playing, fps = 30, maxFrame = MaxFrame, lastFrame = LastFrame(),
                    start = transportStart, end = transportEnd, repeat = transportRepeat, bookmarks,
                    notes = bookmarkMemos, selectedRows = selectedRows.Select(t => t.Id),
                    selectedKeys = selectedKeys.Select(k => new { track = k.track.Id, k.frame }) });
                case "audio": return Json(new { name = soundtrack.clip?.name, samples = soundtrack.clip?.samples,
                    channels = soundtrack.clip?.channels, frequency = soundtrack.clip?.frequency, soundtrack.volume, muteWave, scrubSound, loadingWave });
                case "render": return Json(new { output = JObject.Parse(JsonUtility.ToJson(movie)),
                    view = JObject.Parse(JsonUtility.ToJson(viewOptions)), fpsLimit, screenMode, groundShadow, showSelfShadow, exporting });
                case "physics": return Json(new { engine = physicsEngine, mode = physicsMode, physicalFloor, forceOnPlayback,
                    showBodies, gravity = ToKey(gravityTrack.Current) });
                case "extensions": return Json(packages.Select(p => new { p.manifest.id, p.manifest.version, p.manifest.displayName, p.active }));
                default: throw new ArgumentException("Unknown state section.");
            }
        }

        bool McpCanEdit => modal == null && importDialog == null && !exporting && !importing && !loadingWave && !drainingExtensions;

        string McpCommandId(Command id)
        {
            if ((int)id < 10000) return "core." + id;
            return extensionCommands.FirstOrDefault(p => p.Value == id).Key ?? "core.ManageExtensions";
        }

        IEnumerable<object> McpCommands(MmdMenu.Entry[] entries, string group = "")
        {
            foreach (var entry in entries)
            {
                if (entry.Children.Length > 0)
                {
                    foreach (var child in McpCommands(entry.Children, (group + "/" + entry.Label(true)).TrimStart('/'))) yield return child;
                }
                else if (commands.TryGetValue(entry.Id, out var binding))
                    yield return new { id = McpCommandId(entry.Id), group, ja = entry.Label(false), en = entry.Label(true),
                        enabled = McpCanEdit && Available(entry.Id), @checked = binding.Checked?.Invoke() };
            }
        }

        async Task<JToken> McpDispatch(JObject request)
        {
            WakeMcp();
            if ((string)request["tool"] != "preview") return McpCall(request);
            var args = (JObject)request["args"];
            int size = (int)args["size"];
            if (size < 64 || size > 1600) throw new ArgumentOutOfRangeException("size");
            string surface = (string)args["surface"];
            if (surface != "workspace")
            {
                var target = surface switch {
                    "camera" => previewTexture, "operations" => operations?.Surface,
                    "timeline" => floatingTime?.Surface, "audio" => floatingWave?.Surface, _ => null
                };
                return McpImage(target, size);
            }
#if UNITY_EDITOR
            // Unity only renders the final Game view frame while the tab is visible.
            UnityEditor.EditorApplication.ExecuteMenuItem("Window/General/Game");
#endif
            var result = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var capture = StartCoroutine(CaptureMcpWorkspace(size, result));
            if (await Task.WhenAny(result.Task, Task.Delay(3000)) != result.Task)
            {
                if (this != null) StopCoroutine(capture);
                throw new TimeoutException("Workspace frame unavailable. In the Editor, show the Game view.");
            }
            return await result.Task;
        }

        IEnumerator CaptureMcpWorkspace(int size, TaskCompletionSource<JToken> result)
        {
            yield return new WaitForEndOfFrame();
            Texture2D image = null;
            try { image = ScreenCapture.CaptureScreenshotAsTexture(); result.TrySetResult(McpImage(image, size)); }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { if (image != null) Destroy(image); }
        }

        JToken McpCall(JObject request)
        {
            var args = (JObject)request["args"];
            string tool = (string)request["tool"], owner = (string)args["owner"], trackId = (string)args["track"];
            switch (tool)
            {
                case "state": {
                    JToken state = McpState(args);
                    string pointer = (string)args["pointer"];
                    if (string.IsNullOrEmpty(pointer)) return state;
                    if (!pointer.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("Expected a JSON Pointer starting with /.");
                    foreach (string segment in pointer.Substring(1).Split('/'))
                    {
                        string key = segment.Replace("~1", "/").Replace("~0", "~");
                        state = state is JObject obj ? obj[key] :
                            state is JArray array && int.TryParse(key, out int i) && i >= 0 && i < array.Count ? array[i] : null;
                        if (state == null) throw new ArgumentException("State path does not exist: " + pointer);
                    }
                    return state is JArray list ? Page(list, args) : state;
                }
                case "commands": return Page(McpCommands(combinedMenus), args);
                case "ui": return McpControls(args);
                case "tracks": {
                    var entries = EditableTracks().Where(e => owner == null || e.owner == owner);
                    return Page(entries.Select(e => new { e.owner, id = e.track.Id, name = e.track.Name,
                        channel = ToKey(e.track.CurrentValue).Channel, revision = e.track.Revision, keys = e.track.Frames.Count }), args);
                }
                case "keys": {
                    var t = EditableTracks().First(e => e.owner == owner && e.track.Id == trackId).track;
                    var result = (JObject)Page(t.Frames.Select(f => new { frame = f, value = ToKey(t.KeyAt(f), t.CurveAt(f), t.SourceAt(f)) }), args);
                    result["document"] = documentGeneration; result["revision"] = t.Revision; result["current"] = Json(ToKey(t.CurrentValue));
                    return result;
                }
                case "sample": return Json(SampleMotion(owner, trackId, (float)args["frame"]));
            }
            // Supply file choices only during this synchronous UI action. An async import or a
            // later confirmation receives its own call, with its own paths.
            var paths = new Queue<string>(args["paths"]?.ToObject<string[]>() ?? Array.Empty<string>());
            mcpActionError = null;
            PackageFileDialog.AutomationChooser = (title, extensions, saving) => {
                try
                {
                    if (paths.Count == 0) throw new ArgumentException($"Supply paths for '{title}' ({extensions}).");
                    string path = paths.Dequeue();
                    if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("File paths must be absolute.");
                    if (!extensions.Split(';').Any(ext => ext == "*" || path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("Expected a file with extension: " + extensions);
                    if (!saving && !File.Exists(path)) throw new FileNotFoundException("File does not exist.", path);
                    return path;
                }
                catch (Exception ex) { mcpActionError = ex; return null; }
            };
            try
            {
                if (tool == "control") McpControl(args);
                else if (tool == "input") McpInput(args);
                else
                {
                    if (!McpCanEdit) throw new InvalidOperationException("Finish the current dialog or operation first.");
                    switch (tool)
                    {
                        case "command": {
                            string id = (string)args["id"];
                            var entry = commands.FirstOrDefault(kv => McpCommandId(kv.Key) == id);
                            if (entry.Value == null || !Available(entry.Key)) throw new InvalidOperationException("Unknown or disabled command: " + id);
                            CloseMenu(); entry.Value.Run(); break;
                        }
                        case "select": SelectSceneItem(owner, trackId); break;
                        case "order": EditSceneOrder((long)args["document"], args["owners"].ToObject<string[]>(), (bool)args["physics"]); break;
                        case "edit_keys": ApplyMotionChanges((long)args["document"], args["edits"].ToObject<KeyEdit[]>(mcpJson)); break;
                        case "transport":
                            switch ((string)args["action"])
                            {
                                case "seek":
                                    int at = (int)args["frame"];
                                    if (at < 0 || at > MaxFrame) throw new ArgumentOutOfRangeException("frame");
                                    SetFrame(at); break;
                                case "play": if (!playing) TogglePlayback(); if (!playing) throw new InvalidOperationException(status.text); break;
                                case "stop": StopPlayback(); break;
                                case "undo": if (undo.Count == 0) throw new InvalidOperationException("Nothing to undo."); ApplyHistory(false); break;
                                case "redo": if (redo.Count == 0) throw new InvalidOperationException("Nothing to redo."); ApplyHistory(true); break;
                                default: throw new ArgumentException("Unknown transport action.");
                            }
                            break;
                        default: throw new ArgumentException("Unknown tool.");
                    }
                }
                if (mcpActionError != null) throw new InvalidOperationException(mcpActionError.Message, mcpActionError);
                var result = (JObject)Summary();
                if (paths.Count != 0) result["unusedPaths"] = new JArray(paths);
                return result;
            }
            finally { PackageFileDialog.AutomationChooser = null; mcpActionError = null; }
        }

        JToken McpImage(Texture source, int size)
        {
            if (source == null) throw new InvalidOperationException("The requested view is not open or rendered.");
            float scale = Mathf.Min(1f, (float)size / Mathf.Max(source.width, source.height));
            var target = RenderTexture.GetTemporary(Mathf.Max(1, Mathf.RoundToInt(source.width * scale)),
                Mathf.Max(1, Mathf.RoundToInt(source.height * scale)), 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, target); RenderTexture.active = target;
                copy = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                copy.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                return new JValue(Convert.ToBase64String(copy.EncodeToPNG()));
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); if (copy != null) Destroy(copy); }
        }
    }
}
