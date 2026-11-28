using System;
using System.IO;
using System.Collections.Generic;
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
        const string ShaderButtonHint = "シェーダー、追加マテリアル・テクスチャを読み込み";
        VisualElement dependencyNotice;
        Label dependencyMessage;
        Actor noticeOwner;

        static IEnumerable<PackageDependency> Dependencies(Actor target)
        {
            foreach (var request in target.missingShaders)
                yield return request.Dependency ?? new PackageDependency { Kind=DependencyKind.Shader, Name=request.ExactName,
                    Guid=request.Guid, UsedBy=request.Material?.name };
            foreach (var request in target.missingAssets) yield return request;
        }

        void SetupDependencyNotice(VisualElement bar)
        {
            dependencyNotice = new VisualElement { name="dependency-notice" };
            dependencyNotice.AddToClassList("row");
            bar.parent.Insert(bar.parent.IndexOf(bar)+1,dependencyNotice);
            dependencyMessage = new Label { enableRichText=false };
            dependencyNotice.Add(dependencyMessage);
            Action(dependencyNotice,"不足の詳細",true,ShowMissingShaderMaterials);
            Action(dependencyNotice,"閉じる",true,()=>ShowImportDependencies(null));
            dependencyNotice.style.display=DisplayStyle.None;
        }

        void ShowImportDependencies(Actor target)
        {
            noticeOwner=target;
            RefreshMissingShaderButton();
        }
        void RefreshMissingShaderButton()
        {
            if(shaderAddButton==null)return;
            var missing=drawOrder.SelectMany(model=>Dependencies(model).Select(d=>model.label+" / "+d.KindLabel+" "+(d.Name??d.Guid??"名前不明"))).Distinct().ToArray();
            shaderAddButton.EnableInClassList("missing-shaders",missing.Length>0);
            shaderAddButton.text=missing.Length==0 ? "シェーダー追加" : "シェーダー追加（不足あり）";
            shaderAddButton.tooltip=missing.Length==0 ? ShaderButtonHint : string.Join("\n",missing.Take(12));
            if (noticeOwner!=null && (!drawOrder.Contains(noticeOwner) || !Dependencies(noticeOwner).Any())) noticeOwner=null;
            dependencyNotice.style.display=noticeOwner==null ? DisplayStyle.None : DisplayStyle.Flex;
            if (noticeOwner!=null)
            {
                var first=Dependencies(noticeOwner).First();
                dependencyMessage.text=noticeOwner.label+" の"+first.KindLabel+"「"+(first.Name??"名前不明")+"」が必要です。"+
                    (first.Included ? "「不足の詳細」で同梱シェーダーの追加方法を確認できます。" :
                     first.Kind==DependencyKind.Shader && first.Name==null && !PackageDependency.External(first.Guid) ?
                     "「不足の詳細」で内蔵シェーダーの参照を確認できます。" : "配布元で入手し、「シェーダー追加」から読み込んでください。");
            }
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
            if (drawOrder.Any(model => Dependencies(model).Any()))
            {
                menu.AddItem("不足しているシェーダー・素材を確認…", false, ShowMissingShaderMaterials);
                menu.AddSeparator("");
            }
            menu.AddItem("lilToon を外部から読み込む…", shaderLibrary.LoadedLilToon != null, ShowLilToonImport);
            var vrm = OfficialShaderCatalog.Vrm;
            bool vrmLoaded = OfficialShaderLoaded(vrm);
            menu.AddItem(vrm.Title + (vrmLoaded ? "（導入済み・ライセンス）" : " を導入…"), vrmLoaded, () => ShowShaderLicense(vrm));
            menu.AddSeparator("");
            menu.AddItem("シェーダー・素材のパッケージを追加…", false, PickShaderPackage);
            menu.DropDown(shaderAddButton.worldBound, shaderAddButton, false);
        }

        void ShowMissingShaderMaterials()
        {
            ShowDialog("不足しているシェーダー・素材",out var body);
            body.GetFirstAncestorOfType<ScrollView>().AddToClassList("shader-license-dialog");
            var groups=drawOrder.SelectMany(model=>Dependencies(model).Select(d=>(model:model.label,dep:d)))
                .GroupBy(row=>(row.dep.Kind,identity:row.dep.Guid??row.dep.Name)).ToArray();
            Label Line(string text)
            {
                var label=new Label(text) { enableRichText=false };
                label.AddToClassList("shader-license-description"); body.Add(label);
                return label;
            }
            Line(string.Join("、",groups.SelectMany(g=>g.Select(r=>r.model)).Distinct())).name="missing-shader-materials";
            Line("アバターの配布ページ・READMEで必要なパッケージを確認し、取得した .unitypackage または .avadshader を「シェーダー追加」から選んでください。");
            var actions=Box(body,"dependency-actions","row");
            Action(actions,"取得済みのパッケージを追加…",true,()=>{CloseDialog();PickShaderPackage();});
            Action(actions,"閉じる",true,CloseDialog);
            foreach (var group in groups)
            {
                var dep=group.First().dep;
                var heading=Line(dep.KindLabel+"　"+(dep.Name??"名前不明"));
                heading.AddToClassList("dependency-title");
                Line("使用箇所　"+string.Join("、",group.Select(row=>row.model+" / "+row.dep.UsedBy).Distinct()));
                if (dep.Guid!=null) Line("GUID　"+dep.Guid);
                if (dep.Included) Line("参照先はアバターに同梱されています。対応するシェーダーパック、または ShaderLab ソースを含む元の .unitypackage を追加してください。");
                else if (dep.Kind!=DependencyKind.Shader || PackageDependency.External(dep.Guid) || dep.Name!=null)
                    Line(dep.Name==null ? "参照に名前が記録されていません。配布元の案内か GUID で特定できます。" :
                        "配布元でこの"+dep.KindLabel+"を含むパッケージを確認してください。");
                else { Line("Unity 内蔵シェーダーの参照をこの Player で解決できません。"); continue; }
                if (dep.OfficialPack == "liltoon")
                    Action(body, "lilToon を外部から読み込む…", true, ShowLilToonImport);
                else if (dep.OfficialPack == OfficialShaderCatalog.Vrm.Id)
                    Action(body, OfficialShaderCatalog.Vrm.Title + " を導入…", true, () => ShowShaderLicense(OfficialShaderCatalog.Vrm));
                string url=dep.SearchUrl;
                var link=Action(body,dep.Name==null ? "GUID で Google 検索" : "「"+dep.Name+"」を Google 検索",true,()=>Application.OpenURL(url));
                link.enableRichText=false; link.tooltip=url;
                var address=new TextField {value=url,isReadOnly=true};
                address.AddToClassList("dependency-url");body.Add(address);
            }
        }

        async void PickShaderPackage()
        {
            if (shaderInstalling || importing || exporting) return;
            try
            {
                string path = PackageFileDialog.Open("シェーダー・素材のパッケージを選択", "unitypackage;avadshader");
                if (string.IsNullOrEmpty(path)) return;
                shaderInstalling = true; shaderAddButton.SetEnabled(false);
                status.text = "シェーダーを読み込み中…";
                bool needsLilToon = false;
                using (BeginProjectOperation())
                    status.text = await LoadShaderFile(path, () => needsLilToon = true);
                if (needsLilToon && this != null) ShowLilToonConsent();
            }
            catch (Exception ex) { if (this != null) status.text = "シェーダー追加中止: " + ex.Message; }
            finally { shaderInstalling = false; shaderAddButton?.SetEnabled(true); }
        }

        void ShowLilToonImport()
        {
            ShowDialog("lilToon を外部から読み込む", out var body);
            body.GetFirstAncestorOfType<ScrollView>().AddToClassList("shader-license-dialog");
            var description = new Label("公式の lilToon インストーラー（.unitypackage）を選ぶか、メインウィンドウにドロップしてください。");
            description.AddToClassList("shader-license-description");
            body.Add(description);
            var actions = Box(body, "shader-license-actions", "row");
            Action(actions, "公式配布ページ", true, () => Application.OpenURL(OfficialShaderCatalog.LilToon.Page));
            Action(actions, "インストーラーを選ぶ…", true, () => { CloseDialog(); PickPackage(); });
            Action(actions, "閉じる", true, CloseDialog);
        }

        void ShowLilToonConsent()
        {
            var release = OfficialShaderCatalog.LilToon;
            ShowDialog("lilToon " + release.Version + " を公式VPMから取得", out var body);
            body.name = "liltoon-consent";
            body.GetFirstAncestorOfType<ScrollView>().AddToClassList("shader-license-dialog");
            var description = new Label("公式VPMから取得したZIPのSHA-256を照合し、Windows版 Unity " + ShaderLibrary.UnityVersion +
                " で変換します。変換時に実行するのは、検証済みの公式配布物に含まれる Unity Editor 用コードです。");
            description.AddToClassList("shader-license-description");
            body.Add(description);
            var links = Box(body, "shader-links", "row");
            Action(links, "公式配布ページ・ライセンス", true, () => Application.OpenURL(release.Page));
            var agreement = new Toggle("公式配布物のライセンスと第三者の権利表示を確認しました") { name = "shader-license-consent" };
            body.Add(agreement);
            var actions = Box(body, "shader-license-actions", "row");
            bool supported = Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
            var install = new Button(() => InstallOfficialShader(release))
            {
                name = "shader-install",
                text = supported ? "同意して取得・適用" : "Windows で利用できます"
            };
            install.SetEnabled(false);
            actions.Add(install);
            agreement.RegisterValueChangedCallback(e => install.SetEnabled(e.newValue && supported && !shaderInstalling));
            Action(actions, "閉じる", true, CloseDialog);
        }

        Task<string> CompileOfficialLilToon(IProgress<string> progress, CancellationToken cancellation) =>
            LilToonCompiler.Compile(Path.GetDirectoryName(Application.dataPath), Application.isEditor,
                OfficialShaderStore.CacheDirectory(OfficialShaderCatalog.LilToon), progress, cancellation);

        void ShowShaderLicense(OfficialShaderRelease release)
        {
            ShowDialog(release.Title + " " + release.Version + " のライセンス", out var body);
            body.GetFirstAncestorOfType<ScrollView>().AddToClassList("shader-license-dialog");
            var description = new Label(release.Title + " " + release.Version + " を UniVRM の GitHub リリースからダウンロードします。導入後は読み込み済みの対応する材質にも適用します。\nライセンス本文と第三者の権利表示を確認してください。");
            description.AddToClassList("shader-license-description"); body.Add(description);
            var links = Box(body, "shader-links", "row");
            Action(links, "公式配布ページ", true, () => Application.OpenURL(release.Page));
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
            if (shaderInstalling || importing || exporting || physicsBaking) return;
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
                using var operation = BeginProjectOperation();
                bool external = release == OfficialShaderCatalog.LilToon;
                string pack = external
                    ? await CompileOfficialLilToon(progress, cancellation.Token)
                    : await OfficialShaderStore.Prepare(release, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (this == null) return;
                shaderLibrary.Load(pack);
                int changed = ReapplyPackageShaders();
                if (!external) OfficialShaderStore.RememberConsent(release, pack);
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
