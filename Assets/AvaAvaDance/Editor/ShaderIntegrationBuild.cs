using UnityEditor;
using UnityEngine.Rendering;
namespace AvaAvaDance.Editor
{
 public static class ShaderIntegrationBuild
 {
  public static void SurfaceFixture()
  {
   ShaderPackBuilder.Build(new[]{"Assets/AvaAvaDance/ShaderFixtures/GrabFixture.shader"},"Builds/GrabFixture.avadshader");
  }
  public static void Packs()
  {
   PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
   PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D11});
   ShaderPackBuilder.ValidateFixtures();
   ShaderPackBuilder.Build(new[]{"Assets/AvaAvaDance/ShaderFixtures/CustomFixture.shader"},"Builds/CustomFixture.avadshader");
  }
 }
}
