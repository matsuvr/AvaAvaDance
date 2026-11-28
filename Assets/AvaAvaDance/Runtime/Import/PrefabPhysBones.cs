using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Ava.Extensions;
using Asset = AvaAvaDance.Import.UnityPackageReader.PackageAsset;

namespace AvaAvaDance.Import
{
    // Resolve serialized prefab data against the selected FBX hierarchy.
    internal sealed class PrefabPhysBones
    {
        internal sealed class Node
        {
            internal Binding Model;
            internal Transform Joint=>Model?.Transform;
            internal Node Parent;
            internal readonly List<Node> Children = new List<Node>();
            internal Matrix4x4 PrefabWorld, ToJoint;
            internal bool Active;
        }

        internal sealed class Binding
        {
            internal string Name;
            internal int Index;
            internal Transform Transform;
            internal Matrix4x4 World;
            internal readonly List<Binding> Children=new List<Binding>();
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

        internal readonly List<RigShape> Shapes=new List<RigShape>();
        internal int Components, DisabledComponents, UnmappedComponents;
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

        internal static PrefabPhysBones Read(Asset prefab,GameObject avatar,Asset model)
        {
            var result = new PrefabPhysBones();
            if(prefab==null)return result;
            var transforms=avatar.GetComponentsInChildren<Transform>(true);
            var entries=transforms.Select((t,i)=>new Binding{Name=t.name,Index=i,Transform=t,World=t.localToWorldMatrix}).ToArray();
            var bindings=entries.ToDictionary(b=>b.Transform);
            foreach(var entry in entries)if(entry.Transform.parent!=null && bindings.TryGetValue(entry.Transform.parent,out var parent))parent.Children.Add(entry);
            var root=bindings[avatar.transform];
            if(root.Children.Count==1 && root.Children[0].Name=="")root=root.Children[0];
            result.Bind(Encoding.UTF8.GetString(prefab.Data),root,model);
            return result;
        }

        internal static RigShape[] ReadShapes(Asset prefab,RigProfile rig,Matrix4x4[] world,Asset model)
        {
            if(prefab==null || rig.joints.Length==0)return Array.Empty<RigShape>();
            var entries=rig.joints.Select((j,i)=>new Binding{Name=j.name,Index=i,
                World=world[i]}).ToArray();
            for(int i=0;i<entries.Length;i++)if(rig.joints[i].parent>=0)entries[rig.joints[i].parent].Children.Add(entries[i]);
            var root=Array.FindIndex(rig.joints,j=>j.parent<0);
            if(root<0)return Array.Empty<RigShape>();
            var result=new PrefabPhysBones();result.Bind(Encoding.UTF8.GetString(prefab.Data),entries[root],model);
            return result.Shapes.ToArray();
        }

        // The source model supplies the hierarchy for stripped GameObjects and Transforms.
        // Local references in components still use the prefab's own anchor IDs.
        static IEnumerable<Node> SourceHierarchy(Binding root, Asset model,
            Dictionary<long,long> objectIds, Dictionary<long,long> transformIds,
            Dictionary<long,Node> owners, Dictionary<long,Node> nodes)
        {
            var rest = new Dictionary<(string,string),Matrix4x4>();
            var skeleton = Regex.Match(model.Meta ?? "", @"(?m)^    skeleton:\r?\n((?:    (?:- |  )[^\n]*\n?)+)");
            foreach (Match bone in Regex.Matches(skeleton.Groups[1].Value,
                @"(?m)^    - name: ([^\r\n]*)\r?\n      parentName: ([^\r\n]*)\r?\n((?:      (?:position|rotation|scale): [^\n]*\n?){3})"))
            {
                var fields = new Fields(bone.Groups[3].Value.Replace("      ", "  "));
                rest[(bone.Groups[2].Value,bone.Groups[1].Value)] = Matrix4x4.TRS(fields.Vector("position"),
                    fields.Rotation("rotation"), fields.Vector("scale",Vector3.one));
            }

            var hierarchy = new List<Node>();
            void Add(Binding joint, Node parent, string path, int depth, bool uniquePath)
            {
                if (depth > 256 || path.Length > 16384) return;
                var world = joint.World;
                if (parent == null) world = Matrix4x4.identity;
                else if (rest.TryGetValue((parent.Model.Name, joint.Name), out var local) ||
                    (parent.Parent == null && rest.TryGetValue((System.IO.Path.GetFileNameWithoutExtension(model.Path)+"(Clone)", joint.Name),out local)))
                    world = parent.PrefabWorld * local;
                var node = new Node { Parent=parent, Model=joint, Active=true, PrefabWorld=world };
                parent?.Children.Add(node);
                hierarchy.Add(node);
                if (uniquePath)
                {
                    if (objectIds.TryGetValue(ModelFileId.Hash("Type:GameObject->"+path+"0"), out var obj)) owners[obj]=node;
                    if (transformIds.TryGetValue(ModelFileId.Hash("Type:Transform->"+path+"/Transform0"), out var tr)) nodes[tr]=node;
                }
                foreach (var siblings in joint.Children.GroupBy(child => child.Name))
                {
                    bool uniqueChild = uniquePath && siblings.Count() == 1;
                    foreach (var child in siblings) Add(child,node,path+"/"+child.Name,depth+1,uniqueChild);
                }
            }
            Add(root,null,"//RootNode/root",0,true);
            return hierarchy;
        }

