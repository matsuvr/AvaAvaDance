"""Build the redistributable FBX leg/secondary-bone and stereo WAV used by -avaProductionSmoke."""
import io
import math
from pathlib import Path
import struct
import tarfile
import wave

root = Path(__file__).resolve().parents[1]
out = root / "Builds" / "ProductionTest"
out.mkdir(parents=True, exist_ok=True)
fbx = (root / "Native" / "skinned-triangle.fbx").read_text()
links = [(701, "LeftUpperLeg", "0,1,0"), (702, "LeftLowerLeg", "0,-0.5,0.05"),
         (703, "LeftFoot", "0,-0.5,-0.05"), (704, "Pendant", "0,0.1,0")]
nodes = []
edges = []
parent = 0
for index, name, position in links:
    nodes.append(f''' Model: {index}, "Model::{name}", "LimbNode" {{
 Version: 232
 Properties70: {{ P: "Lcl Translation", "Lcl Translation", "", "A",{position} }}
 }}
''')
    edges.append(f' C: "OO",{index},{parent}')
    parent = index
fbx = fbx.replace(" Model: 300,", "".join(nodes) + " Model: 300,")
fbx = fbx.replace(' C: "OO",300,0', "\n".join(edges + [f' C: "OO",300,{parent}']))
fbx = fbx.replace(" Model: 300,", ''' Model: 705, "Model::HairIgnored", "LimbNode" {
 Properties70: { P: "Lcl Translation", "Lcl Translation", "", "A",0.2,0.1,0 }
 }
 Model: 300,''').replace(' C: "OO",300,704', ' C: "OO",705,704\n C: "OO",300,704')

# Deliberately use a different prefab basename and a chain name unrelated to hair.
hierarchy = [(10, "Rig", 0, "0,0,0"), *[(i, name, 10 if i==701 else i-1, pos)
             for i, name, pos in links], (300, "TestBone", 704, "0,1,0"),
             (705, "HairIgnored", 704, "0.2,0.1,0")]
prefab = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"
for i, name, parent, pos in hierarchy:
    x, y, z = pos.split(',')
    children = [j for j, _, p, _ in hierarchy if p==i]
    prefab += f"""--- !u!1 &{1000+i}
GameObject:
  m_Name: {name}
  m_IsActive: 1
--- !u!4 &{i}
Transform:
  m_GameObject: {{fileID: {1000+i}}}
  m_Father: {{fileID: {parent}}}
  m_LocalPosition: {{x: {x}, y: {y}, z: {z}}}
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_Children:""" + (''.join(f"\n  - {{fileID: {j}}}" for j in children) or " []") + "\n"
prefab += """--- !u!137 &9100
SkinnedMeshRenderer:
  m_Mesh: {fileID: 4300000, guid: 11111111111111111111111111111111, type: 3}
--- !u!114 &9101
MonoBehaviour:
  m_GameObject: {fileID: 1010}
  m_Enabled: 1
  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5, type: 3}
  version: 1
  rootTransform: {fileID: 704}
  ignoreTransforms:
  - {fileID: 705}
  endpointPosition: {x: 0, y: 0.07, z: 0}
  pull: 0.68
  pullCurve:
    m_Curve:
    - serializedVersion: 3
      time: 0
      value: 0.5
    - serializedVersion: 3
      time: 1
      value: 1
  spring: 0.3
  gravity: 0.65
  immobile: 0.4
  limitType: 1
  maxAngleX: 12
  radius: 0.012
  colliders:
  - {fileID: 9102}
--- !u!114 &9102
MonoBehaviour:
  m_GameObject: {fileID: 1701}
  m_Enabled: 1
  m_Script: {fileID: -1631200402, guid: 2a2c05204084d904aa4945ccff20d8e5, type: 3}
  shapeType: 0
  radius: 0.025
  position: {x: 0.3, y: 0, z: 0}
--- !u!114 &9103
MonoBehaviour:
  m_GameObject: {fileID: 1705}
  m_Enabled: 0
  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5, type: 3}
  rootTransform: {fileID: 0}
  endpointPosition: {x: 0, y: 0.25, z: 0}
"""
with tarfile.open(out / "rig.unitypackage", "w:gz", format=tarfile.USTAR_FORMAT) as archive:
    for guid, path, payload in [("1"*32, "Rig.fbx", fbx), ("2"*32, "Dancing Avatar.prefab", prefab),
                                ("3"*32, "Avatar.controller", "controller test data"),
                                ("4"*32, "MetadataOnlyFolder", None)]:
        members = {"pathname": ("Assets/Test/"+path).encode(), "asset.meta": b"fileFormatVersion: 2\n"}
        if payload is not None:
            members["asset"] = payload.encode()
        for name, data in members.items():
            member = tarfile.TarInfo(guid + "/" + name)
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))
with wave.open(str(out / "beat.wav"), "wb") as wav:
    wav.setparams((2, 2, 24000, 0, "NONE", "NONE"))
    wav.writeframes(b"".join(struct.pack("<hh", *(int(5000 * math.sin(2 * math.pi * hz * i / 24000))
                    for hz in (440, 660))) for i in range(48000)))
print(out)
