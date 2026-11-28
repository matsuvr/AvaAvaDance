using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        BackdropPlacement CurrentBackdropPlacement(bool video) =>
            BackdropPlacement.Normalized(video ? viewOptions.videoPlacement : viewOptions.imagePlacement);

        void ShowBackdropPlacement()
        {
            ShowDialog("背景画像・動画の位置と拡大率", out var body);
            var video = new Toggle("背景動画（OFF: 背景画像）") { value = viewOptions.showVideo && videoPlayer != null };
            body.Add(video);
            var x = new FloatField("横移動（画面幅の%）"); body.Add(x);
            var y = new FloatField("縦移動（画面高さの%）"); body.Add(y);
            var scale = new FloatField("拡大率（%）"); body.Add(scale);
            void Refresh() {
                var value = CurrentBackdropPlacement(video.value);
                x.SetValueWithoutNotify(value.x * 100); y.SetValueWithoutNotify(value.y * 100);
                scale.SetValueWithoutNotify(value.scale * 100);
            }
            video.RegisterValueChangedCallback(_ => Refresh()); Refresh();
            body.Add(new Label("背景だけを変更します。カメラ・モデル・キーには影響しません。画像と動画は別々に保存します。"));
            Action(body, "初期値", true, () => { x.value = 0; y.value = 0; scale.value = 100; });
            Action(body, "適用", true, () => {
                bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
                if (!Finite(x.value) || !Finite(y.value) || !Finite(scale.value) ||
                    x.value < -1000 || x.value > 1000 || y.value < -1000 || y.value > 1000 || scale.value < 1 || scale.value > 10000) {
                    status.text = "移動は±1000%、拡大率は1〜10000%の有限値で指定してください。"; return;
                }
                SetBackdropPlacement(video.value, new BackdropPlacement { x = x.value / 100, y = y.value / 100, scale = scale.value / 100 });
                CloseDialog();
            });
            Action(body, "キャンセル", true, CloseDialog);
        }

        void SetBackdropPlacement(bool video, BackdropPlacement value)
        {
            var before = CurrentBackdropPlacement(video);
            var after = BackdropPlacement.Normalized(value);
            if (before.x == after.x && before.y == after.y && before.scale == after.scale) return;
            void Present(BackdropPlacement next) {
                var copy = BackdropPlacement.Normalized(next);
                if (video) viewOptions.videoPlacement = copy; else viewOptions.imagePlacement = copy;
                UpdateBackdrop();
            }
            Present(after);
            PushOperation(cameraTrack, (_, forward) => Present(forward ? after : before));
        }
    }
}
