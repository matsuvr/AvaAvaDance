using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Ava.Extensions;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static bool LocalAsset(string path,string[] roots)
        {
            string full=Path.GetFullPath(path);
            return roots.Any(prefix=>full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) && File.Exists(full);
        }
        static byte[] ReadLocalAsset(string path,string[] roots)
        {
            if(!LocalAsset(path,roots))throw new FileNotFoundException(path);
            if(new FileInfo(path).Length>64L*1024*1024)throw new InvalidDataException("参照ファイルが大きすぎます。");
            return File.ReadAllBytes(path);
        }
        SceneSnapshot SceneState()
        {
            var materials=EnumerateMaterials().ToLookup(entry=>entry.slot.ActorId,entry=>entry.slot.Id);
            var models=drawOrder.Select(a=>new SceneObject{Kind=OwnerKind.Model,Id=a.id,Name=a.label,Source=a.modelGuid,
                Visible=a.avatar.activeSelf,Selected=modelMode && actor==a,Tracks=a.modelTracks.Select(t=>t.Id).ToArray(),Materials=materials[a.id].ToArray()});
            var accessories=props.Select(p=>new SceneObject{Kind=OwnerKind.Accessory,Id=p.Data.id,Name=p.Data.name,Source=p.Data.name,
                Visible=p.Object.activeSelf && !p.Track.Current.Hidden,Selected=!modelMode && selectedTrack==p.Track,Tracks=new[]{p.Track.Id},Materials=materials[p.Data.id].ToArray()});
            return new SceneSnapshot{Document=documentGeneration,Assets=sourceRevision,ActiveTrack=selectedTrack?.Id,Objects=models.Concat(accessories).ToArray(),
                DrawOrder=drawOrder.Select(a=>a.id).ToArray(),CalculationOrder=calculationOrder.Select(a=>a.id).ToArray()};
        }
        void SelectSceneItem(string owner,string id)
        {
            var model=drawOrder.FirstOrDefault(a=>a.id==owner);
            var selected=id==null?null:EditableTracks().First(e=>e.owner==owner && e.track.Id==id).track;
            if(model!=null){ChooseMode(model.label);if(selected!=null)SelectTrack(selected);return;}
            if(owner!="scene")selected=props.First(p=>p.Data.id==owner).Track;
            ChangeMode(false);SelectTrack(selected??cameraTrack);
        }
        void EditSceneOrder(long document,string[] order,bool physics)
        {
            if(document!=documentGeneration || order==null)throw new InvalidOperationException("対象の作品が変更されています。");
            var list=physics?calculationOrder:drawOrder;var incoming=(string[])order.Clone();var previous=list.Select(a=>a.id).ToArray();
            if(previous.SequenceEqual(incoming))return;
            if(incoming.Length!=list.Count || !new HashSet<string>(incoming).SetEquals(previous))throw new InvalidDataException("モデルの順序が不正です。");
            void Apply(MotionTrack _,bool forward){
                var desired=forward?incoming:previous;
                var live=desired.Where(id=>list.Any(a=>a.id==id)).Concat(list.Where(a=>!desired.Contains(a.id)).Select(a=>a.id)).ToArray();
                RestoreOrder(list,live);
                if(physics){ClearBullet();foreach(var model in calculationOrder)ClearSimulation(model);}else ApplyDrawOrder();
                RefreshActorUI();
            }
            Apply(cameraTrack,true);PushOperation(cameraTrack,Apply);
        }
        IEnumerable<SourceFiles> SourceFiles()
        {
            var roots=drawOrder.SelectMany(o=>o.modelAssets.Select(a=>a.ContainerPath)).Concat(props.Select(p=>p.Data.sourcePath))
                .Where(p=>!string.IsNullOrEmpty(p)).Select(p=>Path.GetDirectoryName(Path.GetFullPath(p)).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var owner in drawOrder){
                var asset=owner.modelAssets.FirstOrDefault(m=>m.Guid==owner.modelGuid);
                if(asset==null)continue;
                string folder=string.IsNullOrEmpty(asset.ContainerPath)?null:Path.GetDirectoryName(asset.ContainerPath);
                // A virtual base also resolves sibling files when the original archive is offline.
                string root=folder??Path.Combine(Path.GetTempPath(),"ava-project-assets",owner.id);
                var files=owner.modelAssets.Where(a=>a.HasAsset).GroupBy(a=>Path.GetFullPath(Path.Combine(root,a.Path)),StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
                yield return new SourceFiles{Owner=owner.id,Name=owner.controller?.alias??owner.label,Asset=Path.GetFullPath(Path.Combine(root,asset.Path)),Folder=folder,
                    Exists=p=>files.ContainsKey(p) || LocalAsset(p,roots),
                    Read=p=>files.TryGetValue(p,out var entry)?(byte[])entry.Data.Clone():ReadLocalAsset(p,roots)};
            }
            foreach(var prop in props){
                string path=prop.Data.sourcePath;
                if(path!=null)yield return new SourceFiles{Owner=prop.Data.id,Name=prop.Data.name,Asset=path,Folder=Path.GetDirectoryName(path),
                    Exists=p=>LocalAsset(p,roots),Read=p=>ReadLocalAsset(p,roots)};
            }
        }
    }
}
