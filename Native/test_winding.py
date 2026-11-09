import ctypes as C,tarfile,pathlib,math
r=pathlib.Path(__file__).resolve().parents[1]
dll=C.CDLL(str(r/'Assets/Plugins/x86_64/AvaFbx.dll'))
dll.ava_load.argtypes=[C.c_void_p,C.c_int,C.c_char_p,C.c_int];dll.ava_load.restype=C.c_void_p
for n in ['ava_nodes','ava_vertices']:
 getattr(dll,n).argtypes=[C.c_void_p]+([C.c_int] if n=='ava_vertices' else [])
dll.ava_mesh.argtypes=[C.c_void_p,C.c_int,C.POINTER(C.c_float),C.POINTER(C.c_int),C.POINTER(C.c_int),C.c_int]
dll.ava_transform.argtypes=[C.c_void_p,C.c_int,C.POINTER(C.c_float)]
dll.ava_free.argtypes=[C.c_void_p]
with tarfile.open(r/'Tests/liltoon-lts.unitypackage') as t:data=t.extractfile('1'*32+'/asset').read()
e=C.create_string_buffer(2048);s=dll.ava_load(data,len(data),e,len(e));assert s,e.value
try:
 for n in range(dll.ava_nodes(s)):
  tr=(C.c_float*10)();dll.ava_transform(s,n,tr);print('node',n,'TRS',list(tr))
  vc=dll.ava_vertices(s,n)
  if not vc:continue
  v=(C.c_float*(vc*12))();b=(C.c_int*(vc*4))();m=(C.c_int*vc)();assert dll.ava_mesh(s,n,v,b,m,vc)==vc
  positive=negative=degenerate=0
  for i in range(0,vc,3):
   a=list(v[i*12:i*12+3]);u=[v[(i+1)*12+j]-a[j] for j in range(3)];w=[v[(i+2)*12+j]-a[j] for j in range(3)]
   cross=[u[1]*w[2]-u[2]*w[1],u[2]*w[0]-u[0]*w[2],u[0]*w[1]-u[1]*w[0]]
   normal=list(v[i*12+3:i*12+6]);dot=sum(cross[j]*normal[j] for j in range(3))
   if dot>1e-7:positive+=1
   elif dot < -1e-7:negative+=1
   else:degenerate+=1
  print('cross.dot(normal): positive',positive,'negative',negative,'degenerate',degenerate)
  assert positive>0 and negative==0, 'Winding disagrees with explicit outward FBX normals'
  print('PASS: nondegenerate sphere face winding agrees with outward normals')
finally:dll.ava_free(s)

