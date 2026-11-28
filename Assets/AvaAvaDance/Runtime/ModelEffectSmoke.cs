using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        string CheckEffectModel(string path, string output)
        {
            ImportModelEffect(Path.GetFullPath(path));
            viewOptions.black=true; UpdateBackdrop();
            var playback = actor.bones.Single(b => b.Track.Name == "再生").Track;
            var pose = BonePose.Rest;
            pose.Position.y = 20 * SceneUnit;
            playback.Preview(pose);
            RegisterKey(playback);
            var marker = actor.modelTracks.OfType<MotionTrack<float>>().Single(t => MorphName(t) == "モデル消去");
            marker.Preview(1);
            RegisterKey(marker);
            var center = actor.bones.Single(b => b.Track.Name == "センター").Track;
            var placement = BonePose.Rest;
            placement.Position.y = 8 * SceneUnit;
            center.Preview(placement);
            RegisterKey(center);
            var project = Path.Combine(output, "effect.avaproject");
            SaveProject(project);

            byte[] Capture(string name)
            {
                var file = Path.Combine(output, name + ".png");
                WriteOutputImage(file, 384, 256);
                return File.ReadAllBytes(file);
            }
            actor.avatar.SetActive(false);
            var empty = Capture("hidden");
            actor.avatar.SetActive(true);
            var image = Capture("effect");
            if (image.SequenceEqual(empty)) throw new InvalidOperationException("エフェクトの描画ピクセルがありません。");
            if (!Capture("hold").SequenceEqual(image)) throw new InvalidOperationException("再生ボーンの停止中に絵が変化しました。");
            OpenProject(project);
            if (actor.controller?.alias != Path.GetFileName(path) || !Capture("restored").SequenceEqual(image))
                throw new InvalidOperationException("作品内の素材とキーから同じ絵を復元できません。");
            playback = actor.bones.Single(b => b.Track.Name == "再生").Track;
            var earlier = playback.Current;
            earlier.Position.y -= 10 * SceneUnit;
            playback.Preview(earlier);
            if (!Capture("falling").SequenceEqual(empty) || !Capture("falling-hold").SequenceEqual(empty))
                throw new InvalidOperationException("再生ボーンの Y が減少した位置で粒子が表示されています。");
            playback.Preview(pose);
            if (!Capture("restart").SequenceEqual(image)) throw new InvalidOperationException("再生ボーンの再開位置が不正です。");
            return "PASS Effekseer import, hold, reverse suppression, restart, visibility, image output and project round trip";
        }
    }
}
