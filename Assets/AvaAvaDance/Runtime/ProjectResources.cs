using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AvaAvaDance
{
    internal sealed class ProjectResourceWriter
    {
        internal const long BinaryLimit = 2L * 1024 * 1024 * 1024;
        readonly List<byte[]> entries = new();
        readonly JObject resources = new();
        readonly JArray views = new();
        long length;

        internal void Add(string name, byte[] bytes)
        {
            AddView(name, bytes.Length);
            if (bytes.Length > 0) entries.Add(bytes);
        }

        void AddView(string name, int size)
        {
            if (resources.Count >= Import.UnityPackageReader.MaxMembers || size < 0 || length + size > BinaryLimit)
                throw new IOException("プロジェクトの素材データが大きすぎます。");
            if (size == 0)
            {
                resources.Add(name, JValue.CreateNull());
                return;
            }
            resources.Add(name, new JValue(views.Count));
            views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = length, ["byteLength"] = size });
            length += AvaGlb.Align4(size);
        }

        internal void WritePrefix(Stream stream, JObject state, int jsonLimit, int audioBytes, System.Threading.CancellationToken cancellation)
        {
            if (audioBytes > 0) AddView("audio.f32", audioBytes);
            var root = AvaGlb.Root(AvaGlb.ProjectExtension, new JObject
            {
                ["specVersion"] = AvaGlb.Version, ["state"] = state, ["resources"] = resources
            });
            root["extensionsRequired"] = new JArray(AvaGlb.ProjectExtension);
            if (length > 0)
            {
                root["buffers"] = new JArray(new JObject { ["byteLength"] = length });
                root["bufferViews"] = views;
            }
            AvaGlb.WriteHeader(stream, root, jsonLimit, length);
            byte[] padding = new byte[3];
            foreach (var bytes in entries)
            {
                cancellation.ThrowIfCancellationRequested();
                stream.Write(bytes, 0, bytes.Length);
                stream.Write(padding, 0, (4 - bytes.Length % 4) % 4);
            }
        }
    }

    internal sealed class ProjectResourceReader
    {
        readonly Dictionary<string, (long offset, int length)> entries = new(StringComparer.Ordinal);
        readonly Stream stream;
        internal JObject State { get; }
        internal IEnumerable<string> Names => entries.Keys;

        internal ProjectResourceReader(Stream stream, int jsonLimit)
        {
            this.stream = stream;
            var root = AvaGlb.Read(stream, jsonLimit, out long binStart, out long binLength);
            var payload = AvaGlb.Extension(root, AvaGlb.ProjectExtension);
            if (!(payload["state"] is JObject state) || !(payload["resources"] is JObject resources) ||
                resources.Count > Import.UnityPackageReader.MaxMembers || binLength > ProjectResourceWriter.BinaryLimit)
                throw new InvalidDataException("プロジェクト構成が不正です。");
            State = state;
            var views = root["bufferViews"] as JArray;
            long declaredLength = 0;
            if (resources.Properties().All(resource => resource.Value.Type == JTokenType.Null))
            {
                if (binLength != 0 || root["buffers"] != null || views != null)
                    throw new InvalidDataException("プロジェクトのバッファが不正です。");
            }
            else if (!(root["buffers"] is JArray buffers) || buffers.Count != 1 ||
                !(buffers[0] is JObject buffer) || buffer["uri"] != null || views == null ||
                !Integer(buffer["byteLength"], out declaredLength) || declaredLength < 1 || declaredLength > binLength ||
                AvaGlb.Align4(declaredLength) != binLength || binLength == 0)
                throw new InvalidDataException("プロジェクトのバッファが不正です。");

            long total = 0;
            foreach (var resource in resources.Properties())
            {
                if (resource.Value.Type == JTokenType.Null)
                {
                    entries.Add(resource.Name, (0, 0));
                    continue;
                }
                if (!Integer(resource.Value, out long index) || views == null || index < 0 || index >= views.Count ||
                    !(views[(int)index] is JObject view) || !Integer(view["buffer"], out long bufferIndex) || bufferIndex != 0 ||
                    !Integer(view["byteOffset"] ?? new JValue(0), out long offset) || offset < 0 || offset % 4 != 0 ||
                    !Integer(view["byteLength"], out long size) || size < 1 || size > int.MaxValue || size > declaredLength - offset)
                    throw new InvalidDataException("プロジェクトの素材参照が不正です。");
                entries.Add(resource.Name, (binStart + offset, (int)size));
                total += size;
                if (total > ProjectResourceWriter.BinaryLimit)
                    throw new InvalidDataException("プロジェクトの素材データが大きすぎます。");
            }
        }

        static bool Integer(JToken value, out long number)
        {
            number = 0;
            return value?.Type == JTokenType.Integer && long.TryParse(value.ToString(), out number);
        }

        internal bool Contains(string name) => entries.ContainsKey(name);

        internal int Seek(string name, long limit)
        {
            if (!entries.TryGetValue(name, out var entry)) throw new InvalidDataException("プロジェクト内に " + name + " がありません。");
            if (entry.length > limit) throw new InvalidDataException("プロジェクト内データが大きすぎます。");
            stream.Position = entry.offset;
            return entry.length;
        }

        internal byte[] Read(string name, long limit)
        {
            int length = Seek(name, limit);
            var bytes = new byte[length];
            for (int offset = 0; offset < length;)
            {
                int read = stream.Read(bytes, offset, length - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return bytes;
        }
    }
}
