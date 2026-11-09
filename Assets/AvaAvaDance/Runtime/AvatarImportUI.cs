using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        private GameObject avatar, previewRig;
        private Camera previewCamera;
        private RenderTexture previewTexture;
        private Image previewImage;
        private VisualElement importDialog;
        private Button loadButton;
        private readonly List<UnityEngine.Object> importedResources = new List<UnityEngine.Object>();
        private bool importing;

        private void SetupAvatarImport(VisualElement model, VisualElement target)
        {
            SetupShaders();
            loadButton = model.Query<Button>().ToList().First(b => b.text == "読込");
            loadButton.SetEnabled(true);
            loadButton.tooltip = "unitypackage 内の FBX を直接読み込みます（Windows x64 試作版）";
            loadButton.clicked += PickPackage;
            var remove = model.Query<Button>().ToList().First(b => b.text == "削除");
            remove.SetEnabled(true); remove.tooltip = "読み込んだ表示モデルを削除";
            remove.clicked += () => { ClearAvatar(); viewportTitle.text = "Model：未読込"; };
            previewImage = new Image { pickingMode = PickingMode.Ignore };
            previewImage.style.position = Position.Absolute;
            previewImage.style.left = 0; previewImage.style.top = 0;
            previewImage.style.right = 0; previewImage.style.bottom = 0;
            target.Insert(0, previewImage);
            target.RegisterCallback<GeometryChangedEvent>(_ => ResizePreview());
            target.RegisterCallback<WheelEvent>(evt =>
            {
                if (previewCamera == null || avatar == null) return;
                previewCamera.transform.position += previewCamera.transform.forward * evt.delta.y * .08f;
                evt.StopPropagation();
            });
            Vector2 last = default; bool dragging = false;
            target.RegisterCallback<PointerDownEvent>(evt => { if (evt.button != 0 || avatar == null) return; dragging = true; last = evt.position; target.CapturePointer(evt.pointerId); });
            target.RegisterCallback<PointerMoveEvent>(evt => { if (!dragging || avatar == null) return; var now = (Vector2)evt.position; avatar.transform.Rotate(Vector3.up, -(now.x-last.x)*.5f, Space.World); last=now; });
            target.RegisterCallback<PointerUpEvent>(evt => { dragging=false; target.ReleasePointer(evt.pointerId); });
            target.RegisterCallback<PointerCaptureOutEvent>(_ => dragging=false);
        }

        private async void PickPackage()
        {
            if (importing) return;
            try
            {
                string path = PackageFileDialog.Open();
                if (string.IsNullOrEmpty(path)) return;
                importing = true; loadButton.SetEnabled(false);
                status.text = "パッケージ検査中（外部送信なし）…";
                var assets = await Task.Run(() => UnityPackageReader.Read(path));
                if (this == null) return;
                var models = assets.Where(a => a.HasAsset && a.Path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)).ToList();
                if (models.Count == 0) throw new InvalidDataException("パッケージ内に FBX がありません。prefab / .asset 単独のモデルは未対応です。");
                ShowModelSelection(assets, models);
            }
            catch (Exception ex) { if (this != null) status.text = "読込中止: " + ex.Message; }
            finally { importing = false; if (loadButton != null) loadButton.SetEnabled(true); }
        }

        private void ShowModelSelection(List<UnityPackageReader.PackageAsset> assets, List<UnityPackageReader.PackageAsset> models)
        {
            importDialog?.RemoveFromHierarchy();
            importDialog = new VisualElement(); importDialog.AddToClassList("import-dialog");
            var root = GetComponent<UIDocument>().rootVisualElement; root.Add(importDialog);
            importDialog.Add(new Label("表示する FBX を選択"));
            var choice = new DropdownField(models.Select(m => m.Path).ToList(), 0); importDialog.Add(choice);
            var warning = new Label("FBX の形状・リグの試作読込です。prefab の組立、衣装設定、\nシェーダーは対応パックのみ。PhysBone / VRChat スクリプトは未対応。\n利用・変換が許可されたアバターのみ読み込んでください。"); importDialog.Add(warning);
            var consent = new Toggle("このアプリで利用できるアバターです"); importDialog.Add(consent);
            var row = Box(importDialog, "import-actions", "row");
            var load = new Button(() =>
            {
                try
                {
                    ImportModel(models[choice.index], assets);
                    importDialog.RemoveFromHierarchy(); importDialog = null;
                }
                catch (Exception ex) { status.text = "FBX 読込失敗: " + ex.Message; }
            }) { text = "読み込む" }; load.SetEnabled(false); row.Add(load);
            consent.RegisterValueChangedCallback(e => load.SetEnabled(e.newValue));
            row.Add(new Button(() => { importDialog.RemoveFromHierarchy(); importDialog = null; status.text = "読込をキャンセルしました。"; }) { text = "キャンセル" });
            status.text = models.Count + " 個の FBX を検出。パッケージ内のコードは実行しません。";
        }

        private void ImportModel(UnityPackageReader.PackageAsset model, List<UnityPackageReader.PackageAsset> assets)
        {
            ClearAvatar();
            try
            {
                avatar = FbxNative.Load(model.Data, Path.GetFileNameWithoutExtension(model.Path));
                int textures = ApplyPackageMaterials(assets);
                EnsurePreview();
                foreach (var transform in avatar.GetComponentsInChildren<Transform>()) transform.gameObject.layer = 30;
                var renderers = avatar.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) throw new InvalidDataException("表示可能なメッシュがありません。");
                var bounds = renderers[0].bounds;
                foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                if (!float.IsFinite(size) || size < 0.00001f) throw new InvalidDataException("不正なモデル寸法です。");
                avatar.transform.localScale *= 2f / size;
                avatar.transform.position -= bounds.center * (2f / size);
                previewCamera.transform.position = new Vector3(0, .15f, 3.2f);
                previewCamera.transform.LookAt(Vector3.zero);

                viewportNote.text = "ドラッグ: モデル回転 / ホイール: 距離。prefab・物理は未対応。対応シェーダーを材質から適用。";
                modelNameField.choices = new List<string> { avatar.name }; modelNameField.SetValueWithoutNotify(avatar.name);
                ChangeMode(true); viewportTitle.text = "Model：" + avatar.name + "（FBX 直接読込）";
                status.text = "FBX 表示: " + renderers.Length + " renderer / " + textures + " texture / " + materialReport + "。prefab/物理/アニメ未対応。";
            }
            catch { ClearAvatar(); throw; }
        }

        private void EnsurePreview()
        {
            if (previewRig != null) return;
            previewRig = new GameObject("Runtime preview");
            previewCamera = previewRig.AddComponent<Camera>(); previewCamera.cullingMask=1<<30;
            previewCamera.clearFlags=CameraClearFlags.SolidColor; previewCamera.backgroundColor=Color.white;
            previewCamera.nearClipPlane=.01f; previewCamera.farClipPlane=100; previewCamera.fieldOfView=45;
            var lamp = new GameObject("Preview light"); lamp.transform.SetParent(previewRig.transform);
            lamp.transform.rotation=Quaternion.Euler(35, -25, 0);
            var lightComponent=lamp.AddComponent<Light>(); lightComponent.type=LightType.Directional; lightComponent.intensity=1.2f; lightComponent.cullingMask=1<<30;
            ResizePreview();
        }
        private void ResizePreview()
        {
            if (previewCamera == null) return;
            int w=Mathf.Clamp((int)viewport.contentRect.width,64,2048), h=Mathf.Clamp((int)viewport.contentRect.height,64,2048);
            if (previewTexture != null && previewTexture.width==w && previewTexture.height==h) return;
            if (previewTexture != null) { previewTexture.Release(); Destroy(previewTexture); }
            previewTexture = new RenderTexture(w,h,24); previewTexture.Create();
            previewCamera.targetTexture=previewTexture; previewImage.image=previewTexture;
        }
        private void ClearAvatar()
        {
            if (avatar != null) Destroy(avatar); avatar=null;
            if (modelNameField != null) { modelNameField.choices = new List<string> { "モデル未読込" }; modelNameField.SetValueWithoutNotify("モデル未読込"); }
            foreach (var resource in importedResources) if (resource != null) Destroy(resource);
            importedResources.Clear();
        }
        private void DisposePreview()
        {
            ClearAvatar(); shaderLibrary?.Dispose(); if (previewRig != null) Destroy(previewRig);
            if (previewTexture != null) { previewTexture.Release(); Destroy(previewTexture); }
        }
    }
}
