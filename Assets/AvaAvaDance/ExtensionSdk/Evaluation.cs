using System;

namespace Ava.Extensions
{
    public enum EvaluationStage
    {
        BeforeTrackSample, AfterTrackSampleBeforeIK, AfterIKBeforePhysics,
        AfterPhysics, AfterSceneAndAccessories, BeforeRenderSnapshot
    }

    public readonly struct EvaluationSample
    {
        public readonly EvaluationStage Stage;
        public readonly float Frame;
        public readonly bool PhysicsStep;
        public readonly string Actor;
        public EvaluationSample(EvaluationStage stage,float frame,bool physicsStep,string actor)
        {
            Stage=stage;Frame=frame;PhysicsStep=physicsStep;Actor=actor;
        }
    }

    public interface IEvaluationHost
    {
        IDisposable ObserveEvaluation(string id, EvaluationStage stage, int order, string[] after, Action<EvaluationSample> callback);
    }
}
