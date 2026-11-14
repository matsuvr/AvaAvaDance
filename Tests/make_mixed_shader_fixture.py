"""Build mixed material regression inputs from the test sphere and a supplied VRM 0."""
import io
import json
from pathlib import Path
import struct
import sys
import tarfile

out, legacy = Path(sys.argv[1]), Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)
model = '1' * 32
with tarfile.open(Path(__file__).parent / 'liltoon-lts.unitypackage') as src:
    fbx = src.extractfile(model + '/asset').read()
    template = src.extractfile('2' * 32 + '/asset').read().decode()

with tarfile.open(out / 'mixed.unitypackage', 'w:gz', format=tarfile.USTAR_FORMAT) as tar:
    def asset(guid, path, data):
        for member, payload in [('pathname', ('Assets/Test/' + path).encode()), ('asset', data)]:
            info = tarfile.TarInfo(guid + '/' + member)
            info.size = len(payload)
            tar.addfile(info, io.BytesIO(payload))

    asset(model, 'Sphere.fbx', fbx)
    refs = []
    for i, (name, shader) in enumerate([
        ('Toon', 'df12117ecd77c31469c224178886498e'), ('Legacy', '1a97144e4ad27a04aafd70f7b915cedb'),
        ('Other', '50a07f2e8b7b8084a8fef27aa7bb9f6d'), ('Unused', 'f' * 32)
    ], 2):
        guid = str(i) * 32
        mat = template.replace('SmokeMat', name).replace('df12117ecd77c31469c224178886498e', shader)
        asset(guid, name + '.mat', mat.encode())
        if name != 'Unused':
            refs.append('  - {fileID: 2100000, guid: ' + guid + ', type: 2}')
    prefab = ('--- !u!1 &1\nGameObject:\n  m_Name: Sphere\n--- !u!33 &2\nMeshFilter:\n'
              '  m_GameObject: {fileID: 1}\n  m_Mesh: {fileID: 4300000, guid: ' + model + ', type: 3}\n'
              '--- !u!23 &3\nMeshRenderer:\n  m_GameObject: {fileID: 1}\n  m_Materials:\n' + '\n'.join(refs) + '\n')
    asset('6' * 32, 'Sphere.prefab', prefab.encode())

# Preserve every buffer, index and original extension. Replace one material declaration
# to exercise VRM 0's named shader, including installation after the model is open.
data = legacy.read_bytes()
size, = struct.unpack_from('<I', data, 12)
doc = json.loads(data[20:20+size])
slot = doc['extensions']['VRM']['materialProperties'][0]
slot['shader'] = 'AvaAvaDance/CustomFixture'
slot['vectorProperties']['_Color'] = [.2, .4, .6, .8]
slot['renderQueue'] = 2457
payload = json.dumps(doc, ensure_ascii=False, separators=(',', ':')).encode()
payload += b' ' * (-len(payload) % 4)
tail = data[20+size:]
(out / 'legacy-mixed.vrm').write_bytes(struct.pack('<4sIII4s', b'glTF', 2, 20+len(payload)+len(tail),
                                                   len(payload), b'JSON') + payload + tail)
