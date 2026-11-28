using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvaAvaDance.Import;
using Newtonsoft.Json;
using UniGLTF;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable]
        sealed class SavedMotion
        {
            public string specVersion, scope, model;
            public List<SavedTrack> tracks;
        }

        [Serializable]
        sealed class SavedTrack
        {
            public string name,id,activeLayer,activeClip;
            public MotionLayer[] layers;
            public int[] frames;
            public BonePose[] poses;
            public AccessoryPose[] props;
            public CameraPose[] cameras;
            public float[] weights;
            public EaseCurve[] weightCurves;
            public LightPose[] lights;
            public ShadowPose[] shadows;
            public GravityPose[] gravity;
            public SwitchPose[] switches;
        }

        void FileAction(string title, string extension, bool save, System.Action<string> action)
        {
            StopPlayback();
            try
            {
                string path = save ? PackageFileDialog.Save(title, extension) : PackageFileDialog.Open(title, extension);
                if (string.IsNullOrEmpty(path)) return;
                status.text = title + "　" + Path.GetFileName(path); action(path);
            }
            catch (Exception ex) { ReportActionError(title + "中止　", ex); }
        }

        static void WriteFile(string path, byte[] bytes)
        {
            string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Guid.NewGuid() + ".tmp");
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        const int MaxMotionBytes = 16 * 1024 * 1024;
        const int MaxAvaMotionBytes = 128 * 1024 * 1024;
        void SaveMotion(string path)
        {
            if (!path.EndsWith(".avamotion", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("モーションの保存には .avamotion を指定してください。");
            WriteFile(path, EncodeMotion(CaptureMotion(modelMode), Path.GetFileNameWithoutExtension(path)));
        }

        byte[] EncodeMotion(SavedMotion motion, string name)
        {
            return EncodeMotion(motion, BuildAnimationGltf(name, motion.scope == "model"));
        }

        async Task<byte[]> EncodeProjectMotion(SavedMotion motion, string name, Actor owner, IAwaitCaller caller, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = await BuildAnimationGltfAsync(name, owner, false, caller, cancellation);
            return await caller.Run(() => EncodeMotion(motion, data));
        }

        static byte[] EncodeMotion(SavedMotion motion, ExportingGltfData data)
        {
            var payload = AvaGlb.ParseJson(JsonUtility.ToJson(motion));
            if (data != null)
            {
                var extensions = data.Gltf.extensions as glTFExtensionExport ?? new glTFExtensionExport();
                var json = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
                data.Gltf.extensions = extensions.Add(AvaGlb.MotionExtension, new ArraySegment<byte>(json));
                byte[] projected = data.ToGlbBytes();
                if (projected.Length <= MaxAvaMotionBytes) return projected;
            }

            using var output = new MemoryStream();
            AvaGlb.WriteHeader(output, AvaGlb.Root(AvaGlb.MotionExtension, payload), MaxAvaMotionBytes - 20, 0);
            return output.ToArray();
        }

        SavedMotion CaptureMotion(bool model, bool draft = false, bool contentOnly = false) =>
            CaptureMotionAsync(model ? actor : null, draft, contentOnly, new ImmediateCaller()).GetAwaiter().GetResult();

        async Task<SavedMotion> CaptureMotionAsync(Actor owner, bool draft, bool contentOnly, IAwaitCaller caller)
        {
            var motion = new SavedMotion { specVersion = AvaGlb.Version, scope = owner != null ? "model" : "scene",
                model = owner?.avatar.name, tracks = new List<SavedTrack>() };
            foreach (var track in owner != null ? owner.modelTracks : sceneTracks)
            {
                await caller.NextFrameIfTimedOut();
                if (draft && contentOnly && !track.HasDraft(frame)) continue;
                var saved = new SavedTrack { name = track.Name, frames = draft ? new[] { 0 } : track.Frames.ToArray() };
                if (!draft) {
                    saved.id = track.Id;
                    saved.activeLayer = contentOnly ? null : track.ActiveLayer;
                    saved.activeClip = contentOnly ? null : track.ActiveClip;
                    saved.layers = track.Layers.ToArray();
                }

                async Task<T[]> Values<T>(Func<int, T> readValue)
                {
                    var values = new T[saved.frames.Length];
                    for (int i = 0; i < values.Length; i++) {
                        if (i > 0 && (i & 511) == 0) await caller.NextFrameIfTimedOut();
                        int f = saved.frames[i];
                        values[i] = readValue(f);
                        if (draft) continue;
                        if (track is MotionTrack<float>) {
                            var curve = track.CurveAt(f);
                            if (curve.custom) (saved.weightCurves ??= new EaseCurve[values.Length])[i] = curve;
                        }
                    }
                    return values;
                }
                object Value(int f) => draft ? track.CurrentValue : track.KeyAt(f);
                if (track is MotionTrack<CameraPose>) saved.cameras = await Values(f => (CameraPose)Value(f));
                else if (track is MotionTrack<LightPose>) saved.lights = await Values(f => (LightPose)Value(f));
                else if (track is MotionTrack<float>) saved.weights = await Values(f => (float)Value(f));
                else if (track is MotionTrack<ShadowPose>) saved.shadows = await Values(f => (ShadowPose)Value(f));
                else if (track is MotionTrack<GravityPose>) saved.gravity = await Values(f => (GravityPose)Value(f));
                else if (track is MotionTrack<SwitchPose>) saved.switches = await Values(f => (SwitchPose)Value(f));
                else if (track is MotionTrack<AccessoryPose>) saved.props = await Values(f => SavePropParent((AccessoryPose)Value(f)));
                else saved.poses = await Values(f => (BonePose)Value(f));
                motion.tracks.Add(saved);
            }
            return motion;
        }

        void LoadMotion(string path)
        {
            using var stream = File.OpenRead(path);
            RestoreMotion(DecodeMotion(stream), modelMode, allowMissingGeneratedTracks: true);
            SetFrame(frame);
        }

        static SavedMotion DecodeMotion(Stream stream)
        {
            if (stream.Length > MaxAvaMotionBytes) throw new InvalidDataException("モーションファイルが大きすぎます。");
            var payload = AvaGlb.Extension(AvaGlb.Read(stream, MaxAvaMotionBytes, out _, out _), AvaGlb.MotionExtension);
            return JsonUtility.FromJson<SavedMotion>(payload.ToString(Formatting.None));
        }

        void RestoreMotion(SavedMotion motion, bool model, bool draft = false, bool allowMissingGeneratedTracks = false) =>
            RestoreMotionAsync(motion, model, new ImmediateCaller(), draft, allowMissingGeneratedTracks).GetAwaiter().GetResult();

        async Task<Dictionary<MotionTrack, (SavedTrack data, List<KeyValuePair<int, object>> keys)>> ValidateMotionTracks(
            SavedMotion motion, Actor owner, bool model, IAwaitCaller caller, bool draft, bool allowMissingGeneratedTracks)
        {
            if (motion == null || motion.specVersion != AvaGlb.Version || motion.tracks == null)
                throw new InvalidDataException("AvaAvaDanceのモーション形式を選択してください。");
            if (motion.scope != (model ? "model" : "scene") || (model && (owner.avatar == null || motion.model != owner.avatar.name)))
                throw new InvalidDataException("保存時と同じモデルまたはカメラ編集を選択してください。");
            var expected = (model ? owner.modelTracks : sceneTracks).ToDictionary(t => (t.Name, t.GetType()));
            var loaded = new Dictionary<MotionTrack, (SavedTrack data, List<KeyValuePair<int, object>> keys)>();
            foreach (var saved in motion.tracks)
            {
                await caller.NextFrameIfTimedOut();
                var type = saved?.poses?.Length > 0 ? typeof(MotionTrack<BonePose>) :
                    saved?.props?.Length > 0 ? typeof(MotionTrack<AccessoryPose>) :
                    saved?.shadows?.Length > 0 ? typeof(MotionTrack<ShadowPose>) :
                    saved?.gravity?.Length > 0 ? typeof(MotionTrack<GravityPose>) :
                    saved?.switches?.Length > 0 ? typeof(MotionTrack<SwitchPose>) :
                    saved?.weights?.Length > 0 ? typeof(MotionTrack<float>) :
                    saved?.cameras?.Length > 0 ? typeof(MotionTrack<CameraPose>) : typeof(MotionTrack<LightPose>);
                if (saved == null || saved.name == null || !expected.TryGetValue((saved.name, type), out var track)
                    || loaded.ContainsKey(track) || saved.frames == null || saved.frames.Length == 0 || saved.frames[0] != 0)
                    throw new InvalidDataException("トラックが現在の編集対象と一致しません。");
                Array values = track == cameraTrack ? (Array)saved.cameras : track is MotionTrack<LightPose> ? saved.lights : track is MotionTrack<float> ? saved.weights : track is MotionTrack<AccessoryPose> ? saved.props : track is MotionTrack<ShadowPose> ? saved.shadows : track is MotionTrack<GravityPose> ? saved.gravity : track is MotionTrack<SwitchPose> ? saved.switches : (Array)saved.poses;
                if (values == null || values.Length != saved.frames.Length)
                    throw new InvalidDataException("キー数が一致しません。");
                if (saved.weightCurves?.Length > 0 && (draft || !(track is MotionTrack<float>) ||
                    saved.weightCurves.Length != values.Length || saved.weightCurves.Any(c=>!c.Valid)))
                    throw new InvalidDataException("表情の補間曲線が不正です。");
                var keys = new List<KeyValuePair<int, object>>();
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0 && (i & 511) == 0) await caller.NextFrameIfTimedOut();
                    int f = saved.frames[i];
                    if (f < 0 || f > MaxFrame || (i > 0 && f <= saved.frames[i-1]))
                        throw new InvalidDataException("フレーム番号が不正です。");
                    var value = values.GetValue(i);
                    if (!ValidPose(value)) throw new InvalidDataException("登録値が不正です。");
                    if (saved.weightCurves?.Length > 0) value = new MorphKey {Weight=(float)value, Curve=saved.weightCurves[i]};
                    keys.Add(new KeyValuePair<int, object>(f, value));
                }
                if(!draft)
                {
                    if(saved.layers?.Length>0)
                    {
                        if(!track.SupportsLayers || saved.layers.Length>256 || saved.layers.Any(l=>l==null) || saved.layers.Select(l=>l.id).Distinct().Count()!=saved.layers.Length ||
                            saved.layers.Count(l=>!string.IsNullOrEmpty(l.group))>(track is MotionTrack<BonePose>?1:0))
                            throw new InvalidDataException("レイヤー構成が不正です。");
                        foreach(var layer in saved.layers) layer.Validate();
                    }
                }
                loaded.Add(track, (saved, keys));
            }
            var optional = new HashSet<MotionTrack>();
            if(model)optional.UnionWith(owner.logicalControls);
            if (allowMissingGeneratedTracks)
            {
                optional.UnionWith(new MotionTrack[] { gravityTrack, shadowTrack, owner.modelSwitch });
                optional.UnionWith(owner.ikChains.Select(c => c.Handle.Track));
                var joints = owner.authoredLinks.Keys.Concat(owner.authoredLinks.Values.SelectMany(d => d.Children)).ToHashSet();
                optional.UnionWith(owner.bones.Where(b => joints.Contains(b.Joint)).Select(b => b.Track));
            }
            var missing = expected.Values.FirstOrDefault(t => !loaded.ContainsKey(t) && !optional.Contains(t));
            if (missing != null) throw new InvalidDataException("必要なトラックがありません: " + missing.Name);
            return loaded;
        }

        async Task RestoreMotionAsync(SavedMotion motion, bool model, IAwaitCaller caller, bool draft = false, bool allowMissingGeneratedTracks = false)
        {
            var loaded = await ValidateMotionTracks(motion, actor, model, caller, draft, allowMissingGeneratedTracks);
            // Only replace the target tracks once the entire file has been validated.
            foreach (var entry in loaded)
            {
                var track = entry.Key;
                if (draft) { track.Restore(entry.Value.keys[0].Value); continue; }
                var saved=entry.Value.data; if(!string.IsNullOrEmpty(saved.id)) track.Id=saved.id;
                track.Layers.Clear(); if(saved.layers!=null) track.Layers.AddRange(saved.layers);
                track.ActiveLayer=track.Layers.Any(l=>l.id==saved.activeLayer)?saved.activeLayer:null;
                track.ActiveClip=track.Target?.clips.Find(c=>c.id==saved.activeClip)?.id;
                for (int i = track.Frames.Count - 1; i > 0; i--) {
                    track.Write(track.Frames[i], null);
                    if ((i & 511) == 0) await caller.NextFrameIfTimedOut();
                }
                for (int i = 0; i < entry.Value.keys.Count; i++) {
                    if ((i & 511) == 0) await caller.NextFrameIfTimedOut();
                    var key = entry.Value.keys[i];
                    track.Write(key.Key, key.Value);
                }
            }
            if(!draft) InvalidateDynamics();
            DiscardHistory(undo, loaded.ContainsKey); DiscardHistory(redo, loaded.ContainsKey);
        }

        static bool ValidPose(object value)
        {
            bool VectorOK(Vector3 v) => float.IsFinite(v.sqrMagnitude) && v.sqrMagnitude <= 1e12f;
            if (value is AccessoryPose prop) return VectorOK(prop.Position) && VectorOK(prop.Rotation) && prop.Scale >= 0 && prop.Scale <= 100000 && prop.Opacity >= 0 && prop.Opacity <= 1;
            if (value is ShadowPose shadow) return shadow.Mode >= 0 && shadow.Mode <= 2 && shadow.Range >= 0 && shadow.Range <= 9999;
            if (value is GravityPose g) return g.Acceleration >= 0 && g.Acceleration <= 1000 && VectorOK(g.Direction) && g.NoiseAmount >= 0 && g.NoiseAmount <= 100;
            if (value is SwitchPose sw) return sw.DisabledIK==null || (sw.DisabledIK.Length<=2048 && sw.DisabledIK.All(n=>!string.IsNullOrEmpty(n)));
            if (value is float weight) return float.IsFinite(weight);
            if (value is CameraPose camera)
                return CurveSet.Valid(camera.Curves) && VectorOK(camera.Center) && VectorOK(camera.Angles) && Mathf.Abs(camera.Distance) <= 100000
                    && camera.ViewAngle >= 1 && camera.ViewAngle <= 135;
            if (value is BonePose bone)
                return CurveSet.Valid(bone.Curves) && VectorOK(bone.Position) && Mathf.Abs(Quaternion.Dot(bone.Rotation, bone.Rotation) - 1) < .001f;
            var lamp = (LightPose)value;
            return VectorOK(lamp.Direction) && lamp.Color.r >= 0 && lamp.Color.r <= 1 && lamp.Color.g >= 0
                && lamp.Color.g <= 1 && lamp.Color.b >= 0 && lamp.Color.b <= 1;
        }

        void WritePreview(string path)
        {
            var previous = RenderTexture.active;
            var image = new Texture2D(previewTexture.width, previewTexture.height, TextureFormat.RGB24, false);
            try
            {
                previewCamera.Render();
                RenderTexture.active = previewTexture;
                image.ReadPixels(new Rect(0,0,image.width,image.height),0,0);
                image.Apply();
                string ext=Path.GetExtension(path).ToLowerInvariant();
                byte[] data=ext==".jpg" || ext==".jpeg"?image.EncodeToJPG(95):image.EncodeToPNG();
                if(ext!=".png" && ext!=".jpg" && ext!=".jpeg")data=DirectXImage.Convert(data,ext);
                WriteFile(path,data);
            }
            finally { RenderTexture.active = previous; Destroy(image); }
        }
    }
}
