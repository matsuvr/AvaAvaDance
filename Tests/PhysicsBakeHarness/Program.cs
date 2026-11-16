using System;
using System.Linq;
using AvaAvaDance;
class Program
{
    static int checks;
    static void Check(bool ok) { checks++; if(!ok) throw new Exception("Assertion " + checks); }
    static void Reject(Action action) { bool rejected = false; try { action(); } catch(ArgumentException) { rejected = true; } Check(rejected); }
    static void Main()
    {
        PhysicsBakeRange.Validate(0,0,600000,1,1000000);
        Check(!PhysicsBakeRange.Guards(0,600000,600000).Any());
        Check(PhysicsBakeRange.Guards(0,0,600000).SequenceEqual(new[]{1}));
        Check(PhysicsBakeRange.Guards(600000,600000,600000).SequenceEqual(new[]{599999}));
        Check(PhysicsBakeRange.Guards(100,120,600000).SequenceEqual(new[]{99,121}));
        Reject(()=>PhysicsBakeRange.Validate(-1,10,600000,1,1000000));
        Reject(()=>PhysicsBakeRange.Validate(20,10,600000,1,1000000));
        Reject(()=>PhysicsBakeRange.Validate(0,600001,600000,1,1000000));
        Reject(()=>PhysicsBakeRange.Validate(0,0,600000,0,1000000));
        Reject(()=>PhysicsBakeRange.Validate(0,1000,600000,1000,1000000));
        Reject(()=>PhysicsBakeRange.Validate(0,int.MaxValue,int.MaxValue,int.MaxValue,1000000));
        for(int first=0;first<=100;first++) for(int last=first;last<=100;last++) {
            PhysicsBakeRange.Validate(first,last,100,10,1000000);
            var guards=PhysicsBakeRange.Guards(first,last,100).ToArray();
            Check(guards.All(f=>f>=0 && f<=100 && (f<first || f>last)));
            Check(guards.Length==(first>0?1:0)+(last<100?1:0));
            Check(guards.Distinct().Count()==guards.Length);
        }
        Console.WriteLine("PASS: " + checks + " bake range assertions (not Unity physics execution)");
    }
}
