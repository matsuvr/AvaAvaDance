namespace Ava.Extensions
{
    public sealed class BoneLayerTrack
    {
        public string Id;
        public long Revision;
        public bool IsHumanoid;
        public bool IsSpringBone;
    }

    public sealed class BoneLayerSnapshot
    {
        public long Document;
        public string Owner;
        public BoneLayerTrack[] Tracks;
    }

    public sealed class BoneLayerAssignment
    {
        public string Track, Group, Name;
        public long Revision;
    }

    // Available in SDK 1.1. Moves bone transforms into grouped animation layers
    // on the current model in the ribbon layout. SDK 1.2 adds spring classification and regrouping.
    public interface IBoneLayerEditor
    {
        bool CanOrganizeBoneLayers { get; }
        BoneLayerSnapshot ReadBoneLayerTracks();
        int OrganizeBoneLayers(long document, string owner, BoneLayerAssignment[] assignments);
    }
}
