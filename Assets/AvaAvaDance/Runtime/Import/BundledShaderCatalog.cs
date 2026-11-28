using System;
using UnityEngine;

namespace AvaAvaDance.Import
{
    public sealed class BundledShaderCatalog : ScriptableObject
    {
        [Serializable] public struct Entry
        {
            public string guid;
            public long fileId;
            public Shader shader;
        }
        public Entry[] entries = Array.Empty<Entry>();

        internal Shader Find(string guid, long fileId)
        {
            foreach (var entry in entries)
                if (entry.fileId == fileId && (string.Equals(entry.guid, guid, StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrEmpty(guid) && entry.guid == "0000000000000000f000000000000000")))
                    return entry.shader;
            return null;
        }
    }
}
