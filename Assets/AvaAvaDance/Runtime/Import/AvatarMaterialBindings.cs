using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AvaAvaDance.Import
{
    // Read visual data only. No prefab component, script or referenced asset is instantiated.
    internal sealed class AvatarMaterialBindings
    {
        internal sealed class Slots
        {
            internal bool Visible;
            internal int CastShadows;
            internal bool ReceiveShadows;
            internal string[] Materials;
        }

        internal readonly Dictionary<string, Slots> Renderers = new Dictionary<string, Slots>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> Remaps = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Regex GuidField = new Regex(@"\bguid: ([a-fA-F0-9]{32})\b");

        internal static AvatarMaterialBindings Read(UnityPackageReader.PackageAsset model,
            UnityPackageReader.PackageAsset prefab)
        {
            var result = new AvatarMaterialBindings();
            result.ReadRemaps(model.Meta);
            if (prefab!=null) result.ReadPrefab(prefab.Data, model.Guid);
            return result;
        }

        private void ReadRemaps(string meta)
        {
            if (meta == null || meta.Length > 4 * 1024 * 1024) return;
            using var reader = new StringReader(meta);
            bool section = false, material = false;
            string name = null, line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!section) { section = line == "  externalObjects:"; continue; }
                if (line.StartsWith("  - first:", StringComparison.Ordinal)) { name = null; material = false; }
                else if (line == "      type: UnityEngine:Material") material = true;
                else if (line.StartsWith("      name: ", StringComparison.Ordinal)) name = line.Substring(12).Trim();
                else if (line.StartsWith("    second: ", StringComparison.Ordinal) && material && name != null)
                {
                    var guid = GuidField.Match(line);
                    if (guid.Success) Remaps[name] = Remaps.ContainsKey(name) ? null : guid.Groups[1].Value;
                }
                else if (!line.StartsWith("    ", StringComparison.Ordinal)) break;
            }
        }

        private void ReadPrefab(byte[] bytes, string modelGuid)
        {
            var objects = new Dictionary<string, (string name, bool active)>();
            var transforms = new Dictionary<string, (string owner, string parent)>();
            var parents = new Dictionary<string, string>();
            var renderers = new List<(string owner, Slots slots)>();
            var meshFilters = new HashSet<string>();
            var staticRenderers = new List<(string owner, Slots slots)>();
            var docs = Regex.Split(Encoding.UTF8.GetString(bytes), @"(?m)^--- !u!");
            foreach (string doc in docs)
            {
                if (doc.StartsWith("1 &", StringComparison.Ordinal))
                {
                    var id = Regex.Match(doc, @"^1 &(-?\d+)");
                    var name = Regex.Match(doc, @"(?m)^  m_Name: ([^\r\n]+)");
                    if (id.Success && name.Success) objects[id.Groups[1].Value] = (name.Groups[1].Value, !Regex.IsMatch(doc, @"(?m)^  m_IsActive: 0\r?$"));
                }
                else if (doc.StartsWith("4 &", StringComparison.Ordinal))
                {
                    var id = Regex.Match(doc, @"^4 &(-?\d+)");
                    var owner = Regex.Match(doc, @"(?m)^  m_GameObject: \{fileID: (-?\d+)\}");
                    var parent = Regex.Match(doc, @"(?m)^  m_Father: \{fileID: (-?\d+)\}");
                    if (id.Success && owner.Success && parent.Success)
                    {
                        transforms[id.Groups[1].Value] = (owner.Groups[1].Value, parent.Groups[1].Value);
                        parents[owner.Groups[1].Value] = parent.Groups[1].Value;
                    }
                }
                else if (doc.StartsWith("137 &", StringComparison.Ordinal) || doc.StartsWith("33 &", StringComparison.Ordinal) ||
                         doc.StartsWith("23 &", StringComparison.Ordinal))
                {
                    var owner = Regex.Match(doc, @"(?m)^  m_GameObject: \{fileID: (-?\d+)\}");
                    if (!owner.Success) continue;
                    bool meshRenderer = doc.StartsWith("23 &", StringComparison.Ordinal);
                    if (!meshRenderer)
                    {
                        var mesh = Regex.Match(doc, @"(?m)^  m_Mesh: [^\r\n]*\bguid: ([a-fA-F0-9]{32})");
                        if (!string.Equals(mesh.Groups[1].Value, modelGuid, StringComparison.OrdinalIgnoreCase)) continue;
                        if (doc.StartsWith("33 &", StringComparison.Ordinal)) { meshFilters.Add(owner.Groups[1].Value); continue; }
                    }
                    var list = Regex.Match(doc, @"(?m)^  m_Materials:(?: \[\]\r?$|\r?\n((?:  - [^\r\n]+\r?\n){1,256})(?!  - ))");
                    if (!list.Success) continue;
                    var slots = list.Groups[1].Success ? list.Groups[1].Value.TrimEnd().Split('\n')
                        .Select(s => s.TrimEnd().EndsWith("{fileID: 0}", StringComparison.Ordinal) ? null : GuidField.Match(s).Groups[1].Value).ToArray()
                        : Array.Empty<string>();
                    var cast = Regex.Match(doc, @"(?m)^  m_CastShadows: ([0-3])\r?$");
                    (meshRenderer ? staticRenderers : renderers).Add((owner.Groups[1].Value, new Slots {
                        CastShadows = cast.Success ? cast.Groups[1].Value[0] - '0' : 1,
                        ReceiveShadows = !Regex.IsMatch(doc, @"(?m)^  m_ReceiveShadows: 0\r?$"),
                        Visible = !Regex.IsMatch(doc, @"(?m)^  m_Enabled: 0\r?$"), Materials = slots }));
                }
            }
            foreach (var item in renderers.Concat(staticRenderers.Where(r => meshFilters.Contains(r.owner))))
            {
                if (!objects.TryGetValue(item.owner, out var obj)) continue;
                item.slots.Visible &= obj.active;
                parents.TryGetValue(item.owner, out var parent);
                var seen = new HashSet<string>();
                while (item.slots.Visible && parent != null && transforms.TryGetValue(parent, out var ancestor))
                {
                    item.slots.Visible = seen.Add(parent) && seen.Count <= 256 &&
                        objects.TryGetValue(ancestor.owner, out var owner) && owner.active;
                    parent = ancestor.parent;
                }
                Renderers[obj.name] = Renderers.ContainsKey(obj.name) ? null : item.slots;
            }
        }
    }
}
