using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

internal static partial class Program
{
    static ReleaseVelocityEstimator Track(double vx, double vy, int milliseconds=100, ReleaseVelocityTuning? tuning=null, long origin=0)
    {
        var e=new ReleaseVelocityEstimator(1000000,tuning);
        for(int ms=0;ms<=milliseconds;ms+=10)Check(e.Add(origin+ms*1000,new(-100+vx*ms/1000,50+vy*ms/1000)),"valid sample refused");
        return e;
    }
    static void ReleaseCases()
    {
        TailCases();
        ReleaseDiagnosticCases();
        foreach(var window in new[]{.08,.10,.12})foreach(var v in new[]{(1000d,0d),(-1000d,0d),(0d,-800d),(600d,-900d),(0d,0d)})
            Case("Release",$"{window}s direction {v}",()=>{var e=Track(v.Item1,v.Item2,(int)(window*1000),new(){WindowSeconds=window});var r=e.Estimate((long)(window*1000000));Near(r.Velocity.X,v.Item1);Near(r.Velocity.Y,v.Item2);});
        Case("Release","no samples or one sample zero",()=>{var e=new ReleaseVelocityEstimator(1000);Near(e.Estimate(100).Velocity.X,0);e.Add(100,new(0,0));Check(e.Estimate(100).Reason==ReleaseFailure.InsufficientSamples,"one sample fling");});
        foreach(var stamp in new[]{100000L,90000L})
            Case("Release",$"duplicate/reversed ticks clear history {stamp}",()=>{var e=Track(1000,0);Check(!e.Add(stamp,new(100,50)),"bad tick accepted");Check(e.Estimate(100000).Velocity==default,"stale history survived");});
        Case("Release","short drag rejected",()=>{var e=new ReleaseVelocityEstimator(1000000);e.Add(0,new(0,0));e.Add(1,new(100,0));Check(e.Estimate(1).Velocity==default,"tiny span fling");});
        Case("Release","dwell measured separately from last accepted sample",()=>{var e=Track(1000,0);Check(e.Estimate(221000).Velocity==default,"stale motion released");});
        Case("Release","stationary accepted samples remove old velocity",()=>{var e=Track(1000,0);for(int ms=110;ms<=250;ms+=10)e.Add(ms*1000,new(0,50));Check(e.Estimate(250000).Velocity==default,"stopped mouse flung");});
        Case("Release","single outlier robust fit",()=>{
            var e=new ReleaseVelocityEstimator(1000000);for(int i=0;i<=10;i++)e.Add(i*10000,i==5?new(900000,-800000):new(i*10,-i*5));
            var r=e.Estimate(100000);Near(r.Velocity.X,1000,1e-5);Near(r.Velocity.Y,-500,1e-5);
        });
        Case("Release","last-sample outlier robust fit",()=>{
            var e=new ReleaseVelocityEstimator(1000000);for(int i=0;i<=10;i++)e.Add(i*10000,i==10?new(-900000,0):new(i*10,0));Near(e.Estimate(100000).Velocity.X,1000,1e-5);
        });
        Case("Release","vector speed capped without direction distortion",()=>{var e=Track(60000,80000,tuning:new(){MaximumSpeed=5000});var r=e.Estimate(100000);Near(r.Velocity.X,3000);Near(r.Velocity.Y,4000);});
        Case("Release","low frequency still fits sufficient span",()=>{var e=new ReleaseVelocityEstimator(1000);e.Add(0,new(0,0));e.Add(50,new(50,0));e.Add(100,new(100,0));Near(e.Estimate(100).Velocity.X,1000);});
        Case("Release","two samples insufficient robust estimate",()=>{var e=new ReleaseVelocityEstimator(1000);e.Add(0,new(0,0));e.Add(100,new(100,0));Check(e.Estimate(100).Velocity==default,"unrobust two-frame difference");});
        Case("Release","reset invalidates old fling",()=>{var e=Track(1000,0);e.Reset();Check(e.Estimate(100000).Velocity==default,"reset kept motion");Check(e.Add(0,new(0,0)),"reset did not reset time origin");});
        foreach(var point in new[]{new PhysicalPoint(double.NaN,0),new(double.PositiveInfinity,0),new(1e200,0)})
            Case("Release",$"invalid position {point}",()=>{var e=Track(1000,0);Check(!e.Add(110000,point)&&e.Estimate(110000).Velocity==default,"bad sample stale velocity");});
        Case("Release","near long max ticks do not lose precision",()=>{var e=Track(1000,0,origin:long.MaxValue-200000);Near(e.Estimate(long.MaxValue-100000).Velocity.X,1000,1e-5);});
        Case("Release","tick wrap invalidates",()=>{var e=Track(1000,0,origin:long.MaxValue-200000);Check(!e.Add(long.MinValue,new(0,0))&&e.Estimate(0).Velocity==default,"overflow motion");});
        Case("Release","release time reversal invalidates history",()=>{var e=Track(1000,0);Check(e.Estimate(90000).Velocity==default&&e.Estimate(100000).Velocity==default,"reverse release reused history");});
        foreach(var frequency in new[]{0L,-1L,long.MaxValue})
            Case("Release",$"invalid frequency {frequency}",()=>{var e=new ReleaseVelocityEstimator(frequency);Check(!e.Add(0,new(0,0))&&e.Estimate(0).Velocity==default,"bad frequency accepted");});
        foreach(var tuning in new[]{new ReleaseVelocityTuning{WindowSeconds=double.NaN},new(){WindowSeconds=.001},new(){MaximumSpeed=-1},new(){DwellSeconds=-1},new(){MaximumSamples=100000},new(){MinimumSpanSeconds=0},new(){InlierDistance=double.PositiveInfinity},new(){StationaryDistance=-1}})
            Case("Release","invalid estimator tuning",()=>{var e=new ReleaseVelocityEstimator(1000,tuning);Check(!e.Add(0,new(0,0))&&e.Estimate(0).Reason==ReleaseFailure.InvalidTuning,"bad estimator config");});
        Case("Release","bounded history continuous long drag",()=>{var e=new ReleaseVelocityEstimator(1000000);for(int i=0;i<10000;i++)e.Add(i*1000,new(i,0));var r=e.Estimate(9999000);Check(r.Samples<=64,"unbounded history");Near(r.Velocity.X,1000);});
    }

