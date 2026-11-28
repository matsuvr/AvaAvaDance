using System;
using System.IO;
using System.Linq;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        // Real Unity/Windows checks, invoked by -avaMenuSmoke. These intentionally
        // use the production render path and history, not substitutes for Unity.
        void CheckEditingAndOutput(string directory)
        {
            void Check(bool pass,string message) {
                if(!pass)throw new InvalidOperationException("Editing/output: "+message);
            }
            void Click(string text) => ClickSmokeButton(modal.Query<Button>().ToList().Single(b=>b.text==text));
            var prior=(ribbonProfile,modelMode,frame,selectedTrack,movie);
            var oldUndo=undo.ToArray();var oldRedo=redo.ToArray();
            var tracks=sceneTracks.Concat(drawOrder.SelectMany(a=>a.modelTracks)).ToArray();
            var drafts=tracks.Select(t=>t.CurrentValue).ToArray();
            string preference=WorkspacePreferences.GetString(OutputPresetPreference);
            var chooser=PackageFileDialog.AutomationChooser;
            try {
                ChangeMode(true);
                foreach(bool moving in new[]{false,true}) {
                    ApplyProfile(moving);
                    string extensions=null;
                    PackageFileDialog.AutomationChooser=(_,ext,__)=>{extensions=ext;return null;};
                    Check(Execute(Command.LoadPose) && extensions=="avapose","public pose import filter");
                    Check(Execute(Command.SavePose) && extensions=="avapose","public pose export filter");
                    Check(Execute(Command.ScaleKeyframes) && modal?.Q("retime-factor")!=null,"time scale command in both profiles");
                    CloseDialog();
                }
                PackageFileDialog.AutomationChooser=chooser;

                string posePath=Path.Combine(directory,"native-pose.avapose");
                var bone=actor.bones.First().Track;var original=bone.Current;
                SavePoseFile(posePath);
                var changed=original;changed.Position+=new Vector3(.1f,.2f,.3f);bone.Preview(changed);
                int history=undo.Count;int keys=bone.Frames.Count;
                LoadPoseFile(posePath);
                Check(bone.Current.Position==original.Position && undo.Count==history+1 && bone.Frames.Count==keys,"pose applies as one draft operation");
                ApplyHistory(false);Check(bone.Current.Position==changed.Position,"pose Undo restores draft");
                ApplyHistory(true);Check(bone.Current.Position==original.Position,"pose Redo");

                WorkspacePreferences.DeleteKey(OutputPresetPreference);
                Check(ReadOutputPresets().All(p=>p.Valid),"built-in output presets");
                movie=new MovieSettings{width=640,height=360,fps=30,first="12",last="48",audio=false};
                ShowOutputSize();
                modal.Q<DropdownField>("output-preset").index=4;
                Check(modal.Q<IntegerField>("output-width").value==1920 && modal.Q<IntegerField>("output-fps").value==60,"preset selects resolution and fps");
                Click("キャンセル");Check(movie.width==640 && movie.fps==30,"cancel keeps output settings");
                ShowOutputSize();modal.Q<DropdownField>("output-preset").index=4;Click("OK");
                Check(movie.width==1920 && movie.height==1080 && movie.fps==60 && movie.first=="12","accepted preset retains export range");

                EditOutputPresets(movie,()=>{});Click("追加");
                var added=modal.Q("output-presets").Children().Last();
                added.Q<TextField>().value="縦長テスト";
                var numbers=added.Query<IntegerField>().ToList();numbers[0].value=241;numbers[1].value=321;numbers[2].value=24;
                Click("OK");
                var saved=ReadOutputPresets().Last();
                Check(saved.name=="縦長テスト" && saved.width==241 && saved.height==321 && saved.fps==24,"custom image preset accepts odd dimensions and persists");
                var working=CopyOutputSettings(movie);
                ShowMovieSettings(Path.Combine(directory,"preset.mp4"),working);
                modal.Q<DropdownField>("output-preset").index=ReadOutputPresets().Length;
                Click("プリセット編集…");Click("キャンセル");
                Check(modal.Q<IntegerField>("output-width").value==241 && working.first=="12" && working.last=="48" && !working.audio,"preset editor preserves pending output form");
                Click("キャンセル");Check(movie.width==1920,"cancel pending output form");

                MenuRoot.panel.Pick(Vector2.zero);
                var previousTarget=previewCamera.targetTexture;float aspect=previewCamera.aspect;
                bool grid=floor.activeSelf;var active=RenderTexture.active;
                void CheckRestored()
                {
                    Check(previewCamera.targetTexture == previousTarget, "camera target restored");
                    Check(previewCamera.aspect == aspect, "camera aspect restored");
                    Check(floor.activeSelf == grid, "grid visibility restored");
                    Check(RenderTexture.active == active, "active render texture restored");
                    Check(!exporting, "export guard released");
                }
                foreach(var size in new[]{(321,181),(241,321)}) {
                    string path=Path.Combine(directory,"sized-"+size.Item1+"x"+size.Item2+".png");
                    var settings=CopyOutputSettings(movie);settings.width=size.Item1;settings.height=size.Item2;
                    ShowImageSettings(path,settings);Click("OK");Check(modal==null,"image settings accepted");
                    var image=new Texture2D(2,2);
                    try { Check(image.LoadImage(File.ReadAllBytes(path)) && image.width==size.Item1 && image.height==size.Item2,"actual PNG output dimensions"); }
                    finally {Destroy(image);}
                    var persisted=JsonUtility.FromJson<MovieSettings>(JsonUtility.ToJson(movie));
                    Check(persisted.Valid && persisted.ImageWidth==size.Item1 && persisted.ImageHeight==size.Item2 &&
                        persisted.width==1920 && persisted.height==1080,"image size persists without changing movie dimensions");
                    CheckRestored();
                }
                string missing=Path.Combine(directory,"absent-"+Guid.NewGuid().ToString("N"),"failure.png");
                try {WriteOutputImage(missing,160,120);throw new Exception("Missing output directory accepted");}
                catch(DirectoryNotFoundException) { }
                CheckRestored();
                Debug.Log("AVA_EDITING_OUTPUT PASS public formats, pose history, preset editing, PNG dimensions and render recovery");
            } finally {
                if(modal!=null)CloseDialog();PackageFileDialog.AutomationChooser=chooser;
                if(string.IsNullOrEmpty(preference))WorkspacePreferences.DeleteKey(OutputPresetPreference);
                else WorkspacePreferences.SetString(OutputPresetPreference,preference);
                movie=prior.movie;ApplyProfile(prior.ribbonProfile);ChangeMode(prior.modelMode);SetFrame(prior.frame);SelectTrack(prior.selectedTrack);
                for(int i=0;i<tracks.Length;i++)tracks[i].Restore(drafts[i]);
                undo.Clear();foreach(var item in oldUndo.Reverse())undo.Push(item);
                redo.Clear();foreach(var item in oldRedo.Reverse())redo.Push(item);
                RefreshRigPreview();RestoreSolvedPose();ApplyView();SyncControls();RefreshKeyButtons();
            }
        }
    }
}
