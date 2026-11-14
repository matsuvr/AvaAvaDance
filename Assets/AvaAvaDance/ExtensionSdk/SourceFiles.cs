using System;

namespace Ava.Extensions
{
    // Paths identify files in the source package or next to the imported asset.
    public sealed class SourceFiles
    {
        public string Owner, Name, Asset, Folder;
        public Func<string,bool> Exists;
        public Func<string,byte[]> Read;
    }
    public interface IAssetCatalog
    {
        long SourceRevision {get;}
        SourceFiles[] Sources();
    }
}
