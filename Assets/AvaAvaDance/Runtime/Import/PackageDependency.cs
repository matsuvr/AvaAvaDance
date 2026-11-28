using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AvaAvaDance.Import
{
    internal enum DependencyKind { Shader, Material, Texture }

    internal sealed class PackageDependency
    {
        internal DependencyKind Kind;
        internal string Guid, Name, UsedBy, OfficialPack;
        internal bool Included;
        internal string KindLabel => Kind == DependencyKind.Material ? "マテリアル" : Kind == DependencyKind.Texture ? "テクスチャ" : "シェーダー";
        internal string SearchUrl => "https://www.google.com/search?q=" + Uri.EscapeDataString(Name ?? Guid);
        internal static bool External(string guid) => !string.IsNullOrEmpty(guid) &&
            !(guid.StartsWith("0000000000000000", StringComparison.Ordinal) && guid.EndsWith("000000000000000", StringComparison.Ordinal));

        // The official packs' manifests identify their ShaderLab names even before installation.
        internal static Dictionary<string,(string pack,string name)> ReadNames(string manifest)
        {
            var result = new Dictionary<string,(string,string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in manifest.Split('\n'))
            {
                var fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length == 3) result.Add(fields[0], (fields[1], fields[2]));
            }
            return result;
        }

        internal static PackageDependency Shader(string guid, string usedBy, UnityPackageReader.PackageAsset source,
            Dictionary<string,(string pack,string name)> catalog)
        {
            var item = new PackageDependency { Kind=DependencyKind.Shader, Guid=guid, UsedBy=usedBy, Included=source?.HasAsset==true };
            if (guid != null && catalog.TryGetValue(guid,out var known))
            {
                item.Name=known.name;
                item.OfficialPack=known.pack;
            }
            else if (source != null)
            {
                item.Name=Path.GetFileNameWithoutExtension(source.Path);
                if (source.HasAsset && source.Path.EndsWith(".shader",StringComparison.OrdinalIgnoreCase))
                {
                    string header=Encoding.UTF8.GetString(source.Data,0,Math.Min(source.Data.Length,65536));
                    var match=Regex.Match(header,@"(?m)^[ \t\uFEFF]*Shader\s+""([^""\r\n]{1,256})""");
                    if (match.Success) item.Name=match.Groups[1].Value;
                }
            }
            return item;
        }
    }
}
