$ErrorActionPreference='Stop'
$source=@"
using System;using System.Text;using System.Runtime.InteropServices;
public static class NativeSmoke {
[DllImport(@"$PSScriptRoot\build\Release\AvaFbx.dll",CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr ava_load(byte[] b,int s,StringBuilder e,int c);
[DllImport(@"$PSScriptRoot\build\Release\AvaFbx.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int ava_nodes(IntPtr p);
[DllImport(@"$PSScriptRoot\build\Release\AvaFbx.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int ava_vertices(IntPtr p,int n);
[DllImport(@"$PSScriptRoot\build\Release\AvaFbx.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int ava_mesh(IntPtr p,int n,[Out]float[] v,[Out]int[] b,[Out]int[] m,int c);
[DllImport(@"$PSScriptRoot\build\Release\AvaFbx.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int ava_bones(IntPtr p,int n);
[DllImport(@"$PSScriptRoot\build\Release\AvaFbx.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int ava_bone(IntPtr p,int n,int b,[Out]float[] bind);
[DllImport(@"$PSScriptRoot\build\Release\AvaFbx.dll",CallingConvention=CallingConvention.Cdecl)] public static extern void ava_free(IntPtr p);
}
"@
Add-Type $source
$b=[IO.File]::ReadAllBytes("$PSScriptRoot\triangle.fbx");$e=[Text.StringBuilder]::new(2048)
$p=[NativeSmoke]::ava_load($b,$b.Length,$e,2048)
if($p -eq [IntPtr]::Zero){throw "FBX failed: $e"}
try {
 $total=0
 for($i=0;$i -lt [NativeSmoke]::ava_nodes($p);$i++) {
  $vc=[NativeSmoke]::ava_vertices($p,$i);if(!$vc){continue};$v=New-Object float[] ($vc*12);$bones=New-Object int[] ($vc*4);$m=New-Object int[] $vc
  if([NativeSmoke]::ava_mesh($p,$i,$v,$bones,$m,$vc) -ne $vc){throw 'mesh failed'}
    foreach($value in $v){if([float]::IsNaN($value) -or [float]::IsInfinity($value)){throw 'Nonfinite vertex attribute'}}
  Write-Output ('Triangle positions: ' + (($v[0..2] -join ',')+' / '+($v[12..14] -join ',')+' / '+($v[24..26] -join ',')))
  $total+=$vc
 }
 if($total -ne 3){throw "Expected 3 vertices, got $total"}
} finally {[NativeSmoke]::ava_free($p)}
$bad=[Text.Encoding]::UTF8.GetBytes('not an FBX');$p=[NativeSmoke]::ava_load($bad,$bad.Length,$e,2048)
if($p -ne [IntPtr]::Zero){[NativeSmoke]::ava_free($p);throw 'Invalid FBX accepted'}
Write-Output 'PASS: native ASCII FBX triangle import, triangulation, malformed data rejection'

$b=[IO.File]::ReadAllBytes("$PSScriptRoot\skinned-triangle.fbx");$p=[NativeSmoke]::ava_load($b,$b.Length,$e,2048)
if($p -eq [IntPtr]::Zero){throw "Skinned FBX failed: $e"}
try {
 $found=$false
 for($i=0;$i -lt [NativeSmoke]::ava_nodes($p);$i++) {
  $vc=[NativeSmoke]::ava_vertices($p,$i);if(!$vc){continue};$found=$true
  if([NativeSmoke]::ava_bones($p,$i) -ne 1){throw 'Expected one skin cluster'}
  $bind=New-Object float[] 12;$bone=[NativeSmoke]::ava_bone($p,$i,0,$bind)
  if($bone -lt 0 -or [Math]::Abs($bind[10]+1) -gt 0.001){throw "Unexpected bone or bind Y offset: $bone $($bind[10])"}
  $v=New-Object float[] ($vc*12);$bones=New-Object int[] ($vc*4);$m=New-Object int[] $vc
  if([NativeSmoke]::ava_mesh($p,$i,$v,$bones,$m,$vc) -ne 3){throw 'Expected 3 skinned vertices'}
  for($j=0;$j -lt 3;$j++){if($bones[$j*4] -ne 0 -or [Math]::Abs($v[$j*12+8]-1) -gt 0.001){throw 'Unexpected skin weight'}}
 }
 if(!$found){throw 'No skinned mesh found'}
} finally {[NativeSmoke]::ava_free($p)}
Write-Output 'PASS: native skinned FBX, bone reference, bind Y=-1, all weights=1'
