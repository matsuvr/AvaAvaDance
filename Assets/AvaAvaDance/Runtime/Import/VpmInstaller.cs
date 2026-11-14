using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AvaAvaDance.Import
{
    [Serializable]
    public sealed class VpmPackageIdentity
    {
        public string name;
        public string version;
        public string repo;
    }

    public static class VpmInstaller
    {
        public const string LilToon = "jp.lilxyzw.liltoon";
        public const string OfficialRepository = "https://lilxyzw.github.io/vpm-repos/vpm.json";
        public const string ConfigPath = "Assets/com.anatawa12.vpm-package-auto-installer/config.json";

        // Check the installer's requirements against the selected external shader pack.
        public static string Resolve(byte[] json, VpmPackageIdentity installed, string editorVersion)
        {
            if (json == null || json.Length == 0 || json.Length > 64 * 1024)
                throw new InvalidDataException("VPM config.json のサイズが不正です。");
            XElement config;
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(json, new XmlDictionaryReaderQuotas { MaxDepth = 16, MaxStringContentLength = 8192 }))
                config = XElement.Load(reader);
            var dependencies = config.Element("vpmDependencies");
            var dependency = dependencies?.Elements(LilToon).SingleOrDefault();
            if (dependency == null || (string)dependency.Attribute("type") != "string")
                throw new InvalidDataException("このVPMインストーラーには lilToon の依存指定がありません。");
            string range = dependency.Value;
            if (dependencies.Elements().Count() != 1)
                throw new InvalidDataException("未対応のVPM依存: " + string.Join(", ", dependencies.Elements().Where(e => e != dependency).Select(e => e.Name.LocalName)));
            if (installed == null || installed.name != LilToon || installed.repo != OfficialRepository)
                throw new InvalidDataException("右上の「シェーダー追加 → lilToon を導入」から、ライセンスを確認して導入してください。");
            var repositories = config.Element("vpmRepositories")?.Elements("item")
                .Select(item => (string)item.Attribute("type") == "string" ? item.Value : (string)item.Element("url"));
            if (repositories == null || !repositories.Contains(installed.repo))
                throw new InvalidDataException("lilToonの公式VPMリポジトリが指定されていません。");
            string minimumUnity = (string)config.Element("minimumUnity");
            if (!string.IsNullOrEmpty(minimumUnity))
            {
                string engine = Regex.Match(editorVersion, @"^\d+\.\d+\.\d+").Value;
                if (!Version.TryParse(minimumUnity, out var minimum) ||
                    !Version.TryParse(engine, out var current) || current < minimum)
                    throw new InvalidDataException("このインストーラーに必要なUnityバージョン: " + minimumUnity);
            }
            if (range.Length > 256 || !range.Split(new[] { "||" }, StringSplitOptions.None).Any(group =>
                !string.IsNullOrWhiteSpace(group) && group.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .All(clause => MatchesVersion(installed.version, clause))))
                throw new InvalidDataException("lilToon の要求バージョン: " + range + " / 読込済み: " + installed.version);
            return "VPM: " + LilToon + " " + range + " → lilToon " + installed.version + " 使用可能";
        }

        private static bool MatchesVersion(string installed, string range)
        {
            if (!Version.TryParse(installed, out var version) || string.IsNullOrWhiteSpace(range)) return false;
            var match = Regex.Match(range.Trim(), @"^(?<op>\^|~|>=|<=|>|<|=)?(?<major>\d+|[xX*])(?:\.(?<minor>\d+|[xX*]))?(?:\.(?<patch>\d+|[xX*]))?$");
            if (!match.Success) return false;
            var components = new List<int>();
            bool wildcard = false;
            foreach (var part in new[] { "major", "minor", "patch" })
            {
                string value = match.Groups[part].Value;
                if (value == "" || value == "*" || value.Equals("x", StringComparison.OrdinalIgnoreCase)) { wildcard = true; continue; }
                if (wildcard || !int.TryParse(value, out var number)) return false;
                components.Add(number);
            }
            string op = match.Groups["op"].Value;
            if (op == "" || op == "=")
                return components.SequenceEqual(new[] { version.Major, version.Minor, version.Build }.Take(components.Count));
            if (components.Count != 3) return false;
            var lower = new Version(components[0], components[1], components[2]);
            switch (op)
            {
                case ">=": return version >= lower;
                case "<=": return version <= lower;
                case ">": return version > lower;
                case "<": return version < lower;
                case "~": return version >= lower && version.Major == lower.Major && version.Minor == lower.Minor;
                case "^": return version >= lower && version.Major == lower.Major &&
                        (lower.Major > 0 || (version.Minor == lower.Minor && (lower.Minor > 0 || version.Build == lower.Build)));
                default: return false;
            }
        }
    }
}
