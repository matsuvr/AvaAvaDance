using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;
using Object=UnityEngine.Object;

namespace AvaAvaDance
{
    [Serializable] internal struct AccessoryPose
    {
        public Vector3 Position,Rotation;
        public float Scale,Opacity;
        public bool Hidden;
        public string Parent, Model;
        public static AccessoryPose Initial => new AccessoryPose {Scale=1,Opacity=1};
        public static AccessoryPose Blend(AccessoryPose a,AccessoryPose b,float t) => new AccessoryPose {
            Position=Vector3.Lerp(a.Position,b.Position,t),Rotation=Vector3.Lerp(a.Rotation,b.Rotation,t),
            Scale=Mathf.Lerp(a.Scale,b.Scale,t),Opacity=Mathf.Lerp(a.Opacity,b.Opacity,t),Hidden=a.Hidden,Parent=a.Parent,Model=a.Model
        };
    }

    public sealed partial class DanceApp
    {
        [Serializable] sealed class AccessoryData {
            public string name;
            public string[] textures;
            [NonSerialized] public byte[] mesh;
            [NonSerialized] public Dictionary<string,byte[]> images;
        }
        sealed class Prop {
            public AccessoryData Data;
            public GameObject Object;
            public MotionTrack<AccessoryPose> Track;
            public Material[] Materials;
            public Color[] Colors;
            public string[] Maps;
        }
        readonly List<Prop> props=new List<Prop>();
        DropdownField propList, propParent;
        Toggle propVisible;
        Button propRemove,propRegister;
        FloatField[] propFields=new FloatField[8];
        Prop activeProp;
        readonly List<(Actor owner,BoneBinding bone,string label)> propParents=new List<(Actor,BoneBinding,string)>();
        void RefreshPropParents()
        {
            propParents.Clear();
            foreach(var a in drawOrder)foreach(var b in a.bones)
                propParents.Add((a,b,drawOrder.Count>1?a.label+"："+b.Track.Name:b.Track.Name));
            if(propParent!=null)propParent.choices=new[]{"地面"}.Concat(propParents.Select(b=>b.label)).ToList();
        }
        int screenMode;
        RenderTexture screenImage;
        Vector3 propFloor => new Vector3(0,-1,0);

        void BuildAccessories(VisualElement bottom)
        {
            accessory=Panel(bottom,"accessory","アクセサリ操作");
            propList=Choice(accessory,new[]{"アクセサリ未読込"});
            propList.RegisterValueChangedCallback(e=>{
                var prop=props.Find(p=>p.Data.name==e.newValue); if(prop!=null)SelectTrack(prop.Track);
            });
            var row=Box(accessory,"prop-buttons","row");
            Action(row,"読込",true,()=>FileAction("アクセサリ読込","x;vac",false,LoadAccessory));
            propRemove=Action(row,"削除",false,()=>RemoveProp(activeProp));
            propVisible=new Toggle("表示");accessory.Add(propVisible);
            propVisible.RegisterValueChangedCallback(e=>EditProp(p=>{p.Hidden=!e.newValue;return p;}));
            propParent=Choice(accessory,new[]{"地面"});
            propParent.RegisterValueChangedCallback(e=>EditProp(p=>{var choice=propParent.index<=0?default:propParents[propParent.index-1];p.Parent=choice.bone?.Track.Name;p.Model=choice.owner?.id;return p;}));
            for(int i=0;i<8;i++) {
                if(i==0 || i==3 || i==6)row=Box(accessory,"prop-values-"+i,"row");
                int axis=i;
                var f=new FloatField(new[]{"X","Y","Z","Rx","Ry","Rz","Si","Tr"}[i]) {isDelayed=true,formatString="0.##"};
                propFields[i]=f;row.Add(f);f.RegisterValueChangedCallback(e=>EditProp(p=>{
                    if(axis<3)p.Position[axis]=e.newValue;else if(axis<6)p.Rotation[axis-3]=e.newValue;
                    else if(axis==6)p.Scale=e.newValue;else p.Opacity=e.newValue;
                    return p;
                }));
            }
            propRegister=Action(accessory,"登録",false,()=>RegisterKey(activeProp?.Track));
        }
        void EditProp(Func<AccessoryPose,AccessoryPose> change)
        {
            if(activeProp==null)return;
            var value=change(activeProp.Track.Current);
            if(ValidPose(value)){StopPlayback();activeProp.Track.Preview(value);SelectTrack(activeProp.Track);}
            SyncProp();
        }
        void SyncProp()
        {
            if(propList==null)return;
            var list=props.Select(p=>p.Data.name).ToList();if(list.Count==0)list.Add("アクセサリ未読込");
            propList.choices=list;propList.SetValueWithoutNotify(activeProp?.Data.name??list[0]);propList.SetEnabled(props.Count>1);
            bool ready=activeProp!=null;
            propRemove.SetEnabled(ready);propRegister.SetEnabled(ready);propVisible.SetEnabled(ready);
            var value=ready?activeProp.Track.Current:AccessoryPose.Initial;
            propVisible.SetValueWithoutNotify(!value.Hidden);
            propParent.SetValueWithoutNotify(propParents.Find(b=>b.bone.Track.Name==value.Parent && (string.IsNullOrEmpty(value.Model) || b.owner.id==value.Model)).label??"地面");
            propParent.SetEnabled(ready && propParents.Count>0);
            for(int i=0;i<8;i++) {
                propFields[i].SetEnabled(ready);
                propFields[i].SetValueWithoutNotify(i<3?value.Position[i]:i<6?value.Rotation[i-3]:i==6?value.Scale:value.Opacity);
            }
        }

