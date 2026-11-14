"""Generate original test motions, rest poses and a two-pass surface."""
import json
import struct
import zlib
from pathlib import Path

root = Path(__file__).resolve().parents[2]
out = root / "Builds" / "ExtensionFixtures"
out.mkdir(parents=True, exist_ok=True)


def write_json(name, value):
    (out / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def fixed(text, count):
    return text.encode("cp932").ljust(count, b"\0")


row = fixed("センター", 15) + struct.pack("<I7f", 7, 1, 2, 3, 0, 0, 0, 1) + bytes(64)
(out / "motion.vmd").write_bytes(fixed("Vocaloid Motion Data 0002", 30) + fixed("trial", 20)
                                + struct.pack("<I", 1) + row + bytes(20))
for name, ratio in [("source", 1), ("target", 2)]:
    joints = [{"name": role, "role": role, "parent": parent, "x": 0, "y": y * ratio, "z": 0, "qw": 1}
              for role, parent, y in [("hips", -1, 1), ("leftUpperLeg", 0, 1),
                                      ("leftLowerLeg", 1, .5), ("leftFoot", 2, 0)]]
    write_json(name + ".avarig", {"format": "AvaAvaDance rig 1", "schemaVersion": 1, "name": name,
                                  "metersPerMotionUnit": 1, "joints": joints})
write_json("sizing.json", {"people": [{"Motion": "motion.vmd", "Source": "source.avarig",
                                      "Target": "target.avarig", "Movement": 1, "Output": "sized.vmd"}]})
(out / "paint.fx").write_text(
    'float Standard:STANDARDSGLOBAL<string ScriptClass="sceneorobject";>=0.8;\n'
    'texture Tmp:RENDERCOLORTARGET<float2 ViewportRatio={0.5,0.5};>;\n'
    'sampler2D Reader=sampler_state{Texture=<Tmp>;};\n'
    'float4 Fill():SV_Target{return float4(1,0,1,1);}\n'
    'float4 Read(float2 uv:TEXCOORD0):SV_Target{return tex2D(Reader,uv);}\n'
    'technique Main<string Script="RenderColorTarget=Tmp;Pass=A;RenderColorTarget=;Pass=B;";>{\n'
    'pass A<string Script="Draw=Buffer;";>{PixelShader=compile ps_3_0 Fill();}\n'
    'pass B{PixelShader=compile ps_3_0 Read();}}', encoding="utf-8")
def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


def frame_control(sequence):
    return struct.pack(">IIIIIHHBB", sequence, 1, 1, 0, 0, 1, 10, 0, 0)


first_pixel = zlib.compress(b"\0\xff\0\0\xff")
second_pixel = zlib.compress(b"\0\0\xff\0\xff")
apng = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
        + chunk(b"acTL", struct.pack(">II", 2, 0)) + chunk(b"fcTL", frame_control(0))
        + chunk(b"IDAT", first_pixel) + chunk(b"fcTL", frame_control(1))
        + chunk(b"fdAT", struct.pack(">I", 2) + second_pixel) + chunk(b"IEND", b""))
(out / "blink.png").write_bytes(apng)
# Two 1x1 indexed GIF frames with the same tenth-second delays.
gif = (b"GIF89a\1\0\1\0\x80\0\0\xff\0\0\0\xff\0" +
       b"\x21\xf9\x04\0\x0a\0\0\0\x2c\0\0\0\0\1\0\1\0\0\2\2\x44\1\0" +
       b"\x21\xf9\x04\0\x0a\0\0\0\x2c\0\0\0\0\1\0\1\0\0\2\2\x4c\1\0\x3b")
(out / "blink.gif").write_bytes(gif)
for ext in ("png", "gif"):
    image = "blink." + ext
    fx = ext + ".fx"
    (out / fx).write_text('texture T:ANIMATEDTEXTURE<string ResourceName="' + image + '";>; '
                          'sampler2D S=sampler_state{Texture=<T>;}; '
                          'float4 Paint(float2 uv:TEXCOORD):SV_Target{return tex2D(S,uv);}'
                          'technique M{pass P{PixelShader=compile ps_3_0 Paint();}}', encoding="utf-8")
    for frame, color in [(0, (255, 0, 0)), (4, (0, 255, 0))]:
        write_json(f"{ext}-{frame}.json", {"package": "../../Tests/triangle.unitypackage", "fx": fx,
                   "image": image, "frame": frame, "red": color[0], "green": color[1], "blue": color[2]})

