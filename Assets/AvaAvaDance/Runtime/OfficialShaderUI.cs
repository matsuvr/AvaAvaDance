using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        Button shaderAddButton;
        bool shaderInstalling;
        CancellationTokenSource shaderDownloadCancellation;
        const string ShaderButtonHint = "lilToon・VRMを公式配布元から導入、またはシェーダーパックを読み込み";
        void RefreshMissingShaderButton()
        {
            if(shaderAddButton==null)return;
            var missing=drawOrder.SelectMany(model=>model.missingShaders.Select(s=>model.label+" / "+s.Label)).ToArray();
            shaderAddButton.EnableInClassList("missing-shaders",missing.Length>0);
            shaderAddButton.text=missing.Length==0 ? "シェーダー追加" : "シェーダー追加（不足あり）";
            shaderAddButton.tooltip=missing.Length==0 ? ShaderButtonHint : "必要なシェーダーが未導入です。\n"+string.Join("\n",missing.Distinct());
        }

        static string ShaderInstallResult(string message,int applied) =>
            applied==0 ? message : message+" シーンの材質 "+applied+" 件に適用しました。";

        bool OfficialShaderLoaded(OfficialShaderRelease release) => release == OfficialShaderCatalog.LilToon
            ? shaderLibrary.LoadedLilToon?.version == release.Version
            : shaderLibrary.Resolve(null, "VRM/MToon") != null && shaderLibrary.Resolve(null, "VRM10/MToon10") != null;

        void OpenShaderMenu()
        {
            if (shaderInstalling || importing || exporting || modal != null || importDialog != null) return;
            CloseMenu();
            var menu = new GenericDropdownMenu();
            if (drawOrder.Any(model => model.missingShaders.Count > 0))
            {
                menu.AddItem("不足しているシェーダーを確認…", false, ShowMissingShaderMaterials);
                menu.AddSeparator("");
            }
            foreach (var release in OfficialShaderCatalog.All)
                menu.AddItem(release.Title + (OfficialShaderLoaded(release) ? "（導入済み・ライセンス）" : " を導入…"),
                    OfficialShaderLoaded(release), () => ShowShaderLicense(release));
            menu.AddSeparator("");
            menu.AddItem("その他のシェーダー（UnityPackage / パック）…", false, PickShaderPackage);
            menu.DropDown(shaderAddButton.worldBound, shaderAddButton, false);
        }

        void ShowMissingShaderMaterials()
        {
            ShowDialog("不足しているシェーダー", out var body);
            var details = new Label(shaderAddButton.tooltip) { name = "missing-shader-materials" };
            details.style.whiteSpace = WhiteSpace.Normal;
            body.Add(details);
            Action(body, "閉じる", true, CloseDialog);
        }

        async void PickShaderPackage()
        {
            if (shaderInstalling || importing || exporting) return;
            try
            {
                string path = PackageFileDialog.Open("シェーダーの UnityPackage / パックを選択", "unitypackage;avadshader");
                if (string.IsNullOrEmpty(path)) return;
                shaderInstalling = true; shaderAddButton.SetEnabled(false);
                status.text = "シェーダーを読み込み中…";
                status.text = await LoadShaderFile(path);
            }
            catch (Exception ex) { if (this != null) status.text = "シェーダー追加中止: " + ex.Message; }
            finally { shaderInstalling = false; shaderAddButton?.SetEnabled(true); }
        }

        void ShowShaderLicense(OfficialShaderRelease release)
        {
            ShowDialog(release.Title + " " + release.Version + " のライセンス", out var body);
            body.GetFirstAncestorOfType<ScrollView>().AddToClassList("shader-license-dialog");
            var description = new Label(release.Title + " " + release.Version + " を " +
                (release == OfficialShaderCatalog.LilToon ? "公式VPM" : "UniVRM の GitHub リリース") +
                " からダウンロードします。導入後は読み込み済みの対応する材質にも適用します。\nライセンス本文と第三者の権利表示を確認してください。");
            description.AddToClassList("shader-license-description"); body.Add(description);
            var links = Box(body, "shader-links", "row");
            Action(links, "公式配布ページ", true, () => Application.OpenURL(release.Page));
            if (release == OfficialShaderCatalog.LilToon)
                Action(links, "公式VPM", true, () => Application.OpenURL(VpmInstaller.OfficialRepository));
            var license = Resources.Load<TextAsset>("ShaderLicenses/" + release.Id);
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.AddToClassList("shader-license-text"); body.Add(scroll);
            var text = new Label(license?.text ?? "ライセンス文書が見つかりません。アプリ一式を再配置してください。");
            text.style.whiteSpace = WhiteSpace.Normal; scroll.Add(text);
            var agreement = new Toggle { text = "上記のライセンスと第三者の権利表示を確認し、同意します", name = "shader-license-consent" };
            body.Add(agreement);
            var actions = Box(body, "shader-license-actions", "row");
            bool loaded = OfficialShaderLoaded(release);
            var install = new Button(() => InstallOfficialShader(release)) { name = "shader-install", text = loaded ? "導入済み" : "同意してダウンロード・適用" };
            install.SetEnabled(false); actions.Add(install);
            agreement.RegisterValueChangedCallback(e => install.SetEnabled(e.newValue && license != null && !loaded && !shaderInstalling));
            Action(actions, "閉じる", true, CloseDialog);
        }

        async void InstallOfficialShader(OfficialShaderRelease release)
        {
            if (shaderInstalling) return;
            shaderInstalling = true; shaderAddButton.SetEnabled(false);
            shaderDownloadCancellation = new CancellationTokenSource();
            var cancellation = shaderDownloadCancellation;
            ShowDialog(release.Title + " を導入中", out var body);
            var progressLabel = new Label("公式配布物を確認中…"); body.Add(progressLabel);
            var cancel = Action(body, "キャンセル", true, () => cancellation.Cancel());
            var progress = new Progress<string>(message => {
                if (this == null || cancellation.IsCancellationRequested) return;
                progressLabel.text = message; status.text = message;
            });
            try
            {
                string pack = await OfficialShaderStore.Prepare(release, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (this == null) return;
                shaderLibrary.Load(pack);
                int changed = ReapplyPackageShaders();
                OfficialShaderStore.RememberConsent(release, pack);
                status.text = ShaderInstallResult(release.Title + " " + release.Version + " を導入しました。", changed);
                Debug.Log("AVA_OFFICIAL_SHADER_INSTALLED " + release.Id + " " + release.Version + " materials=" + changed);
            }
            catch (OperationCanceledException) { if (this != null) status.text = "シェーダーの導入をキャンセルしました。"; }
            catch (Exception ex) { if (this != null) status.text = "シェーダー導入失敗: " + ex.Message; Debug.LogWarning(ex); }
            finally
            {
                shaderInstalling = false;
                shaderDownloadCancellation = null;
                cancellation.Dispose();
                if (this != null) { shaderAddButton.SetEnabled(true); CloseDialog(); }
            }
        }

        int ReapplyPackageShaders()
        {
            int changed = 0;
            var previous = actor;
            try
            {
                foreach (var target in drawOrder)
                {
                    var ready = target.missingShaders.Select(request => (request,shader:request.Resolve(shaderLibrary)))
                        .Where(pair => pair.shader != null).ToArray();
                    if (ready.Length==0) continue;
                    RestoreSurfaces(previewCamera);
                    actor = target;
                    RestoreModelDisplay();
                    try
                    {
                        foreach (var pair in ready)
                        {
                            pair.request.Apply(pair.shader,target.importedResources);
                            target.missingShaders.Remove(pair.request);
                            changed++;
                        }
                    }
                    finally { ApplyModelDisplay(); }
                }
            }
            finally
            {
                actor = previous;
                if(changed>0){BindShadowRenderers();sourceRevision++;}
                RefreshMissingShaderButton();
            }
            return changed;
        }
    }
}