        void ApplyProp(Prop prop,AccessoryPose pose)
        {
            var obj=prop.Object;obj.SetActive(!pose.Hidden);
            PositionProp(prop,pose);
            for(int i=0;i<prop.Materials.Length;i++) {
                Color color=prop.Colors[i];color.a*=pose.Opacity;var m=prop.Materials[i];m.color=color;
                bool blend=color.a<.999f || prop.Maps[i]?.EndsWith(".tga",StringComparison.OrdinalIgnoreCase)==true;
                m.SetInt("_SrcBlend",blend?5:1);m.SetInt("_DstBlend",blend?10:0);m.SetInt("_ZWrite",blend?0:1);
                if(blend)m.EnableKeyword("_ALPHABLEND_ON");else m.DisableKeyword("_ALPHABLEND_ON");
                m.renderQueue=blend?3000:2000;
            }
        }

        void PositionProp(Prop prop,AccessoryPose pose)
        {
            var owner=string.IsNullOrEmpty(pose.Model)?drawOrder.FirstOrDefault():drawOrder.Find(a=>a.id==pose.Model);
            var parent=pose.Parent==null?null:owner?.bones.Find(b=>b.Track.Name==pose.Parent)?.Joint;
            var offset=pose.Position*SceneUnit;var t=prop.Object.transform;
            t.SetPositionAndRotation(parent==null?propFloor+offset:parent.position+parent.rotation*offset,
                (parent!=null?parent.rotation:Quaternion.identity)*Quaternion.Euler(pose.Rotation));
            t.localScale=Vector3.one*(SceneUnit*pose.Scale);
        }
        void LateUpdate() {foreach(var prop in props)if(prop.Track.Current.Parent!=null)PositionProp(prop,prop.Track.Current);}
        void LoadAccessory(string path)
        {
            if(props.Count>=255)throw new IOException("アクセサリ上限です。");
            string basis=Path.GetFileNameWithoutExtension(path);
            var pose=AccessoryPose.Initial;
            if(path.EndsWith(".vac",StringComparison.OrdinalIgnoreCase)) {
                var lines=MmdText.Decode(ReadMmdFile(path)).Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
                if(lines.Length<5)throw new InvalidDataException("VAC形式が不正です。");
                var directory=Path.GetDirectoryName(Path.GetFullPath(path));
                var target=Path.GetFullPath(Path.Combine(directory,lines[1].Trim()));
                if(!target.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !target.EndsWith(".x",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("VACと同じフォルダー内のXファイルが必要です。");
                float Scalar(string n)=>float.Parse(n.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture);
                Vector3 Triplet(string line) {
                    var n=line.Split(','); if(n.Length!=3)throw new InvalidDataException("VACの座標が不正です。");
                    return new Vector3(Scalar(n[0]),Scalar(n[1]),Scalar(n[2]));
                }
                basis=lines[0].Trim();path=target;pose.Scale=Scalar(lines[2]);pose.Position=Triplet(lines[3]);pose.Rotation=Triplet(lines[4]);
                pose.Model=actor.id;pose.Parent=lines.Length>5 && !lines[5].StartsWith("//")?lines[5].Trim():null;
                if(!ValidPose(pose))throw new InvalidDataException("VACの姿勢が不正です。");
            }
            string name=basis;
            for(int n=2;sceneTracks.Any(t=>t.Name==name);n++)name=basis+" ("+n+")";
            var data=new AccessoryData {name=name,mesh=ReadMmdFile(path),images=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase)};
            AddProp(data,Path.GetDirectoryName(Path.GetFullPath(path)));activeProp.Track.Write(0,pose);activeProp.Track.Seek(frame);
            ChangeMode(false);SelectTrack(activeProp.Track);
        }
        void AddProp(AccessoryData data,string sourceDir=null)
        {
            var mesh=DirectXMesh.Load(data.mesh,out var colors,out var maps);
            var obj=new GameObject(data.name);obj.layer=30;obj.transform.SetParent(previewRig.transform,false);
            var owned=obj.AddComponent<FbxOwnedResources>();owned.Own(mesh);
            try {
                obj.AddComponent<MeshFilter>().sharedMesh=mesh;
                var mats=new Material[colors.Length];
                var cache=new Dictionary<string,Texture2D>(StringComparer.OrdinalIgnoreCase);
                for(int i=0;i<mats.Length;i++) {
                    var m=new Material(Shader.Find("Standard"));owned.Own(m);mats[i]=m;
                    string map=maps[i];
                    if(string.IsNullOrEmpty(map) || IsScreen(map))continue;
                    if(!data.images.TryGetValue(map,out var bytes) && sourceDir!=null && !Path.IsPathRooted(map)) {
                        var full=Path.GetFullPath(Path.Combine(sourceDir,map));
                        if(full.StartsWith(sourceDir.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && File.Exists(full)) {
                            bytes=ReadMmdFile(full);
                            if(data.images.Values.Sum(b=>(long)b.Length)+bytes.Length>BackdropLimit)throw new IOException("アクセサリ画像サイズ上限です。");
                            data.images[map]=bytes;
                        }
                    }
                    if(bytes==null)continue;
                    if(!cache.TryGetValue(map,out var tex)) {
                        tex=AccessoryTexture(map,bytes);cache.Add(map,tex);owned.Own(tex);
                    }
                    m.mainTexture=tex;
                }
                data.textures=data.images.Keys.ToArray();
                obj.AddComponent<MeshRenderer>().sharedMaterials=mats;
                var prop=new Prop {Data=data,Object=obj,Materials=mats,Colors=colors,Maps=maps};
                prop.Track=new MotionTrack<AccessoryPose>(data.name,AccessoryPose.Initial,AccessoryPose.Blend,p=>ApplyProp(prop,p));
                props.Add(prop);sceneTracks.Add(prop.Track);activeProp=prop;prop.Track.Seek(frame);
                UpdateScreenMaterials();SyncProp(); RefreshTimelineLabels();
            } catch {Destroy(obj);throw;}
        }

        static Texture2D AccessoryTexture(string name,byte[] bytes)
        {
            string ext=Path.GetExtension(name).ToLowerInvariant();
            if(ext!=".png" && ext!=".jpg" && ext!=".jpeg")bytes=DirectXImage.Convert(bytes,".png");
            var image=new Texture2D(2,2);
            if(!image.LoadImage(bytes) || image.width>8192 || image.height>8192){Destroy(image);throw new InvalidDataException("アクセサリ画像を読み込めません。");}return image;
        }

        void RemoveProp(Prop prop)
        {
            if(prop==null)return;
            DiscardHistory(undo,t=>t==prop.Track);DiscardHistory(redo,t=>t==prop.Track);
            keyClipboard.RemoveAll(k=>k.Track==prop.Track);selectedKeys.RemoveWhere(k=>k.track==prop.Track);
            props.Remove(prop);sceneTracks.Remove(prop.Track);prop.Object.SetActive(false);Destroy(prop.Object);
            activeProp=props.FirstOrDefault();if(sceneSelection==prop.Track)sceneSelection=cameraTrack;
            if(selectedTrack==prop.Track)SelectTrack(sceneSelection);
            SyncProp();RefreshKeyButtons();RefreshTimelineLabels();
        }
        void ShowAccessoryEdit()
        {
            ShowDialog("アクセサリ編集",out var body);
            var list=new DropdownField(props.Select(p=>p.Data.name).ToList(),Math.Max(0,props.IndexOf(activeProp)));body.Add(list);
            void Move(int step) {
                int i=list.index,j=Mathf.Clamp(i+step,0,props.Count-1);if(i==j)return;
                var prop=props[i];props.RemoveAt(i);props.Insert(j,prop);list.choices=props.Select(p=>p.Data.name).ToList();list.SetValueWithoutNotify(prop.Data.name);
                sceneTracks.RemoveAll(t=>t is MotionTrack<AccessoryPose>);
                for(int k=0;k<props.Count;k++) {sceneTracks.Add(props[k].Track);props[k].Object.GetComponent<Renderer>().sortingOrder=k;}
                SyncProp();RefreshTimelineLabels();
            }
            Action(body,"上へ",true,()=>Move(-1));Action(body,"下へ",true,()=>Move(1));Action(body,"閉じる",true,CloseDialog);
        }
        static bool IsScreen(string map)=>string.Equals(Path.GetFileName(map),"screen.bmp",StringComparison.OrdinalIgnoreCase);
        void UpdateScreenMaterials()
        {
            Texture image=screenMode==3?videoPlayer?.texture:screenMode!=0?screenImage:null;
            foreach(var prop in props)for(int i=0;i<prop.Materials.Length;i++)
                if(IsScreen(prop.Maps[i]))prop.Materials[i].mainTexture=image;
        }
        void UpdatePropScreen()
        {
            if(screenMode==0 || !props.Any(p=>p.Maps.Any(IsScreen)))return;
            if(screenMode!=3 && previewTexture!=null) {
                int h=previewTexture.height,w=screenMode==2?h*4/3:previewTexture.width;
                if(screenImage==null || screenImage.width!=w || screenImage.height!=h) {
                    if(screenImage!=null){screenImage.Release();Destroy(screenImage);}
                    screenImage=new RenderTexture(w,h,0);
                }
                Graphics.Blit(previewTexture,screenImage);
            }
            UpdateScreenMaterials();
        }
        void BindPropCommands()
        {
            Bind(Command.AccessoryEdit,ShowAccessoryEdit,()=>props.Count>0);
            Bind(Command.SelectAccessory,()=>{SelectTrack(activeProp.Track);SelectKeys(0,MaxFrame,false);},()=>!modelMode && activeProp!=null);
            void Mode(Command id,int index)=>Bind(id,()=>{screenMode=index; UpdateScreenMaterials();},check:()=>screenMode==index);
            Mode(Command.ScreenOff,0);Mode(Command.ScreenFull,1);Mode(Command.ScreenFourThree,2);Mode(Command.ScreenVideo,3);
        }
    }
}
