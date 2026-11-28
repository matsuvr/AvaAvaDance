using System;
using System.Buffers.Binary;
using System.Text;

namespace AvaAvaDance.Import
{
    // Unity ModelImporter fileIdsGeneration 2 uses XXH64 over a typed UTF-8 hierarchy path.
    internal static class ModelFileId
    {
        const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL;
        const ulong P3 = 1609587929392839161UL, P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

        static ulong Rotate(ulong value, int bits) => (value << bits) | (value >> (64 - bits));
        static ulong Round(ulong state, ulong lane) => Rotate(state + lane * P2, 31) * P1;

        internal static long Hash(string identifier)
        {
            ReadOnlySpan<byte> data = Encoding.UTF8.GetBytes(identifier);
            int length = data.Length;
            ulong hash = P5;
            if (length >= 32)
            {
                ulong a = unchecked(P1 + P2), b = P2, c = 0, d = unchecked(0UL - P1);
                do
                {
                    a = Round(a, BinaryPrimitives.ReadUInt64LittleEndian(data));
                    b = Round(b, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8)));
                    c = Round(c, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(16)));
                    d = Round(d, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(24)));
                    data = data.Slice(32);
                } while (data.Length >= 32);
                hash = Rotate(a, 1) + Rotate(b, 7) + Rotate(c, 12) + Rotate(d, 18);
                foreach (ulong lane in new[] { a, b, c, d })
                    hash = (hash ^ Round(0, lane)) * P1 + P4;
            }
            hash += (uint)length;
            while (data.Length >= 8)
            {
                hash ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data));
                hash = Rotate(hash, 27) * P1 + P4;
                data = data.Slice(8);
            }
            if (data.Length >= 4)
            {
                hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data) * P1;
                hash = Rotate(hash, 23) * P2 + P3;
                data = data.Slice(4);
            }
            foreach (byte value in data) hash = Rotate(hash ^ (value * P5), 11) * P1;
            hash = (hash ^ (hash >> 33)) * P2;
            hash = (hash ^ (hash >> 29)) * P3;
            return unchecked((long)(hash ^ (hash >> 32)));
        }
    }
}
