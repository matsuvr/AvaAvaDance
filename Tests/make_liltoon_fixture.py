"""Generate a sphere and a material referencing the selected shader by GUID."""
import io,math,pathlib,tarfile,struct,zlib,sys,re
r=pathlib.Path(__file__).resolve().parents[1]
verts=[]; ids=[]; normals=[]
rows,cols=12,24
for y in range(rows+1):
 for x in range(cols):
  t=math.pi*y/rows;p=2*math.pi*x/cols
  v=(math.sin(t)*math.cos(p),math.cos(t),math.sin(t)*math.sin(p))
  verts.extend(v);normals.extend(v)
for y in range(rows):
 for x in range(cols):
  a=y*cols+x;b=y*cols+(x+1)%cols;c=(y+1)*cols+(x+1)%cols;d=(y+1)*cols+x
  ids.extend([a,b,-c-1,a,c,-d-1])
header=(r/'Native/triangle.fbx').read_text().split('Objects:')[0]
fbx=header+'''Objects: {
 Geometry: 100, "Geometry::Sphere", "Mesh" {
 Vertices: *%d { a: %s }
 PolygonVertexIndex: *%d { a: %s }
 LayerElementNormal: 0 { Version: 101
 Name: ""
 MappingInformationType: "ByVertice"
 ReferenceInformationType: "Direct"
 Normals: *%d { a: %s }
 }
 }
 Model: 200, "Model::Sphere", "Mesh" { Version: 232
 Properties70: { P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0 }
 }
 Material: 300, "Material::SmokeMat", "" { Version: 102
 ShadingModel: "phong"
 MultiLayer: 0
 }
}
Connections: { C: "OO",100,200
 C: "OO",200,0
 C: "OO",300,200
}
'''%(len(verts),','.join(map(str,verts)),len(ids),','.join(map(str,ids)),len(normals),','.join(map(str,normals)))
variant=sys.argv[1] if len(sys.argv)>1 else "custom"
meta=(r/"Assets/AvaAvaDance/ShaderFixtures/CustomFixture.shader.meta") if variant=="custom" else (r/"Builds/LilToonPackProject/Assets/lilToon/Shader"/(variant+".shader.meta"))
guid=re.search(r"guid: ([0-9a-f]{32})",meta.read_text()).group(1)
mat='''%%YAML 1.1
%%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_Name: SmokeMat
  m_Shader: {fileID: 4800000, guid: %s, type: 3}
  m_CustomRenderQueue: -1
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs: []
    m_Ints: []
    m_Floats:
    - _Cull: 2
    - _OutlineWidth: 0.03
    - _UseShadow: 1
    - _ShadowBorder: 0.6
    - _ShadowBlur: 0.05
    - _LightMinLimit: 0.1
    m_Colors:
    - _Color: {r: 0.15, g: 0.65, b: 1, a: 1}
    - _OutlineColor: {r: 0, g: 0, b: 0, a: 1}
    - _ShadowColor: {r: 0.3, g: 0.2, b: 0.5, a: 1}
'''%guid
out=r/"Tests"/("liltoon-"+variant+".unitypackage")
with tarfile.open(out,'w:gz',format=tarfile.USTAR_FORMAT) as t:
 for g,path,data in [('1'*32,'Assets/Test/Sphere.fbx',fbx.encode()),('2'*32,'Assets/Test/SmokeMat.mat',mat.encode())]:
  for name,blob in [('pathname',path.encode()),('asset',data),('asset.meta',('fileFormatVersion: 2\nguid: '+g+'\n').encode())]:
   m=tarfile.TarInfo(g+'/'+name);m.size=len(blob);t.addfile(m,io.BytesIO(blob))
print(out)
