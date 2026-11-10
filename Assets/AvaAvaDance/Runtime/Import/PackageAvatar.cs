using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance.Import
{
    internal sealed class PackageAvatar
    {
        internal Asset Model, Prefab;
        internal string Label => Prefab==null ? Model.Path+" （FBXのみ）" : Prefab.Path+"  →  "+Path.GetFileName(Model.Path);
        static readonly Regex Mesh=new Regex(@"(?m)^  m_Mesh: \{fileID: -?\d+, guid: ([0-9a-fA-F]{32})\b");

        internal static List<PackageAvatar> Choices(IEnumerable<Asset> assets,IEnumerable<Asset> models)
        {
            var byGuid=models.ToDictionary(a=>a.Guid,StringComparer.OrdinalIgnoreCase);
            var choices=new List<PackageAvatar>();
            foreach(var prefab in assets) {
                if(!prefab.HasAsset || prefab.Data.Length>16*1024*1024 || !prefab.Path.EndsWith(".prefab",StringComparison.OrdinalIgnoreCase))continue;
                var guids=Mesh.Matches(Encoding.UTF8.GetString(prefab.Data)).Cast<Match>().Select(m=>m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach(var guid in guids)
                    if(byGuid.TryGetValue(guid,out var model))choices.Add(new PackageAvatar {Model=model,Prefab=prefab});
            }
            var paired=new HashSet<Asset>(choices.Select(a=>a.Model));
            choices.AddRange(byGuid.Values.Where(a=>!paired.Contains(a)).Select(a=>new PackageAvatar {Model=a}));
            return choices.OrderByDescending(a=>a.Prefab!=null).ThenByDescending(a=>a.Model.Data.Length)
                .ThenByDescending(a=>a.SameName).ThenBy(a=>a.Label,StringComparer.Ordinal).ToList();
        }

        bool SameName => Prefab!=null && string.Equals(Path.GetFileNameWithoutExtension(Prefab.Path),
            Path.GetFileNameWithoutExtension(Model.Path),StringComparison.OrdinalIgnoreCase);

        // Older projects stored only the model GUID. Recover the unique corresponding prefab.
        internal static Asset LegacyPrefab(IEnumerable<Asset> assets,Asset model)
        {
            var choices=Choices(assets,new[]{model});
            var named=choices.Where(a=>a.SameName).ToList();
            return named.Count==1 ? named[0].Prefab : choices.Count==1 ? choices[0].Prefab : null;
        }
    }
}
