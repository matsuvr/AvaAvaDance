using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AvaAvaDance
{
    static class WorkspacePreferences
    {
        static readonly string checkDirectory=CheckDirectory();
        static readonly Dictionary<string,object> temporary=new Dictionary<string,object>();
        static string CheckDirectory(){
            var args=Environment.GetCommandLineArgs();int output=Array.IndexOf(args,"-avaSmokeOutput");
            return output>=0 && output+1<args.Length?Path.Combine(Path.GetFullPath(args[output+1]),"settings"):null;
        }
        internal static string ExtensionRoot=>checkDirectory??Application.persistentDataPath;
        internal static string WindowDirectory=>Path.Combine(checkDirectory??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AvaAvaDance"),"Windows");

        internal static int GetInt(string key,int fallback=0)=>checkDirectory==null?PlayerPrefs.GetInt(key,fallback):
            temporary.TryGetValue(key,out var v) && v is int n?n:fallback;
        internal static string GetString(string key,string fallback="")=>checkDirectory==null?PlayerPrefs.GetString(key,fallback):
            temporary.TryGetValue(key,out var v) && v is string s?s:fallback;
        internal static void SetInt(string key,int value){if(checkDirectory==null)PlayerPrefs.SetInt(key,value);else temporary[key]=value;}
        internal static void SetString(string key,string value){if(checkDirectory==null)PlayerPrefs.SetString(key,value);else temporary[key]=value;}
        internal static void DeleteKey(string key){if(checkDirectory==null)PlayerPrefs.DeleteKey(key);else temporary.Remove(key);}
    }
}