for config in ("effect.json", "mme.json"):
    write_json(config, {"package": "../../Tests/triangle.unitypackage", "fx": "paint.fx",
                        "red": 255, "green": 0, "blue": 255})
(out / "values.fx").write_text(
    'texture T:ANIMATEDTEXTURE<string ResourceName="blink.png";>; '
    'float4 Colors[1]:TEXTUREVALUE<string TextureName="T";>; '
    'float4 Read():SV_Target{return Colors[0];} technique Main{pass P{PixelShader=compile ps_3_0 Read();}}',
    encoding="utf-8")
for dim in ("Cube", "3D"):
    cube = dim == "Cube"
    head = [124, 0x100f | (0 if cube else 0x800000), 1, 1, 4, 0 if cube else 2, 1] + [0] * 11
    head += [32, 0x41, 0, 32, 0xff0000, 0xff00, 0xff, 0xff000000, 0x1008, 0xfe00 if cube else 0x200000, 0, 0, 0]
    colors = bytes([255, 0, 255, 255]) * (6 if cube else 2)
    image = dim + ".dds"
    (out / image).write_bytes(b"DDS " + struct.pack("<31I", *head) + colors)
    fx = dim + ".fx"
    intrinsic = "texCUBE" if cube else "tex3D"
    (out / fx).write_text('texture T<string ResourceType="' + dim + '";string ResourceName="' + image + '";>; '
                          'sampler S=sampler_state{Texture=<T>;}; float4 P():COLOR{return ' + intrinsic + '(S,float3(.5,.5,.5));}'
                          'technique Main{pass A{PixelShader=compile ps_3_0 P();}}', encoding="utf-8")
    write_json(dim + ".json", {"package": "../../Tests/triangle.unitypackage", "image": image, "fx": fx, "red": 255, "green": 0, "blue": 255})
(out / "seek.fx").write_text(
    'float Cursor:CONTROLOBJECT<string Name="(self)";>;'
    'texture T:ANIMATEDTEXTURE<string ResourceName="blink.png";string SeekVariable="Cursor";float Speed=0.15;>;'
    'sampler2D S=sampler_state{Texture=<T>;};float4 P(float2 uv:TEXCOORD):COLOR{return tex2D(S,uv);}'
    'technique Main{pass A{PixelShader=compile ps_3_0 P();}}', encoding="utf-8")
write_json("seek.json", {"package": "../../Tests/triangle.unitypackage", "fx": "seek.fx",
                         "image": "blink.png", "frame": 0, "red": 0, "green": 255, "blue": 0})
(out / "subset.fx").write_text(
    'float4 Miss():COLOR{return float4(1,0,0,1);} float4 Hit():COLOR{return float4(0,1,0,1);}'
    'technique Other<string Subset="2-4,7";>{pass A{PixelShader=compile ps_3_0 Miss();}}'
    'technique Selected<string Subset="0";>{pass B{PixelShader=compile ps_3_0 Hit();}}', encoding="utf-8")
write_json("subset.json", {"package": "../../Tests/triangle.unitypackage","fx": "subset.fx","red":0,"green":255,"blue":0})
(out / "ramp.fx").write_text(
    'float X:STANDARDSGLOBAL<string ScriptClass="scene";string ScriptOrder="postprocess";>=.8;'
    'float4 P(float2 uv:TEXCOORD):COLOR{return float4(uv,0,1);}'
    'technique Main<string Script="ScriptExternal=Color;Pass=Ramp;";>'
    '{pass Ramp<string Script="Draw=Buffer;";>{PixelShader=compile ps_3_0 P();}}', encoding="utf-8")
write_json("ramp.json", {"package": "../../Tests/triangle.unitypackage", "fx": "ramp.fx",
                         "red": 128, "green": 64, "blue": 0})
(out / "forwarded.fx").write_text(
    'texture T:ANIMATEDTEXTURE<string ResourceName="blink.png";>;'
    'sampler2D S=sampler_state{Texture=T;};'
    'float4 SampleColor(sampler tex,float2 uv){return tex2D(tex,uv);}'
    'float4 Nested(sampler tex,float2 uv){return SampleColor(tex,uv);}'
    'float4 P(float2 uv:TEXCOORD,uniform sampler tex):COLOR{return Nested(tex,uv);}'
    'technique Main{pass A{PixelShader=compile ps_3_0 P(S);}}', encoding="utf-8")
write_json("forwarded.json", {"package": "../../Tests/triangle.unitypackage", "fx": "forwarded.fx",
                              "image": "blink.png", "frame": 4, "red": 0, "green": 255, "blue": 0})
