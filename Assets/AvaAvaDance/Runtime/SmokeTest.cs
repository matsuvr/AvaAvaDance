using System;
using System.Collections;
using System.IO;
using System.Linq;
using AvaAvaDance.Import;
using UnityEngine;
namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        private IEnumerator Start()
        {
            var args=Environment.GetCommandLineArgs();
            int input=Array.IndexOf(args,"-avaSmokePackage"), output=Array.IndexOf(args,"-avaSmokeOutput");
            if(input<0 || input+1>=args.Length || output<0 || output+1>=args.Length) yield break;
            string directory=args[output+1]; Directory.CreateDirectory(directory);
            for(int i=0;i<20;i++) yield return null;
            string result;
            try
            {
                var package=UnityPackageReader.Read(args[input+1]);
                var model=package.First(a=>a.HasAsset&&a.Path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase));
                ImportModel(model,package);
                Debug.Log("AVA_ROOT_RECT " + GetComponent<UnityEngine.UIElements.UIDocument>().rootVisualElement.worldBound + " VIEW=" + viewport.worldBound);
                int renderers=avatar.GetComponentsInChildren<Renderer>().Length;
                if(renderers<1) throw new Exception("No renderers");
                var skin=avatar.GetComponentInChildren<SkinnedMeshRenderer>();
                if(skin!=null) { var before=new Mesh(); var after=new Mesh(); skin.BakeMesh(before); var rotation=skin.bones[0].localRotation; skin.bones[0].localRotation=Quaternion.Euler(0,0,25)*rotation; skin.BakeMesh(after); bool changed=false; for(int v=0;v<before.vertexCount;v++) if(Vector3.Distance(before.vertices[v],after.vertices[v])>.001f) changed=true; skin.bones[0].localRotation=rotation; Destroy(before); Destroy(after); if(!changed) throw new Exception("Skin did not deform when bone rotated"); }
                SetFrame(12);
                result="PASS standalone package->FBX->Renderer; renderers="+renderers+"; skins="+avatar.GetComponentsInChildren<SkinnedMeshRenderer>().Length+"; frame=12";
            }
            catch(Exception ex) { result="FAIL "+ex; Debug.LogException(ex); }
            for(int i=0;i<20;i++) yield return null;
            yield return new WaitForEndOfFrame();
            previewCamera.Render();
            var previous=RenderTexture.active; RenderTexture.active=previewTexture;
            var preview=new Texture2D(previewTexture.width,previewTexture.height,TextureFormat.RGB24,false); preview.ReadPixels(new Rect(0,0,previewTexture.width,previewTexture.height),0,0); preview.Apply();
            File.WriteAllBytes(Path.Combine(directory,"preview.png"),preview.EncodeToPNG()); Destroy(preview); RenderTexture.active=previous;
            var image=ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(directory,"standalone.png"),image.EncodeToPNG()); Destroy(image);
            File.WriteAllText(Path.Combine(directory,"result.txt"),result);
            Debug.Log(result); Application.Quit(result.StartsWith("PASS")?0:1);
        }
    }
}
