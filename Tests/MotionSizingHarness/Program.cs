using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using Ava.Extensions;
using Ava.MotionSizing;

static class Program
{
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true };
    static int checks;

    static int Main()
    {
        var cases = new (string name, Action run)[] {
            ("native identity, edit rest and source immutability", Identity),
            ("movement in metres and full Unicode joint identity", MetresAndIdentity),
            ("different actor proportions and rotated/scaled parent", Retarget),
            ("FK foot contact without enabling IK", Feet),
            ("retargeted FK disables target leg IK only in the selected range", TargetIk),
            ("frame range and unrelated owners/channels", Range),
            ("full-clip end holds and the last editable frame", EndFrames),
            ("camera uses displayed model geometry and keeps inactive keys", Camera),
            ("close wrists retain contact across arm proportions", Hands),
            ("native morph remap preserves missing destinations", Morphs),
            ("named twist helpers preserve contact and converge", Twist),
            ("native curves and physics metadata", Metadata),
            ("cancellation returns no partially applied motion", Cancellation)
        };
        int failures = 0;
        foreach (var test in cases)
        {
            try { test.run(); Console.WriteLine("PASS " + test.name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + test.name + "\n" + error); }
        }
        Console.WriteLine($"{cases.Length - failures}/{cases.Length} cases passed; {checks} assertions");
        return failures == 0 ? 0 : 1;
    }

    static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    static void Near(float actual, float expected, string message, float tolerance = .00002f)
        => Check(Math.Abs(actual - expected) <= tolerance, $"{message}: expected {expected}, got {actual}");

    static void Near(Vector3 actual, Vector3 expected, string message, float tolerance = .00002f)
        => Check(Vector3.Distance(actual, expected) <= tolerance, $"{message}: expected {expected}, got {actual}");

    static void RotationNear(Quaternion actual, Quaternion expected, string message)
        => Check(Math.Abs(Quaternion.Dot(Quaternion.Normalize(actual), Quaternion.Normalize(expected))) > .99999f, message);

    static NativeSizingOptions Plain() => new NativeSizingOptions {
        MatchFeet = false, AlignHands = false, AlignFingers = false, AvoidBody = false, Twist = false, Camera = false
    };

    static RigJoint Joint(string name, string role, int parent, float x, float y, float z = 0)
        => new RigJoint { name = name, role = role, parent = parent, x = x, y = y, z = z };

    static RigProfile Rig(float heightScale = 1)
    {
        var joints = new[] {
            Joint("root", null, -1, 0, 0),
            Joint("腰_🦊_長い名前_hips", "hips", 0, 0, 1),
            Joint("spine", "spine", 1, 0, 1.2f),
            Joint("head", "head", 2, 0, 1.7f),
            Joint("同じ名前", "leftUpperLeg", 1, .12f, .9f),
            Joint("同じ名前", "leftLowerLeg", 4, .12f, .5f),
            Joint("足首_😀_abcdefghijklmnop", "leftFoot", 5, .12f, .1f),
            Joint("足先_😀_abcdefghijklmnop", "leftToes", 6, .12f, .1f, .15f),
            Joint("同じ名前", "rightUpperLeg", 1, -.12f, .9f),
            Joint("同じ名前", "rightLowerLeg", 8, -.12f, .5f),
            Joint("足首_🦊_abcdefghijklmnop", "rightFoot", 9, -.12f, .1f),
            Joint("足先_🦊_abcdefghijklmnop", "rightToes", 10, -.12f, .1f, .15f),
            Joint("腕", "leftUpperArm", 2, .2f, 1.4f),
            Joint("肘", "leftLowerArm", 12, .5f, 1.4f),
            Joint("手首", "leftHand", 13, .8f, 1.4f),
            Joint("腕", "rightUpperArm", 2, -.2f, 1.4f),
            Joint("肘", "rightLowerArm", 15, -.5f, 1.4f),
            Joint("手首", "rightHand", 16, -.8f, 1.4f)
        };
        foreach (var joint in joints) { joint.x *= heightScale; joint.y *= heightScale; joint.z *= heightScale; }
        return new RigProfile { name = "native rig " + heightScale, joints = joints, metersPerMotionUnit = .1 };
    }

    static KeyValue Bone(Vector3 position = default, Quaternion? rotation = null)
        => new KeyValue { Channel = TrackChannel.Bone, Position = position, Rotation = rotation ?? Quaternion.Identity };

    static TrackSnapshot Track(string owner, int joint, RigProfile rig, int[] frames, params KeyValue[] values)
        => new TrackSnapshot {
            Owner = owner, Id = owner + "/joint/" + joint, Name = joint % 2 == 0 ? "表示名_🦊_同一" : "表示名_🦊_同一 (2)",
            Channel = TrackChannel.Bone, RigJointIndex = joint, Role = rig.joints[joint].role,
            Revision = 400 + joint, Frames = frames, Values = values, Current = values[0]
        };

    static List<TrackSnapshot> Tracks(string owner, RigProfile rig)
        => Enumerable.Range(0, rig.joints.Length).Select(i => Track(owner, i, rig, new[] { 0, 10 }, Bone(), Bone())).ToList();

    static MotionSnapshot Snapshot(IEnumerable<TrackSnapshot> tracks)
        => new MotionSnapshot { Document = 91, Frame = 0, Tracks = tracks.ToArray(), DrawOrder = new[] { "source", "target" } };

    static NativeSizingResult Calculate(MotionSnapshot snapshot, RigProfile source, RigProfile target,
        NativeSizingOptions options = null, string targetOwner = "source", int first = 0, int last = -1,
        CancellationToken token = default, Matrix4x4? sourceToScene = null, Matrix4x4? targetToScene = null)
    {
        string before = JsonSerializer.Serialize(snapshot, Json);
        string sourceBefore = JsonSerializer.Serialize(source, Json), targetBefore = JsonSerializer.Serialize(target, Json);
        var result = NativeMotionSizing.Calculate(snapshot, source, target, new NativeSizingRequest {
            SourceOwner = "source", TargetOwner = targetOwner, FirstFrame = first, LastFrame = last, Options = options ?? Plain(),
            SourceToScene = sourceToScene ?? Matrix4x4.Identity, TargetToScene = targetToScene ?? Matrix4x4.Identity
        }, token);
        Check(result.Document == snapshot.Document, "document generation must be retained");
        Check(result.Edits != null && result.Messages != null, "complete calculation result");
        Check(before == JsonSerializer.Serialize(snapshot, Json), "calculation mutated input snapshot");
        Check(sourceBefore == JsonSerializer.Serialize(source, Json) && targetBefore == JsonSerializer.Serialize(target, Json), "calculation mutated a source rig");
        Check(result.Edits.GroupBy(e => (e.Owner, e.Track, e.Frame)).All(g => g.Count() == 1), "duplicate edits would make atomic application order-dependent");
        foreach (var edit in result.Edits)
        {
            var track = snapshot.Tracks.Single(t => t.Owner == edit.Owner && t.Id == edit.Track);
            Check(edit.Revision == track.Revision, "edit lost target revision");
        }
        return result;
    }

    static Dictionary<int, KeyValue> Apply(TrackSnapshot track, NativeSizingResult result)
    {
        var values = track.Frames.Select((frame, i) => (frame, key: Copy(track.Values[i]))).ToDictionary(p => p.frame, p => p.key);
        foreach (var edit in result.Edits.Where(e => e.Owner == track.Owner && e.Track == track.Id))
        {
            if (edit.Value == null) { values.Remove(edit.Frame); continue; }
            var key = Copy(edit.Value);
            values.TryGetValue(edit.Frame, out var previous);
            if (edit.Curves == CurveAction.Preserve)
            {
                key.Curves = previous?.Curves;
                key.RawInterpolation = previous?.RawInterpolation;
                key.ModifiedChannels = previous?.ModifiedChannels ?? 0;
            }
            else if (edit.Curves == CurveAction.Clear) { key.Curves = null; key.RawInterpolation = null; key.ModifiedChannels = 0; }
            if (edit.SourceMetadata == CurveAction.Preserve) { key.SourceName = previous?.SourceName; key.SourceIK = previous?.SourceIK; }
            else if (edit.SourceMetadata == CurveAction.Clear) { key.SourceName = null; key.SourceIK = null; }
            values[edit.Frame] = key;
        }
        return values;
    }

    static KeyValue Copy(KeyValue value)
        => JsonSerializer.Deserialize<KeyValue>(JsonSerializer.Serialize(value, Json), Json);

    static KeyValue At(TrackSnapshot track, NativeSizingResult result, float frame)
    {
        var keys = Apply(track, result);
        if (track.Channel == TrackChannel.Camera) keys = keys.Where(pair => !pair.Value.Disabled).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (keys.Count == 0) return Copy(track.Current);
        if (frame == (int)frame && keys.TryGetValue((int)frame, out var exact)) return exact;
        var before = keys.Keys.Where(f => f <= frame).DefaultIfEmpty(keys.Keys.Min()).Max();
        var after = keys.Keys.Where(f => f >= frame).DefaultIfEmpty(keys.Keys.Max()).Min();
        if (before == after) return keys[before];
        var a = keys[before]; var b = keys[after]; float t = (frame - before) / (float)(after - before);
        var p = a.Position; var delta = b.Position - p;
        var value = Copy(a);
        value.Position = p + delta * new Vector3(Ease(b, 0, t), Ease(b, 1, t), Ease(b, 2, t));
        value.Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, Ease(b, 3, t));
        value.Euler = Vector3.Lerp(a.Euler, b.Euler, Ease(b, 3, t));
        value.Distance = a.Distance + (b.Distance - a.Distance) * Ease(b, 4, t);
        value.ViewAngle = a.ViewAngle + (b.ViewAngle - a.ViewAngle) * Ease(b, 5, t);
        value.Weight = a.Weight + (b.Weight - a.Weight) * Ease(b, 0, t);
        return value;
    }

    static float Ease(KeyValue key, int channel, float t)
    {
        if (key.Curves == null || key.Curves.Length <= channel || !key.Curves[channel].Custom) return t;
        var curve = key.Curves[channel];
        float Cubic(float a, float b, float value) => 3 * (1 - value) * (1 - value) * value * a + 3 * (1 - value) * value * value * b + value * value * value;
        float lower = 0, upper = 1;
        for (int i = 0; i < 24; i++) { float mid = (lower + upper) / 2; if (Cubic(curve.First.X, curve.Second.X, mid) < t) lower = mid; else upper = mid; }
        return Cubic(curve.First.Y, curve.Second.Y, (lower + upper) / 2);
    }

    static NativeSizingResult None => new NativeSizingResult { Edits = Array.Empty<KeyEdit>() };

    static (Vector3 position, Quaternion rotation) World(RigProfile rig, List<TrackSnapshot> tracks, int joint, int frame, NativeSizingResult result)
    {
        var rest = rig.editRest ?? rig.joints;
        var j = rest[joint];
        var track = tracks.SingleOrDefault(t => t.Channel == TrackChannel.Bone && t.RigJointIndex == joint);
        var value = track == null ? Bone() : At(track, result, frame);
        var position = new Vector3((float)j.x, (float)j.y, (float)j.z);
        var rotation = new Quaternion(j.qx, j.qy, j.qz, j.qw);
        if (j.parent < 0) return (position + value.Position, Quaternion.Normalize(rotation * value.Rotation));
        var parent = rest[j.parent];
        var restParentRotation = new Quaternion(parent.qx, parent.qy, parent.qz, parent.qw);
        var restParentPosition = new Vector3((float)parent.x, (float)parent.y, (float)parent.z);
        var currentParent = World(rig, tracks, j.parent, frame, result);
        var offset = Vector3.Transform(position - restParentPosition, Quaternion.Inverse(restParentRotation));
        var localDisplacement = value.Position * new Vector3(parent.scaleX, parent.scaleY, parent.scaleZ);
        return (currentParent.position + Vector3.Transform(offset + localDisplacement, currentParent.rotation),
            Quaternion.Normalize(currentParent.rotation * Quaternion.Inverse(restParentRotation) * rotation * value.Rotation));
    }

    static void Identity()
    {
        var rig = Rig(); rig.editRest = Rig().joints;
        // Authored rest and editing rest intentionally differ. Preview or VMD units must not enter sizing.
        foreach (var joint in rig.joints) { joint.y *= 1.75; joint.qz = MathF.Sin(.3f); joint.qw = MathF.Cos(.3f); }
        var tracks = Tracks("source", rig);
        tracks[1].Values[1] = Bone(new Vector3(.24f, .15f, .36f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .25f));
        tracks[5].Values[1] = Bone(rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitX, .5f));
        var snapshot = Snapshot(tracks);
        var result = Calculate(snapshot, rig, rig);
        Check(result.Edits.Length == 0, "identity sizing should not rebake or quantize native content");
        var defaults = Calculate(snapshot, rig, rig, new NativeSizingOptions());
        Near(At(tracks[1], defaults, 10).Position, tracks[1].Values[1].Position,
            "default corrections applied the import height ratio again");
    }

    static void MetresAndIdentity()
    {
        var rig = Rig(); var tracks = Tracks("source", rig);
        tracks[1].Values[1] = Bone(new Vector3(.2f, .3f, .4f));
        tracks[4].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .2f);
        tracks[5].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.4f);
        var options = Plain(); options.MovementScale = 1.5f;
        var result = Calculate(Snapshot(tracks), rig, rig, options);
        Near(At(tracks[1], result, 10).Position, new Vector3(.3f, .3f, .6f), "native metre-scale movement");
        RotationNear(At(tracks[4], result, 10).Rotation, tracks[4].Values[1].Rotation, "first duplicate-named joint was remapped");
        RotationNear(At(tracks[5], result, 10).Rotation, tracks[5].Values[1].Rotation, "second duplicate-named joint was remapped");
        Check(result.Edits.All(e => e.Track == tracks[1].Id), "movement sizing changed a non-movement track");
    }

    static void Retarget()
    {
        var source = Rig(); var target = Rig(1.5f);
        var from = Tracks("source", source); var to = Tracks("target", target);
        from[1].Values[1] = Bone(new Vector3(.2f, .1f, .4f));
        var result = Calculate(Snapshot(from.Concat(to)), source, target, targetOwner: "target");
        Near(At(to[1], result, 10).Position, new Vector3(.3f, .15f, .6f), "cross-actor leg proportion scaling");
        Check(result.Edits.All(e => e.Owner == "target"), "source actor must not be modified during retarget");

        target = Rig(); to = Tracks("target", target);
        var parentRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var restRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .35f);
        target.joints[0].qz = parentRotation.Z; target.joints[0].qw = parentRotation.W;
        target.joints[0].scaleX = target.joints[0].scaleY = target.joints[0].scaleZ = 2;
        target.joints[1].qx = restRotation.X; target.joints[1].qw = restRotation.W;
        from[1].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .45f);
        var original = World(source, from, 1, 10, None);
        result = Calculate(Snapshot(from.Concat(to)), source, target, targetOwner: "target");
        var actual = World(target, to, 1, 10, result);
        Near(actual.position, original.position, "retarget must account for target parent rotation and scale");
        RotationNear(actual.rotation * Quaternion.Inverse(restRotation), original.rotation, "retarget world orientation delta");

        source = Rig(); target = Rig();
        var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        foreach (var joint in source.joints) {
            var point = Vector3.Transform(new Vector3((float)joint.x, (float)joint.y, (float)joint.z), facing);
            joint.x = point.X; joint.y = point.Y; joint.z = point.Z;
            joint.qx = facing.X; joint.qy = facing.Y; joint.qz = facing.Z; joint.qw = facing.W;
        }
        from = Tracks("source", source); to = Tracks("target", target);
        from[12].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f);
        from[1].Values[1].Position = new Vector3(.2f, 0, .4f);
        result = Calculate(Snapshot(from.Concat(to)), source, target, targetOwner: "target");
        Check(World(source, from, 14, 10, None).position.Y > 1.4f && World(target, to, 14, 10, result).position.Y > 1.4f,
            "an arm lift became a downward swing when source anatomy faced the opposite direction");
        RotationNear(At(to[12], result, 10).Rotation, from[12].Values[1].Rotation, "anatomical facing must preserve local arm lift");
        Near(At(to[1], result, 10).Position, new Vector3(.2f, 0, .4f), "forward movement must follow target anatomical facing");

        source = Rig(); target = Rig(); target.editRest = Rig().joints;
        var relaxed = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 4);
        var shoulder = new Vector3(.2f, 1.4f, 0);
        foreach (int index in new[] { 12, 13, 14 }) {
            var joint = target.editRest[index];
            var point = shoulder + Vector3.Transform(new Vector3((float)joint.x, (float)joint.y, (float)joint.z) - shoulder, relaxed);
            joint.x = point.X; joint.y = point.Y; joint.z = point.Z; joint.qz = relaxed.Z; joint.qw = relaxed.W;
        }
        from = Tracks("source", source); to = Tracks("target", target);
        result = Calculate(Snapshot(from.Concat(to)), source, target, targetOwner: "target");
        Near(World(target, to, 14, 10, result).position, World(source, from, 14, 10, None).position,
            "target preview rest pose must not replace the authored source pose");
    }

    static void Feet()
    {
        var rig = Rig(); var tracks = Tracks("source", rig);
        foreach (int joint in new[] { 4, 8 }) tracks[joint].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .25f);
        foreach (int joint in new[] { 5, 9 }) tracks[joint].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.5f);
        var switchTrack = new TrackSnapshot { Owner = "source", Id = "source/switch", Name = "表示", Channel = TrackChannel.ModelSwitch,
            Revision = 3, Frames = new[] { 0 }, Values = new[] { new KeyValue { Channel = TrackChannel.ModelSwitch,
                DisabledIK = new[] { "左足ＩＫ", "右足ＩＫ" } } } };
        switchTrack.Current = switchTrack.Values[0]; tracks.Add(switchTrack);
        var options = Plain(); options.MatchFeet = true; options.FloorHeight = .05f;
        var original = World(rig, tracks, 6, 10, None);
        var result = Calculate(Snapshot(tracks), rig, rig, options);
        var corrected = World(rig, tracks, 6, 10, result);
        var goal = original.position + Vector3.UnitY * .05f;
        Check(Vector3.Distance(corrected.position, goal) < Vector3.Distance(original.position, goal) * .15f,
            $"FK correction did not bring the ankle to support height: before {original.position}, after {corrected.position}, goal {goal}");
        RotationNear(corrected.rotation, original.rotation, "foot orientation should survive FK contact correction");
        Check(result.Edits.Any(e => e.Track == tracks[4].Id || e.Track == tracks[5].Id), "FK correction must change thigh/knee rotations");
        Check(result.Edits.All(e => e.Track != switchTrack.Id), "same-actor sizing must retain imported IK OFF switches");
    }

    static void Range()
    {
        var rig = Rig(); var tracks = Tracks("source", rig);
        tracks[1].Frames = new[] { 0, 10, 20 };
        tracks[1].Values = new[] { Bone(), Bone(new Vector3(1, 0, 2)), Bone(new Vector3(2, 0, 4)) };
        var unrelated = Tracks("other", rig);
        var camera = new TrackSnapshot { Owner = "scene", Id = "camera", Channel = TrackChannel.Camera, Name = "camera", Revision = 7,
            Frames = new[] { 0 }, Values = new[] { new KeyValue { Channel = TrackChannel.Camera, Position = new Vector3(2, 3, 4), Distance = 35, ViewAngle = 45 } } };
        camera.Current = camera.Values[0];
        var options = Plain(); options.MovementScale = 1.3f;
        var result = Calculate(Snapshot(tracks.Concat(unrelated).Append(camera)), rig, rig, options, first: 8, last: 12);
        Near(At(tracks[1], result, 10).Position, new Vector3(1.3f, 0, 2.6f), "selected frame was not sized");
        foreach (int frame in new[] { 0, 3, 7, 13, 17, 20, 24 })
            Near(At(tracks[1], result, frame).Position, At(tracks[1], None, frame).Position, "frame outside selected range " + frame);
        Check(result.Edits.All(e => e.Owner == "source"), "range operation changed another actor or an unselected camera");
        var curve = new BezierCurve { Custom = true, First = new Vector2(.2f, .03f), Second = new Vector2(.8f, .92f) };
        tracks[1].Values[1].Curves = new[] { curve, curve, curve, default };
        tracks[1].Values[2].Curves = new[] { curve, curve, curve, default };
        result = Calculate(Snapshot(tracks), rig, rig, options, first: 8, last: 12);
        foreach (float frame in new[] { .5f, 2.5f, 6.5f, 13.5f, 17.5f, 19.5f })
            Near(At(tracks[1], result, frame).Position, At(tracks[1], None, frame).Position,
                "range boundary changed interpolation outside the selection at " + frame, .0001f);
    }

    static void TargetIk()
    {
        var source = Rig(); var target = Rig(1.1f);
        var from = Tracks("source", source); var to = Tracks("target", target);
        from[5].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.3f);
        TrackSnapshot Switch(string owner, string[] disabled) {
            var value = new KeyValue { Channel = TrackChannel.ModelSwitch, DisabledIK = disabled };
            return new TrackSnapshot { Owner = owner, Id = owner + "/switch", Name = "表示", Channel = TrackChannel.ModelSwitch,
                Revision = 72, Frames = new[] { 0 }, Values = new[] { value }, Current = value };
        }
        from.Add(Switch("source", new[] { "LeftFootControl", "RightFootControl" }));
        var targetSwitch = Switch("target", new[] { "OtherIK" }); to.Add(targetSwitch);
        foreach (var (name, role) in new[] { ("LeftFootControl", "LeftFootIK"), ("RightFootControl", "RightFootIK") }) {
            var value = Bone();
            to.Add(new TrackSnapshot { Owner = "target", Id = "target/" + role, Name = name, Role = role, RigJointIndex = -1,
                Channel = TrackChannel.Bone, Revision = 73, Frames = new[] { 0 }, Values = new[] { value }, Current = value });
        }
        var result = Calculate(Snapshot(from.Concat(to)), source, target, targetOwner: "target", first: 2, last: 8);
        var keys = Apply(targetSwitch, result);
        KeyValue SampleSwitch(int frame) => keys[keys.Keys.Where(f => f <= frame).DefaultIfEmpty(keys.Keys.Min()).Max()];
        var active = SampleSwitch(5).DisabledIK ?? Array.Empty<string>();
        Check(active.Contains("LeftFootControl") && active.Contains("RightFootControl") && active.Contains("OtherIK"),
            "retargeted FK must disable corresponding target IK and retain unrelated disabled chains");
        Check((SampleSwitch(1).DisabledIK ?? Array.Empty<string>()).SequenceEqual(new[] { "OtherIK" }), "IK changes leaked before selection");
        Check((SampleSwitch(9).DisabledIK ?? Array.Empty<string>()).SequenceEqual(new[] { "OtherIK" }), "IK changes leaked after selection");
        Check(result.Edits.All(e => e.Owner == "target"), "retarget changed source switch");
    }

    static void Metadata()
    {
        var rig = Rig(); var tracks = Tracks("source", rig);
        var curves = Enumerable.Range(0, 4).Select(i => new BezierCurve { Custom = true,
            First = new Vector2(.11f + i * .02f, .31f), Second = new Vector2(.71f, .91f - i * .03f) }).ToArray();
        var end = tracks[1].Values[1] = Bone(new Vector3(.1f, .2f, .3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f));
        end.PhysicsOff = true; end.Curves = curves; end.SourceName = Enumerable.Repeat((byte)9, 15).ToArray();
        var morph = new TrackSnapshot { Owner = "source", Id = "source/morph/🦊", Name = "表情_😀_長い名前", Channel = TrackChannel.Morph,
            Revision = 1000, Frames = new[] { 0, 10 }, Values = new[] { new KeyValue { Channel = TrackChannel.Morph, Weight = .2f },
                new KeyValue { Channel = TrackChannel.Morph, Weight = .7f, Curves = new[] { curves[0] } } } };
        morph.Current = morph.Values[0]; tracks.Add(morph);
        var options = Plain(); options.MovementScale = 1.4f;
        var result = Calculate(Snapshot(tracks), rig, rig, options);
        var after = At(tracks[1], result, 10);
        Check(after.PhysicsOff, "physics switch lost on changed bone");
        RotationNear(after.Rotation, end.Rotation, "movement-only operation changed bone rotation");
        Check(result.Edits.All(e => e.Track != morph.Id), "unrequested morph mapping changed a morph");
        // Affine movement scaling has an exact curve-preserving solution and must not quantize it.
        for (float frame = 0; frame <= 10; frame += .25f)
        {
            var value = At(tracks[1], result, frame); var before = At(tracks[1], None, frame);
            Near(value.Position, new Vector3(before.Position.X * 1.4f, before.Position.Y, before.Position.Z * 1.4f),
                "custom interpolation changed during movement-only sizing at " + frame, .00005f);
        }

        // XZ scaling in world axes mixes Y/Z local channels under a pitched parent.
        // Different per-axis curves cannot be preserved merely by scaling endpoint values.
        var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4);
        rig.joints[0].qx = pitch.X; rig.joints[0].qw = pitch.W;
        end.Curves[1] = new BezierCurve { Custom = true, First = new Vector2(.2f, .02f), Second = new Vector2(.8f, .4f) };
        end.Curves[2] = new BezierCurve { Custom = true, First = new Vector2(.2f, .6f), Second = new Vector2(.8f, .99f) };
        result = Calculate(Snapshot(tracks), rig, rig, options);
        for (int frame = 0; frame <= 10; frame++) {
            var before = At(tracks[1], None, frame).Position;
            var world = Vector3.Transform(before, pitch) * new Vector3(1.4f, 1, 1.4f);
            Near(At(tracks[1], result, frame).Position, Vector3.Transform(world, Quaternion.Inverse(pitch)),
                "pitched-parent movement mixes differently eased local channels at " + frame, .00005f);
        }
    }

    static void EndFrames()
    {
        var rig = Rig(); var tracks = Tracks("source", rig);
        tracks[1].Values[1] = Bone(new Vector3(.2f, .3f, .4f));
        var options = Plain(); options.MovementScale = 1.5f;
        // The UI supplies an explicit end even when the user chooses the whole clip.
        var result = Calculate(Snapshot(tracks), rig, rig, options, last: 10);
        foreach (int frame in new[] { 10, 11, 40 })
            Near(At(tracks[1], result, frame).Position, new Vector3(.3f, .3f, .6f), "full-clip motion snapped back after its end at " + frame);
        tracks[1].Frames = new[] { 0, 600000 };
        result = Calculate(Snapshot(tracks), rig, rig, options, first: 600000, last: 600000);
        Check(result.Edits.All(edit => edit.Frame <= 600000), "range guard exceeds the host's last editable frame");
        Near(At(tracks[1], result, 600000).Position, new Vector3(.3f, .3f, .6f), "last editable frame was not sized");
    }

    static void Hands()
    {
        var source = Rig(); var target = Rig();
        target.joints[14].x = .9; target.joints[17].x = -.9; // Longer target forearms.
        var from = Tracks("source", source); var to = Tracks("target", target);
        float shoulder = MathF.Acos(1f / 3), elbow = MathF.PI - shoulder;
        from[12].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -shoulder);
        from[13].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -elbow);
        from[15].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, shoulder);
        from[16].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, elbow);
        var snapshot = Snapshot(from.Concat(to));
        var unaligned = Calculate(snapshot, source, target, targetOwner: "target");
        float before = Vector3.Distance(World(target, to, 14, 10, unaligned).position, World(target, to, 17, 10, unaligned).position);
        var options = Plain(); options.AlignHands = true; options.ContactDistance = .03f;
        var aligned = Calculate(snapshot, source, target, options, targetOwner: "target");
        float after = Vector3.Distance(World(target, to, 14, 10, aligned).position, World(target, to, 17, 10, aligned).position);
        Check(before > .15f && after < .003f, $"authored wrist contact was not restored after arm-length change: {before} -> {after}");
    }

    static void Morphs()
    {
        var rig = Rig(); var tracks = Tracks("source", rig);
        TrackSnapshot Morph(string id, string name, float a, float b) {
            var values = new[] { new KeyValue { Channel = TrackChannel.Morph, Weight = a }, new KeyValue { Channel = TrackChannel.Morph, Weight = b } };
            return new TrackSnapshot { Owner = "source", Id = id, Name = "表情：" + name, Channel = TrackChannel.Morph, Revision = 88,
                Frames = new[] { 0, 10 }, Values = values, Current = values[0] };
        }
        var from = Morph("morph/from", "元_😀_長い名前", .4f, .6f);
        var to = Morph("morph/to", "先_🦊_長い名前", .1f, .2f);
        var other = Morph("morph/other", "維持_🐱", .3f, .4f);
        tracks.AddRange(new[] { from, to, other });
        var options = Plain(); options.MorphRules = "元_😀_長い名前:先_🦊_長い名前:0.5";
        var result = Calculate(Snapshot(tracks), rig, rig, options);
        Near(At(from, result, 10).Weight, 0, "successfully remapped source morph should be cleared");
        Near(At(to, result, 10).Weight, .5f, "morph remap must combine scaled source with existing destination");
        Near(At(other, result, 10).Weight, .4f, "morph remap changed unrelated expression");
        options.MorphRules = "元_😀_長い名前:存在しない_🦊:2";
        result = Calculate(Snapshot(tracks), rig, rig, options);
        Near(At(from, result, 10).Weight, .6f, "missing destination must not erase original expression");
        Check(result.Edits.All(edit => edit.Track != from.Id && edit.Track != other.Id), "missing morph target emitted destructive edits");
    }

    static void Twist()
    {
        var rig = Rig(); var joints = rig.joints.ToList();
        joints.Add(Joint("左腕捩", null, 12, .35f, 1.4f));
        joints.Add(Joint("左手捩", null, 13, .65f, 1.4f));
        joints[13].parent = 18; joints[14].parent = 19; rig.joints = joints.ToArray();
        var tracks = Tracks("source", rig);
        tracks[12].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f);
        tracks[13].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f);
        tracks[14].Values[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.25f);
        var original = World(rig, tracks, 14, 10, None);
        var options = Plain(); options.Twist = true;
        var result = Calculate(Snapshot(tracks), rig, rig, options);
        var actual = World(rig, tracks, 14, 10, result);
        Near(actual.position, original.position, "twist distribution moved hand contact");
        RotationNear(actual.rotation, original.rotation, "twist distribution changed hand world orientation");
        Check(Math.Abs(At(tracks[18], result, 10).Rotation.X) > .05f && Math.Abs(At(tracks[19], result, 10).Rotation.X) > .03f,
            "null-role Japanese twist helper aliases were not used");
        var repeatedTracks = tracks.Select(track => {
            var keys = Apply(track, result).OrderBy(pair => pair.Key).ToArray();
            return new TrackSnapshot { Owner = track.Owner, Id = track.Id, Name = track.Name, Role = track.Role, RigJointIndex = track.RigJointIndex,
                Channel = track.Channel, Revision = track.Revision + 1, Frames = keys.Select(pair => pair.Key).ToArray(),
                Values = keys.Select(pair => pair.Value).ToArray(), Current = keys[0].Value };
        }).ToList();
        var repeated = Calculate(Snapshot(repeatedTracks), rig, rig, options);
        foreach (int joint in new[] { 12, 13, 14, 18, 19 })
            RotationNear(At(repeatedTracks[joint], repeated, 10).Rotation, At(tracks[joint], result, 10).Rotation,
                "repeated twist distribution continued changing joint " + joint);
        Near(World(rig, repeatedTracks, 14, 10, repeated).position, original.position, "repeated twist changed hand contact");
    }

    static void Camera()
    {
        var rig = Rig(); var tracks = Tracks("source", rig);
        tracks[1].Values[1] = Bone(new Vector3(.2f, 0, .4f));
        var camera = new TrackSnapshot { Owner = "scene", Id = "camera", Name = "カメラ", Channel = TrackChannel.Camera, Revision = 18,
            Frames = new[] { 0, 5, 10 }, Values = new[] {
                new KeyValue { Channel = TrackChannel.Camera, Position = new Vector3(0, 20, 0), Distance = 30, ViewAngle = 40, Perspective = true },
                new KeyValue { Channel = TrackChannel.Camera, Position = new Vector3(99, -70, 12), Distance = 99, ViewAngle = 90, Disabled = true },
                new KeyValue { Channel = TrackChannel.Camera, Position = new Vector3(3, 23, 5), Distance = 40, ViewAngle = 45, Perspective = true }
            } };
        camera.Current = camera.Values[0];
        var display = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(0, -1, 0);
        var options = Plain(); options.MovementScale = .5f; options.Camera = true;
        var result = Calculate(Snapshot(tracks.Append(camera)), rig, rig, options, sourceToScene: display, targetToScene: display);
        var atEnd = At(camera, result, 10);
        Near(atEnd.Position, new Vector3(1, 23, 1), "camera should follow the scaled scene-space head displacement");
        Near(atEnd.Distance, 40, "same displayed model size must retain camera distance");
        var ignored = Apply(camera, result)[5];
        Check(ignored.Disabled && ignored.Position == camera.Values[1].Position && ignored.Distance == camera.Values[1].Distance,
            "camera sizing overwrote or enabled an inactive camera key");
        Near(At(camera, result, 5).Position, new Vector3(.5f, 21.5f, .5f), "inactive key must not affect sampled camera motion");

        var source = Rig(); var target = Rig(2);
        var from = Tracks("source", source); var to = Tracks("target", target);
        from[1].Values[1] = Bone(new Vector3(.2f, .1f, .4f));
        options = Plain(); options.Camera = true;
        var before = At(camera, None, 10);
        result = Calculate(Snapshot(from.Concat(to).Append(camera)), source, target, options, targetOwner: "target",
            sourceToScene: Matrix4x4.CreateTranslation(0, -.2f, 0),
            targetToScene: Matrix4x4.CreateScale(.5f) * Matrix4x4.CreateTranslation(0, -.2f, 0));
        Near(At(camera, result, 10).Position, before.Position, "equal displayed rigs must retain camera framing despite different authored stature");
        Near(At(camera, result, 10).Distance, before.Distance, "camera distance used authored instead of displayed stature");
    }

    static void Cancellation()
    {
        var rig = Rig(); var snapshot = Snapshot(Tracks("source", rig));
        var source = JsonSerializer.Serialize(snapshot, Json);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool stopped = false;
        try { Calculate(snapshot, rig, rig, token: cancellation.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "pre-cancelled sizing should stop");
        Check(source == JsonSerializer.Serialize(snapshot, Json), "cancelled operation changed native keys");
    }
}
