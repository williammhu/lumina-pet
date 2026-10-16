using System;
using System.Collections.Generic;
using System.Linq;

namespace LuminaDesktop
{
    public sealed class PetAction
    {
        public string State,Label,Category;
        public double Seconds;
        public int Weight;
        public bool Click;
    }

    // Selection holds metadata only. FrameStream remains the sole owner of decoded pixels.
    public sealed class ActionDirector
    {
        readonly Random random;
        readonly List<PetAction> ambient,clicks;
        PetAction active;
        DateTime until,next;
        string previous="",previousClick="";
        public ActionDirector(IEnumerable<PetAction> actions,DateTime now,int seed)
        {
            random=new Random(seed);ambient=actions.Where(a=>a.Weight>0).ToList();clicks=actions.Where(a=>a.Click).ToList();Reset(now);
        }
        public void Reset(DateTime now){active=null;next=now.AddSeconds(8+random.Next(9));}
        PetAction Pick(List<PetAction> pool,string last)
        {
            var choices=pool.Where(a=>pool.Count==1||a.State!=last).ToList();
            if(choices.Count==0)return null;
            int ticket=random.Next(choices.Sum(a=>Math.Max(1,a.Weight)));
            foreach(var a in choices){ticket-=Math.Max(1,a.Weight);if(ticket<0)return a;}
            return choices[choices.Count-1];
        }
        public string ClickResponse(){var a=Pick(clicks,previousClick);if(a==null)return "waving";previousClick=a.State;return a.State;}
        public string Select(string workState,bool enabled,DateTime now)
        {
            if(workState!="idle"||!enabled){Reset(now);return workState;}
            if(active!=null&&now<until)return active.State;
            if(active!=null){Reset(now);return "idle";}
            if(now<next)return "idle";
            active=Pick(ambient,previous);if(active==null){Reset(now);return "idle";}
            previous=active.State;until=now.AddSeconds(Math.Max(.5,active.Seconds));return active.State;
        }
    }
}
