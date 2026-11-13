# Windows runtime plugins

The Windows x64 player loads its native libraries from `Assets/Plugins/x86_64/`. AvaClassicUi hosts tool windows on their own STA threads. The editing windows forward keyboard, IME and pointer events to their UI Toolkit panels. AvaRenderBridge presents their textures on Direct3D 11 and submits surface draw commands. The application wrappers use the project's [MIT license](../LICENSE).

## Build

Install CMake and Visual Studio C++ Build Tools with a Windows SDK. Run these commands from the repository root.

```powershell
python Tools/restore_inputs.py
powershell -File Native/build.ps1
```

The script builds Release DLLs with the static MSVC runtime and copies them into the Unity project. The render bridge uses the Unity Editor's PluginAPI headers for graphics-device reset and shutdown notifications. Set `UNITY_PLUGIN_API` to the `Editor/Data/PluginAPI` directory when the Editor is in a custom location. Its first run fetches the Bullet sources with Git into `Native/build/bullet-3.25/` and verifies the selected revision.

## FBX import

`AvaFbx.dll` links `ufbx.c` and `ufbx.h`, fetched during build preparation from ufbx v0.21.3, revision `83bc7cf44f76bc8622de63b809a42b5d557cd733`. The project uses the MIT option. [Upstream license](../ThirdPartyNotices/ufbx-LICENSE.txt) · [Source revision](https://github.com/ufbx/ufbx/tree/83bc7cf44f76bc8622de63b809a42b5d557cd733)

`AvaAvaDance.Import.FbxNative.Load(byte[] bytes, string name)` builds a Unity GameObject hierarchy from ASCII or binary FBX data. The importer reads node transforms, triangulated meshes, normals, UV0, material slots, bind poses and the four largest skin weights per vertex. Blend shapes use position offsets and positive in-between weights, with limits of 512 channels per mesh, 128 frames per channel and 64 million expanded vertex-frame pairs per model. Geometry and skin data are copied before the native scene is freed.

The parser runs in the player process and reads from memory with `UFBX_NO_STDIO`. Input is capped at 256 MiB. The result and temporary allocators each have a 512 MiB limit. Additional limits cover parse time (15 seconds, checked during parsing), nodes (20,000), hierarchy depth (256), expanded triangle vertices (3 million), material slots (256) and skin bones (1,024).

## Bullet physics

`AvaBullet.dll` links BulletDynamics, BulletCollision and LinearMath from Bullet 3.25. The C ABI creates a discrete dynamics world, adds shapes and spring joints, drives kinematic bodies and samples dynamic transforms at 60 Hz. The build includes Bullet's `btMiniSDF` implementation derived from Discregrid. [Bullet source revision](https://github.com/bulletphysics/bullet3/tree/2c204c49e56ed15ec5fcfa71d199ab6d6570b3f5) · [Bullet license](../ThirdPartyNotices/Bullet-LICENSE.txt) · [Discregrid license](../ThirdPartyNotices/Discregrid-LICENSE.txt)

## MP4 export

`AvaMp4.dll` converts bottom-up RGBA frames to NV12 and uses the Windows Media Foundation sink writer to encode H.264 video and optional stereo AAC. The player controls timeline sampling, audio alignment and output-file replacement. [Microsoft Media Foundation sink writer](https://learn.microsoft.com/en-us/windows/win32/medfound/using-the-sink-writer)

## Tests

Run the following commands from the repository root after building the plugins.

```powershell
powershell -File Native/test.ps1
python Tests/make_liltoon_fixture.py custom
python Native/test_winding.py
python Native/test_bullet.py
```

The FBX tests use the triangle files in this directory and a generated sphere. They cover geometry, malformed input, skinning, bind offsets and triangle winding. The Bullet tests exercise contacts, collision masks, joints and switching between animated and simulated motion.
