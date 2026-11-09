using System;
using System.IO;
using System.Text;
using AvaAvaDance.Import;
class UnityMaterialDataTests
{
    static int tests;
    const string Head="%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!21 &2100000\nMaterial:\n  serializedVersion: 8\n  m_Name: \"Face \\u65e5\"\n  m_Shader: {fileID: 4800000, guid: 0123456789abcdef0123456789abcdef, type: 3}\n  m_CustomRenderQueue: 2450\n  m_SavedProperties:\n    serializedVersion: 3\n";
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);tests++;}
    static void Reject(string data,string name){try{UnityMaterialData.Parse(data);throw new Exception("Accepted "+name);}catch(InvalidDataException){tests++;}}
    static void Main()
    {
        var m=UnityMaterialData.Parse(Head+"    m_TexEnvs:\n    - _MainTex:\n        m_Texture: {fileID: 2800000, guid: ABCDEF0123456789ABCDEF0123456789, type: 3}\n        m_Scale: {x: 2, y: 3}\n        m_Offset: {x: -0.5, y: 1e-2}\n    m_Ints:\n    - _Mode: 2\n    m_Floats:\n    - _Cutoff: 0.5\n    m_Colors:\n    - _Color: {r: 1, g: 0.5, b: 0, a: 0.75}\n  m_BuildTextureStacks: []\n");
        Check(m.Name=="Face 日","name");Check(m.ShaderFileId==4800000&&m.ShaderGuid=="0123456789abcdef0123456789abcdef","shader");
        Check(m.CustomRenderQueue==2450,"queue");Check(m.Floats["_Cutoff"]==0.5f,"float");Check(m.Ints["_Mode"]==2,"int");
        Check(m.Colors["_Color"].A==0.75f&&m.Colors["_Color"].G==0.5f,"color");
        Check(m.Textures["_MainTex"].Scale.X==2&&m.Textures["_MainTex"].Offset.Y==0.01f,"tex transform");
        Check(m.Textures["_MainTex"].Guid=="abcdef0123456789abcdef0123456789","tex guid");
        var builtin=UnityMaterialData.Parse("Material:\n  m_Name: 'Bob''s material'\n  m_Shader: {fileID: 46}\n  m_SavedProperties:\n    m_Floats: []\n    m_Ints: []\n    m_Colors: []\n    m_TexEnvs:\n    - _MainTex:\n        m_Texture: {fileID: 0}\n");
        Check(builtin.ShaderGuid==null&&builtin.CustomRenderQueue==-1,"builtin");Check(builtin.Name=="Bob's material","single quoted");
        Check(builtin.Textures["_MainTex"].Scale.Y==1&&builtin.Textures["_MainTex"].Offset.X==0,"tex defaults");
        Check(UnityMaterialData.ResolveByName(new[]{m,builtin},"Material::Face 日")==m,"resolve prefix");
        Check(UnityMaterialData.ResolveByName(new[]{m,m},m.Name)==null,"ambiguous resolve");
        Reject(Head+"    m_Floats:\n    - _A: NaN\n","NaN");
        Reject(Head+"    m_Floats:\n    - _A: Infinity\n","infinity");
        Reject(Head+"    m_Floats:\n    - _A: 1e999\n","overflow");
        Reject(Head+"    m_Ints:\n    - _A: 1.5\n","fraction int");
        Reject(Head+"    m_Ints:\n    - _A: 2147483648\n","int overflow");
        Reject(Head+"    m_Floats:\n    - _A: 1\n    - _A: 2\n","duplicate property");
        Reject(Head+"    m_Floats:\n    - _A: 1\n    m_Ints:\n    - _A: 1\n","cross-section duplicate");
        Reject(Head+"    m_Floats: []\n    m_Floats: []\n","duplicate section");
        Reject(Head+"    m_Colors:\n    - _C: {r: 1, g: 1, b: 1}\n","missing alpha");
        Reject(Head+"    m_Colors:\n    - _C: {r: 1, g: 1, b: 1, a: 1, r: 0}\n","duplicate inline");
        Reject(Head+"    m_TexEnvs:\n    - _T:\n        m_Texture: {fileID: 1, guid: ../../x}\n","bad guid");
        Reject(Head+"    m_TexEnvs:\n    - _T:\n        m_Scale: {x: 1, y: 1}\n        m_Scale: {x: 2, y: 2}\n","duplicate tex field");
        Reject(Head+"    m_Floats:\n    - _A: !!float 1\n","tag");
        Reject(Head+"    m_Floats:\n    - _A: *alias\n","alias");
        Reject("Material:\n  m_Name: !!custom object\n  m_Shader: {fileID: 0}\n","custom object");
        Reject("%TAG !evil! tag:example.com\nMaterial:\n  m_Shader: {fileID: 0}\n","custom directive");
        Reject(Head+"--- !u!21 &2\nMaterial:\n","multiple docs");
        Reject("Material:\n  m_Name: no shader\n","no shader");
        Reject(Head+"    m_Floats:\n\t- _A: 1\n","tab");
        Reject(new string('a',UnityMaterialData.MaxUtf8Bytes+1),"length bound");
        Reject(new string('日',UnityMaterialData.MaxUtf8Bytes/2),"UTF8 bound");
        var lots=new StringBuilder(Head+"    m_Floats:\n");for(int i=0;i<512;i++)lots.Append("    - _P"+i+": 1\n");
        Check(UnityMaterialData.Parse(lots.ToString()).Floats.Count==512,"512 properties accepted");
        lots.Append("    - _P512: 1\n");Reject(lots.ToString(),"513 properties");
        const string keywordHead="Material:\n  m_Name: keywords\n  m_Shader: {fileID: 46}\n";
        var oldKeywords=UnityMaterialData.Parse(keywordHead+"  m_ShaderKeywords: _NORMALMAP  _ALPHATEST_ON _NORMALMAP\n");
        Check(oldKeywords.Keywords.Count==2&&oldKeywords.Keywords[0]=="_NORMALMAP"&&oldKeywords.Keywords[1]=="_ALPHATEST_ON","old keywords deduplicated");
        var newKeywords=UnityMaterialData.Parse(keywordHead+"  m_ValidKeywords:\n  - _NORMALMAP\n  - _ALPHATEST_ON\n  - _NORMALMAP\n  m_InvalidKeywords:\n  - _OBSOLETE\n  m_CustomRenderQueue: 2000\n");
        Check(newKeywords.Keywords.Count==2&&newKeywords.Keywords[1]=="_ALPHATEST_ON"&&newKeywords.CustomRenderQueue==2000,"new keywords and ignored invalid");
        Check(UnityMaterialData.Parse(keywordHead+"  m_ValidKeywords: []\n  m_InvalidKeywords: []\n").Keywords.Count==0,"empty keywords");
        Check(UnityMaterialData.Parse(keywordHead+"  m_ShaderKeywords: \"_NORMALMAP\"\n  m_ValidKeywords:\n  - _NORMALMAP\n").Keywords.Count==1,"combined keyword dedup");
        Reject(keywordHead+"  m_ShaderKeywords: bad-key\n","keyword punctuation");
        Reject(keywordHead+"  m_ValidKeywords:\n  - 日本語\n","keyword non-ascii");
        Reject(keywordHead+"  m_ValidKeywords:\n  - *alias\n","keyword alias");
        Reject(keywordHead+"  m_ValidKeywords:\n  - !tag VALUE\n","keyword tag");
        Reject(keywordHead+"  m_ValidKeywords:\n  - "+new string('A',129)+"\n","keyword length");
        Reject(keywordHead+"  m_ValidKeywords: [_NORMALMAP]\n","unsupported inline keyword list");
        var keywordLimit=new StringBuilder(keywordHead+"  m_ValidKeywords:\n");
        for(int i=0;i<64;i++)keywordLimit.Append("  - _K"+i+"\n");
        Check(UnityMaterialData.Parse(keywordLimit.ToString()).Keywords.Count==64,"64 keyword limit accepted");
        keywordLimit.Append("  - _K64\n");Reject(keywordLimit.ToString(),"65 keywords rejected");
        var zeroTexture=UnityMaterialData.Parse(Head+"    m_TexEnvs:\n    - _Empty:\n        m_Texture: {fileID: 0, guid: 00000000000000000000000000000000, type: 3}\n");
        Check(zeroTexture.Textures["_Empty"].FileId==0&&zeroTexture.Textures["_Empty"].Guid=="00000000000000000000000000000000","zero texture reference");
        Console.WriteLine("PASS "+tests+" material checks");
    }
}