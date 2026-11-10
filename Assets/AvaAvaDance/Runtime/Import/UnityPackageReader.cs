using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AvaAvaDance.Import
{
    /// <summary>
    /// Reads Unity's GUID-based gzip/tar packages as inert bytes. Never extracts files,
    /// resolves filesystem paths, loads assemblies or executes package code.
    /// Unsupported tar extensions are rejected, not interpreted permissively.
    /// </summary>
    public static class UnityPackageReader
    {
        public const long MaxFileBytes = 256L * 1024 * 1024;
        public const long MaxExpandedBytes = 1024L * 1024 * 1024;
        public const int MaxMembers = 20000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public sealed class PackageAsset
        {
            public string Guid { get; private set; }
            public string Path { get; private set; }
            // Metadata-only folder entries have no asset payload.
            public byte[] Data { get; private set; }
            public string Meta { get; private set; }
            public bool HasAsset { get { return Data != null; } }
            internal PackageAsset(string guid, string path, byte[] data, string meta)
            { Guid = guid; Path = path; Data = data; Meta = meta; }
        }

        private sealed class Parts
        {
            internal byte[] Data, Meta;
            internal string Path;
        }

        /// <summary>Leaves the caller-owned stream open. Throws InvalidDataException on invalid input.</summary>
        public static List<PackageAsset> Read(string path)
        {
            using (var source = File.OpenRead(path)) return Read(source);
        }

        public static List<PackageAsset> Read(Stream source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!source.CanRead) throw new ArgumentException("The stream is not readable.", nameof(source));
            using (var gzip = new GZipStream(source, CompressionMode.Decompress, true))
            {
                long expanded = 0;
                int members = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries = new Dictionary<string, Parts>(StringComparer.Ordinal);
                var header = new byte[512];
                while (true)
                {
                    ReadExactly(gzip, header, 0, header.Length, ref expanded);
                    if (IsZero(header))
                    {
                        // POSIX end marker consists of two complete zero records.
                        ReadExactly(gzip, header, 0, header.Length, ref expanded);
                        if (!IsZero(header)) throw Bad("Missing second tar end record.");
                        // Consume through gzip EOF, both to validate its trailer and to reject
                        // appended archives/nonzero content. Zero record padding is allowed.
                        int count;
                        long trailing = 0;
                        while ((count = gzip.Read(header, 0, header.Length)) != 0)
                        {
                            Count(count, ref expanded); trailing += count;
                            for (int i = 0; i < count; i++)
                                if (header[i] != 0) throw Bad("Nonzero data after tar end marker.");
                        }
                        if (trailing % 512 != 0) throw Bad("Partial tar padding record.");
                        break;
                    }
                    if (++members > MaxMembers) throw Bad("Package has too many tar members.");
                    ValidateChecksum(header);
                    string name = Text(header, 0, 100);
                    string prefix = Text(header, 345, 155);
                    if (prefix.Length != 0) throw Bad("Tar path prefixes are unsupported.");
                    // No links, devices, sparse files, PAX headers or GNU long names.
                    byte type = header[156];
                    if (type != 0 && type != (byte)'0' && type != (byte)'5')
                        throw Bad("Unsupported tar member type.");
                    if (Text(header, 157, 100).Length != 0) throw Bad("Tar link names are forbidden.");
                    long size = Octal(header, 124, 12);
                    if (size > MaxFileBytes) throw Bad("Tar member exceeds 256 MiB.");
                    if (size > MaxExpandedBytes - expanded) throw Bad("Package exceeds expanded size limit.");
                    // VPM auto-installers created with tar use ./ and ./<guid>/.
                    bool directory = type == (byte)'5';
                    if (directory && size != 0) throw Bad("Directory member has a payload.");
                    if (directory && (name == "./" || name == ".")) continue;
                    if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                    string guid, member;
                    ParseMemberName(name, type == (byte)'5', out guid, out member);
                    if (!seen.Add(name)) throw Bad("Duplicate tar member.");
                    if (type == (byte)'5')
                    {
                        if (size != 0) throw Bad("Directory member has a payload.");
                        continue;
                    }
                    if (member == "pathname" && size > 4096) throw Bad("Pathname is too long.");
                    var payload = new byte[(int)size];
                    ReadExactly(gzip, payload, 0, payload.Length, ref expanded);
                    int padding = (int)((512 - (size % 512)) % 512);
                    if (padding != 0)
                    {
                        ReadExactly(gzip, header, 0, padding, ref expanded);
                        for (int i = 0; i < padding; i++)
                            if (header[i] != 0) throw Bad("Nonzero tar member padding.");
                    }
                    Parts parts;
                    if (!entries.TryGetValue(guid, out parts)) entries.Add(guid, parts = new Parts());
                    switch (member)
                    {
                        case "asset": parts.Data = payload; break;
                        case "asset.meta": parts.Meta = payload; break;
                        case "pathname": parts.Path = ValidateAssetPath(Decode(payload)); break;
                        // Preview is deliberately discarded; callers never decode it implicitly.
                        case "preview.png": break;
                    }
                }
                var result = new List<PackageAsset>(entries.Count);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in entries)
                {
                    if (pair.Value.Path == null) throw Bad("GUID entry has no pathname.");
                    if (!paths.Add(pair.Value.Path)) throw Bad("Multiple GUIDs use the same asset path.");
                    if (pair.Value.Data == null && pair.Value.Meta == null)
                        throw Bad("GUID entry contains neither asset nor metadata.");
                    result.Add(new PackageAsset(pair.Key, pair.Value.Path, pair.Value.Data, pair.Value.Meta == null ? null : Decode(pair.Value.Meta)));
                }
                result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
                return result;
            }
        }

        private static void ParseMemberName(string name, bool directory, out string guid, out string member)
        {
            if (name.Length < 33 || name[32] != '/') throw Bad("Invalid GUID member path.");
            for (int i = 0; i < 32; i++)
            {
                char c = name[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    throw Bad("Invalid asset GUID.");
            }
            guid = name.Substring(0, 32).ToLowerInvariant();
            member = name.Substring(33);
            if (directory)
            {
                if (member.Length != 0) throw Bad("Only GUID directories are allowed.");
            }
            else if (member != "asset" && member != "asset.meta" && member != "pathname" && member != "preview.png")
                throw Bad("Unexpected Unity package member.");
        }

        private static string ValidateAssetPath(string path)
        {
            // Unity writes raw UTF-8 pathname bytes, not a NUL-terminated string.
            if (path.Length == 0 || path.Length > 1024 || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0)
                throw Bad("Invalid asset pathname.");
            if (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal))
                throw Bad("Asset pathname must be inside Assets/.");
            foreach (char c in path) if (char.IsControl(c)) throw Bad("Control character in asset pathname.");
            foreach (string segment in path.Split('/'))
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(" ", StringComparison.Ordinal) || segment.EndsWith(".", StringComparison.Ordinal))
                    throw Bad("Unsafe asset pathname segment.");
            return path;
        }

        private static string Decode(byte[] bytes)
        {
            try { return Utf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw Bad("Invalid UTF-8."); }
        }
        private static string Text(byte[] bytes, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && bytes[end] != 0) end++;
            for (int i = end; i < offset + length; i++)
                if (bytes[i] != 0) throw Bad("Garbage after tar string terminator.");
            try { return Utf8.GetString(bytes, offset, end - offset); }
            catch (DecoderFallbackException) { throw Bad("Invalid tar UTF-8."); }
        }
        private static long Octal(byte[] bytes, int offset, int length)
        {
            long value = 0; bool digits = false, terminated = false;
            for (int i = offset; i < offset + length; i++)
            {
                byte c = bytes[i];
                if (c == 0 || c == (byte)' ')
                { if (digits || c == 0) terminated = true; continue; }
                if (terminated || c < (byte)'0' || c > (byte)'7') throw Bad("Invalid tar octal number.");
                digits = true;
                if (value > (long.MaxValue - 7) / 8) throw Bad("Tar number overflow.");
                value = value * 8 + (c - (byte)'0');
            }
            if (!digits) throw Bad("Missing tar number.");
            return value;
        }
        private static void ValidateChecksum(byte[] header)
        {
            long expected = Octal(header, 148, 8), sum = 0;
            for (int i = 0; i < header.Length; i++) sum += i >= 148 && i < 156 ? 32 : header[i];
            if (sum != expected) throw Bad("Tar checksum mismatch.");
        }
        private static bool IsZero(byte[] bytes)
        { for (int i = 0; i < bytes.Length; i++) if (bytes[i] != 0) return false; return true; }
        private static void ReadExactly(Stream stream, byte[] bytes, int offset, int length, ref long expanded)
        {
            while (length != 0)
            {
                int count = stream.Read(bytes, offset, length);
                if (count == 0) throw Bad("Truncated tar archive.");
                Count(count, ref expanded); offset += count; length -= count;
            }
        }
        private static void Count(int count, ref long expanded)
        {
            if (count > MaxExpandedBytes - expanded) throw Bad("Package exceeds 1 GiB expanded size.");
            expanded += count;
        }
        private static InvalidDataException Bad(string message) { return new InvalidDataException(message); }
    }
}
