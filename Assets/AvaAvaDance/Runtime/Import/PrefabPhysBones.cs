using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance.Import
{
    // Only data from unpacked Unity text prefabs referencing the selected FBX is used.
    internal sealed class PrefabPhysBones
    {
        internal sealed class Node
        {
            internal Transform Joint;
            internal Node Parent;
            internal readonly List<Node> Children = new List<Node>();
            internal Matrix4x4 PrefabWorld, ToJoint;
            internal bool Active;
        }

        internal sealed class Collider
        {
            internal Transform Anchor;
            internal Vector3 Center, Up;
            internal int Shape;
            internal float Height, Radius;
            internal bool Inside, BonesAsSpheres, GlobalSelf;
        }

        internal sealed class Chain
        {
            internal Node Root;
            internal HashSet<Node> Ignore;
            internal Collider[] Colliders;
            internal Vector3 Endpoint;
            internal int MultiChild;
            internal bool IgnoreOtherRoots;
            internal Fields Parameters;
        }

        internal int Components;
        internal readonly List<Chain> Chains = new List<Chain>();
        const string SdkGuid = "2a2c05204084d904aa4945ccff20d8e5";
        const long PhysBoneId = 1661641543, ColliderId = -1631200402;
        static readonly Regex Header = new Regex(@"(?m)^--- !u!(\d+) &(-?\d+)[^\n]*\n");
        static readonly Regex LocalRef = new Regex(@"\{fileID: (-?\d+)\}");
        static readonly Regex Field = new Regex(@"(?m)^  (\w+): *([^\n]*)(\n(?:(?:    |  - )[^\n]*\n?)*)");

        internal sealed class Fields
        {
            readonly Dictionary<string,string> scalars = new Dictionary<string,string>();
            readonly Dictionary<string,string> blocks = new Dictionary<string,string>();
            readonly Dictionary<string,AnimationCurve> curves = new Dictionary<string,AnimationCurve>();

            internal Fields(string document)
            {
                foreach (Match m in Field.Matches(document)) {
                    string name = m.Groups[1].Value;
                    scalars[name] = m.Groups[2].Value.Trim();
                    blocks[name] = m.Groups[3].Value;
                }
            }

            internal string Get(string key) => scalars.TryGetValue(key,out var value) ? value : "";
            internal float Number(string key, float fallback = 0) => Parse(Get(key), fallback);
            internal int Int(string key, int fallback = 0) => int.TryParse(Get(key), out int i) ? i : fallback;
            internal long Ref(string key) => Id(LocalRef.Match(Get(key)).Groups[1].Value);
            internal IEnumerable<long> Refs(string key) => blocks.TryGetValue(key,out var block)
                ? LocalRef.Matches(block).Cast<Match>().Select(m => Id(m.Groups[1].Value)) : Enumerable.Empty<long>();
            internal bool AllowsSelfCollision => Int("allowCollision")==1 || (Int("allowCollision")==2 &&
                blocks.TryGetValue("collisionFilter",out var filter) && Regex.IsMatch(filter,@"(?m)^    allowSelf: 1\s*$"));
            internal Vector3 Vector(string key, Vector3 fallback = default)
            {
                var values = Tuple(key);
                return values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : fallback;
            }
            internal Quaternion Rotation(string key)
            {
                var v = Tuple(key);
                return v.Length == 4 ? new Quaternion(v[0], v[1], v[2], v[3]).normalized : Quaternion.identity;
            }
            float[] Tuple(string key) => Regex.Matches(Get(key), @"[xyzw]: *([^,}]+)").Cast<Match>()
                .Select(m => Parse(m.Groups[1].Value, 0)).ToArray();
            internal float Value(string key, float fallback, float depth)
            {
                float scalar = Number(key, fallback);
                return scalar * Multiplier(key + "Curve", depth);
            }
            internal float Multiplier(string key, float depth)
            {
                if (!curves.TryGetValue(key,out var curve)) {
                    if (blocks.TryGetValue(key,out var block)) {
                        var keys = new List<Keyframe>();
                        foreach (var part in block.Split(new[]{"    - serializedVersion:"}, StringSplitOptions.None).Skip(1).Take(256)) {
                            float Read(string name,float def = 0) => Parse(Regex.Match(part, "(?m)^      "+name+@": *([^\r\n]+)").Groups[1].Value,def);
                            keys.Add(new Keyframe(Read("time"),Read("value"),Read("inSlope"),Read("outSlope"),
                                Read("inWeight",1f/3),Read("outWeight",1f/3)) {weightedMode=(WeightedMode)(int)Read("weightedMode")});
                        }
                        if(keys.Count>0) curve=new AnimationCurve(keys.ToArray());
                    }
                    curves[key]=curve;
                }
                float value=curve==null ? 1 : curve.Evaluate(depth);
                return float.IsFinite(value) ? value : 1;
            }
        }

        static long Id(string s) => long.TryParse(s, NumberStyles.Integer,CultureInfo.InvariantCulture,out var i) ? i : 0;
        static float Parse(string s, float fallback) => float.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)
            && float.IsFinite(v) ? v : fallback;

        internal static PrefabPhysBones Read(Asset prefab,GameObject avatar)
        {
            var result = new PrefabPhysBones();
            if(prefab!=null)result.Bind(Encoding.UTF8.GetString(prefab.Data),avatar);
            return result;
        }

        void Bind(string yaml,GameObject avatar)
        {
            var objects=new Dictionary<long,(string name,bool active)>();
            var nodes=new Dictionary<long,Node>();
            var transforms=new Dictionary<long,(long owner,long parent,Fields data)>();
            var owners=new Dictionary<long,Node>();
            var components=new List<(long id,long script,Fields data)>();
            var headers=Header.Matches(yaml);
            if(headers.Count>50000)return;
            for(int i=0;i<headers.Count;i++) {
                var h=headers[i]; string type=h.Groups[1].Value;
                if(type!="1" && type!="4" && type!="114")continue;
                int start=h.Index+h.Length, end=i+1<headers.Count ? headers[i+1].Index : yaml.Length;
                var f=new Fields(yaml.Substring(start,end-start));
                long id=Id(h.Groups[2].Value);
                if(type=="1") {
                    string name=f.Get("m_Name");
                    if(name.Length>=2 && name[0]=='"' && name[name.Length-1]=='"')
                        name=Regex.Replace(name.Substring(1,name.Length-2),@"\\(u[0-9a-fA-F]{4}|[""\\])",m=>
                            m.Groups[1].Length==5 ? ((char)Convert.ToInt32(m.Groups[1].Value.Substring(1),16)).ToString() : m.Groups[1].Value);
                    objects[id]=(name,f.Int("m_IsActive",1)!=0);
                }
                if(type=="4") {
                    var node=new Node(); nodes[id]=node; owners[f.Ref("m_GameObject")]=node;
                    transforms[id]=(f.Ref("m_GameObject"),f.Ref("m_Father"),f);
                }
                if(type=="114") {
                    string script=f.Get("m_Script");
                    if(script.IndexOf(SdkGuid,StringComparison.OrdinalIgnoreCase)<0)continue;
                    long fileId=Id(Regex.Match(script,@"\bfileID: (-?\d+)").Groups[1].Value);
                    if(fileId==PhysBoneId || fileId==ColliderId)components.Add((id,fileId,f));
                    if(fileId==PhysBoneId)Components++;
                }
            }
            if(Components==0)return;
            var fbxRoot=avatar.transform;
            if(fbxRoot.childCount==1 && fbxRoot.GetChild(0).name=="")fbxRoot=fbxRoot.GetChild(0);
            var visited=new HashSet<long>();
            void Map(long id,Node parent,int depth) {
                if(depth>256 || !visited.Add(id) || !transforms.TryGetValue(id,out var info) || !objects.TryGetValue(info.owner,out var obj))return;
                var n=nodes[id]; var f=info.data;
                parent?.Children.Add(n);
                n.Parent=parent; n.Active=obj.active && (parent?.Active ?? true);
                n.PrefabWorld=parent==null ? Matrix4x4.identity : parent.PrefabWorld *
                    Matrix4x4.TRS(f.Vector("m_LocalPosition"),f.Rotation("m_LocalRotation"),f.Vector("m_LocalScale",Vector3.one));
                if(parent==null)n.Joint=fbxRoot;
                else if(parent.Joint!=null) {
                    for(int j=0;j<parent.Joint.childCount;j++) {
                        var t=parent.Joint.GetChild(j);
                        if(t.name!=obj.name)continue;
                        if(n.Joint!=null) {n.Joint=null; break;}
                        n.Joint=t;
                    }
                }
                foreach(var child in f.Refs("m_Children")) {
                    if(!transforms.TryGetValue(child,out var ci) || ci.parent!=id)continue;
                    Map(child,n,depth+1);
                }
            }
            foreach(var p in transforms)if(p.Value.parent==0)Map(p.Key,null,0);

            // Unity's ModelImporter can bake handedness/units into each joint while ufbx
            // keeps them on its scene root. Register the two bind spaces before posing the arms.
            var segments=nodes.Values.Where(n=>n.Joint!=null && n.Parent?.Joint!=null)
                .Select(n=>(source:(Vector3)(n.PrefabWorld.GetColumn(3)-n.Parent.PrefabWorld.GetColumn(3)),
                            target:n.Joint.position-n.Parent.Joint.position))
                .Where(s=>s.source.sqrMagnitude>1e-10f && s.target.sqrMagnitude>1e-10f).ToList();
            var first=segments.OrderByDescending(s=>s.source.sqrMagnitude).FirstOrDefault();
            var second=segments.OrderByDescending(s=>Vector3.Cross(first.source,s.source).sqrMagnitude).FirstOrDefault();
            Quaternion align=Quaternion.identity;
            float scale=1;
            if(first.source!=Vector3.zero) {
                scale=first.target.magnitude/first.source.magnitude;
                align=Vector3.Cross(first.source,second.source).sqrMagnitude>1e-10f && Vector3.Cross(first.target,second.target).sqrMagnitude>1e-10f
                    ? Quaternion.LookRotation(first.target,second.target)*Quaternion.Inverse(Quaternion.LookRotation(first.source,second.source))
                    : Quaternion.FromToRotation(first.source,first.target);
            }
            var basis=Matrix4x4.TRS(Vector3.zero,align,Vector3.one*scale);
            foreach(var n in nodes.Values) {
                var anchor=n;
                while(anchor!=null && !anchor.Joint)anchor=anchor.Parent;
                if(anchor==null)continue;
                var local=anchor.Joint.worldToLocalMatrix*basis*n.PrefabWorld;
                Vector3 offset=anchor.Joint.InverseTransformVector(basis.MultiplyVector(n.PrefabWorld.GetColumn(3)-anchor.PrefabWorld.GetColumn(3)));
                local.SetColumn(3,new Vector4(offset.x,offset.y,offset.z,1)); n.ToJoint=local;
            }
            Node Root(Fields f) {
                owners.TryGetValue(f.Ref("m_GameObject"),out var owner);
                if(owner==null || !owner.Active || f.Int("m_Enabled",1)==0)return null;
                string reference=f.Get("rootTransform");
                if(reference.Length!=0 && !LocalRef.IsMatch(reference))return null;
                long root=f.Ref("rootTransform");
                return root==0 ? owner : nodes.TryGetValue(root,out var n) && n.Active ? n : null;
            }
            var colliders=new Dictionary<long,Collider>();
            foreach(var c in components.Where(c=>c.script==ColliderId)) {
                var f=c.data; var n=Root(f); var anchor=n;
                while(anchor!=null && !anchor.Joint)anchor=anchor.Parent;
                if(anchor==null)continue;
                var up=n.ToJoint.MultiplyVector(f.Rotation("rotation")*Vector3.up);
                colliders[c.id]=new Collider {Anchor=anchor.Joint,Center=n.ToJoint.MultiplyPoint3x4(f.Vector("position")),
                    Up=up, Height=Mathf.Max(0,f.Number("height")), Radius=Mathf.Max(0,f.Number("radius")),
                    Shape=f.Int("shapeType"), Inside=f.Int("insideBounds")!=0,BonesAsSpheres=f.Int("bonesAsSpheres")!=0,
                    GlobalSelf=f.Int("shapeType")!=2 && (f.Int("globalCollision")==1 || (f.Int("globalCollision")==2 && f.Int("globalCollisionAllowSelf")!=0))};
            }
            foreach(var c in components.Where(c=>c.script==PhysBoneId)) {
                var f=c.data; var n=Root(f);
                if(n?.Joint==null)continue;
                Chains.Add(new Chain {Root=n, Ignore=new HashSet<Node>(f.Refs("ignoreTransforms").Where(nodes.ContainsKey).Select(i=>nodes[i])),
                    Endpoint=f.Vector("endpointPosition"), MultiChild=f.Int("multiChildType"),IgnoreOtherRoots=f.Int("ignoreOtherPhysBones",1)!=0,
                    Colliders=f.Refs("colliders").Where(colliders.ContainsKey).Select(i=>colliders[i])
                        .Concat(colliders.Values.Where(collider=>collider.GlobalSelf && f.AllowsSelfCollision)).Distinct().ToArray(), Parameters=f});
            }
        }
    }
}
