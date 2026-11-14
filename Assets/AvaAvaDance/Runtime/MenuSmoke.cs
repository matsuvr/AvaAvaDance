using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        static byte[] VsqFixture()
        {
            var text=MmdText.Encode("DM:0000:[Master]\nPreMeasure=0\n[EventList]\n0=ID#0001\n[ID#0001]\nLength=480\nLyricHandle=h#0001\n[h#0001]\nL0=\"あ\",\"a\"\n");
            using var s=new MemoryStream();using var w=new BinaryWriter(s);
            w.Write(new byte[]{77,84,104,100,0,0,0,6,0,0,0,1,1,224,77,84,114,107});
            byte[] len=text.Length<128?new[]{(byte)text.Length}:new[]{(byte)(128+(text.Length>>7)),(byte)(text.Length&127)};
            int size=7+3+len.Length+text.Length+4;
            w.Write(new byte[]{0,0,(byte)(size>>8),(byte)size,0,255,81,3,7,161,32,0,255,1});
            w.Write(len);w.Write(text);w.Write(new byte[]{0,255,47,0});return s.ToArray();
        }
        void CheckMenuCompatibility(string dir)
        {
            void Check(bool ok,string what) {if(!ok)throw new InvalidOperationException("MMD: "+what);}
            void Key(KeyCode key,EventModifiers modifiers=0,VisualElement target=null) {
                var raw=new Event {type=EventType.KeyDown,keyCode=key,modifiers=modifiers};
                var destination=target??menuLayer??MenuRoot.Q("workspace"); destination.Focus();
                using var e=KeyDownEvent.GetPooled(raw); e.target=destination; destination.SendEvent(e);
            }
            bool lang=englishMenus; if(lang)SwitchLanguage();
            Check(MmdMenu.Bar.Length==7 && menuButtons.Count==7,"menu count");
            foreach(var group in MmdMenu.Bar) {
                Key((KeyCode)group.Access(false),EventModifiers.Alt);
                Check(menuGroup==group,"Alt+"+group.Access(false));Key(KeyCode.Escape);
            }
            Key(KeyCode.V,EventModifiers.Alt); Key(KeyCode.G); Check(!gridToggle.value && menuLayer==null,"View/G");
            Key(KeyCode.V,EventModifiers.Alt); Key(KeyCode.G); Check(gridToggle.value,"View/G repeat");
            Key(KeyCode.H,EventModifiers.Alt); Key(KeyCode.M);
            Check(submenu?.Id==Command.Enhance && subList.Q<Button>("ModelNames")!=null,"nested access");
            Key(KeyCode.Escape); Check(submenu==null && menuGroup?.Id==Command.Help,"close submenu");Key(KeyCode.Escape);
            Key(KeyCode.B,EventModifiers.Alt); Key(KeyCode.D); Check(viewOptions.black,"background mnemonic");
            Key(KeyCode.B);Check(!viewOptions.black,"B");
            if(Application.isBatchMode) {
                Execute(Command.SeparateWindow);Check(detached.Active,"preview window opens");detached.Draw(previewTexture);
                Execute(Command.Topmost);Check(previewTop,"topmost");Execute(Command.Topmost);
                Execute(Command.SeparateWindow);Check(!detached.Active,"preview closes");
            }
            int f=frame;Key(KeyCode.RightArrow,0,transformFields[0]);Check(frame==f,"field focus");
            Key(KeyCode.X);Check(boneTool==BoneTool.Rotate,"X");Key(KeyCode.Z);Check(boneTool==BoneTool.Move,"Z");
            Key(KeyCode.C);Check(boneTool==BoneTool.Select,"C");
            Key(KeyCode.Tab);Check(!modelMode,"Tab");Key(KeyCode.Tab,EventModifiers.Shift);Check(modelMode,"Shift+Tab");
            SelectTrack(actor.bones[0].Track);SetFrame(80);Key(KeyCode.Return);
            SetFrame(76);Key(KeyCode.I);Check(actor.bones[0].Track.KeyAt(81)!=null,"I shifts bones");
            Key(KeyCode.H);Check(actor.bones[0].Track.KeyAt(81)!=null && actor.modelTracks.OfType<MotionTrack<float>>().All(t=>t.KeyAt(76)!=null),"H registers morphs only");
            Key(KeyCode.Z,EventModifiers.Control);Key(KeyCode.X,EventModifiers.Control);
            Check(actor.modelTracks.OfType<MotionTrack<float>>().All(t=>t.KeyAt(76)!=null),"Ctrl+X redo");
            SelectTrack(actor.bones[0].Track);SetFrame(76);Key(KeyCode.RightArrow,EventModifiers.Control);Check(frame==81,"Ctrl+right");
            string vpd=Path.Combine(dir,"pose.vpd"),vmd=Path.Combine(dir,"bones.vmd");
            // Exercise a VSQ text track with note lyrics, plus tempo meta data.
            var lips=morphGroups[1].Tracks;
            var testLip=new MotionTrack<float>("表情：あ",0,Mathf.Lerp,_=>{});lips.Add(testLip);actor.modelTracks.Add(testLip);
            var vsq=Path.Combine(dir,"lip.vsq");File.WriteAllBytes(vsq,VsqFixture());SetFrame(0);ImportSongLipSync(vsq);
            Check(Equals(testLip.KeyAt(0),1f) && Equals(testLip.KeyAt(15),0f),"VSQ lyric timing");
            lips.Remove(testLip);actor.modelTracks.Remove(testLip);DiscardHistory(undo,t=>t==testLip);DiscardHistory(redo,t=>t==testLip);
            var pose=actor.bones[0].Track.Current;pose.Rotation=Quaternion.Euler(12,15,18);pose.Position=new Vector3(.1f,.2f,.3f);pose.PhysicsOff=true;
            actor.bones[0].Track.Preview(pose);SaveVpd(vpd);actor.bones[0].Track.Preview(BonePose.Rest);LoadVpd(vpd);
            Check(Quaternion.Angle(actor.bones[0].Track.Current.Rotation,pose.Rotation)<.1f && Vector3.Distance(actor.bones[0].Track.Current.Position,pose.Position)<.0001f,"VPD");
            actor.bones[0].Track.Preview(pose);RegisterKey(actor.bones[0].Track);SaveVmd(vmd);actor.bones[0].Track.Write(frame,BonePose.Rest);LoadVmd(vmd);
            var actual=(BonePose)actor.bones[0].Track.KeyAt(frame);
            Check(actual.PhysicsOff && Quaternion.Angle(actual.Rotation,pose.Rotation)<.1f && Vector3.Distance(actual.Position,pose.Position)<.0001f,"bone VMD");
            string camera=Path.Combine(dir,"camera.vmd");ChangeMode(false);SetFrame(80);var cp=CameraPose.Initial;
            cp.Center=new Vector3(1,2,3);cp.Angles=new Vector3(9,-22,39);cp.Distance=26;cp.Perspective=false;
            cameraTrack.Preview(cp);RegisterKey(cameraTrack);SaveVmd(camera);cameraTrack.Write(frame,null);LoadVmd(camera);
            var ck=(CameraPose)cameraTrack.KeyAt(frame);
            Check(Vector3.Distance(ck.Angles,cp.Angles)<.001f && ck.Distance==26 && !ck.Perspective,"camera VMD");
            byte[] bytes=File.ReadAllBytes(camera);File.WriteAllBytes(camera+".bad",bytes.Take(bytes.Length-1).ToArray());
            int history=undo.Count;try {LoadVmd(camera+".bad");throw new Exception("Accepted truncated VMD");}
            catch(EndOfStreamException){} catch(InvalidDataException){}
            Check(undo.Count==history,"partial VMD edited history");
            var tex=new Texture2D(2,2);tex.SetPixels(Enumerable.Repeat(Color.cyan,4).ToArray());tex.Apply();
            var image=tex.EncodeToPNG();Destroy(tex);
            string bitmap=Path.Combine(dir,"frame.bmp");WritePreview(bitmap);
            byte[] bmp=File.ReadAllBytes(bitmap);Check(bmp[0]=='B' && bmp[1]=='M',"BMP image export");
            SetBackdrop("frame.bmp",bmp,false);Check(backdropImage!=null && backdropImage.width==previewTexture.width,"BMP panel import");
            SetBackdrop("test.png",image,false);viewOptions.showImage=true;UpdateBackdrop();
            string fixture=Path.Combine(dir,"prop.x");
            File.WriteAllText(fixture, "xof 0303txt 0032\nMesh { 3; -1.0;0.0;0.0;,1.0;0.0;0.0;,0.0;2.0;0.0;; 1; 3;0,1,2;; }");
            LoadAccessory(fixture);
            var ap=AccessoryPose.Initial;ap.Position=new Vector3(1,2,3);activeProp.Track.Preview(ap);RegisterKey(activeProp.Track);
            var original=actor;var source=actor.modelAssets.First(a=>a.Guid==actor.modelGuid);var payload=actor.modelAssets.ToList();
            ImportModel(source,payload,payload.Find(a=>a.Guid==original.prefabGuid));
            Check(drawOrder.Count==2 && actor.label!=original.label,"second model");
            var second=actor;SetFrame(90);RegisterKey(actor.bones[0].Track);CycleModel(true);
            Check(actor==original && modelMode && actor.bones[0].Track.KeyAt(90)==null,"independent actors");
            calculationOrder.Reverse();drawOrder.Reverse();ApplyDrawOrder();
            string project=Path.Combine(dir,"menu.avadance");SaveProject(project);NewProject();OpenProject(project);
            Check(viewOptions.showImage && imageBytes.SequenceEqual(image),"embedded project background");
            Check(props.Count==1 && props[0].Track.Current.Position==ap.Position,"X import and project restore");
            Check(drawOrder.Count==2 && drawOrder[0].id==second.id && calculationOrder[0].id==second.id && actor.id==original.id && drawOrder[0].bones[0].Track.KeyAt(90)!=null,"persisted model order and motion");
            if(lang)SwitchLanguage();ChangeMode(true);
            var standard=MmdMenu.Bar.SelectMany(g=>g.Children).SelectMany(e=>e.Children.Length==0?new[]{e}:e.Children)
                .Where(e=>e.Id!=Command.Separator && e.Id<Command.ExportMp4).ToArray();
            Check(standard.Length==96 && standard.All(e=>commands.ContainsKey(e.Id)),"standard command coverage");
            Debug.Log("AVA_MMD_MENU standard="+standard.Length+" bound="+standard.Count(e=>commands.ContainsKey(e.Id)));
        }
    }
}
