# FBX runtime native plugin

ufbx v0.21.3, upstream commit 83bc7cf44f76bc8622de63b809a42b5d557cd733.
https://github.com/ufbx/ufbx/tree/v0.21.3
Vendored ufbx.c / ufbx.h are unmodified. This project chooses the MIT option in LICENSE.
AvaFbx.dll statically links the C runtime and ufbx; distribute the ufbx LICENSE with every application binary.

Build Windows x64: install CMake and Visual Studio C++ Build Tools; run ./build.ps1.
Smoke test: ./test.ps1. Uses self-authored triangle.fbx and skinned-triangle.fbx (no third-party avatar). Validates triangle geometry, rejection of malformed input, one skin bone, bind offset Y=-1, and normalized vertex weights.

API: AvaAvaDance.Import.FbxNative.Load(byte[] bytes, string name) returns a GameObject hierarchy.
C# data is copied before the native scene is freed. Meshes and materials are destroyed with the root.
Textures can be assigned afterwards using the generated material names. The app uses its bundled AvaAvaDance/Preview shader; keep it in Resources.

Supported initial subset: ASCII/binary FBX via ufbx, nodes/TRS, triangulated geometry, normals, UV0, material slots/names, first skin deformer with top four normalized weights and bind poses.
Not implemented: blend shapes, FBX animation curves, custom shaders, Unity prefab composition/overrides, humanoid Avatar mapping, PhysBones/constraints. Skeleton transforms are retained but no Animator/avatar is synthesized.

Safety: memory-only FBX parse, UFBX_NO_STDIO, external file loading disabled, embedded blobs ignored, 256 MiB input / 512 MiB each result and temp allocator caps, allocation caps, cooperative 15-second loading timeout, 20k nodes / hierarchy depth 256 / 3m expanded triangle vertices including instances / 256 material slots / 1024 skin bones caps. The plugin is not an OS sandbox; native parser bugs remain possible. Never describe it as safe for arbitrary hostile files. No package scripts/DLLs/shader source are executed.

Validation performed: MSVC x64 Release DLL compile; standalone native triangle import and malformed-input rejection. Real skinned avatar and rendered Unity validation are separate integration checks, not implied by these smoke tests.

