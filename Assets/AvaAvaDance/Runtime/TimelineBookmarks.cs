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

        void ConfirmClearBookmarks()
        {
            if(bookmarks.Count==0)return;
            ShowDialog("ブックマークをすべて削除",out var body);
            body.Add(new Label(bookmarks.Count+" 件のブックマークとメモを削除します。Undoで元に戻せます。"));
            var buttons=Box(body,"clear-bookmarks","row");
            Action(buttons,"削除",true,()=>{CloseDialog();ClearBookmarks();});
            Action(buttons,"キャンセル",true,CloseDialog);
        }

        void ClearBookmarks()
        {
            if(bookmarks.Count==0)return;
            var frames=bookmarks.ToArray();
            var notes=new Dictionary<int,string>(bookmarkMemos);
            void Apply(MotionTrack _,bool forward)
            {
                bookmarks.Clear();bookmarkMemos.Clear();
                if(!forward){foreach(int at in frames)bookmarks.Add(at);foreach(var note in notes)bookmarkMemos[note.Key]=note.Value;}
                RefreshTimelineLabels();
            }
            Apply(null,true);PushOperation(null,Apply);
        }

        void CheckClearBookmarks()
        {
            var original=bookmarks.ToArray();var originalNotes=new Dictionary<int,string>(bookmarkMemos);
            int history=undo.Count;
            try
            {
                bookmarks.Clear();bookmarkMemos.Clear();bookmarks.Add(0);bookmarks.Add(MaxFrame);bookmarkMemos[MaxFrame]="保存メモ";
                ClearBookmarks();
                if(bookmarks.Count!=0 || bookmarkMemos.Count!=0 || undo.Count!=history+1)throw new InvalidOperationException("bookmark clear transaction");
                ApplyHistory(false);
                if(!bookmarks.SetEquals(new[]{0,MaxFrame}) || bookmarkMemos[MaxFrame]!="保存メモ")throw new InvalidOperationException("bookmark clear undo");
                ApplyHistory(true);
                if(bookmarks.Count!=0 || bookmarkMemos.Count!=0)throw new InvalidOperationException("bookmark clear redo");
                ClearBookmarks();if(undo.Count!=history+1)throw new InvalidOperationException("bookmark clear no-op");
                ApplyHistory(false);redo.Clear();
            }
            finally
            {
                bookmarks.Clear();bookmarks.UnionWith(original);bookmarkMemos.Clear();foreach(var p in originalNotes)bookmarkMemos[p.Key]=p.Value;
                RefreshTimelineLabels();
            }
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
