using System;
using System.Linq;
using Ava.Extensions;

namespace Ava.BoneLayers
{
    public sealed class BoneLayerPlugin : IAvaPlugin
    {
        IPluginContext host;
        IBoneLayerEditor editor;
        IDisposable command;

        public void Register(IPluginRegistrar registrar)
        {
            command = registrar.AddCommand("ava.bone-layers.organize", "プラグイン(&E)", "ボーンをレイヤーに自動分類",
                Organize, () => editor?.CanOrganizeBoneLayers == true);
        }

        public void Activate(IPluginContext context)
        {
            host = context;
            editor = (IBoneLayerEditor)context;
        }

        void Organize()
        {
            var snapshot = editor.ReadBoneLayerTracks();
            if (snapshot == null) return;
            var assignments = snapshot.Tracks.Select(track =>
            {
                var (group, name) = track.IsHumanoid ? ("humanoid", "ヒューマノイド") : track.IsSpringBone
                    ? ("spring", "スプリングボーン") : ("unmapped", "その他のボーン");
                return new BoneLayerAssignment { Track = track.Id, Revision = track.Revision, Group = group, Name = name };
            }).ToArray();
            int count = editor.OrganizeBoneLayers(snapshot.Document, snapshot.Owner, assignments);
            host.Report(count == 0 ? "対象のボーンは分類済みです。" : $"{count} 本のボーンをレイヤーに分類しました。");
        }

        public void Deactivate()
        {
            host = null;
            editor = null;
        }

        public void Dispose() => command?.Dispose();
    }
}
