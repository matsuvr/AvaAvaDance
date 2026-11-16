using System;
using System.IO;
using AvaAvaDance.Import;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        void ChooseImageOutput()
        {
            StopPlayback();
            try {
                string path=PackageFileDialog.Save("画像ファイルに出力",DirectXImage.Extensions);
                if(!string.IsNullOrEmpty(path)) {
                    var working=CopyOutputSettings(movie);working.width=movie.ImageWidth;working.height=movie.ImageHeight;
                    ShowImageSettings(path,working);
                }
            } catch(Exception ex) { ReportActionError("画像出力中止　",ex); }
        }

        void ShowImageSettings(string path, MovieSettings working)
        {
            ShowDialog("画像出力設定",out var body);
            AddOutputPresetFields(body,working,()=>ShowImageSettings(path,working),true);
            var error=new Label{name="output-error"};error.style.whiteSpace=WhiteSpace.Normal;body.Add(error);
            var row=Box(body,"image-actions","row");
            Action(row,"OK",true,()=>{
                if(!ValidImageSize(working.width,working.height)) {
                    error.text="幅・高さは1～"+SystemInfo.maxTextureSize+"で指定してください。";return;
                }
                try {
                    WriteOutputImage(path,working.width,working.height);
                    movie.imageWidth=working.width;movie.imageHeight=working.height;
                    CloseDialog();status.text="画像出力　"+Path.GetFileName(path)+"　"+working.width+" × "+working.height;
                } catch(Exception ex) { error.text="画像出力失敗　"+ex.Message; }
            });
            Action(row,"キャンセル",true,CloseDialog);
        }

        void ExportImage(string path) => WriteOutputImage(path,movie.ImageWidth,movie.ImageHeight);

        static bool ValidImageSize(int width,int height) => width>0 && height>0 &&
            width<=SystemInfo.maxTextureSize && height<=SystemInfo.maxTextureSize;

        void WriteOutputImage(string path,int width,int height)
        {
            if(exporting)throw new InvalidOperationException("出力中です。");
            if(!ValidImageSize(width,height))throw new ArgumentException("画像の出力サイズがGPUの対応範囲外です。");
            StopPlayback();
            var previousTarget=previewCamera.targetTexture;
            float previousAspect=previewCamera.aspect;
            var previousActive=RenderTexture.active;
            bool previousGrid=floor.activeSelf;
            var previousClockState=(previousClock,previousSample,renderSeconds,renderDelta,syncedSeconds,syncedDelta,outputDelta);
            RenderTexture target=null;Texture2D image=null;
            movieHistory.Dispose();exporting=true;outputDelta=0;
            try {
                var descriptor=new RenderTextureDescriptor(width,height,RenderTextureFormat.ARGB32,24) {
                    msaaSamples=viewOptions.noAA?1:4
                };
                descriptor.msaaSamples=SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
                target=new RenderTexture(descriptor);
                if(!target.Create())throw new IOException("画像用RenderTextureを作成できません。");
                image=new Texture2D(width,height,TextureFormat.RGB24,false);
                previewCamera.targetTexture=target;previewCamera.aspect=(float)width/height;
                floor.SetActive(false);UpdateBackdrop();
                // Keep the current camera and unregistered pose. Re-evaluating the
                // timeline here would discard the exact pose the user is exporting.
                previewCamera.Render();RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();
                string extension=Path.GetExtension(path).ToLowerInvariant();
                byte[] bytes=extension==".jpg" || extension==".jpeg"?image.EncodeToJPG(95):image.EncodeToPNG();
                if(extension!=".png" && extension!=".jpg" && extension!=".jpeg")bytes=DirectXImage.Convert(bytes,extension);
                WriteFile(path,bytes);
            } finally {
                RenderTexture.active=previousActive;
                previewCamera.targetTexture=previousTarget;previewCamera.aspect=previousAspect;
                if(target!=null){target.Release();Destroy(target);}if(image!=null)Destroy(image);
                floor.SetActive(previousGrid);exporting=false;movieHistory.Dispose();UpdateScreenMaterials();UpdateBackdrop();
                (previousClock,previousSample,renderSeconds,renderDelta,syncedSeconds,syncedDelta,outputDelta)=previousClockState;
            }
        }
    }
}