    // These assertions catch a missing/stale snapshot, incorrect branch attribution,
    // or invented sample statistics. Reflection lets RED fail at runtime on R1.
    static object Diagnostic(ReleaseVelocityEstimator e) =>
        e.GetType().GetProperty("LastDiagnostic", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic) ?.GetValue(e)
        ?? throw new Exception("release diagnostic missing for this Estimate");
    static object? Field(object d, string name) => d.GetType().GetProperty(name)!.GetValue(d);
    static void Branch(ReleaseVelocityEstimator e, string branch) =>
        Check((string)Field(Diagnostic(e), "Branch")! == branch, "expected diagnostic branch " + branch);
    static void ReleaseDiagnosticCases()
    {
        Case("Diagnostic","1kHz bounded uniform accepted",()=>{
            var e=new ReleaseVelocityEstimator(1000000);
            for(int ms=0;ms<=100;ms++)e.Add(ms*1000,new(ms,-ms*2));
            var r=e.Estimate(100000);Near(r.Velocity.X,1000);Near(r.Velocity.Y,-2000);
            Branch(e,"Accepted");var d=Diagnostic(e);
            Check((int)Field(d,"BufferSamples")! ==64 && (int)Field(d,"ActiveSamples")! ==64 &&
                (int)Field(d,"InlierSamples")! ==64,"actual bounded counts");
            Near((double)Field(d,"ActiveSpanMs")!,63);Near((double)Field(d,"InlierSpanMs")!,63);
            Check((int)Field(d,"DistinctPositions")! ==64 && (int)Field(d,"DuplicatePositionCount")! ==0,"uniform positions");
        });
        foreach(bool decelerating in new[]{false,true})Case("Diagnostic","curved trajectory retains full refusal evidence "+decelerating,()=>{
            var e=new ReleaseVelocityEstimator(1000000);
            for(int ms=0;ms<=100;ms++){double t=ms/1000d;e.Add(ms*1000,new(decelerating?4000*t-20000*t*t:20000*t*t,0));}
            e.Estimate(100000);Check(e.LastDiagnostic!.FullOutcome==ReleaseFailure.InsufficientSamples,"full rejection lost");
            Branch(e,"InlierMajority");var d=Diagnostic(e);
            Check((int)Field(d,"InlierSamples")! <33 && (int)Field(d,"ActiveSamples")! ==64,"actual inlier rejection");
            Check(Field(d,"MedianVelocityMagnitude") is double,"median measured");
        });
        foreach(int step in new[]{4,8,16})Case("Diagnostic","staircase distinct count "+step,()=>{
            var e=new ReleaseVelocityEstimator(1000000);
            for(int ms=0;ms<=100;ms++)e.Add(ms*1000,new(ms/step*step,0));
            e.Estimate(100000);var d=Diagnostic(e);
            int distinct=100/step-37/step+1;
            Check((int)Field(d,"DistinctPositions")! ==distinct &&
                (int)Field(d,"DuplicatePositionCount")! ==64-distinct,"global duplicates counted in active window");
            Near((double)Field(d,"DuplicatePositionRatio")!, (64-distinct)/64d);
            Console.WriteLine($"STAIR {step} branch={Field(d,"Branch")} inliers={Field(d,"InlierSamples")} distinct={distinct}");
        });
        Case("Diagnostic","empty buffer",()=>{var e=new ReleaseVelocityEstimator(1000);e.Estimate(0);Branch(e,"EmptyBuffer");Check(Field(Diagnostic(e),"ActiveSamples")==null,"not evaluated is null");});
        Case("Diagnostic","two active samples",()=>{var e=new ReleaseVelocityEstimator(1000);e.Add(0,new(0,0));e.Add(50,new(50,0));e.Estimate(50);Branch(e,"ActiveCount");});
        Case("Diagnostic","active span short",()=>{var e=new ReleaseVelocityEstimator(1000);for(int i=0;i<3;i++)e.Add(i,new(i,0));e.Estimate(2);Branch(e,"ActiveSpan");Near((double)Field(Diagnostic(e),"ActiveSpanMs")!,2);});
        Case("Diagnostic","inlier span short despite majority",()=>{
            var e=new ReleaseVelocityEstimator(1000000);e.Add(0,new(10000,0));
            foreach(int ms in new[]{88,90,92,94,96,100})e.Add(ms*1000,new(ms,0));
            Check(e.Estimate(100000).Reason==ReleaseFailure.InsufficientSamples,"inlier span result changed");
            Branch(e,"InlierSpan");var d=Diagnostic(e);Check((int)Field(d,"InlierSamples")! ==6,"inlier majority present");
            Near((double)Field(d,"InlierSpanMs")!,12);
        });
        foreach(int age in new[]{100,101,110,119})Case("Diagnostic","window dwell gap "+age,()=>{
            var e=Track(1000,0);var r=e.Estimate(100000+age*1000);Check(r.Reason==ReleaseFailure.InsufficientSamples,"gap classification changed");
            Branch(e,"ActiveCount");var d=Diagnostic(e);Check((int)Field(d,"ActiveSamples")! ==(age==100?1:0),"actual gap active count");
            Near((double)Field(d,"LastSampleAgeMs")!,age);Check(Field(d,"ActiveSpanMs")==null,"span not evaluated");
        });
        Case("Diagnostic","dwell last sample",()=>{var e=Track(1000,0);e.Estimate(220000);Branch(e,"LastSampleDwell");});
        Case("Diagnostic","dwell last motion",()=>{var e=Track(0,0);e.Add(120000,new(-100,50));e.Estimate(120000);Branch(e,"LastMotionDwell");});
        Case("Diagnostic","invalid tuning",()=>{var e=new ReleaseVelocityEstimator(0);e.Estimate(0);Branch(e,"InvalidTuning");});
        Case("Diagnostic","invalid release timestamp",()=>{var e=Track(1000,0);e.Estimate(99999);Branch(e,"InvalidTimestamp");});
        foreach(long tick in new[]{90000L,100000L})Case("Diagnostic","Add fault retained "+tick,()=>{var e=Track(1000,0);e.Add(tick,new(0,0));e.Estimate(100000);Branch(e,"AddFault");});
        Case("Diagnostic","snapshot immutable across Add Reset and Estimate",()=>{
            var e=Track(1000,0);e.Estimate(100000);var d=Diagnostic(e);e.Reset();e.Add(1,new(0,0));e.Estimate(1);
            Check((string)Field(d,"Branch")! =="Accepted" && (int)Field(d,"BufferSamples")! ==11,"snapshot reused/mutated");
            Branch(e,"ActiveCount");Check(!Equals(Field(d,"Sequence"),Field(Diagnostic(e),"Sequence")),"Estimate sequence reused");
        });
    }
}
