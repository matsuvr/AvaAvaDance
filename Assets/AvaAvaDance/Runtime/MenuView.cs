using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class ViewOptions
        {
            public bool black, information, follow, hidden, translucent, wireframe, noAA, noAniso;
            public bool showImage, showVideo;
            public string imageName, videoName;
            public float edgeWidth = -1;
            public bool customEdgeColor;
            public Color edgeColor = Color.black;
            public Color shadowTint = Color.white;
        }
        ViewOptions viewOptions = new ViewOptions();
        PreviewWindow detached;
        bool previewTop;
        Label infoLabel;
        int fpsLimit = 60;
        bool savePower, originalWireframe;



        void BindViewCommands()
        {
            Bind(Command.OutputSize, ShowOutputSize);
            Bind(Command.SeparateWindow, () => { if(detached.Active)detached.Dispose(); else detached.Open(previewTop); },check:()=>detached.Active);
            Bind(Command.Topmost, () => { previewTop=!previewTop; detached.SetTop(previewTop); },check:()=>previewTop);
            Bind(Command.Grid, () => gridToggle.value = !gridToggle.value, check: () => gridToggle.value);
            Bind(Command.FollowCamera, () => { viewOptions.follow = !viewOptions.follow; ApplyView(); }, check: () => viewOptions.follow);
            Bind(Command.Information, () => { viewOptions.information = !viewOptions.information; UpdateViewInfo(); }, check: () => viewOptions.information);
            Bind(Command.GroundShadow, () => { groundShadow = !groundShadow; ApplyShadow(shadowTrack.Current); }, check: () => groundShadow);
            Bind(Command.ShadowColor, ShadowSettings);
            Bind(Command.TransparentGround, () => { transparentGround = !transparentGround; ApplyShadow(shadowTrack.Current); }, check: () => transparentGround);
            Bind(Command.TransparentModel, () => { viewOptions.translucent = !viewOptions.translucent; ForEachActor(drawOrder,ApplyModelDisplay); }, check: () => viewOptions.translucent);
            Bind(Command.HideModel, () => { viewOptions.hidden = !viewOptions.hidden; ForEachActor(drawOrder,ApplyModelDisplay); }, check: () => viewOptions.hidden);
            Bind(Command.EdgeWidth, EdgeSettings, () => HasOutline("_OutlineWidth"));
            Bind(Command.EdgeColor, EdgeColorSettings, () => HasOutline("_OutlineColor"));
            Bind(Command.Antialias, () => { viewOptions.noAA = !viewOptions.noAA; ResizePreview(); }, check: () => !viewOptions.noAA);
            Bind(Command.Mipmap, () => { viewOptions.noAniso = !viewOptions.noAniso; ApplyFiltering(); }, check: () => !viewOptions.noAniso);
            Bind(Command.SelfShadow, () => { showSelfShadow = !showSelfShadow; ApplyShadow(shadowTrack.Current); }, check: () => showSelfShadow);
            Bind(Command.Wireframe, () => viewOptions.wireframe = !viewOptions.wireframe, check: () => viewOptions.wireframe);
            Bind(Command.Fullscreen, ToggleFullscreen,check: () => fullPreview);
            void Fps(Command id, int limit) => Bind(id, () => { fpsLimit = limit; ApplyFrameLimit(); }, check: () => fpsLimit == limit);
            Fps(Command.Unlimited, -1); Fps(Command.Limit30, 30); Fps(Command.Limit60, 60);
            Bind(Command.SavePower, () => { savePower = !savePower; ApplyFrameLimit(); }, check: () => savePower);
            Bind(Command.Black, () => { viewOptions.black = !viewOptions.black; UpdateBackdrop(); }, check: () => viewOptions.black);
            Bind(Command.LoadImage, () => FileAction("背景画像ファイル読込", Import.DirectXImage.Extensions+";jpeg", false, p => LoadBackdrop(p, false)));
            Bind(Command.ShowImage, () => { viewOptions.showImage = !viewOptions.showImage; UpdateBackdrop(); }, () => backdropImage != null, () => viewOptions.showImage);
            Bind(Command.LoadVideo, () => FileAction("背景AVIファイル読込", "avi;mp4", false, p => LoadBackdrop(p, true)));
            Bind(Command.ShowVideo, () => { viewOptions.showVideo = !viewOptions.showVideo; UpdateBackdrop(); }, () => videoPlayer != null, () => viewOptions.showVideo);
        }

        bool fullPreview;
        void ToggleFullscreen()
        {
            fullPreview=!fullPreview;Screen.fullScreenMode=fullPreview?FullScreenMode.FullScreenWindow:FullScreenMode.Windowed;
            MenuRoot.Q("workspace").EnableInClassList("fullscreen-preview",fullPreview);
            MenuRoot.Q("workspace").Focus();
        }

        void InitView()
        {
            detached=new PreviewWindow(this,()=>previewTexture);
            infoLabel = new Label { pickingMode = PickingMode.Ignore }; viewport.Add(infoLabel);
            infoLabel.style.position = Position.Absolute; infoLabel.style.left = 8; infoLabel.style.top = 30;
            Camera.onPreRender += BeginViewRender; Camera.onPostRender += EndViewRender;
            ApplyFrameLimit(); ApplyFiltering(); UpdateViewInfo(); InitBackdrop();
        }

        void BeginViewRender(Camera c) { if (c == previewCamera) { originalWireframe = GL.wireframe; if (viewOptions.wireframe) GL.wireframe = true; } }
        void EndViewRender(Camera c) { if (c == previewCamera) GL.wireframe = originalWireframe; }
        void ApplyFrameLimit() { QualitySettings.vSyncCount = 0; Application.targetFrameRate = savePower && !Application.isFocused && !lastToolFocus ? 5 : fpsLimit; }
        void OnApplicationFocus(bool focus) { ApplyFrameLimit(); ReleaseWorkspaceInput(focus); if (!focus) CloseMenu(); }
        void ApplyFiltering() => QualitySettings.anisotropicFiltering = viewOptions.noAniso ? AnisotropicFiltering.Disable : AnisotropicFiltering.ForceEnable;
        void UpdateViewInfo()
        {
            if (infoLabel == null) return;
            infoLabel.EnableInClassList("hidden", !viewOptions.information);
            if (viewOptions.information) infoLabel.text = "フレーム " + frame + "　" + (modelMode ? actor.avatar.name : SceneModeName);
        }
        void DisposeView()
        {
            Camera.onPreRender -= BeginViewRender; Camera.onPostRender -= EndViewRender;
            previewHistory.Dispose();movieHistory.Dispose();
            accessoryMaterials.Clear();foreach(var prop in props)if(prop.Object!=null)Destroy(prop.Object);
            detached?.Dispose(); RestoreModelDisplay(); DisposeBackdrop();
        }
        void RestoreModelDisplay()
        {
            foreach (var entry in actor.originals) if (entry.Key != null) entry.Key.sharedMaterials = entry.Value;
            foreach (var mat in actor.ghosts) Destroy(mat);
            actor.originals.Clear(); actor.ghosts.Clear();
        }
        void ApplyModelDisplay()
        {
            RestoreModelDisplay();
            foreach (var r in actor.shadowCasters)
            {
                r.forceRenderingOff = viewOptions.hidden;
                var materials = r.sharedMaterials;
                foreach (var m in materials) {
                    if (m == null) continue;
                    if (viewOptions.edgeWidth >= 0 && m.HasProperty("_OutlineWidth")) m.SetFloat("_OutlineWidth", viewOptions.edgeWidth);
                    if (viewOptions.customEdgeColor && m.HasProperty("_OutlineColor")) m.SetColor("_OutlineColor", viewOptions.edgeColor);
                }
                if (!viewOptions.translucent && !materials.Any(m=>m!=null && actor.modelOverrides.toons.Any(t=>t.material==m.name))) continue;
                actor.originals[r] = materials;
                r.sharedMaterials = materials.Select(m => {
                    if (m == null) return null;
                    var toon=actor.modelOverrides.toons.Find(t=>t.material==m.name);
                    if(toon!=null && !viewOptions.translucent) {
                        var replacement=new Material(Resources.Load<Shader>("MmdToon"));actor.ghosts.Add(replacement);
                        if(m.HasProperty("_MainTex")){replacement.mainTexture=m.mainTexture;replacement.mainTextureOffset=m.mainTextureOffset;replacement.mainTextureScale=m.mainTextureScale;}
                        replacement.color=m.HasProperty("_Color")?m.color:Color.white;
                        replacement.SetTexture("_Ramp",ToonImage(toon));return replacement;
                    }
                    if(!viewOptions.translucent)return m;
                    var ghost = new Material(Shader.Find("Standard")); actor.ghosts.Add(ghost);
                    if (m.HasProperty("_MainTex")) { ghost.mainTexture = m.mainTexture; ghost.mainTextureScale = m.mainTextureScale; ghost.mainTextureOffset = m.mainTextureOffset; }
                    var color = m.HasProperty("_Color") ? m.color : Color.white; color.a *= .5f; ghost.color = color;
                    ghost.SetFloat("_Mode", 2); ghost.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    ghost.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha); ghost.SetInt("_ZWrite", 0);
                    ghost.EnableKeyword("_ALPHABLEND_ON"); ghost.renderQueue = (int)RenderQueue.Transparent;
                    return ghost;
                }).ToArray();
            }
            RedrawOverlay();
        }

        bool HasOutline(string property) => actor.shadowCasters.SelectMany(r => actor.originals.TryGetValue(r, out var a) ? a : r.sharedMaterials)
            .Any(m => m != null && m.HasProperty(property));
        void ShadowSettings()
        {
            ShowDialog("地面影色設定", out var body);
            var slider = new Slider("明るさ", 0, 2) { value = groundBrightness, showInputField = true };
            body.Add(slider); slider.RegisterValueChangedCallback(e => { groundBrightness = e.newValue; ApplyShadow(shadowTrack.Current); });
            for(int i=0;i<3;i++){
                int ch=i;var f=new SliderInt("RGB"[i].ToString(),0,255){value=Mathf.RoundToInt(viewOptions.shadowTint[i]*255),showInputField=true};
                body.Add(f);f.RegisterValueChangedCallback(e=>{viewOptions.shadowTint[ch]=e.newValue/255f;ApplyShadow(shadowTrack.Current);});
            }
            Action(body, "閉じる", true, CloseDialog);
        }
        void EdgeSettings()
        {
            ShowDialog("エッジ太さ", out var body);
            var slider = new Slider("太さ", 0, 1) { value = Mathf.Max(0, viewOptions.edgeWidth), showInputField = true };
            body.Add(slider); slider.RegisterValueChangedCallback(e => { viewOptions.edgeWidth = e.newValue; ApplyModelDisplay(); });
            Action(body, "閉じる", true, CloseDialog);
        }
        void EdgeColorSettings()
        {
            ShowDialog("エッジ色", out var body);
            for (int i = 0; i < 3; i++) {
                int channel = i;
                var slider = new SliderInt("RGB"[i].ToString(), 0, 255) { value = Mathf.RoundToInt(viewOptions.edgeColor[i]*255), showInputField = true };
                body.Add(slider); slider.RegisterValueChangedCallback(e => {
                    viewOptions.edgeColor[channel] = e.newValue / 255f; viewOptions.customEdgeColor = true; ApplyModelDisplay();
                });
            }
            Action(body, "閉じる", true, CloseDialog);
        }

        const int BackdropLimit = 128 * 1024 * 1024;
        Texture2D backdropImage;
        VideoPlayer videoPlayer;
        Material backdropMaterial;
        CommandBuffer backdropCommands;
        byte[] imageBytes, videoBytes;
        string cachedVideo;
        Texture lastBackdrop;
        void InitBackdrop()
        {
            backdropMaterial = new Material(Resources.Load<Shader>("Backdrop"));
            backdropCommands = new CommandBuffer { name = "MMD背景" };
            previewCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, backdropCommands);
            UpdateBackdrop();
        }
        void LoadBackdrop(string path, bool video)
        {
            if (new FileInfo(path).Length > BackdropLimit) throw new IOException("背景ファイルは128 MiBまでです。");
            SetBackdrop(Path.GetFileName(path), File.ReadAllBytes(path), video);
            if (video) viewOptions.showVideo = true; else viewOptions.showImage = true;
            UpdateBackdrop();
        }
        void SetBackdrop(string name, byte[] bytes, bool video)
        {
            if (video) {
                if (videoBytes == bytes) return;
                string path = null;
                if (bytes != null) {
                    string ext = Path.GetExtension(name);
                    if (!string.Equals(ext,".avi",StringComparison.OrdinalIgnoreCase) && !string.Equals(ext,".mp4",StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("背景動画の形式が不正です。");
                    path = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString("N") + ext);
                    File.WriteAllBytes(path, bytes);
                }
                if (videoPlayer != null) { videoPlayer.Stop(); Destroy(videoPlayer); videoPlayer = null; }
                if (cachedVideo != null && File.Exists(cachedVideo)) File.Delete(cachedVideo);
                cachedVideo = path; videoBytes = bytes; viewOptions.videoName = name;
                if (path != null) {
                    videoPlayer = previewRig.AddComponent<VideoPlayer>(); videoPlayer.playOnAwake = false;
                    videoPlayer.renderMode = VideoRenderMode.APIOnly; videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
                    videoPlayer.url = path;
                    videoPlayer.prepareCompleted += p => { p.time = frame / 30.0; p.Play(); p.Pause(); };
                    videoPlayer.errorReceived += (p, message) => { if(p==videoPlayer)status.text = "背景動画読込失敗　" + message; };
                    videoPlayer.Prepare();
                }
            } else {
                if (imageBytes == bytes) return;
                Texture2D tex = null;
                if (bytes != null) {
                    tex=AccessoryTexture(name,bytes);
                }
                if (backdropImage != null) Destroy(backdropImage); backdropImage = tex;
                imageBytes = bytes; viewOptions.imageName = name;
            }
        }
        void UpdateBackdrop()
        {
            previewCamera.backgroundColor = viewOptions.black ? Color.black : Color.white;
            if (backdropCommands == null) return;
            Texture texture = viewOptions.showVideo && videoPlayer != null && videoPlayer.texture != null ? videoPlayer.texture :
                viewOptions.showImage ? backdropImage : null;
            if (texture != null) {
                backdropMaterial.SetFloat("_Aspect", texture.width / (float)texture.height / previewCamera.aspect);
                backdropMaterial.SetColor("_Border", previewCamera.backgroundColor);
            }
            if (lastBackdrop == texture && texture != null) return;
            lastBackdrop = texture; backdropCommands.Clear();
            if (texture != null) backdropCommands.Blit(texture, BuiltinRenderTextureType.CameraTarget, backdropMaterial);
        }
        void SyncVideo(float time)
        {
            if (viewOptions.showVideo && videoPlayer != null && videoPlayer.isPrepared && videoPlayer.canSetTime)
            {
                var desired = Math.Min(videoPlayer.length, Math.Max(0,time / 30.0));
                if (Math.Abs(desired - videoPlayer.time) > .5 / Math.Max(1, videoPlayer.frameRate)) videoPlayer.time = desired;
            }
        }
        void DisposeBackdrop()
        {
            if (previewCamera != null && backdropCommands != null) previewCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, backdropCommands);
            backdropCommands?.Release(); backdropCommands = null;
            SetBackdrop(null, null, false); SetBackdrop(null, null, true);
            if (backdropMaterial != null) Destroy(backdropMaterial);
        }
    }
}
