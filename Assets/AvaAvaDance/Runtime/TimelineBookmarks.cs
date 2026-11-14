using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        [Serializable] sealed class BookmarkNote {public int frame;public string memo;}
        readonly Dictionary<int,string> bookmarkMemos=new Dictionary<int,string>();

        void ChangeBookmark(int from,int? to,string memo)
        {
            bool existed=bookmarks.Contains(from);
            bookmarkMemos.TryGetValue(from,out string oldMemo);
            if(to.HasValue && to.Value!=from && bookmarks.Contains(to.Value)){status.text="移動先にブックマークがあります。";return;}
            void Clear(int at){bookmarks.Remove(at);bookmarkMemos.Remove(at);}
            void Put(int at,string note){bookmarks.Add(at);if(!string.IsNullOrEmpty(note))bookmarkMemos[at]=note;else bookmarkMemos.Remove(at);}
            void Apply(MotionTrack _,bool forward)
            {
                if(forward){Clear(from);if(to.HasValue)Put(to.Value,memo);}
                else{if(to.HasValue)Clear(to.Value);if(existed)Put(from,oldMemo);}
                timeline.MarkDirtyRepaint();
            }
            Apply(null,true);PushOperation(null,Apply);
        }

        void EditBookmark(int at)
        {
            ShowDialog("ブックマーク",out var body);
            var target=new IntegerField("フレーム"){value=at};
            var note=new TextField("メモ"){value=bookmarkMemos.TryGetValue(at,out var memo)?memo:""};
            body.Add(target);body.Add(note);var buttons=Box(body,"actions","row");
            Action(buttons,"OK",true,()=>{if(target.value<0 || target.value>MaxFrame)return;CloseDialog();ChangeBookmark(at,target.value,note.value);});
            Action(buttons,"削除",true,()=>{CloseDialog();ChangeBookmark(at,null,null);});
            Action(buttons,"キャンセル",true,CloseDialog);
        }
    }
}
