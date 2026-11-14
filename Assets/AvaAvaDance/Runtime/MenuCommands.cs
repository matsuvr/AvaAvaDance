using System;
using System.Linq;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        bool ModelBones() => modelMode && actor.bones.Count != 0;
        bool HasMorphs() => modelMode && actor.modelTracks.Any(t => t is MotionTrack<float>);
        bool SceneMode() => !modelMode;

        void BindFileCommands()
        {
            Bind(Command.New, NewProjectCommand);
            Bind(Command.Open, OpenProjectCommand);
            Bind(Command.Save, () => SaveProjectCommand(false));
            Bind(Command.SaveAs, () => SaveProjectCommand(true));
            Bind(Command.ExportAvi, () => ChooseMovie(true));
            Bind(Command.ExportMp4, () => ChooseMovie(false));
            Bind(Command.ExportImage, () => FileAction("画像ファイルに出力", DirectXImage.Extensions, true, ExportImage));
            Bind(Command.LoadPose, () => FileAction("ポーズデータ読込", "vpd", false, LoadVpd), ModelBones);
            Bind(Command.SavePose, () => FileAction("ポーズデータ保存", "vpd", true, SaveVpd), ModelBones);
            Bind(Command.LoadMotion, () => FileAction("モーションデータ読込", "vrma;bvh;fbx;anim;avamotion", false, ImportMotionFile));
            Bind(Command.SaveMotion, () => FileAction("モーションデータ保存", "avamotion", true, SaveMotion));
            Bind(Command.LoadAvaMotion, () => FileAction("AvaAvaDanceモーション読込", "avamotion", false, LoadMotion));
            Bind(Command.SaveAvaMotion, () => FileAction("AvaAvaDanceモーション保存", "avamotion", true, SaveMotion));
            Bind(Command.LoadWave, () => FileAction("WAVファイル読込", "wav", false, p => StartCoroutine(LoadWave(p))), () => !loadingWave);
            Bind(Command.ScrubWave, () => scrubSound = !scrubSound, check: () => scrubSound);
            Bind(Command.MuteWave, () => {
                muteWave = !muteWave;
                if (muteWave) soundtrack.Stop(); else if (playing) PlayAudioAt(frame);
            }, check: () => muteWave);
            Bind(Command.PreviousFolder, () => PackageFileDialog.RememberFolder = !PackageFileDialog.RememberFolder,
                check: () => PackageFileDialog.RememberFolder);
            Bind(Command.Quit, () => Application.Quit());
        }

        void ImportMotionFile(string path)
        {
            switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
            {
                case ".vrma": LoadVrma(path); break;
                case ".bvh": case ".fbx": case ".anim": LoadHumanoidMotion(path); break;
                case ".avamotion": LoadMotion(path); break;
                default: throw new System.IO.InvalidDataException("対応するモーションファイルを選択してください。");
            }
        }

        void BindEditCommands()
        {
            Bind(Command.EditTransform, () => NumericTransform(), () => !modelMode || actor.selectedBone != null);
            Bind(Command.ZeroAngles, () => {
                if (modelMode) { var p = actor.selectedBone.Track.Current; p.Rotation = Quaternion.identity; PreviewBone(p); }
                else { var p = cameraTrack.Current; p.Angles = Vector3.zero; PreviewCameraPose(p); }
            }, () => !modelMode || actor.selectedBone != null);
            Bind(Command.DeleteUnused, DeleteUnusedKeys);
            Bind(Command.SelectCamera, () => SelectTrackKeys(cameraTrack), SceneMode);
            Bind(Command.SelectLight, () => SelectTrackKeys(lightTrack), SceneMode);
            Bind(Command.SelectShadow, () => SelectTrackKeys(shadowTrack), SceneMode);
            Bind(Command.SelectGravity, () => SelectTrackKeys(gravityTrack), SceneMode);
            Bind(Command.SelectBones, () => SelectGroup(false), ModelBones);
            Bind(Command.SelectMorphs, () => SelectGroup(true), HasMorphs);
            Bind(Command.SelectSwitch, () => SelectTrackKeys(actor.modelSwitch), () => modelMode && actor.modelSwitch != null);
            Bind(Command.PasteAt, PasteKeys, () => CanPaste() && keyClipboard.All(k=>k.Track==keyClipboard[0].Track) && selectedTrack is MotionTrack<BonePose>);
            Bind(Command.InsertBone, () => ShiftColumn(false, true));
            Bind(Command.DeleteBoneColumn, () => ShiftColumn(false, false));
            Bind(Command.InsertMorph, () => ShiftColumn(true, true));
            Bind(Command.DeleteMorphColumn, () => ShiftColumn(true, false));
            Bind(Command.AdjustBones, () => AdjustKeys(Command.AdjustBones), ModelBones);
            Bind(Command.CenterBias, () => AdjustKeys(Command.CenterBias), ModelBones);
            Bind(Command.AdjustMorphs, () => AdjustKeys(Command.AdjustMorphs), HasMorphs);
            Bind(Command.AdjustCamera, () => AdjustKeys(Command.AdjustCamera), SceneMode);
        }

        void BindOtherCommands()
        {
            Bind(Command.DeleteLips, () => DeleteMorphGroup(1), HasMorphs);
            Bind(Command.DeleteEyes, () => DeleteMorphGroup(0), HasMorphs);
            Bind(Command.DeleteBrows, () => DeleteMorphGroup(2), HasMorphs);
            Bind(Command.ShiftLips, ShiftLipFrames, () => modelMode && morphGroups[1].Tracks.Count > 0);
            Bind(Command.Blink, RandomBlink, () => modelMode && BlinkTrack() != null);
            Bind(Command.LipSync, () => FileAction("VSQ / VSQXによるリップシンク", "vsq;vsqx", false, ImportLipSync),
                () => modelMode && morphGroups[1].Tracks.Count > 0);
            Bind(Command.ResetMorphs, () => {
                StopPlayback(); foreach (var t in actor.modelTracks.OfType<MotionTrack<float>>()) t.Preview(0); SyncControls();
            }, HasMorphs);
            Bind(Command.RegisterMorphs, () => CommitKeys(actor.modelTracks.OfType<MotionTrack<float>>()
                .Select(t => new KeyChange { Track = t, Frame = frame, After = t.Current })), HasMorphs);

            void Mode(Command id, PhysicsMode mode) => Bind(id, () => {
                physicsMode = mode; InvalidateDynamics(); SetFrame(frame);
            }, check: () => physicsMode == mode);
            Mode(Command.PhysicsOnOff, PhysicsMode.OnOff); Mode(Command.PhysicsAlways, PhysicsMode.Always);
            Mode(Command.PhysicsTrace, PhysicsMode.Trace); Mode(Command.PhysicsOff, PhysicsMode.None);
            Bind(Command.PlaybackPhysics, () => { forceOnPlayback = !forceOnPlayback; InvalidateDynamics(); }, check: () => forceOnPlayback);
            Bind(Command.ShowBodies, () => { showBodies = !showBodies; RedrawOverlay(); }, check: () => showBodies);
            Bind(Command.Gravity, ShowGravity);
            Bind(Command.ResetBodies, ResetBodies);
            Bind(Command.PhysicsFloor, () => { physicalFloor = !physicalFloor; InvalidateDynamics(); }, check: () => physicalFloor);
            Bind(Command.SelectDynamic, () => SelectPhysicsKeys(false), () => modelMode && actor.dynamicLinks.Count > 0);
            Bind(Command.SelectPhysicsOn, () => SelectPhysicsKeys(true), () => modelMode && actor.dynamicLinks.Count > 0);
            Bind(Command.ConvertPhysics, ConvertPhysicsKeys, () => modelMode && EditKeys().Any(k => actor.dynamicLinks.Any(d => d.Bone.Track == k.track)));
            Bind(Command.DynamicBone, () => ToggleDynamicBone(actor.selectedBone), ModelBones, () => actor.dynamicLinks.Any(d => d.Bone == actor.selectedBone));
            Bind(Command.AddIK, () => AddSelectedIK(actor.selectedBone), ModelBones);
            Bind(Command.BulletEngine,()=>SetPhysicsEngine(PhysicsEngine.Bullet),check:()=>physicsEngine==PhysicsEngine.Bullet);
            Bind(Command.PhysBoneEngine,()=>SetPhysicsEngine(PhysicsEngine.PhysBone),check:()=>physicsEngine==PhysicsEngine.PhysBone);
            Bind(Command.AboutPhysics, () => Message("物理エンジンについて",physicsEngine==PhysicsEngine.Bullet
                ? "Bullet "+(BulletWorld.ava_bullet_version()/100f).ToString("0.00")+"　固定60 Hzの剛体・ジョイント演算"
                : "PhysBone互換物理　固定60 Hzの関節シミュレーション"));
            Bind(Command.Language, SwitchLanguage);
            Bind(Command.About, () => Message("バージョン情報", "AvaAvaDance " + Application.version + "\nUnity " + Application.unityVersion));
        }

        void Message(string title, string text)
        {
            ShowDialog(title, out var body); body.Add(new Label(text)); Action(body, "OK", true, CloseDialog);
        }

        void SelectTrackKeys(MotionTrack track)
        {
            SelectTrack(track); SelectKeys(0, MaxFrame, false);
        }

        bool Shortcut(KeyCode key, bool ctrl, bool shift, bool other)
        {
            if (other) return false;
            if (ctrl)
            {
                if (shift && key != KeyCode.Z && key != KeyCode.S) return false;
                switch (key)
                {
                    case KeyCode.N: return Execute(Command.New);
                    case KeyCode.O: return Execute(Command.Open);
                    case KeyCode.S: return Execute(Command.Save);
                    case KeyCode.Z: ApplyHistory(shift); break;
                    case KeyCode.X: case KeyCode.Y: ApplyHistory(true); break;
                    case KeyCode.LeftArrow: JumpKey(false); break;
                    case KeyCode.RightArrow: JumpKey(true); break;
                    case KeyCode.C: if (copyButton.enabledSelf) CopyKey(); break;
                    case KeyCode.V: PasteOriginalKeys(false); break;
                    case KeyCode.W: PasteOriginalKeys(true); break;
                    default: return false;
                }
                return true;
            }
            if (shift && key == KeyCode.V) return Execute(Command.HideModel);
            if (shift && key != KeyCode.Tab && key != KeyCode.Backslash && key != KeyCode.Underscore) return false;
            switch (key)
            {
                case KeyCode.RightBracket: autoCurve.value=!autoCurve.value;break;
                case KeyCode.Tab: case KeyCode.Backslash: case KeyCode.Underscore: if (drawOrder.Count==0) return false; CycleModel(shift); break;
                case KeyCode.UpArrow: case KeyCode.DownArrow:
                    int row = VisibleTracks.IndexOf(selectedTrack) + (key == KeyCode.UpArrow ? -1 : 1);
                    if (VisibleTracks.Count > 0) SelectTrack(VisibleTracks[Mathf.Clamp(row,0,VisibleTracks.Count-1)]); break;
                case KeyCode.G: SetFrame(bookmark.value); break;
                case KeyCode.Escape: if (!fullPreview) return false; return Execute(Command.Fullscreen);
                case KeyCode.LeftArrow: SetFrame(frame - 1); break;
                case KeyCode.RightArrow: SetFrame(frame + 1); break;
                case KeyCode.Delete: DeleteKeys(); break;
                case KeyCode.P: TogglePlayback(); break;
                case KeyCode.Return: case KeyCode.KeypadEnter: RegisterSelection(); break;
                case KeyCode.I: return Execute(Command.InsertBone);
                case KeyCode.K: return Execute(Command.DeleteBoneColumn);
                case KeyCode.U: return Execute(Command.InsertMorph);
                case KeyCode.J: return Execute(Command.DeleteMorphColumn);
                case KeyCode.H: return Execute(Command.RegisterMorphs);
                case KeyCode.F: return Execute(Command.PasteAt);
                case KeyCode.R: return Execute(Command.AdjustBones);
                case KeyCode.D: return Execute(Command.CenterBias);
                case KeyCode.V: return Execute(Command.TransparentModel);
                case KeyCode.B: return Execute(Command.Black);
                case KeyCode.A: case KeyCode.S:
                    if (!ModelBones()) return false;
                    SelectBoneSet(key == KeyCode.S); break;
                case KeyCode.L: if (!modelMode) return false; localAxes = !localAxes; SyncAxes(); break;
                case KeyCode.Z: case KeyCode.X: case KeyCode.C:
                    if (!ModelBones()) return false;
                    boneTool = key == KeyCode.X ? BoneTool.Rotate : key == KeyCode.Z ? BoneTool.Move : BoneTool.Select;
                    SyncBoneControls(); break;
                case KeyCode.Keypad2: case KeyCode.Keypad8: case KeyCode.Keypad4: case KeyCode.Keypad6:
                case KeyCode.Keypad5: case KeyCode.Keypad0: case KeyCode.Keypad1:
                    if (!modelMode) return false;
                    SetViewAngles(key == KeyCode.Keypad5 ? 90 : key == KeyCode.Keypad0 || key == KeyCode.Keypad1 ? -90 : 0,
                        key == KeyCode.Keypad8 ? 180 : key == KeyCode.Keypad4 ? -90 : key == KeyCode.Keypad6 ? 90 : 0);
                    break;
                default: return false;
            }
            return true;
        }
    }
}
