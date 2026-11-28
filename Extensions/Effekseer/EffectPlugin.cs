using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Ava.Extensions;

namespace Ava.Effekseer
{
    public sealed class EffectPlugin : IAvaPlugin, IModelEffectFormat
    {
        IModelEffectEditor editor;
        string packageDirectory;
        public string[] Extensions { get; } = { "efk", "efkefc" };

        public void Register(IPluginRegistrar registrar)
        {
            registrar.AddCommand("ava.effekseer.open", "Effekseer", "エフェクト読込…",
                () => editor.PickModelEffect(this), () => editor?.CanImportEffect == true);
        }
        public void Activate(IPluginContext context)
        {
            editor = (IModelEffectEditor)context;
            packageDirectory = context.PackageDirectory;
            editor.AddModelEffect(this);
        }
        public void Deactivate() => editor = null;
        public void Dispose() { }

        public string CompanionModel(string sourcePath)
        {
            var local = Path.ChangeExtension(sourcePath, ".pmd");
            var shared = Path.Combine(packageDirectory, "efk.pmd");
            return File.Exists(local) ? local : File.Exists(shared) ? shared : null;
        }

        public byte[] ControllerDescription(string sourcePath)
        {
            using var input = typeof(EffectPlugin).Assembly.GetManifestResourceStream("Ava.Effekseer.Controller.json");
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate void ResourcePath([MarshalAs(UnmanagedType.LPWStr)] string name);
        const string Library = "AvaEffekseer";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int ava_efk_inspect(byte[] source, int length, ResourcePath visit);
        public string[] Dependencies(byte[] source)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ava_efk_inspect(source, source.Length, path => result.Add(path.Replace('\\', '/'))) == 0)
                throw new InvalidDataException("Effekseer のエフェクトデータを読み込めません。");
            var paths = new string[result.Count];
            result.CopyTo(paths);
            return paths;
        }

        public IModelEffectInstance Open(byte[] source, IReadOnlyDictionary<string, byte[]> resources)
            => new Instance(source, resources);

        sealed class Instance : IModelEffectInstance
        {
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            static extern int ava_efk_create(byte[] source, int length);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            static extern void ava_efk_resource(int id, string path, byte[] data, int length);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            static extern int ava_efk_queue(int id, IntPtr target, float[] view, float[] projection, float[] center, float[] basis, float[] controls);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            static extern IntPtr ava_efk_event();
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            static extern void ava_efk_release(int id);
            int id;

            public Instance(byte[] source, IReadOnlyDictionary<string, byte[]> resources)
            {
                id = ava_efk_create(source, source.Length);
                if (id == 0) throw new InvalidDataException("Effekseer の初期化に失敗しました。");
                try
                {
                    foreach (var file in resources)
                        ava_efk_resource(id, file.Key, file.Value, file.Value.Length);
                }
                catch { Dispose(); throw; }
            }

            public int Prepare(ModelEffectFrame frame, out IntPtr renderEvent)
            {
                var morph = frame.Morph;
                var controls = new[] {
                    frame.Timeline, frame.Bone("再生")[13] - .5f + 100 * morph("フレーム"),
                    1 + 10 * morph("拡大") - morph("縮小"), 1 + morph("速度UP") - morph("速度DOWN"),
                    morph("トリガー"), morph("トリガー削除"), morph("オート再生"), morph("ループ"),
                    morph("stop root"), morph("@エフェクトテスト"), frame.PreviewDelta, frame.Exporting ? 1f : 0f
                };
                renderEvent = ava_efk_event();
                var token = ava_efk_queue(id, frame.TargetTexture, frame.View, frame.Projection,
                    frame.Bone("センター"), frame.Bone("ベース"), controls);
                if (token < 0) throw new InvalidOperationException("Effekseer の Direct3D 11 描画処理に失敗しました。");
                return token;
            }

            public void Dispose()
            {
                if (id != 0) ava_efk_release(id);
                id = 0;
            }
        }
    }
}
