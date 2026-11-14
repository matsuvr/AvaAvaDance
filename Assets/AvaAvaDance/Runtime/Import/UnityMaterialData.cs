using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AvaAvaDance.Import
{
    /// <summary>Inert, bounded reader for the saved-property subset of Unity text .mat files.
    /// This is not a YAML deserializer. Scripts, tags, aliases and custom objects are never instantiated.</summary>
    public sealed class UnityMaterialData
    {
        public const int MaxUtf8Bytes = 2 * 1024 * 1024;
        public const int MaxProperties = 2048;
        public struct Float2 { public float X, Y; public Float2(float x, float y) { X=x; Y=y; } }
        public struct Float4 { public float R, G, B, A; public Float4(float r,float g,float b,float a) { R=r;G=g;B=b;A=a; } }
        public sealed class TextureReference
        {
            public string Guid;
            public long FileId;
            public Float2 Scale = new Float2(1,1);
            public Float2 Offset = new Float2(0,0);
        }
        private readonly List<string> keywords = new List<string>();
        public IReadOnlyList<string> Keywords { get { return keywords.AsReadOnly(); } }
        public List<string> DisabledPasses { get; } = new List<string>();
        public Dictionary<string,string> Tags { get; } = new Dictionary<string,string>(StringComparer.Ordinal);
        public string Name { get; private set; }
        public string ShaderGuid { get; private set; }
        public long ShaderFileId { get; private set; }
        public int CustomRenderQueue { get; private set; } = -1;
        public Dictionary<string,float> Floats { get; } = new Dictionary<string,float>(StringComparer.Ordinal);
        public Dictionary<string,int> Ints { get; } = new Dictionary<string,int>(StringComparer.Ordinal);
        public Dictionary<string,Float4> Colors { get; } = new Dictionary<string,Float4>(StringComparer.Ordinal);
        public Dictionary<string,TextureReference> Textures { get; } = new Dictionary<string,TextureReference>(StringComparer.Ordinal);

        public static UnityMaterialData Parse(string yaml)
        {
            if (yaml == null) throw new ArgumentNullException(nameof(yaml));
            if (yaml.Length > MaxUtf8Bytes || Encoding.UTF8.GetByteCount(yaml) > MaxUtf8Bytes)
                throw Bad("Material text exceeds 2 MiB.");
            var result = new UnityMaterialData();
            bool root=false, saved=false, document=false, tags=false;
            string section=null, keywordSection=null;
            int propertyCount=0, lines=0;
            TextureReference texture=null;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            var properties = new HashSet<string>(StringComparer.Ordinal);
            var textureFields = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = new StringReader(yaml))
            {
                string line;
                while ((line=reader.ReadLine()) != null)
                {
                    if (++lines > 32768 || line.Length > 16384) throw Bad("Material text is too complex.");
                    if (line.IndexOf('\t') >= 0 || line.IndexOf('\0') >= 0) throw Bad("Invalid material whitespace.");
                    int indent=0; while(indent<line.Length && line[indent]==' ') indent++;
                    string t=line.Substring(indent).TrimEnd();
                    if (t.Length==0 || t.StartsWith("#",StringComparison.Ordinal)) continue;
                    if (t.StartsWith("%",StringComparison.Ordinal))
                    {
                        if (root || (t!="%YAML 1.1" && t!="%TAG !u! tag:unity3d.com,2011:")) throw Bad("Unsupported YAML directive.");
                        continue;
                    }
                    if (t.StartsWith("---",StringComparison.Ordinal))
                    {
                        if (root || document || !t.StartsWith("--- !u!21 &",StringComparison.Ordinal)) throw Bad("Expected one Material document.");
                        long anchor; if(!long.TryParse(t.Substring(11), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,out anchor)) throw Bad("Invalid material document header.");
                        document=true;continue;
                    }
                    if (!root)
                    {
                        if(indent!=0 || t!="Material:") throw Bad("Expected Material root.");
                        root=true;continue;
                    }
                    if(indent==0) throw Bad("Multiple YAML roots are unsupported.");
                    if(indent==2 && t.StartsWith("- ",StringComparison.Ordinal))
                    {
                        if(keywordSection=="disabledShaderPasses")
                        {
                            var pass=Scalar(t.Substring(2).Trim());
                            if(pass.Length==0 || pass.Length>128 || result.DisabledPasses.Count>=64) throw Bad("Invalid disabled shader pass.");
                            result.DisabledPasses.Add(pass);
                        }
                        else if(keywordSection=="m_ValidKeywords") result.AddKeyword(Scalar(t.Substring(2).Trim()));
                        else if(keywordSection=="m_InvalidKeywords") RejectActiveScalar(t.Substring(2).Trim());
                        else throw Bad("Unexpected material sequence.");
                        continue;
                    }
                    if(keywordSection!=null && indent>2) throw Bad("Unsupported keyword indentation.");
                    if(indent==2)
                    {
                        saved=false; section=null; texture=null; keywordSection=null; tags=false;
                        string key,value; Split(t,out key,out value);
                        if(key=="m_Name" || key=="m_Shader" || key=="m_CustomRenderQueue" || key=="m_SavedProperties")
                        {
                            if(!fields.Add(key)) throw Bad("Duplicate material field.");
                            if(key=="m_Name") result.Name=Scalar(value);
                            else if(key=="m_Shader") {string guid; long id; Reference(value,out guid,out id);result.ShaderGuid=guid;result.ShaderFileId=id;}
                            else if(key=="m_CustomRenderQueue") result.CustomRenderQueue=Integer(value);
                            else { if(value.Length!=0 && value!="{}") throw Bad("Invalid saved properties."); saved=value.Length==0; }
                        }
                        else if(key=="m_ShaderKeywords")
                        {
                            if(!fields.Add(key)) throw Bad("Duplicate keyword field.");
                            string raw=Scalar(value);
                            string[] tokens=raw.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
                            if(tokens.Length>64) throw Bad("Too many shader keyword tokens.");
                            foreach(string token in tokens) result.AddKeyword(token);
                        }
                        else if(key=="m_ValidKeywords" || key=="m_InvalidKeywords" || key=="disabledShaderPasses")
                        {
                            if(!fields.Add(key)) throw Bad("Duplicate keyword field.");
                            if(value.Length!=0 && value!="[]") throw Bad("Invalid keyword sequence.");
                            keywordSection=value.Length==0?key:null;
                        }
                        else if(key=="stringTagMap")
                        {
                            if(!fields.Add(key) || (value.Length!=0 && value!="{}")) throw Bad("Invalid material tag map.");
                            tags=value.Length==0;
                        }
                        else RejectActiveScalar(value);
                        continue;
                    }
                    if(tags)
                    {
                        string key,value; Split(t,out key,out value);
                        key=Scalar(key); value=Scalar(value);
                        if(indent!=4 || key.Length>128 || value.Length>256 || result.Tags.Count>=64 || result.Tags.ContainsKey(key)) throw Bad("Invalid material tag.");
                        result.Tags.Add(key,value); continue;
                    }
                    if(!saved) continue;
                    if(indent==4 && !t.StartsWith("- ",StringComparison.Ordinal))
                    {
                        string key,value;Split(t,out key,out value);
                        texture=null;
                        if(key=="m_Floats" || key=="m_Ints" || key=="m_Colors" || key=="m_TexEnvs")
                        {
                            if(!fields.Add(key)) throw Bad("Duplicate property section.");
                            if(value.Length!=0 && value!="[]") throw Bad("Invalid property list.");
                            section=value=="[]"?null:key;
                        }
                        else {section=null;RejectActiveScalar(value);}
                        continue;
                    }
                    if(section==null) continue;
                    if(indent==4 && t.StartsWith("- ",StringComparison.Ordinal))
                    {
                        string key,value;Split(t.Substring(2),out key,out value);key=PropertyName(key);
                        if(++propertyCount>MaxProperties || !properties.Add(key)) throw Bad("Too many or duplicate material properties.");
                        texture=null;
                        if(section=="m_Floats") result.Floats.Add(key,Number(value));
                        else if(section=="m_Ints") result.Ints.Add(key,Integer(value));
                        else if(section=="m_Colors")
                        {
                            var v=Map(value); Require(v,"r","g","b","a");
                            result.Colors.Add(key,new Float4(VectorComponent(v["r"]),VectorComponent(v["g"]),VectorComponent(v["b"]),VectorComponent(v["a"])));
                        }
                        else
                        {
                            if(value.Length!=0) throw Bad("Invalid texture property.");
                            texture=new TextureReference();textureFields.Clear();result.Textures.Add(key,texture);
                        }
                        continue;
                    }
                    if(section=="m_TexEnvs" && indent==8 && texture!=null)
                    {
                        string key,value;Split(t,out key,out value);
                        if(!textureFields.Add(key)) throw Bad("Duplicate texture field.");
                        if(key=="m_Texture") {string guid;long id;Reference(value,out guid,out id);texture.Guid=guid;texture.FileId=id;}
                        else if(key=="m_Scale" || key=="m_Offset")
                        {var v=Map(value);Require(v,"x","y");var xy=new Float2(Number(v["x"]),Number(v["y"]));if(key=="m_Scale")texture.Scale=xy;else texture.Offset=xy;}
                        else throw Bad("Unknown texture field.");
                        continue;
                    }
                    throw Bad("Unsupported saved-property indentation or structure.");
                }
            }
            if(!root || !fields.Contains("m_Shader")) throw Bad("Material or shader field is missing.");
            return result;
        }
        /// <summary>Conservative exact-name lookup; ambiguous matches deliberately return null.</summary>
        public static UnityMaterialData ResolveByName(IEnumerable<UnityMaterialData> materials, string fbxMaterialName)
        {
            if(materials==null) throw new ArgumentNullException(nameof(materials));
            if(string.IsNullOrEmpty(fbxMaterialName)) return null;
            string name=fbxMaterialName.StartsWith("Material::",StringComparison.Ordinal)?fbxMaterialName.Substring(10):fbxMaterialName;
            UnityMaterialData match=null;
            foreach(var material in materials)
                if(material!=null && string.Equals(material.Name,name,StringComparison.Ordinal))
                {if(match!=null)return null;match=material;}
            return match;
        }
        private void AddKeyword(string keyword)
        {
            if(keyword.Length==0 || keyword.Length>128) throw Bad("Invalid shader keyword length.");
            foreach(char c in keyword)
                if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'))
                    throw Bad("Invalid shader keyword token.");
            if(keywords.Contains(keyword)) return;
            if(keywords.Count>=64) throw Bad("Too many shader keywords.");
            keywords.Add(keyword);
        }
        private static void Split(string text,out string key,out string value)
        {int colon=text.IndexOf(':');if(colon<=0)throw Bad("Expected material key/value.");key=text.Substring(0,colon).Trim();value=text.Substring(colon+1).Trim();}
        private static string PropertyName(string value)
        {
            value=Scalar(value);
            if(value.Length==0 || value.Length>128)throw Bad("Invalid shader property name.");
            foreach(char c in value)if(!(char.IsLetterOrDigit(c)||c=='_'))throw Bad("Unsupported shader property name.");
            return value;
        }
        private static string Scalar(string value)
        {
            RejectActiveScalar(value);
            if(value.Length==0)return value;
            if(value[0]=='\'')
            {if(value.Length<2||value[value.Length-1]!='\'')throw Bad("Unclosed quoted scalar.");return value.Substring(1,value.Length-2).Replace("''","'");}
            if(value[0]=='"')
            {
                if(value.Length<2||value[value.Length-1]!='"')throw Bad("Unclosed quoted scalar.");
                var s=new StringBuilder();
                for(int i=1;i<value.Length-1;i++)
                {
                    char c=value[i];if(c=='"')throw Bad("Invalid quote in scalar.");
                    if(c=='\\')
                    {
                        if(++i>=value.Length-1)throw Bad("Invalid string escape.");c=value[i];
                        if(c=='u') {if(i+4>=value.Length-1)throw Bad("Invalid unicode escape.");int code;if(!int.TryParse(value.Substring(i+1,4),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out code))throw Bad("Invalid unicode escape.");s.Append((char)code);i+=4;continue;}
                        if(c=='n')c='\n';else if(c=='r')c='\r';else if(c=='t')c='\t';else if(c!='\\'&&c!='"'&&c!='/')throw Bad("Unsupported string escape.");
                    }
                    s.Append(c);
                }
                return s.ToString();
            }
            return value;
        }
        private static void RejectActiveScalar(string value)
        {
            if(value.Length!=0 && (value[0]=='!'||value[0]=='&'||value[0]=='*'||value[0]=='|'||value[0]=='>'))
                throw Bad("YAML tags, aliases, anchors and multiline scalars are unsupported.");
        }
        private static Dictionary<string,string> Map(string value)
        {
            if(value.Length<2||value[0]!='{'||value[value.Length-1]!='}')throw Bad("Expected inline material map.");
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(string item in value.Substring(1,value.Length-2).Split(','))
            {string k,v;Split(item.Trim(),out k,out v);RejectActiveScalar(v);if(result.ContainsKey(k))throw Bad("Duplicate inline key.");result.Add(k,v);if(result.Count>8)throw Bad("Inline map too large.");}
            return result;
        }
        private static void Require(Dictionary<string,string> value,params string[] keys)
        {if(value.Count!=keys.Length)throw Bad("Unexpected inline map key.");foreach(string k in keys)if(!value.ContainsKey(k))throw Bad("Missing inline map key.");}
        private static void Reference(string value,out string guid,out long id)
        {
            var map=Map(value);string file;
            if(!map.TryGetValue("fileID",out file)||!long.TryParse(file,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out id))throw Bad("Invalid fileID.");
            foreach(string k in map.Keys)if(k!="fileID"&&k!="guid"&&k!="type")throw Bad("Unexpected reference key.");
            if(map.ContainsKey("type"))Integer(map["type"]);
            guid=null;string raw;
            if(map.TryGetValue("guid",out raw))
            {if(raw.Length!=32)throw Bad("Invalid reference GUID.");foreach(char c in raw)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F')))throw Bad("Invalid reference GUID.");guid=raw.ToLowerInvariant();}
        }
        private static int Integer(string text)
        {int n;if(!int.TryParse(text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out n))throw Bad("Invalid integer material value.");return n;}
        private static float Number(string text)
        {float n;if(!float.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out n)||float.IsNaN(n)||float.IsInfinity(n))throw Bad("Invalid float material value.");return n;}
        private static float VectorComponent(string text)
        {
            // Unity serializes shader vectors in m_Colors. Particle fade reciprocals can be infinite.
            if (text == "Infinity") return float.PositiveInfinity;
            if (text == "-Infinity") return float.NegativeInfinity;
            return Number(text);
        }
        private static InvalidDataException Bad(string text){return new InvalidDataException(text);}
    }
}
