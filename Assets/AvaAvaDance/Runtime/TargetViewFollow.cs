using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        Actor viewFollowActor;
        Transform viewFollowTarget;
        Vector3 viewFollowOrigin;

        void FollowViewTarget(bool bone)
        {
            if (actor.avatar == null) return;
            Transform target = bone ? actor.selectedBone?.Joint : actor.logicalPelvis?.Joint ?? actor.avatar.transform;
            if (target == null) return;
            // Follow a fixed target selected when enabled, not whichever model a
            // later editing operation temporarily makes active.
            viewFollowActor = actor; viewFollowTarget = target; viewFollowOrigin = target.position;
            ApplyView();
        }

        void StopViewTargetFollow()
        {
            viewFollowActor = null; viewFollowTarget = null; ApplyView();
        }

        Vector3 ViewTargetFollowOffset()
        {
            // This is a viewport aid, never a hidden change to output camera keys.
            if (exporting || viewFollowTarget == null || viewFollowActor == null ||
                viewFollowActor.avatar == null || !drawOrder.Contains(viewFollowActor)) return Vector3.zero;
            return viewFollowTarget.position - viewFollowOrigin;
        }

        void UpdateTargetViewFollow()
        {
            if (viewFollowActor == null) return;
            if (viewFollowTarget == null || viewFollowActor.avatar == null || !drawOrder.Contains(viewFollowActor))
            { StopViewTargetFollow(); return; }
            ApplyView();
        }

        void ShowViewTargetFollow()
        {
            ShowDialog("視点の連続追従", out var body);
            body.Add(new Label("現在の構図を保ち、選んだ対象の移動分だけ視点を動かします。動画出力のカメラキーは変更しません。"));
            Action(body, "現在モデルを追従", true, () => { FollowViewTarget(false); CloseDialog(); }).SetEnabled(actor.avatar != null);
            Action(body, "選択ボーンを追従", true, () => { FollowViewTarget(true); CloseDialog(); }).SetEnabled(actor.avatar != null && actor.selectedBone != null);
            Action(body, "追従解除", true, () => { StopViewTargetFollow(); CloseDialog(); });
            Action(body, "閉じる", true, CloseDialog);
        }
    }
}
