using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UniJSON;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class StateDescriptor
        {
            public string pluginId,pluginVersion,stateEntry;
            public int stateSchema,revision;
            public string[] blobEntries;
        }

        static string JsonObject(JsonNode? original,Dictionary<string,string> fields)
        {
            var parts=new List<string>();
            if(original.HasValue)
                foreach(var pair in original.Value.ObjectItems())
                    if(!fields.ContainsKey(pair.Key.GetString()))parts.Add(pair.Key.Value.Segment+":"+pair.Value.Value.Segment);
            parts.AddRange(fields.Select(f=>"\""+f.Key+"\":"+f.Value));return "{"+string.Join(",",parts)+"}";
        }

        static string BlobPath(PluginManifest manifest,string id)
        {
            if(string.IsNullOrEmpty(id) || id.Length>180 || id=="." || id==".." || id.IndexOfAny(new[]{'/','\\',':','\0'})>=0)
                throw new ArgumentException("バイナリ資産のIDが不正です。");
            return "extensions/"+manifest.id+"/blobs/"+id;
        }
        void StoreExtension(PluginManifest manifest,byte[] value,IReadOnlyDictionary<string,byte[]> blobChanges=null)
        {
            string key="extensions/"+manifest.id+"/state.json";
            var changed=new Dictionary<string,byte[]>();
            foreach(var blob in blobChanges??new Dictionary<string,byte[]>()){
                var path=BlobPath(manifest,blob.Key);
                if(blob.Value?.LongLength>64L*1024*1024)throw new IOException("バイナリ資産が大きすぎます。");
                if(extraEntries.TryGetValue(path,out var data)?blob.Value==null || !data.SequenceEqual(blob.Value):blob.Value!=null)
                    changed.Add(path,blob.Value==null?null:(byte[])blob.Value.Clone());
            }
            if(value==null && changed.Any(p=>p.Value!=null))throw new InvalidDataException("バイナリ資産を追加するには拡張の状態を指定してください。");
            if(changed.Count==0 && (extraEntries.TryGetValue(key,out var prior) ? value!=null && prior.SequenceEqual(value) : value==null))return;
            if(value?.LongLength>64L*1024*1024)throw new IOException("拡張設定が大きすぎます。");
            const string indexKey="extensions/index.json";
            JsonNode? root=null,existing=null;
            var entries=new List<string>();
            if(extraEntries.TryGetValue(indexKey,out var raw))
            {
                root=JsonParser.Parse(Encoding.UTF8.GetString(raw));
                if(root.Value["schemaVersion"].GetInt32()!=1)throw new InvalidDataException("拡張索引の版を読み取れません。");
                foreach(var block in root.Value["extensions"].ArrayItems())
                    if(block["pluginId"].GetString()==manifest.id)existing=block;else entries.Add(block.Value.Segment.ToString());
            }
            if(existing.HasValue && existing.Value["stateSchema"].GetInt32()>manifest.stateSchema)throw new InvalidDataException("拡張設定には新しい版が必要です。");
            if(value!=null)
            {
                var info=new StateDescriptor{pluginId=manifest.id,pluginVersion=manifest.version,stateEntry=key,stateSchema=manifest.stateSchema,
                    revision=1,blobEntries=extraEntries.Keys.Where(n=>n.StartsWith("extensions/"+manifest.id+"/blobs/",StringComparison.Ordinal) && !changed.ContainsKey(n)).Concat(changed.Where(p=>p.Value!=null).Select(p=>p.Key)).OrderBy(n=>n,StringComparer.Ordinal).ToArray()};
                var known=JsonParser.Parse(JsonUtility.ToJson(info)).ObjectItems().ToDictionary(p=>p.Key.GetString(),p=>p.Value.Value.Segment.ToString());
                entries.Add(JsonObject(existing,known));
            }
            string json=JsonObject(root,new Dictionary<string,string>{{"schemaVersion","1"},{"extensions","["+string.Join(",",entries)+"]"}});
            foreach(var entry in changed){if(entry.Value==null)extraEntries.Remove(entry.Key);else extraEntries[entry.Key]=entry.Value;}
            if(value!=null)extraEntries[key]=(byte[])value.Clone();else extraEntries.Remove(key);
            if(entries.Count==0 && (!root.HasValue || root.Value.ObjectItems().Count()==2))extraEntries.Remove(indexKey);
            else extraEntries[indexKey]=Encoding.UTF8.GetBytes(json);
        }
    }
}
