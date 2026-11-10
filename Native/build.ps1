param()
$ErrorActionPreference = 'Stop'
$bulletDir = Join-Path $PSScriptRoot 'build/bullet-3.25'
$bulletRevision = '2c204c49e56ed15ec5fcfa71d199ab6d6570b3f5'
if (!(Test-Path -LiteralPath $bulletDir)) {
    git clone --depth 1 --filter=blob:none --sparse --branch 3.25 https://github.com/bulletphysics/bullet3.git $bulletDir
    if ($LASTEXITCODE -ne 0) { throw 'Bullet download failed' }
}
if ((git -C $bulletDir rev-parse HEAD) -ne $bulletRevision) { throw 'Unexpected Bullet source revision' }
git -C $bulletDir sparse-checkout set src/LinearMath src/BulletCollision src/BulletDynamics
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare Bullet source' }
cmake -S $PSScriptRoot -B (Join-Path $PSScriptRoot 'build') -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }
cmake --build (Join-Path $PSScriptRoot 'build') --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native compile failed' }
$target = Join-Path $PSScriptRoot '..\Assets\Plugins\x86_64'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaFbx.dll') -Destination $target
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaMp4.dll') -Destination $target
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaBullet.dll') -Destination $target
