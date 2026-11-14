using System;

namespace Ava.Extensions
{
    public enum OwnerKind { Model, Accessory }
    public sealed class SceneObject
    {
        public OwnerKind Kind;
        public string Id,Name,Source;
        public bool Visible,Selected;
        public string[] Tracks,Materials;
    }
    public sealed class SceneSnapshot
    {
        public long Document,Assets;
        public SceneObject[] Objects;
        public string[] DrawOrder,CalculationOrder;
        public string ActiveTrack;
    }
    public interface ISceneEditor
    {
        SceneSnapshot ReadScene();
        RigProfile ReadRig(string owner);
        void Select(string owner,string track=null);
        void SetOrder(long document,string[] owners,bool physics);
    }
}
