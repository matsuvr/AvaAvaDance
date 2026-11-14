using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AvaAvaDance.Import;
using UniVRM10;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        private GameObject previewRig;
        private Camera previewCamera;
        private RenderTexture previewTexture;
        private Image previewImage;
        private VisualElement importDialog;
        private Button loadButton, removeButton;

        private bool importing;

        private void SetupAvatarImport(VisualElement model, VisualElement target)
        {
            SetupShaders();
            loadButton = model.Query<Button>().ToList().First(b => b.text == "読込");
            loadButton.SetEnabled(true);
            loadButton.tooltip = "VRM または unitypackage のアバターを読み込みます";
            loadButton.clicked += PickPackage;
            removeButton = model.Query<Button>().ToList().First(b => b.text == "削除");
            removeButton.tooltip = "読み込んだ表示モデルを削除";
            removeButton.clicked += RemoveCurrentAvatar;
            removeButton.SetEnabled(false);
            previewImage = new Image { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.StretchToFill };
            previewImage.style.position = Position.Absolute;
            previewImage.style.left = 0; previewImage.style.top = 0;
            previewImage.style.right = 0; previewImage.style.bottom = 0;
            target.Insert(0, previewImage);
            target.RegisterCallback<GeometryChangedEvent>(_ => ResizePreview());
            SetupViewportInput(target);
        }

        private async void PickPackage()
        {
            if (importing) return;
            try
            {
                string path = PackageFileDialog.Open("モデルを選択", "unitypackage;vrm;avaeffect");
                if (string.IsNullOrEmpty(path)) return;
                importing = true; loadButton.SetEnabled(false);
                if (IsVrm(path) || IsController(path))
                {
                    status.text = "モデル読込中…";
                    var model = await Task.Run(() => ReadVrmFile(path));
                    if (this != null) ImportModel(model, new List<UnityPackageReader.PackageAsset> { model }, null);
                    return;
                }
                status.text = "パッケージ検査中（外部送信なし）…";
                var assets = await Task.Run(() => UnityPackageReader.Read(path));
                if (this == null) return;
                var installer = assets.FirstOrDefault(a => a.Path == VpmInstaller.ConfigPath);
                var models = assets.Where(a => a.HasAsset && a.Path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.Data.Length)
                    .ThenBy(a => a.Path, StringComparer.Ordinal)
                    .ToList();
                if (installer != null)
                {
                    status.text = ResolveInstaller(installer);
                    if (models.Count == 0) return;
                }
                if (models.Count == 0) throw new InvalidDataException("パッケージ内に FBX がありません。prefab / .asset 単独のモデルは未対応です。");
                var choices=await Task.Run(()=>PackageAvatar.Choices(assets,models));
                if(this!=null)ShowModelSelection(assets, choices);
            }
            catch (Exception ex) { if (this != null) ReportActionError("読込中止: ", ex); }
            finally { importing = false; if (loadButton != null) loadButton.SetEnabled(true); }
        }

        private void ShowModelSelection(List<UnityPackageReader.PackageAsset> assets, List<PackageAvatar> models)
        {
            importDialog?.RemoveFromHierarchy();
            importDialog = new VisualElement(); importDialog.AddToClassList("import-dialog");
            var root = GetComponent<UIDocument>().rootVisualElement; root.Add(importDialog);
            importDialog.Add(new Label("読み込むアバターを選択"));
            var list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("fbx-list");
            importDialog.Add(list);
            var choice = new RadioButtonGroup(null, models.Select(m => m.Label).ToList());
            list.Add(choice);
            choice.value = 0;
            var warning = new Label("FBXに対応するprefabから、髪や衣装のPhysBone設定を読み取ります。\n利用・変換が許可されたアバターのみ読み込んでください。"); importDialog.Add(warning);
            var consent = new Toggle("このアプリで利用できるアバターです"); importDialog.Add(consent);
            var row = Box(importDialog, "import-actions", "row");
            var load = new Button(() =>
            {
                try
                {
                    var selected=models[choice.value];
                    ImportModel(selected.Model, assets, selected.Prefab);
                    importDialog.RemoveFromHierarchy(); importDialog = null;
                }
                catch (Exception ex) { ReportActionError("FBX 読込失敗: ", ex); }
            }) { text = "読み込む" }; load.SetEnabled(false); row.Add(load);
            consent.RegisterValueChangedCallback(e => load.SetEnabled(e.newValue));
            row.Add(new Button(() => { importDialog.RemoveFromHierarchy(); importDialog = null; status.text = "読込をキャンセルしました。"; }) { text = "キャンセル" });
            status.text = models.Count + " 件の読込候補を検出。パッケージ内のコードは実行しません。";
        }

        private void ImportModel(UnityPackageReader.PackageAsset model, List<UnityPackageReader.PackageAsset> assets, UnityPackageReader.PackageAsset prefab)
        {
            if(drawOrder.Count>=255)throw new InvalidDataException("モデルは255体までです。");
            var previousActor=actor;
            string name = Path.GetFileNameWithoutExtension(model.Path);
            var missing = new List<MissingMaterialShader>();
            var vrm = IsVrm(model.Path) ? LoadVrm(model.Data,missing) : null;
            var descriptor=IsController(model.Path)?Controller(model.Data):null;
            var controlJoints=new List<Transform>();
            var candidate = descriptor!=null?BuildController(descriptor,name,out controlJoints):vrm != null ? vrm.gameObject : FbxNative.Load(model.Data, name);
            candidate.name = name;
            var resources = new List<UnityEngine.Object>();
            try
            {
                var physBones = PrefabPhysBones.Read(prefab, candidate);
                var human = MapHumanBones(candidate, vrm, model.Meta);
                var rawRig=RigFiles.FromHierarchy(candidate,human);
                rawRig.sourceId=prefab==null?model.Guid:prefab.Guid+"/"+model.Guid;
                rawRig.shapes=vrm!=null?RigFiles.SpringShapes(candidate):physBones.Shapes.ToArray();
                RigMeasurements.FromScene(candidate,rawRig);
                using(var hash=System.Security.Cryptography.SHA256.Create())rawRig.sourceHash=Convert.ToBase64String(hash.ComputeHash(model.Data));
                var bindPose = human.Values.Distinct().ToDictionary(t => t, t => new HumanoidBindRotation(t));
                var bindPositions = bindPose.Keys.ToDictionary(t => t, t => candidate.transform.InverseTransformPoint(t.position));
                var facing = Vector3.forward;
                int textures = 0;
                if (vrm == null && descriptor==null)
                {
                    HumanoidPreview.TryPose(candidate, model.Meta, out facing);
                    textures = ApplyPackageMaterials(candidate, resources, model, assets, prefab, missing);
                }
                rawRig.editRest=RigMeasurements.EditRest(rawRig,RigFiles.FromHierarchy(candidate,human).joints);
                EnsurePreview();
                foreach (var t in candidate.GetComponentsInChildren<Transform>()) t.gameObject.layer = 30;
                var renderers = candidate.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToArray();
                if(descriptor==null)
                {
                    if (renderers.Length == 0) throw new InvalidDataException("表示可能なメッシュがありません。");
                    var bounds = renderers[0].bounds;
                    foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                    float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                    if (!float.IsFinite(size) || size < 0.00001f) throw new InvalidDataException("不正なモデル寸法です。");
                    candidate.transform.localScale *= 2f / size;
                    candidate.transform.position -= bounds.center * (2f / size);
                }
                else if(descriptor.coordinateSpace=="motion"){
                    candidate.transform.SetPositionAndRotation(propFloor,Quaternion.Euler(0,180,0));
                }
                BeginActor(candidate);actor.controller=descriptor;actor.rigProfile=rawRig;
                actor.vrm = vrm;
                actor.missingShaders = missing;
                actor.humanBones = human;
                actor.bindPose = bindPose;
                actor.bindPositions = bindPositions;
                actor.humanForward = HumanoidFacing(human, facing);
                if (human.TryGetValue(HumanBodyBones.Hips, out var hips))
                {
                    actor.hipsParentInverse = hips.parent.worldToLocalMatrix;
                    actor.hipsHeight = hips.position.y - candidate.transform.position.y;
                }
                actor.importedResources.AddRange(resources);
                front = facing;
                modelView = CameraPose.Initial;
                useCameraView = false;
                var dynamics = BuildPhysBoneLinks(physBones);
                var stretchChildren=dynamics.Values.Where(d=>d.MaxStretch>0 || d.MaxSquish>0).SelectMany(d=>d.Children);
                var joints = dynamics.Keys.Concat(stretchChildren).Concat(controlJoints).Concat(human.Values);
                if (vrm != null)
                {
                    actor.gaze = new GameObject("両目").transform;
                    actor.gaze.SetParent(human[HumanBodyBones.Head], false);
                    joints = joints.Append(actor.gaze);
                }
                BindBones(joints); BindLogicalRig(); BindMorphs();
                if(descriptor!=null)foreach(var m in descriptor.morphs)AddMorph(m.name,_=>{},m.value);
                BindRig(model.Meta, dynamics);
                rawRig.morphs=actor.modelTracks.OfType<MotionTrack<float>>().Select(MorphName).ToArray();
                if(descriptor!=null)RefreshActorUI();
                actor.shadowCasters = renderers;
                foreach (var r in renderers) if (r.receiveShadows) actor.sourceShadowReceivers.Add(r);
                ApplyDrawOrder(); BindShadowRenderers(); ApplyModelDisplay();
                RememberModel(model,assets,prefab);
                RefreshMissingShaderButton();
                viewportNote.EnableInClassList("hidden", true);
                removeButton.SetEnabled(true);
                ChangeMode(true);
                if(descriptor!=null){status.text=model.Path+" / 制御ボーン "+descriptor.bones.Length+" / モーフ "+descriptor.morphs.Length;return;}
                if (vrm != null)
                {
                    status.text = model.Path + " / " + human.Count + " ヒューマノイドボーン";
                    return;
                }
                status.text = (prefab?.Path ?? model.Path) + " / " + renderers.Length + " renderer / " + textures + " texture / " + materialReport;
                status.text += $" / PhysBone {physBones.Chains.Count}/{physBones.Components} 設定・{actor.dynamicLinks.Count} 関節";
                Debug.Log($"AVA_PHYSBONE prefab={prefab?.Path} components={physBones.Components} mappedRoots={physBones.Chains.Count} joints={actor.dynamicLinks.Count}");
                int skipped = actor.avatar.GetComponent<FbxOwnedResources>().SkippedShapes;
                if (skipped > 0) status.text += " / 表情読込対象外 " + skipped;
            }
            catch
            {
                if (actor.avatar == candidate) { ClearAvatar(); actor=previousActor; RefreshActorUI(); ChangeMode(actor.avatar!=null); }
                else
                {
                    Destroy(candidate);
                    foreach (var resource in resources) Destroy(resource);
                }
                throw;
            }
        }

        private void EnsurePreview()
        {
            if (previewRig != null) return;
            previewRig = new GameObject("Runtime preview");
            previewCamera = new GameObject("Preview camera").AddComponent<Camera>();
            previewCamera.transform.SetParent(previewRig.transform, false);
            previewCamera.cullingMask=1<<30;
            previewCamera.clearFlags=CameraClearFlags.SolidColor; previewCamera.backgroundColor=Color.white;
            previewCamera.nearClipPlane=.01f; previewCamera.farClipPlane=100; previewCamera.fieldOfView=45;
            var lamp = new GameObject("Preview light"); lamp.transform.SetParent(previewRig.transform);
            lamp.transform.rotation=Quaternion.Euler(35, -25, 0);
            previewLamp = lamp.AddComponent<Light>(); previewLamp.type = LightType.Directional; previewLamp.cullingMask = 1<<30;
            MakeFloor();
            ResizePreview();
        }
        private void ResizePreview()
        {
            if (previewCamera == null || exporting) return;
            var size = viewport.contentRect.size;
            if (!(size.x > 0 && size.y > 0)) return;
            float scale = Mathf.Min(panel.scale, 2048f / Mathf.Max(size.x, size.y));
            int w = Mathf.Max(1, Mathf.RoundToInt(size.x * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(size.y * scale));
            previewCamera.aspect = size.x / size.y;
            var desc = new RenderTextureDescriptor(w,h,RenderTextureFormat.ARGB32,24) { msaaSamples = viewOptions.noAA ? 1 : 4 };
            desc.msaaSamples = SystemInfo.GetRenderTextureSupportedMSAASampleCount(desc);
            if (previewTexture != null && previewTexture.width==w && previewTexture.height==h && previewTexture.antiAliasing==desc.msaaSamples) return;
            if (previewTexture != null) { previewTexture.Release(); Destroy(previewTexture); }
            previewTexture = new RenderTexture(desc); previewTexture.Create();
            previewCamera.targetTexture=previewTexture; previewImage.image=previewTexture;
        }
        void RemoveCurrentAvatar()
        {
            if(actor.avatar==null)return;
            ClearAvatar();ChangeMode(false);status.text="モデルを削除しました。";
        }
        private void ClearAvatar(bool refresh = true)
        {
            StopPlayback();
            ReleaseBones(); ClearRig(); actor.modelAssets.Clear(); actor.modelGuid=null; actor.prefabGuid=null;
            ClearToons(); actor.modelOverrides=new ModelOverrides();
            RestoreModelDisplay(); actor.shadowCasters = Array.Empty<Renderer>(); ClearShadowRenderers();
            if (actor.avatar != null) { actor.avatar.SetActive(false); Destroy(actor.avatar); }
            actor.avatar = null;
            viewportNote?.EnableInClassList("hidden", false);
            removeButton?.SetEnabled(false);
            foreach (var resource in actor.importedResources) if (resource != null) Destroy(resource);
            actor.importedResources.Clear();
            externalParents.RemoveAll(l=>l.owner==actor.id || l.parentOwner==actor.id);
            sourceRevision++;drawOrder.Remove(actor);calculationOrder.Remove(actor);actor=drawOrder.LastOrDefault()??new Actor();
            if (refresh)
            {
                if (actor.avatar != null) RefreshActorUI();
                BindShadowRenderers();
                RefreshMissingShaderButton();
            }
        }
        private void DisposePreview()
        {
            shaderDownloadCancellation?.Cancel();
            ClearAllActors(refresh: false);
            DisposeShadowRendering();
            if (shadowFloor!=null) Destroy(shadowFloor.GetComponent<MeshFilter>().sharedMesh);
            if (groundMaterial != null) Destroy(groundMaterial);
            if (floorMesh != null) Destroy(floorMesh);
            if (floorMaterial != null) Destroy(floorMaterial);
            shaderLibrary?.Dispose(); if (previewRig != null) Destroy(previewRig);
            if (previewTexture != null) { previewTexture.Release(); Destroy(previewTexture); }
        }
    }
}
