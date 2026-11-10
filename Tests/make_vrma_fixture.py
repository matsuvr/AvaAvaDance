"""Generate a one-second VRMA with independently verifiable body, face and gaze."""
import json
import math
import struct
import sys
from pathlib import Path


def make_clip(path):
    nodes, bones, views, accessors, channels, samplers = [], {}, [], [], [], []
    binary = bytearray()

    def node(position, parent=None, bone=None):
        index = len(nodes)
        nodes.append(dict(name=f"joint_{index}", translation=position))
        if parent is not None:
            nodes[parent].setdefault("children", []).append(index)
        if bone:
            bones[bone] = dict(node=index)
        return index

    def accessor(rows, shape):
        values = [v for row in rows for v in row]
        raw = struct.pack(f"<{len(values)}f", *values)
        views.append(dict(buffer=0, byteOffset=len(binary), byteLength=len(raw)))
        binary.extend(raw)
        accessors.append(dict(bufferView=len(views)-1, componentType=5126, count=len(rows), type=shape))
        return len(accessors)-1

    time = accessor([(0,), (.5,), (1,)], "SCALAR")
    accessors[time].update(min=[0], max=[1])

    def channel(target, prop, mode, values):
        out = accessor(values, "VEC4" if prop == "rotation" else "VEC3")
        channels.append(dict(sampler=len(samplers), target=dict(node=target, path=prop)))
        samplers.append(dict(input=time, output=out, interpolation=mode))

    hips = node([0, 1, 0], bone="hips")
    spine = node([0, .2, 0], hips, "spine")
    head = node([0, .4, 0], spine, "head")
    for side, sign in [("left", 1), ("right", -1)]:
        arm = node([sign*.15, .25, 0], spine, side+"UpperArm")
        elbow = node([sign*.3, 0, 0], arm, side+"LowerArm")
        node([sign*.25, 0, 0], elbow, side+"Hand")
        leg = node([sign*.12, -.1, 0], hips, side+"UpperLeg")
        knee = node([0, -.45, 0], leg, side+"LowerLeg")
        node([0, -.45, 0], knee, side+"Foot")
    blink = node([0, 0, 0])
    gaze = node([0, 0, 0])
    zero = [0, 0, 0]
    channel(hips, "translation", "CUBICSPLINE",
            [zero, [0, 1, 0], zero, zero, [.2, 1, 0], zero, zero, [0, 1, 0], zero])
    def yaw(degrees):
        a = math.radians(degrees)/2
        return [0, math.sin(a), 0, math.cos(a)]
    nodes[head]["rotation"] = yaw(20)
    channel(head, "rotation", "LINEAR", [yaw(20), yaw(80), yaw(20)])
    channel(blink, "translation", "STEP", [zero, [1, 0, 0], zero])
    channel(gaze, "rotation", "LINEAR", [yaw(0), yaw(30), yaw(0)])
    extension = dict(specVersion="1.0", humanoid=dict(humanBones=bones),
                     expressions=dict(preset=dict(blink=dict(node=blink))), lookAt=dict(node=gaze))
    document = dict(asset=dict(version="2.0"), scene=0, scenes=[dict(nodes=[hips, blink, gaze])],
                    nodes=nodes, extensionsUsed=["VRMC_vrm_animation"],
                    extensions=dict(VRMC_vrm_animation=extension),
                    animations=[dict(channels=channels, samplers=samplers)], accessors=accessors,
                    bufferViews=views, buffers=[dict(byteLength=len(binary))])
    encoded = json.dumps(document, separators=(",", ":")).encode()
    encoded += b" " * (-len(encoded) % 4)
    path.write_bytes(struct.pack("<4sII", b"glTF", 2, 28+len(encoded)+len(binary)) +
                     struct.pack("<I4s", len(encoded), b"JSON")+encoded +
                     struct.pack("<I4s", len(binary), b"BIN\0")+binary)


if __name__ == "__main__":
    make_clip(Path(sys.argv[1]))
