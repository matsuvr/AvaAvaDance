using System;
using System.IO;

namespace Ava.Extensions
{
    public static class AtomicFile
    {
        public static void Write(string path,Action<string> write)
        {
            path=Path.GetFullPath(path);
            string temp=Path.Combine(Path.GetDirectoryName(path),Guid.NewGuid()+".tmp");
            try {write(temp); if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);}
            finally {if(File.Exists(temp)) File.Delete(temp);}
        }
    }
}