(out / "cutout.fx").write_text(
    'float4 Solid():COLOR{return float4(1,0,0,1);}float4 Hole():COLOR{return float4(0,1,0,.1);}'
    'technique M<string Script="Pass=Base;" "Pass=Mask;";>{pass Base{PixelShader=compile ps_3_0 Solid();}'
    'pass Mask{ZEnable=false;AlphaTestEnable=true;AlphaFunc=Greater;AlphaRef=127;PixelShader=compile ps_3_0 Hole();}}',
    encoding="utf-8")
write_json("cutout.json", {"package": "../../Tests/triangle.unitypackage", "fx": "cutout.fx",
                           "red": 255, "green": 0, "blue": 0})
(out / "roles.fx").write_text(
    'float4 Red():COLOR{return float4(1,0,0,1);}float4 Green():COLOR{return float4(0,1,0,1);}'
    'float4 Depth():COLOR{return .25;}'
    'technique Main{pass A{PixelShader=compile ps_3_0 Red();}}'
    'technique Border<string MMDPass="edge";>{pass B{ZEnable=false;PixelShader=compile ps_3_0 Green();}}'
    'technique DepthPass<string MMDPass="zplot";>{pass C{PixelShader=compile ps_3_0 Depth();}}',
    encoding="utf-8")
write_json("roles.json", {"package": "../../Tests/triangle.unitypackage", "fx": "roles.fx",
                          "red": 0, "green": 255, "blue": 0, "probeDepth": True})
for fx, pixel in [("outer", "float4(0,tex2D(S,uv).r,0,1)"), ("inner", "float4(1,0,0,1)")]:
    (out / (fx + ".fx")).write_text(
        'float Standard:STANDARDSGLOBAL<string ScriptOrder="postprocess";string ScriptClass="scene";>=0.8; '
        'texture T:RENDERCOLORTARGET;sampler2D S=sampler_state{Texture=<T>;}; '
        'float4 Paint(float2 uv:TEXCOORD):SV_Target{return ' + pixel + ';}'
        'technique Main<string Script="RenderColorTarget=T;ScriptExternal=Color;RenderColorTarget=;Pass=P;";>'
        '{pass P<string Script="Draw=Buffer;";>{PixelShader=compile ps_3_0 Paint();}}', encoding="utf-8")
write_json("nested.json", {"package": "../../Tests/triangle.unitypackage", "fx": "outer.fx", "innerFx": "inner.fx",
                           "red": 0, "green": 255, "blue": 0})
(out / "prepared.fx").write_text(
    'float Standard:STANDARDSGLOBAL<string ScriptOrder="postprocess";string ScriptClass="scene";>=0.8;'
    'float4 Tint=float4(0.25,0.5,0.75,1);texture T:RENDERCOLORTARGET;sampler2D S=sampler_state{Texture=<T>;};'
    'float Zero(float3x3 m){return m[0][0]-m[0][0];}'
    'float3x3 Divisor(float value){return float3x3(value,value,value,value,value,value,value,value,value);}'
    'float4 Read():COLOR{float4 c=tex2Dlod(S,float4(.05,.05,0,0));'
    'float3x3 numerator=float3x3(c.rgb,c.rgb,c.rgb)*2;float3x3 divisor=Divisor(2);'
    'float3x3 result=numerator/divisor;float3 row=result[2];c.rgb=row;'
    'float fromMatrix=Zero(divisor)/Zero(numerator);'
    'float other=tex2Dlod(S,float4(.06,.06,0,0)).a;float ratio=(c.a-1)/(other-1);'
    'return float4(sqrt(-c.r)+ratio+fromMatrix,pow(-c.g,2.0)+pow(-c.r,3)+c.r*c.r*c.r,c.b,1);}'
    'technique Main<string Script="RenderColorTarget=T;ClearSetColor=Tint;Clear=Color;ScriptExternal=Color;RenderColorTarget=;Pass=P;";>'
    '{pass P<string Script="Draw=Buffer;";>{PixelShader=compile ps_3_0 Read();}}', encoding="utf-8")
write_json("prepared.json", {"package": "../../Tests/triangle.unitypackage", "fx": "prepared.fx",
                            "red": 128, "green": 64, "blue": 191})
write_json("values.json", {"package": "../../Tests/triangle.unitypackage", "fx": "values.fx",
                          "image": "blink.png", "frame": 4, "red": 0, "green": 255, "blue": 0})
