using System;
using System.Collections.Generic;
using System.Linq;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class ModelName {public string name,english;}
        [Serializable] sealed class DynamicSetting {public string name;public float pull,spring,stiffness,gravity,radius;}
        [Serializable] sealed class ToonSetting {public string material,file,contentHash;[NonSerialized] public byte[] data;}
        [Serializable] sealed class ModelOverrides
        {
            public string english;
            public List<ToonSetting> toons=new List<ToonSetting>();
            public List<ModelName> names=new List<ModelName>();
            public List<BulletSetting> bullet=new List<BulletSetting>();
            public List<DynamicSetting> physics=new List<DynamicSetting>();
        }


        void BindModelCommands()
        {
            Bind(Command.DrawOrder,()=>ModelOrder(true),()=>drawOrder.Count>0);
            Bind(Command.CalculationOrder,()=>ModelOrder(false),()=>drawOrder.Count>0);
            Bind(Command.ModelNames,EditModelNames,()=>modelMode);
            Bind(Command.ReloadTextures,ReloadTextures,()=>actor.avatar!=null && actor.vrm==null);
            Bind(Command.ToonTexture,EditToonTexture,()=>modelMode && actor.shadowCasters.Length>0);
            Bind(Command.EditPhysics,EditModelPhysics,ModelBones);
        }


        Texture2D ToonImage(ToonSetting setting)
        {
            if(!actor.toonImages.TryGetValue(setting,out var image)) {
                image=AccessoryTexture(setting.file,setting.data);image.wrapMode=TextureWrapMode.Clamp;
                actor.toonImages.Add(setting,image);
            }
            return image;
        }
        void ClearToons() {foreach(var image in actor.toonImages.Values)Destroy(image);actor.toonImages.Clear();}
        void EditToonTexture()
        {
            ShowDialog("ﾄｩｰﾝﾃｸｽﾁｬ変更",out var body);
            var materials=actor.shadowCasters.SelectMany(r=>actor.originals.TryGetValue(r,out var mats)?mats:r.sharedMaterials)
                .Where(m=>m!=null).Select(m=>m.name).Distinct().ToList();
            if(materials.Count==0){Action(body,"閉じる",true,CloseDialog);return;}
            var field=new DropdownField("材質",materials,0);body.Add(field);
            void Clear() {
                var prior=actor.modelOverrides.toons.Find(t=>t.material==field.value);
                if(prior==null)return;actor.modelOverrides.toons.Remove(prior);
                if(actor.toonImages.TryGetValue(prior,out var image)){actor.toonImages.Remove(prior);Destroy(image);}
            }
            Action(body,"画像読込",true,()=>FileAction("トゥーンテクスチャ","bmp;png;jpg;tga",false,path=>{
                var setting=new ToonSetting {material=field.value,file=System.IO.Path.GetFileName(path),data=ReadMmdFile(path)};
                using(var sha=System.Security.Cryptography.SHA256.Create())setting.contentHash=Convert.ToBase64String(sha.ComputeHash(setting.data));
                ToonImage(setting);Clear();actor.modelOverrides.toons.Add(setting);ApplyModelDisplay();
            }));
            Action(body,"元に戻す",true,()=>{Clear();ApplyModelDisplay();});
            Action(body,"閉じる",true,CloseDialog);
        }
        string EnglishName(string name)=>actor.modelOverrides.names.Find(n=>n.name==name)?.english??name;
        string DisplayName(MotionTrack track)=>englishMenus && actor.modelTracks.Contains(track)?EnglishName(track.Name):track.Name;
        void EditModelNames()
        {
            ShowDialog("英語名編集",out var body);
            var modelName=new TextField("モデル英語名") {value=actor.modelOverrides.english??actor.avatar.name};body.Add(modelName);
            var list=new DropdownField("ボーン・表情",actor.modelTracks.Select(t=>t.Name).Distinct().ToList(),0);body.Add(list);
            var field=new TextField("英語名"){value=EnglishName(list.value)};body.Add(field);
            var names=new Dictionary<string,string>();
            list.RegisterValueChangedCallback(e=>field.SetValueWithoutNotify(names.TryGetValue(e.newValue,out var name)?name:EnglishName(e.newValue)));
            field.RegisterValueChangedCallback(e=>names[list.value]=e.newValue);
            Action(body,"OK",true,()=>{
                actor.modelOverrides.english=modelName.value;
                foreach(var kv in names) {
                    actor.modelOverrides.names.RemoveAll(n=>n.name==kv.Key);
                    if(!string.IsNullOrWhiteSpace(kv.Value))actor.modelOverrides.names.Add(new ModelName {name=kv.Key,english=kv.Value});
                }
                RefreshTimelineLabels(); CloseDialog();
            });
            Action(body,"キャンセル",true,CloseDialog);
        }
        void ReloadTextures()
        {
            RestoreModelDisplay();var resources=new List<UnityEngine.Object>();
            try {
                var model=actor.modelAssets.First(a=>a.Guid==actor.modelGuid);var prefab=actor.modelAssets.Find(a=>a.Guid==actor.prefabGuid);
                int count=ApplyPackageMaterials(actor.avatar,resources,model,actor.modelAssets,prefab);
                var live=actor.shadowCasters.SelectMany(r=>r.sharedMaterials).Where(m=>m!=null)
                    .SelectMany(m=>m.GetTexturePropertyNames().Select(m.GetTexture)).ToHashSet();
                foreach(var old in actor.importedResources.OfType<Texture2D>().Where(t=>!live.Contains(t)).ToArray()) {
                    actor.importedResources.Remove(old);Destroy(old);
                }
                status.text=count+" 枚のテクスチャを再読込";
            } finally {actor.importedResources.AddRange(resources);BindShadowRenderers();ApplyModelDisplay();}
        }

        void EditModelPhysics()
        {
            ShowDialog("物理演算編集",out var body);
            var list=new DropdownField("ボーン",actor.bones.Select(b=>b.Track.Name).ToList(),Math.Max(0,actor.bones.IndexOf(actor.selectedBone)));body.Add(list);
            var active=new Toggle("物理演算");body.Add(active);
            var sliders=new Slider[5];
            string[] names={"引き寄せ","ばね","硬さ","重力","半径"};
            for(int i=0;i<sliders.Length;i++){sliders[i]=new Slider(names[i],i==3?-1:0,i==4?2:1){showInputField=true};body.Add(sliders[i]);}
            BoneBinding Bone()=>actor.bones.Find(b=>b.Track.Name==list.value);
            DynamicLink Link()=>actor.dynamicLinks.Find(d=>d.Bone==Bone());
            var syncBullet=physicsEngine==PhysicsEngine.Bullet?BuildBulletControls(body,Link):null;
            void Sync() {
                var link=Link();active.SetValueWithoutNotify(link!=null);syncBullet?.Invoke();
                foreach(var f in sliders)f.SetEnabled(link!=null);
                if(link==null)return;
                float[] v={link.Pull,link.Spring,link.Stiffness,link.Gravity,link.Radius};
                for(int i=0;i<5;i++)sliders[i].SetValueWithoutNotify(v[i]);
            }
            list.RegisterValueChangedCallback(_=>Sync());active.RegisterValueChangedCallback(e=>{
                if(e.newValue!=(Link()!=null))ToggleDynamicBone(Bone());Sync();
            });
            foreach(var slider in sliders)slider.RegisterValueChangedCallback(_=>{
                var link=Link();if(link==null)return;
                var item=new DynamicSetting{name=link.Bone.Track.Name,pull=sliders[0].value,spring=sliders[1].value,
                    stiffness=sliders[2].value,gravity=sliders[3].value,radius=sliders[4].value};
                actor.modelOverrides.physics.RemoveAll(s=>s.name==item.name);actor.modelOverrides.physics.Add(item);
                ApplyDynamicSetting(item);InvalidateDynamics();RefreshRigPreview();
            });
            Sync();Action(body,"閉じる",true,CloseDialog);
        }
        void ApplyDynamicSetting(DynamicSetting s)
        {
            var link=actor.dynamicLinks.Find(d=>d.Bone.Track.Name==s.name);if(link==null)return;
            if(!float.IsFinite(s.pull+s.spring+s.stiffness+s.gravity+s.radius))throw new System.IO.InvalidDataException("物理設定が不正です。");
            link.Pull=Mathf.Clamp01(s.pull);link.Spring=Mathf.Clamp01(s.spring);link.Stiffness=Mathf.Clamp01(s.stiffness);
            link.Gravity=Mathf.Clamp(s.gravity,-1,1);link.Radius=Mathf.Clamp(s.radius,0,2);
        }
    }
}
