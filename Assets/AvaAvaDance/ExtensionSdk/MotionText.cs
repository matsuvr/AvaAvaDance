using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace Ava.Extensions
{
    // Fixed-width motion names use Windows-31J on every system locale.
    public static class MmdText
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int MultiByteToWideChar(uint cp, uint flags, byte[] src, int size, [Out] StringBuilder dst, int chars);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern int WideCharToMultiByte(uint cp, uint flags, string src, int chars,
            byte[] dst, int size, IntPtr replacement, out int replaced);
        public static string Decode(byte[] b)
        {
            int n = Array.IndexOf(b,(byte)0); if (n < 0) n = b.Length; if (n == 0) return "";
            var chars = new StringBuilder(n+1); int size = MultiByteToWideChar(932, 0, b, n, chars, chars.Capacity);
            if (size == 0) throw new InvalidDataException("Shift-JIS文字列が不正です。");
            return chars.ToString(0,size);
        }
        public static byte[] Encode(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
            var bytes = new byte[checked(s.Length * 2)];
            int n = WideCharToMultiByte(932,0x400,s,s.Length,bytes,bytes.Length,IntPtr.Zero,out int replaced);
            if (n == 0 || replaced != 0) throw new InvalidDataException("Shift-JISで表現できない文字があります。");
            Array.Resize(ref bytes,n); return bytes;
        }
        public static byte[] Fixed(string s, int size)
        {
            var b = Encode(s); var result = new byte[size];
            int n = Math.Min(size,b.Length);
            // Do not split a CP932 lead/trail byte pair at the fixed field boundary.
            for (int i=0;i<n;i++) if (b[i] >= 0x81 && b[i] <= 0x9f || b[i] >= 0xe0 && b[i] <= 0xfc)
                if (++i == n) n--;
            Array.Copy(b,result,n); return result;
        }
    }

}
