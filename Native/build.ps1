param()
$ErrorActionPreference = 'Stop'
$effectDir = Join-Path $PSScriptRoot 'build/effekseer-1808'
if (!(Test-Path -LiteralPath $effectDir)) {
    git clone --depth 1 --filter=blob:none --sparse --branch 1808 https://github.com/effekseer/Effekseer.git $effectDir
    if ($LASTEXITCODE -ne 0) { throw 'Effekseer download failed' }
}
if ((git -C $effectDir rev-parse HEAD) -ne 'b5700082b49bc5f919610880dafddc90211a9a4e') { throw 'Unexpected Effekseer source revision' }
git -C $effectDir sparse-checkout set cmake Dev/Cpp/3rdParty Dev/Cpp/Effekseer Dev/Cpp/EffekseerRendererCommon Dev/Cpp/EffekseerRendererDX11 Dev/Cpp/EffekseerMaterialCompiler
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare Effekseer source' }
cmake -S $PSScriptRoot -B (Join-Path $PSScriptRoot 'build') -A x64 "-DEFFEKSEER_SOURCE_DIR=$effectDir"
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }
cmake --build (Join-Path $PSScriptRoot 'build') --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native compile failed' }
$target = Join-Path $PSScriptRoot '..\Assets\Plugins\x86_64'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaFbx.dll') -Destination $target
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaRenderBridge.dll') -Destination $target
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaWindows.dll') -Destination $target
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaMp4.dll') -Destination $target
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaEffekseer.dll') -Destination $target
