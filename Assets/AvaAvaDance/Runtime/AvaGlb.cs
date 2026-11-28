using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AvaAvaDance
{
    internal static class AvaGlb
    {
        internal const string MotionExtension = "AVA_motion";
        internal const string ProjectExtension = "AVA_project";
        internal const string VrmaExtension = "VRMC_vrm_animation";
        internal const string Version = "1.0";
        const uint Magic = 0x46546C67, JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static JObject ParseJson(string json)
        {
            using var input = new StringReader(json);
            using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None };
            var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonReaderException("Unexpected content after GLB JSON.");
            return root;
        }

        internal static JObject Root(string extension, JObject payload)
        {
            return new JObject
            {
                ["asset"] = new JObject { ["version"] = "2.0", ["generator"] = "AvaAvaDance" },
                ["extensionsUsed"] = new JArray(extension),
                ["extensions"] = new JObject { [extension] = payload }
            };
        }

        internal static JObject Read(Stream stream, int jsonLimit, out long binStart, out long binLength)
        {
            using var reader = new BinaryReader(stream, Utf8, leaveOpen: true);
            if (stream.Length < 20 || reader.ReadUInt32() != Magic || reader.ReadUInt32() != 2 ||
                reader.ReadUInt32() != stream.Length)
                throw new InvalidDataException("glTF 2.0 の GLB ファイルを選択してください。");

            uint length = reader.ReadUInt32();
            if (reader.ReadUInt32() != JsonChunk || length == 0 || length % 4 != 0 ||
                length > jsonLimit || length > stream.Length - stream.Position)
                throw new InvalidDataException("GLB の JSON チャンクが不正です。");

            JObject root;
            try
            {
                root = ParseJson(Utf8.GetString(reader.ReadBytes((int)length)));
            }
            catch (Exception e) when (e is JsonException || e is DecoderFallbackException)
            {
                throw new InvalidDataException("GLB の JSON が不正です。", e);
            }
            if (!(root["asset"] is JObject asset) || asset["version"]?.Type != JTokenType.String || (string)asset["version"] != "2.0")
                throw new InvalidDataException("GLB の asset.version が不正です。");

            binStart = binLength = 0;
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 8) throw new InvalidDataException("GLB のチャンクが途中で終わっています。");
                length = reader.ReadUInt32();
                uint type = reader.ReadUInt32();
                if (length % 4 != 0 || length > stream.Length - stream.Position || type == JsonChunk)
                    throw new InvalidDataException("GLB のチャンク長または種類が不正です。");
                if (type == BinChunk)
                {
                    if (binStart != 0) throw new InvalidDataException("GLB の BIN チャンクが重複しています。");
                    binStart = stream.Position;
                    binLength = length;
                }
                stream.Position += length;
            }
            return root;
        }

        internal static JObject Extension(JObject root, string name)
        {
            if (!(root["extensions"] is JObject extensions) || !(extensions[name] is JObject payload) ||
                !(root["extensionsUsed"] is JArray used) ||
                !used.Any(item => item.Type == JTokenType.String && (string)item == name) ||
                payload["specVersion"]?.Type != JTokenType.String || (string)payload["specVersion"] != Version)
                throw new InvalidDataException(name + " " + Version + " の GLB ファイルを選択してください。");
            return payload;
        }

        internal static long Align4(long value) => (value + 3) & ~3L;

        internal static void WriteHeader(Stream stream, JObject root, int jsonLimit, long binaryLength)
        {
            byte[] json = Utf8.GetBytes(root.ToString(Formatting.None));
            long jsonLength = Align4(json.Length), binLength = Align4(binaryLength);
            long length = 20 + jsonLength + (binLength == 0 ? 0 : 8 + binLength);
            if (jsonLength > jsonLimit || length > uint.MaxValue)
                throw new IOException("GLB のサイズ上限を超えています。");

            using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(2u);
            writer.Write((uint)length);
            writer.Write((uint)jsonLength);
            writer.Write(JsonChunk);
            writer.Write(json);
            for (long i = json.Length; i < jsonLength; i++) writer.Write((byte)0x20);
            if (binLength == 0) return;

            writer.Write((uint)binLength);
            writer.Write(BinChunk);
        }
    }
}
