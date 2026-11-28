param(
    [Parameter(Mandatory=$true)][string]$PlayerDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$player = (Resolve-Path -LiteralPath $PlayerDirectory).Path.TrimEnd('\')
$output = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)).TrimEnd('\')
if ($output -eq $player -or $output.StartsWith($player + '\', [StringComparison]::OrdinalIgnoreCase) -or $player.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The native output and the engine staging directory must be separate.'
}
foreach ($item in 'UnityPlayer.dll','AvaAvaDance_Data','MonoBleedingEdge') {
    if (!(Test-Path -LiteralPath (Join-Path $player $item))) { throw "Missing Unity build input: $item" }
}
$parentDir = Split-Path -Parent $output
$leaf = Split-Path -Leaf $output
if (!$parentDir -or !$leaf) { throw 'Specify an application output directory.' }
New-Item -ItemType Directory -Force -Path $parentDir | Out-Null
$nonce = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $parentDir "$leaf.stage-$nonce"
$backup = Join-Path $parentDir "$leaf.previous-$nonce"
$marker = '.ava-desktop-build'

function Get-BuildSignature([string]$folder) {
    # Preserve previous builds which have acquired user files or edits.
    $lines = Get-ChildItem -LiteralPath $folder -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        if ($_.FullName -ne (Join-Path $folder $marker)) {
            $hash = [Security.Cryptography.SHA256]::Create()
            $stream = [IO.File]::OpenRead($_.FullName)
            try { $_.FullName.Substring($folder.Length + 1) + ' ' + [BitConverter]::ToString($hash.ComputeHash($stream)) }
            finally { $stream.Dispose(); $hash.Dispose() }
        }
    }
    $digest = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))) }
    finally { $digest.Dispose() }
}
function Remove-GeneratedDirectory([string]$folder) {
    $resolved = (Resolve-Path -LiteralPath $folder).Path
    if (($resolved -ne $stage -and $resolved -ne $backup) -or (Split-Path -Parent $resolved) -ne $parentDir) {
        throw "Unexpected build cleanup path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Force -Recurse
}
$lock = [IO.FileStream]::new((Join-Path $parentDir "$leaf.build-lock"), [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    # Extract CoreCLR and WinUI into the bundle cache. Mono probes beside the exe,
    # where a loose CoreCLR mscorlib.dll would shadow the Unity runtime assembly.
    dotnet publish (Join-Path $PSScriptRoot 'AvaAvaDance.csproj') -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true -p:IncludeAllContentForSelfExtract=true -p:PublishSingleFile=true -o $stage --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
    Get-ChildItem -LiteralPath $player | Where-Object { $_.Name -ne 'AvaAvaDance.exe' -and $_.Name -notlike '*_DoNotShip' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stage -Force -Recurse
    }
    [IO.File]::WriteAllText((Join-Path $stage $marker), (Get-BuildSignature $stage))
    $replaceable = $false
    if (Test-Path -LiteralPath $output) {
        $current = Get-Item -LiteralPath $output
        if (!$current.PSIsContainer -or $current.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Invalid existing application directory: $output"
        }
        if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) {
            if (!(Test-Path -LiteralPath (Join-Path $output 'UnityPlayer.dll')) -or
                !(Test-Path -LiteralPath (Join-Path $output 'AvaAvaDance_Data'))) { throw "Not an AvaAvaDance build: $output" }
            $prior = Join-Path $output $marker
            if ((Test-Path -LiteralPath $prior) -and
                !(Get-ChildItem -LiteralPath $output -Force -Recurse -Attributes ReparsePoint | Select-Object -First 1)) {
                $replaceable = (Get-BuildSignature $output) -eq [IO.File]::ReadAllText($prior)
            }
        } else { $replaceable = $true }
        Move-Item -LiteralPath $output -Destination $backup
    }
    try { Move-Item -LiteralPath $stage -Destination $output }
    catch {
        if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $output }
        throw
    }
    if (Test-Path -LiteralPath $backup) {
        if ($replaceable) { Remove-GeneratedDirectory $backup }
        else { Write-Output "Previous application preserved at $backup" }
    }
    Write-Output (Join-Path $output 'AvaAvaDance.exe')
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-GeneratedDirectory $stage }
    $lock.Dispose()
}
