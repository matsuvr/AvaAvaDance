using System;
using System.Collections.Generic;
using System.Linq;
using Ava.Extensions;
using UnityEngine;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        sealed class EvaluationHook
        {
            public string Id;
            public int Order;
            public string[] After;
            public Action<EvaluationSample> Run;
            public bool Active=true;
        }

        readonly Dictionary<EvaluationStage,EvaluationHook[]> evaluationHooks=new Dictionary<EvaluationStage,EvaluationHook[]>();
        IDisposable ObserveStage(string id,EvaluationStage stage,int order,string[] after,Action<EvaluationSample> callback)
        {
            if(!Enum.IsDefined(typeof(EvaluationStage),stage) || callback==null)throw new ArgumentException(nameof(stage));
            if(!evaluationHooks.TryGetValue(stage,out var current))current=Array.Empty<EvaluationHook>();
            if(evaluationHooks.Values.Any(group=>group.Any(h=>h.Id==id)))throw new ArgumentException("評価フックのIDが重複しています。");
            var item=new EvaluationHook{Id=id,Order=order,After=after?.Distinct().ToArray()??Array.Empty<string>(),Run=callback};
            var pending=current.Append(item).OrderBy(h=>h.Order).ThenBy(h=>h.Id,StringComparer.Ordinal).ToList();
            var sorted=new List<EvaluationHook>();var seen=new HashSet<string>();
            if(item.After.Any(dep=>pending.All(h=>h.Id!=dep)))throw new ArgumentException("同じ段階に依存先がありません。");
            while(pending.Count>0){
                var next=pending.Find(h=>h.After.All(dep=>seen.Contains(dep) || !pending.Any(p=>p.Id==dep)));
                if(next==null)throw new ArgumentException("評価順が循環しています。");
                sorted.Add(next);pending.Remove(next);seen.Add(next.Id);
            }
            evaluationHooks[stage]=sorted.ToArray();
            return new Registration(()=>{
                item.Active=false;
                evaluationHooks[stage]=evaluationHooks[stage].Where(h=>h!=item).ToArray();
            });
        }

        void EmitEvaluation(EvaluationStage stage,float time,bool step=false,string actorId=null)
        {
            if(!evaluationHooks.TryGetValue(stage,out var hooks) || hooks.Length==0)return;
            var sample=new EvaluationSample(stage,time,step,actorId);
            foreach(var hook in hooks)
                if(hook.Active)try{hook.Run(sample);}catch(Exception ex){Debug.LogWarning(hook.Id+": "+ex.Message);}
        }
    }
}
