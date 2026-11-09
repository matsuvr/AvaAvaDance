param()
$ErrorActionPreference = 'Stop'
cmake -S $PSScriptRoot -B (Join-Path $PSScriptRoot 'build') -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }
cmake --build (Join-Path $PSScriptRoot 'build') --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native compile failed' }
$target = Join-Path $PSScriptRoot '..\Assets\Plugins\x86_64'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\Release\AvaFbx.dll') -Destination $target
