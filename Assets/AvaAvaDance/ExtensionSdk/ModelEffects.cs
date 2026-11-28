using System;
using System.Collections.Generic;

namespace Ava.Extensions
{
    public interface IModelEffectEditor
    {
        IDisposable AddModelEffect(IModelEffectFormat format);
        void PickModelEffect(IModelEffectFormat format);
        bool CanImportEffect { get; }
    }

    public interface IModelEffectFormat
    {
        string[] Extensions { get; }
        // The host embeds the controller, source and referenced files together as a model.
        byte[] ControllerDescription(string sourcePath);
        string CompanionModel(string sourcePath);
        string[] Dependencies(byte[] source);
        IModelEffectInstance Open(byte[] source, IReadOnlyDictionary<string, byte[]> resources);
    }

    public interface IModelEffectInstance : IDisposable
    {
        // Queue a snapshot for Unity's render thread. Zero skips this draw.
        int Prepare(ModelEffectFrame frame, out IntPtr renderEvent);
    }

    public sealed class ModelEffectFrame
    {
        public float Timeline, PreviewDelta;
        public bool Exporting;
        public IntPtr TargetTexture;
        // Column-major matrices. Bone transforms and view use the editor's motion units.
        public float[] View, Projection;
        public Func<string, float[]> Bone;
        public Func<string, float> Morph;
    }
}