        void Bind(string yaml,Binding fbxRoot,Asset model)
        {
            var objects=new Dictionary<long,(string name,bool active)>();
            var nodes=new Dictionary<long,Node>();
            var transforms=new Dictionary<long,(long owner,long parent,Fields data)>();
            var owners=new Dictionary<long,Node>();
            var components=new List<(long id,long script,Fields data)>();
            var meshOwners = new HashSet<long>();
            var sourceInstances = new HashSet<long>();
            var stripped = new List<(string type,long id,long source,long instance)>();
            var headers=Header.Matches(yaml);
            if(headers.Count>50000)return;
            for(int i=0;i<headers.Count;i++) {
                var h=headers[i]; string type=h.Groups[1].Value;
                if(type!="1" && type!="4" && type!="114" && type!="65" && type!="135" && type!="136" && type!="1001" && type!="137" && type!="33")continue;
                int start=h.Index+h.Length, end=i+1<headers.Count ? headers[i+1].Index : yaml.Length;
                var f=new Fields(yaml.Substring(start,end-start));
                long id=Id(h.Groups[2].Value);
                if(type=="137" || type=="33")
                {
                    if(f.Get("m_Mesh").IndexOf("guid: "+model.Guid,StringComparison.OrdinalIgnoreCase)>=0)
                        meshOwners.Add(f.Ref("m_GameObject"));
                    continue;
                }
                if(type=="1001")
                {
                    if (f.Get("m_SourcePrefab").IndexOf("guid: "+model.Guid,StringComparison.OrdinalIgnoreCase)>=0)
                        sourceInstances.Add(id);
                    continue;
                }
                if ((type=="1" || type=="4") && h.Value.Contains(" stripped"))
                {
                    var source=Regex.Match(f.Get("m_CorrespondingSourceObject"),@"fileID: (-?\d+), guid: ([0-9a-fA-F]{32})");
                    if(source.Success && string.Equals(model.Guid,source.Groups[2].Value,StringComparison.OrdinalIgnoreCase))
                        stripped.Add((type,id,Id(source.Groups[1].Value),f.Ref("m_PrefabInstance")));
                    continue;
                }
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
                if(type=="65" || type=="135" || type=="136")components.Add((id,Id(type),f));
                if(type=="114") {
                    string script=f.Get("m_Script");
                    if(script.IndexOf(SdkGuid,StringComparison.OrdinalIgnoreCase)<0)continue;
                    long fileId=Id(Regex.Match(script,@"\bfileID: (-?\d+)").Groups[1].Value);
                    if(fileId==PhysBoneId || fileId==ColliderId)components.Add((id,fileId,f));
                    if(fileId==PhysBoneId)Components++;
                }
            }
            if(components.Count==0)return;
            // A prefab may embed several FBXs with identically named armatures. Locate
            // the chosen FBX by matching its mesh nodes to renderers referencing its GUID.
            var meshNames = new HashSet<string>(meshOwners.Where(objects.ContainsKey).Select(id => objects[id].name), StringComparer.Ordinal);
            var meshPaths = new Dictionary<string,List<string[]>>(StringComparer.Ordinal);
            void IndexPaths(Binding node,List<string> path)
            {
                if (path.Count>=256) return;
                foreach(var child in node.Children)
                {
                    path.Add(child.Name);
                    if (meshNames.Contains(child.Name))
                    {
                        if (!meshPaths.TryGetValue(child.Name,out var paths)) meshPaths.Add(child.Name,paths=new List<string[]>());
                        paths.Add(path.ToArray());
                    }
                    IndexPaths(child,path);
                    path.RemoveAt(path.Count-1);
                }
            }
            var modelRoots=new HashSet<long>();
            if(meshOwners.Count>0)
            {
                IndexPaths(fbxRoot,new List<string>());
                foreach(var pair in transforms)
                {
                    if(!meshOwners.Contains(pair.Value.owner) || !objects.TryGetValue(pair.Value.owner,out var obj) ||
                        !meshPaths.TryGetValue(obj.name,out var paths))continue;
                    foreach(var path in paths)
                    {
                        long ancestor=pair.Key;
                        for(int i=path.Length-1;i>=0;i--)
                        {
                            if(!transforms.TryGetValue(ancestor,out var info) || !objects.TryGetValue(info.owner,out var entry) || entry.name!=path[i])
                            { ancestor=0; break; }
                            ancestor=info.parent;
                        }
                        if(ancestor!=0)modelRoots.Add(ancestor);
                    }
                }
            }
            var visited=new HashSet<long>();
            void Map(long id,Node parent,int depth) {
                if(depth>256 || !visited.Add(id) || !transforms.TryGetValue(id,out var info) || !objects.TryGetValue(info.owner,out var obj))return;
                var n=nodes[id]; var f=info.data;
                parent?.Children.Add(n);
                n.Parent=parent; n.Active=obj.active && (parent?.Active ?? true);
                n.PrefabWorld=parent==null ? Matrix4x4.identity : parent.PrefabWorld *
                    Matrix4x4.TRS(f.Vector("m_LocalPosition"),f.Rotation("m_LocalRotation"),f.Vector("m_LocalScale",Vector3.one));
                if(modelRoots.Count==1 ? modelRoots.Contains(id) : modelRoots.Count==0 && parent==null)n.Model=fbxRoot;
                else if(parent?.Model!=null)foreach(var child in parent.Model.Children){
                    if(child.Name!=obj.name)continue;
                    if(n.Model!=null){n.Model=null;break;}
                    n.Model=child;
                }
                foreach(var child in f.Refs("m_Children")) {
                    if(!transforms.TryGetValue(child,out var ci) || ci.parent!=id)continue;
                    Map(child,n,depth+1);
                }
            }
            foreach(var p in transforms)if(p.Value.parent==0)Map(p.Key,null,0);

            // Unity's ModelImporter can bake handedness/units into each joint while ufbx
            // keeps them on its scene root. Register the two bind spaces before posing the arms.
            IEnumerable<Node> mappedNodes = nodes.Values;
            if (sourceInstances.Count == 1 && stripped.Count != 0)
            {
                var refs = stripped.Where(r=>sourceInstances.Contains(r.instance)).ToArray();
                var objIds = refs.Where(r=>r.type=="1").GroupBy(r=>r.source).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single().id);
                var trIds = refs.Where(r=>r.type=="4").GroupBy(r=>r.source).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single().id);
                var inherited = SourceHierarchy(fbxRoot,model,objIds,trIds,owners,nodes);
                mappedNodes = nodes.Values.Concat(inherited).Distinct();
            }
            var segments=mappedNodes.Where(n=>n.Model!=null && n.Parent?.Model!=null)
                .Select(n=>(source:(Vector3)(n.PrefabWorld.GetColumn(3)-n.Parent.PrefabWorld.GetColumn(3)),
                            target:(Vector3)(n.Model.World.GetColumn(3)-n.Parent.Model.World.GetColumn(3))))
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
            foreach(var n in mappedNodes) {
                var anchor=n;
                while(anchor!=null && anchor.Model==null)anchor=anchor.Parent;
                if(anchor==null)continue;
                var inverse=anchor.Model.World.inverse;
                var local=inverse*basis*n.PrefabWorld;
                Vector3 offset=inverse.MultiplyVector(basis.MultiplyVector(n.PrefabWorld.GetColumn(3)-anchor.PrefabWorld.GetColumn(3)));
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
            double[] Values(Vector3 v)=>new double[]{v.x,v.y,v.z};
            foreach(var c in components.Where(c=>c.script!=PhysBoneId)) {
                var f=c.data;var n=Root(f);var anchor=n;
                while(anchor!=null && anchor.Model==null)anchor=anchor.Parent;
                if(anchor==null)continue;
                bool phys=c.script==ColliderId;
                if(phys && (f.Int("insideBounds")!=0 || f.Int("shapeType")>1))continue;
                var basisLocal=n.ToJoint;
                var center=basisLocal.MultiplyPoint3x4(f.Vector(phys?"position":"m_Center"));
                var bound=new RigShape{name=anchor.Model.Name+" "+(Shapes.Count+1),role=anchor.Model.Name,jointIndex=anchor.Model.Index,center=Values(center)};
                if(c.script==65){
                    var half=f.Vector("m_Size",Vector3.one)*.5f;
                    bound.shape="box";
                    bound.boxAxes=new[]{Vector3.right*half.x,Vector3.up*half.y,Vector3.forward*half.z}
                        .SelectMany(v=>Values(basisLocal.MultiplyVector(v))).ToArray();
                }else{
                    bool capsule=phys?f.Int("shapeType")==1:c.script==136;
                    var direction=f.Int("m_Direction",1);
                    var axis=phys?f.Rotation("rotation")*Vector3.up:direction==0?Vector3.right:direction==2?Vector3.forward:Vector3.up;
                    var up=basisLocal.MultiplyVector(axis);
                    float radius=Math.Max(0,f.Number(phys?"radius":"m_Radius"));
                    var scales=basisLocal.lossyScale;
                    float radiusScale=Mathf.Max(Mathf.Abs(scales.x),Mathf.Abs(scales.y),Mathf.Abs(scales.z));
                    bound.shape=capsule?"capsule":"sphere";bound.radius=radius*radiusScale;
                    bound.height=Math.Max(0,f.Number(phys?"height":"m_Height"))*up.magnitude;bound.axis=Values(up.normalized);
                }
                Shapes.Add(bound);
            }
            foreach(var c in components.Where(c=>c.script==ColliderId)) {
                var f=c.data; var n=Root(f); var anchor=n;
                while(anchor!=null && anchor.Model==null)anchor=anchor.Parent;
                if(anchor==null)continue;
                var up=n.ToJoint.MultiplyVector(f.Rotation("rotation")*Vector3.up);
                colliders[c.id]=new Collider {Anchor=anchor.Joint,Center=n.ToJoint.MultiplyPoint3x4(f.Vector("position")),
                    Up=up, Height=Mathf.Max(0,f.Number("height")), Radius=Mathf.Max(0,f.Number("radius")),
                    Shape=f.Int("shapeType"), Inside=f.Int("insideBounds")!=0,BonesAsSpheres=f.Int("bonesAsSpheres")!=0,
                    GlobalSelf=f.Int("shapeType")!=2 && (f.Int("globalCollision")==1 || (f.Int("globalCollision")==2 && f.Int("globalCollisionAllowSelf")!=0))};
            }
            foreach(var c in components.Where(c=>c.script==PhysBoneId)) {
                var f=c.data; var n=Root(f);
                if (n?.Joint==null)
                {
                    owners.TryGetValue(f.Ref("m_GameObject"),out var owner);
                    nodes.TryGetValue(f.Ref("rootTransform"),out var root);
                    if (f.Int("m_Enabled",1)==0 || owner?.Active==false || root?.Active==false) DisabledComponents++;
                    else UnmappedComponents++;
                    continue;
                }
                Chains.Add(new Chain {Root=n, Ignore=new HashSet<Node>(f.Refs("ignoreTransforms").Where(nodes.ContainsKey).Select(i=>nodes[i])),
                    Endpoint=f.Vector("endpointPosition"), MultiChild=f.Int("multiChildType"),IgnoreOtherRoots=f.Int("ignoreOtherPhysBones",1)!=0,
                    Colliders=f.Refs("colliders").Where(colliders.ContainsKey).Select(i=>colliders[i])
                        .Concat(colliders.Values.Where(collider=>collider.GlobalSelf && f.AllowsSelfCollision)).Distinct().ToArray(), Parameters=f});
            }
        }
    }
}
