using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable]
        sealed class SavedMotion
        {
            public string format, model;
            public bool bones;
            public VmdSidecar detached;
            public List<SavedTrack> tracks;
        }

        [Serializable]
        sealed class SavedTrack
        {
            public string name,id,activeLayer,activeClip;
            public MotionLayer[] layers;
            public int[] frames;
            public KeySource[] sources;
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

        void ExportImage(string path) => WritePreview(path);

        const int MaxMotionBytes = 16 * 1024 * 1024;
        const int MaxAvaMotionBytes = 128 * 1024 * 1024;
        void SaveMotion(string path)
        {
            if (!path.EndsWith(".avamotion", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("モーションの保存には .avamotion を指定してください。");
            var motion = CaptureMotion(modelMode);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(motion));
            if (bytes.Length > MaxAvaMotionBytes) throw new IOException("モーションファイルのサイズ上限を超えています。");
            WriteFile(path, bytes);
        }

        SavedMotion CaptureMotion(bool model, bool draft = false, bool contentOnly = false)
        {
            var motion = new SavedMotion { format = "AvaAvaDance motion 2", bones = model,
                model = model ? actor.avatar.name : null, tracks = new List<SavedTrack>() };
            if(!draft)motion.detached=model?actor.vmdSidecar:sceneSidecar;
            foreach (var track in model ? actor.modelTracks : sceneTracks)
            {
                if(draft && contentOnly && !track.HasDraft(frame))continue;
                var saved = new SavedTrack { name = track.Name, frames = draft ? new[] { 0 } : track.Frames.ToArray() };
                if (!draft) { saved.id=track.Id; saved.activeLayer=contentOnly?null:track.ActiveLayer; saved.activeClip=contentOnly?null:track.ActiveClip; saved.layers=track.Layers.ToArray(); }
                if(!draft && saved.frames.Any(f=>track.SourceAt(f)!=null))
                    saved.sources=saved.frames.Select(track.SourceAt).ToArray();
                T[] Values<T>() => saved.frames.Select(f => (T)(draft ? track.CurrentValue : track.KeyAt(f))).ToArray();
                if (track is MotionTrack<CameraPose>) saved.cameras = Values<CameraPose>();
                else if (track is MotionTrack<LightPose>) saved.lights = Values<LightPose>();
                else if (track is MotionTrack<float>)
                {
                    saved.weights = Values<float>();
                    if (!draft && saved.frames.Any(f => track.CurveAt(f).custom))
                        saved.weightCurves=saved.frames.Select(track.CurveAt).ToArray();
                }
                else if (track is MotionTrack<ShadowPose>) saved.shadows = Values<ShadowPose>();
                else if (track is MotionTrack<GravityPose>) saved.gravity = Values<GravityPose>();
                else if (track is MotionTrack<SwitchPose>) saved.switches = Values<SwitchPose>();
                else if (track is MotionTrack<AccessoryPose>) saved.props = Values<AccessoryPose>().Select(SavePropParent).ToArray();
                else saved.poses = Values<BonePose>();
                motion.tracks.Add(saved);
            }
            return motion;
        }

        void LoadMotion(string path)
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxAvaMotionBytes) throw new InvalidDataException("モーションファイルが大きすぎます。");
            using var input = new StreamReader(stream, Encoding.UTF8);
            var motion = JsonUtility.FromJson<SavedMotion>(input.ReadToEnd());
            RestoreMotion(motion, modelMode, false, true);
            SetFrame(frame);
        }

        void RestoreMotion(SavedMotion motion, bool model, bool draft = false, bool legacy = false)
        {
            if (motion == null || (motion.format != "AvaAvaDance motion 1" && motion.format != "AvaAvaDance motion 2") || motion.tracks == null)
                throw new InvalidDataException("AvaAvaDanceのモーション形式を選択してください。");
            if (motion.bones != model || (model && (actor.avatar == null || motion.model != actor.avatar.name)))
                throw new InvalidDataException("保存時と同じモデルまたはカメラ編集を選択してください。");
            var expected = (model ? actor.modelTracks : sceneTracks).ToDictionary(t => (t.Name, t.GetType()));
            var loaded = new Dictionary<MotionTrack, List<KeyValuePair<int, object>>>();
            var layerData=new Dictionary<MotionTrack,SavedTrack>();
            foreach (var saved in motion.tracks)
            {
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
                Array values = track == cameraTrack ? (Array)saved.cameras : track == lightTrack ? saved.lights : track is MotionTrack<float> ? saved.weights : track is MotionTrack<AccessoryPose> ? saved.props : track is MotionTrack<ShadowPose> ? saved.shadows : track is MotionTrack<GravityPose> ? saved.gravity : track is MotionTrack<SwitchPose> ? saved.switches : (Array)saved.poses;
                if (values == null || values.Length != saved.frames.Length)
                    throw new InvalidDataException("キー数が一致しません。");
                if(saved.sources?.Length>0 && (draft || saved.sources.Length!=saved.frames.Length ||
                    saved.sources.Any(s=>s!=null && (s.name?.Length>0 && s.name.Length!=15 ||
                        s.ik?.Length>0 && (s.ik.Length%21!=0 || !(track is MotionTrack<SwitchPose>))))))
                    throw new InvalidDataException("キーの入力情報が不正です。");
                if (saved.weightCurves?.Length > 0 && (draft || !(track is MotionTrack<float>) ||
                    saved.weightCurves.Length != values.Length || saved.weightCurves.Any(c=>!c.Valid)))
                    throw new InvalidDataException("表情の補間曲線が不正です。");
                var keys = new List<KeyValuePair<int, object>>();
                for (int i = 0; i < values.Length; i++)
                {
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
                        if(!track.SupportsLayers || saved.layers.Length>256 || saved.layers.Any(l=>l==null) || saved.layers.Select(l=>l.id).Distinct().Count()!=saved.layers.Length)
                            throw new InvalidDataException("レイヤー構成が不正です。");
                        foreach(var layer in saved.layers) layer.Validate();
                    }
                    layerData.Add(track,saved);
                }
                loaded.Add(track, keys);
            }
            var optional = new HashSet<MotionTrack>();
            if(model)optional.UnionWith(actor.logicalControls);
            if (legacy)
            {
                optional.UnionWith(new MotionTrack[] { gravityTrack, shadowTrack, actor.modelSwitch });
                optional.UnionWith(actor.ikChains.Select(c => c.Handle.Track));
                var joints = actor.authoredLinks.Keys.Concat(actor.authoredLinks.Values.SelectMany(d => d.Children)).ToHashSet();
                optional.UnionWith(actor.bones.Where(b => joints.Contains(b.Joint)).Select(b => b.Track));
            }
            var missing = expected.Values.FirstOrDefault(t => !loaded.ContainsKey(t) && !optional.Contains(t));
            if (missing != null) throw new InvalidDataException("必要なトラックがありません: " + missing.Name);
            if(!draft)
            {
                var detached=motion.detached??new VmdSidecar();detached.records??=new List<DetachedKey>();
                int[] sizes={111,23,61,28,9,9};
                foreach(var key in detached.records)
                {
                    int ch=(int)(key?.channel??(Ava.Extensions.MotionChannel)99);
                    if(ch<0 || ch>=6 || key.data==null || (ch==5?key.data.Length<9 || key.data.Length!=9L+21L*BitConverter.ToUInt32(key.data,5):key.data.Length!=sizes[ch]))
                        throw new InvalidDataException("未割当キーが不正です。");
                }
                if(model)actor.vmdSidecar=detached;else sceneSidecar=detached;
            }
            // Only replace the target tracks once the entire file has been validated.
            foreach (var entry in loaded)
            {
                var track = entry.Key;
                if (draft) { track.Restore(entry.Value[0].Value); continue; }
                var saved=layerData[track]; if(!string.IsNullOrEmpty(saved.id)) track.Id=saved.id;
                track.Layers.Clear(); if(saved.layers!=null) track.Layers.AddRange(saved.layers);
                track.ActiveLayer=track.Layers.Any(l=>l.id==saved.activeLayer)?saved.activeLayer:null;
                track.ActiveClip=track.Target?.clips.Find(c=>c.id==saved.activeClip)?.id;
                foreach (int f in track.Frames.Skip(1).ToArray()) track.Write(f, null);
                track.ClearSources();
                foreach (var key in entry.Value) track.Write(key.Key, key.Value);
                if(saved.sources?.Length>0)for(int i=0;i<saved.frames.Length;i++)if(saved.sources[i]!=null &&
                    (saved.sources[i].name?.Length>0 || saved.sources[i].ik?.Length>0))track.SetSource(saved.frames[i],saved.sources[i]);
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
