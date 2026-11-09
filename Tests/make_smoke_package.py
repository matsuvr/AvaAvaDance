import gzip,tarfile,io,pathlib
root=pathlib.Path(__file__).resolve().parents[1]
source=root/'Native'/'skinned-triangle.fbx'
out=root/'Tests'/'triangle.unitypackage'
with tarfile.open(out,'w:gz',format=tarfile.USTAR_FORMAT) as t:
 for name,data in [('pathname',b'Assets/Test/Triangle.fbx'),('asset',source.read_bytes()),('asset.meta',b'fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n')]:
  member=tarfile.TarInfo('11111111111111111111111111111111/'+name);member.size=len(data);t.addfile(member,io.BytesIO(data))
print(out)
