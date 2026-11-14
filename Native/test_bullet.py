"""Exercise the shipped Windows x64 Bullet DLL with gravity, contact filters and joints."""
import ctypes as c
import math
from pathlib import Path

dll = c.CDLL(str(Path(__file__).resolve().parents[1] / "Assets/Plugins/x86_64/AvaBullet.dll"))

class Vec(c.Structure):
    _fields_ = [(k, c.c_float) for k in "xyz"]

class Pose(c.Structure):
    _fields_ = [("position", Vec)] + [(k, c.c_float) for k in "xyzw"]

class Body(c.Structure):
    _fields_ = [("pose", Pose), ("size", Vec)] + [
        (k, c.c_float) for k in ("mass", "linear", "angular", "friction", "bounce", "gravity")
    ] + [(k, c.c_int) for k in ("shape", "group", "mask", "restricted")]

def function(name, result, *args):
    f = getattr(dll, "ava_bullet_" + name)
    f.restype = result
    f.argtypes = list(args)
    return f

world_type = c.c_void_p
create = function("create", world_type)
destroy = function("destroy", None, world_type)
body = function("body", c.c_int, world_type, c.POINTER(Body))
step = function("step", None, world_type, c.POINTER(Vec))
read = function("read", None, world_type, c.c_int, c.POINTER(Pose))
drive = function("drive", None, world_type, c.c_int, c.c_int, c.POINTER(Pose))
transport = function("transport", None, world_type, c.c_int, c.c_int, c.POINTER(Pose))
joint = function("joint", c.c_int, world_type, c.c_int, c.c_int, c.POINTER(Pose), c.POINTER(Vec), c.c_float, c.c_float)
allow = function("allow", c.c_int, world_type, c.c_int, c.c_int)

def pose(x, y):
    return Pose(Vec(x, y, 0), 0, 0, 0, 1)

def add(w, shape, x, y, size, mass, group, mask, restricted=False):
    desc = Body(pose(x,y), size, mass, .05, .1, .5, 0, 1, shape, group, mask, restricted)
    index = body(w, c.byref(desc))
    assert index >= 0
    return index

def y(w, index):
    result = Pose()
    read(w, index, c.byref(result))
    return result.position.y

def advance(w, frames):
    for _ in range(frames):
        step(w, c.byref(Vec(0,-9.8,0)))

assert dll.ava_bullet_version() == 325
for _ in range(3):
    w = create()
    assert w
    try:
        floor = add(w, 3, 0, -1, Vec(), 0, 1, 65535)
        sphere = add(w, 0, 0, 2, Vec(.25,0,0), 1, 2, 1)
        box = add(w, 1, 2, 2, Vec(.2,.2,.2), 1, 2, 1)
        capsule = add(w, 2, 4, 2, Vec(.1,.5,0), 1, 2, 1)
        passing = add(w, 0, 6, 2, Vec(.1,0,0), 1, 4, 0)
        anchor = add(w, 0, 9, 2, Vec(.1,0,0), 0, 0, 0)
        hanging = add(w, 0, 9, 1, Vec(.1,0,0), 1, 2, 1)
        assert joint(w, anchor, hanging, c.byref(pose(9,2)), c.byref(Vec(.6,.2,.6)), 1, .7)
        obstacle = add(w, 0, 12, 0, Vec(.7,0,0), 0, 1, 65535, True)
        caught = add(w, 0, 12, 2, Vec(.2,0,0), 1, 2, 1)
        assert allow(w, obstacle, caught)
        lower = add(w, 1, 16, 0, Vec(.5,.3,.5), 2, 8, 1 | 8)
        upper = add(w, 0, 16, 1, Vec(.25,0,0), 1, 8, 1 | 8)
        advance(w, 25)
        assert .5 < y(w,sphere) < 1.7
        advance(w, 155)
        assert abs(y(w,sphere) + .75) < .015
        assert abs(y(w,box) + .8) < .015
        assert abs(y(w,capsule) + .65) < .015
        assert y(w,passing) < -10 and abs(y(w,hanging)-1) < .02
        assert abs(y(w,caught)-.9) < .02
        assert abs(y(w,lower)+.7)<.02 and abs(y(w,upper)+.15)<.02
        drive(w,sphere,1,c.byref(pose(0,3)))
        advance(w,10)
        assert abs(y(w,sphere)-3) < .001
        drive(w,sphere,0,c.byref(pose(0,3)))
        advance(w,45)
        assert y(w,sphere) < 1
    finally:
        destroy(w)
# Full PhysBone immobility should carry a segment at rest through a moving and
# rotating reference frame without the kinematic anchor kicking the segment.
w = create()
try:
    anchor = add(w, 0, 2, 1, Vec(.01,0,0), 0, 0, 0)
    tip = add(w, 0, 2, 0, Vec(.05,0,0), 1, 2, 0)
    assert joint(w, anchor, tip, c.byref(pose(2,1)), c.byref(Vec(.4,0,.4)), 2, 1.5)
    angle = math.radians(4)
    for i in range(1, 91):
        x = 2 + (i-1)*.02
        dx = x+.02 - (x*math.cos(angle)-math.sin(angle))
        dy = 1 - (x*math.sin(angle)+math.cos(angle))
        delta = Pose(Vec(dx,dy,0), 0,0,math.sin(angle/2),math.cos(angle/2))
        transport(w, tip, anchor, c.byref(delta))
        turn = i*angle
        target = Pose(Vec(x+.02,1,0), 0,0,math.sin(turn/2),math.cos(turn/2))
        drive(w, anchor, 1, c.byref(target))
        step(w, c.byref(Vec(0,0,0)))
        result = Pose()
        read(w, tip, c.byref(result))
        assert abs(result.position.x - (target.position.x+math.sin(turn))) < .001
        assert abs(result.position.y - (1-math.cos(turn))) < .001
        assert abs(result.z*target.z+result.w*target.w) > .9999
finally:
    destroy(w)
print("PASS Bullet 3.25: contacts, joints, keyed follow/release and moving PhysBone reference")
