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
        async Task<string> CheckMixedShaders(string vrmPath,string packagePath,string legacyPath,string customPack,string unrelatedPack,string directory)
        {
            void Require(bool ok,string cause){if(!ok)throw new InvalidDataException(cause);}
            void CheckIndicator(int count)
            {
                Require(drawOrder.Sum(a=>a.missingShaders.Count)==count,"Unexpected number of unresolved materials");
                Require(shaderAddButton.ClassListContains("missing-shaders")==(count>0),"Shader button state");
                Require(shaderAddButton.parent.name=="profile-bar","Shader button must be accessible in both layouts");
            }
            int Install(string file){shaderLibrary.Load(file);return ReapplyPackageShaders();}
            void ImportVrm(string file)
            {
                var asset=ReadVrmFile(file);
                ImportModel(asset,new List<UnityPackageReader.PackageAsset>{asset},null);
            }
            void ImportPackage()
            {
                var assets=UnityPackageReader.Read(packagePath);
                var model=assets.Single(a=>a.Path.EndsWith(".fbx"));
                ImportModel(model,assets,PackageAvatar.LegacyPrefab(assets,model));
            }
            ClearAllActors();shaderLibrary.Dispose();shaderLibrary=new ShaderLibrary();
            ImportVrm(vrmPath);CheckIndicator(0);
            var sample=actor;
            var sampleSlots=actor.shadowCasters.SelectMany(r=>r.sharedMaterials).Distinct().ToArray();
            using(var data=new UniGLTF.GlbBinaryParser(File.ReadAllBytes(vrmPath),"test").Parse())
            {
                var lines=new List<string>();
                foreach(var src in data.GLTF.materials)
                {
                    var mat=sampleSlots.Single(m=>m.name==src.name);
                    bool toon=UniGLTF.Extensions.VRMC_materials_mtoon.GltfDeserializer.TryGet(src.extensions,out var mtoon);
                    bool unlit=UniGLTF.glTF_KHR_materials_unlit.IsEnable(src);
                    var name=toon?"VRM10/MToon10":unlit?"UniGLTF/UniUnlit":src.doubleSided?"AvaAvaDance/glTF Double Sided PBR":"Standard";
                    Require(mat.shader.name==name && mat.shader.isSupported,src.name+" shader: "+mat.shader.name);
                    Require(mat.mainTexture!=null,src.name+" base texture");
                    if(src.alphaMode=="MASK")Require(Mathf.Approximately(mat.GetFloat("_Cutoff"),src.alphaCutoff),src.name+" cutoff");
                    if(toon)Require(Mathf.Approximately(mat.GetFloat("_ShadingToonyFactor"),mtoon.ShadingToonyFactor.Value),src.name+" shading");
                    if(!toon && !unlit)Require(Mathf.Approximately(mat.GetFloat("_Glossiness"),1-src.pbrMetallicRoughness.roughnessFactor) &&
                        Mathf.Approximately(mat.GetFloat("_Metallic"),src.pbrMetallicRoughness.metallicFactor),src.name+" PBR");
                    lines.Add(src.name+"\t"+name+"\t"+src.alphaMode+"\tdoubleSided="+src.doubleSided+"\t"+mat.mainTexture.name);
                }
                Require(sampleSlots.Select(m=>m.shader).Distinct().Count()==3,"Expected three shader families in sample");
                File.WriteAllLines(Path.Combine(directory,"vrm-materials.tsv"),lines);
            }
            WritePreview(Path.Combine(directory,"vrm.png"));
            ImportPackage();CheckIndicator(3);
            Require(!shaderAddButton.tooltip.Contains("Unused"),"Unused .mat was flagged");
            ShowMissingShaderMaterials();
            Require(modal.Q<Label>("missing-shader-materials").text.Contains(actor.label),"Missing material list");
            CloseDialog();
            bool prior=ribbonProfile;
            try
            {
                foreach(bool moving in new[]{false,true})
                {
                    ApplyProfile(moving);
                    for(int i=0;i<3;i++)await Task.Yield();
                    Color32 tint=shaderAddButton.resolvedStyle.backgroundColor;
                    var bounds=shaderAddButton.worldBound;
                    Require(bounds.width>40 && bounds.xMin>MenuRoot.worldBound.center.x &&
                        bounds.xMax<=MenuRoot.worldBound.xMax+1 && tint.r==255 && tint.g==213,"Visible highlighted button, layout="+moving);
                }
            }
            finally { ApplyProfile(prior); }
            ClearAvatar();CheckIndicator(0);
            ImportPackage();CheckIndicator(3);
            var mixed=actor;var renderer=actor.shadowCasters.Single();
            var before=renderer.sharedMaterials;
            Require(Install(unrelatedPack)==0 && renderer.sharedMaterials.SequenceEqual(before),"Unrelated pack modified the scene");
            CheckIndicator(3);
            Require(Install(OfficialShaderStore.ValidatePayload(OfficialShaderCatalog.LilToon))==1,"lilToon matching slots");
            CheckIndicator(2);
            var partial=renderer.sharedMaterials;
            Require(partial[0].shader.name=="lilToon" && partial[1]==before[1] && partial[2]==before[2],"Selective application");
            ImportVrm(legacyPath);
            var legacyActor=actor;
            var other=actor.missingShaders.Single(s=>s.ExactName=="AvaAvaDance/CustomFixture").Material;
            Require(Install(customPack)==2,"Named VRM and GUID package custom materials");
            Require(other.shader.name=="AvaAvaDance/CustomFixture" && other.renderQueue==2457 &&
                Vector4.Distance(other.color,new Vector4(.2f,.4f,.6f,.8f))<.001f,"Legacy shader state");
            Require(renderer.sharedMaterials[0]==partial[0] && renderer.sharedMaterials[2].shader.name==other.shader.name,"Package custom material");
            Require(Install(OfficialShaderStore.ValidatePayload(OfficialShaderCatalog.Vrm))>0,"MToon matching slots");
            CheckIndicator(0);
            Require(renderer.sharedMaterials[1].shader.name=="VRM/MToon","Original MToon shader");
            Require(legacyActor.shadowCasters.SelectMany(r=>r.sharedMaterials).Any(m=>m.shader.name=="VRM/MToon"),"VRM 0 original MToon");
            Require(sampleSlots.SequenceEqual(sample.shadowCasters.SelectMany(r=>r.sharedMaterials).Distinct()),"Existing native VRM materials replaced");
            string project=Path.Combine(directory,"mixed.avadance");
            SaveProject(project);OpenProject(project);CheckIndicator(0);
            var reopened=drawOrder.Single(a=>a.id==mixed.id).shadowCasters.Single().sharedMaterials;
            Require(reopened.Select(m=>m.shader.name).SequenceEqual(new[]{"lilToon","VRM/MToon","AvaAvaDance/CustomFixture"}),"Project did not restore mixed renderer");
            Require(drawOrder.Single(a=>a.id==legacyActor.id).shadowCasters.SelectMany(r=>r.sharedMaterials)
                .Any(m=>m.shader.name=="AvaAvaDance/CustomFixture" && m.renderQueue==2457),"Preinstalled named shader");
            return "PASS mixed VRM 1, VRM 0, lilToon, MToon, custom shader, partial and unrelated installs, missing indicator and project restore";
        }

        async Task<string> CheckOfficialShaders(string directory, string lilFixture, string vrmFixture, string customFixture)
        {
            void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
            string previousCache = OfficialShaderStore.VerificationCacheRoot;
            try
            {
                OfficialShaderStore.VerificationCacheRoot = Path.Combine(directory, "Cache");
                ClearAllActors(); shaderLibrary.Dispose(); shaderLibrary = new ShaderLibrary();
                foreach (var release in OfficialShaderCatalog.All)
                {
                    ShowShaderLicense(release);
                    await Task.Yield();
                    await Task.Yield();
                    Check(modal.Q<ScrollView>(className: "shader-license-dialog")?.resolvedStyle.width >= 600, "License dialog must show readable text and consent");
                    var agree = modal.Q<Toggle>("shader-license-consent");
                    var install = modal.Q<Button>("shader-install");
                    Check(agree != null && install != null && !agree.value && !install.enabledSelf, "Consent must default to disabled");
                    agree.value = true; Check(install.enabledSelf, "Consent enables install");
                    agree.value = false; Check(!install.enabledSelf, "Revoking consent disables install");
                    CloseDialog();
                    Check(!Directory.Exists(OfficialShaderStore.CacheDirectory(release)), "Opening/cancelling license must not download");
                }
                foreach (string path in new[] { lilFixture, lilFixture, vrmFixture })
                {
                    var assets = UnityPackageReader.Read(path);
                    var model = assets.First(a => a.HasAsset && a.Path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase));
                    ImportModel(model, assets, PackageAvatar.LegacyPrefab(assets, model));
                }
                SetFrame(17);
                var identities = drawOrder.Select(a => a.avatar.GetInstanceID()).ToArray();
                var tracks = drawOrder.Select(a => a.modelTracks.Count).ToArray();
                WritePreview(Path.Combine(directory, "before.png"));
                foreach (var release in OfficialShaderCatalog.All)
                {
                    string pack = await OfficialShaderStore.Prepare(release, new Progress<string>(s => status.text = s), CancellationToken.None);
                    Check(!OfficialShaderStore.HasConsent(release), "Downloading alone must not persist consent");
                    Check(shaderLibrary.Load(pack) > 0, "Official shader pack loads");
                    ReapplyPackageShaders();
                    OfficialShaderStore.RememberConsent(release, pack);
                    Check(OfficialShaderStore.HasConsent(release), "Consent/source identity persists");
                    Check(shaderLibrary.Load(pack) == 0, "Duplicate install is idempotent");
                    Check(OfficialShaderLoaded(release), "Official shader status");
                    if (release == OfficialShaderCatalog.LilToon)
                        foreach (string name in new[] { "Hidden/lilToonLite", "Hidden/lilToonRefraction", "Hidden/lilToonFur", "Hidden/lilToonGem" })
                            Check(shaderLibrary.Resolve(null, name) != null, "lilToon variant missing: " + name);
                    string download = Path.Combine(OfficialShaderStore.CacheDirectory(release), release.Downloads[0].FileName);
                    var original = File.ReadAllBytes(download);
                    File.WriteAllBytes(download, new byte[] {1,2,3});
                    Check(!OfficialShaderStore.HasConsent(release), "Corrupt cache must not auto-enable shaders");
                    File.WriteAllBytes(download, original);
                }
                Check(frame == 17 && identities.SequenceEqual(drawOrder.Select(a => a.avatar.GetInstanceID())) && tracks.SequenceEqual(drawOrder.Select(a => a.modelTracks.Count)), "Reapply preserves model/frame/tracks");
                Check(drawOrder.Take(2).All(a => a.avatar.GetComponentInChildren<Renderer>().sharedMaterial.shader.name == "lilToon"), "All loaded lilToon models updated");
                Check(drawOrder.Last().avatar.GetComponentInChildren<Renderer>().sharedMaterial.shader.name == "VRM/MToon", "VRM material updated by original GUID");
                WritePreview(Path.Combine(directory, "after.png"));
                string saved = Path.Combine(directory, "installed.avadance");
                SaveProject(saved); OpenProject(saved);
                Check(drawOrder.Count == 3 && OfficialShaderLoaded(OfficialShaderCatalog.LilToon) && OfficialShaderLoaded(OfficialShaderCatalog.Vrm), "Installed shaders round-trip in project");
                string custom = await LoadShaderFile(customFixture);
                Check(shaderLibrary.Resolve("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") != null, "Other UnityPackage compiles and loads");
                Debug.Log("AVA_SHADER_CUSTOM " + custom);
                return "PASS official downloads, consent gate/cancel, SHA-256, cache corruption, immediate multi-model apply, project round trip, other UnityPackage";
            }
            catch (Exception ex) { Debug.LogException(ex); return "FAIL " + ex; }
            finally { CloseDialog(); OfficialShaderStore.VerificationCacheRoot = previousCache; }
        }
    }
}
