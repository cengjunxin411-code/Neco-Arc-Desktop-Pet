using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

internal static partial class Program
{
    static ReleaseVelocityEstimator Curve(double initial, double acceleration, double direction=1)
    {
        var e=new ReleaseVelocityEstimator(1000000);
        for(int ms=0;ms<=100;ms++){
            var t=ms/1000d;
            Check(e.Add(ms*1000,new(direction*(initial*t+acceleration*t*t/2),0)),"valid physical curve");
        }
        return e;
    }
    static object? TailField(object d,string name)=>d.GetType().GetProperty(name)?.GetValue(d);
    static void TailCases()
    {
        // Missing fallback is a real product RED before inspecting new fields.
        foreach(var data in new[]{(0d,40000d,3400d),(5000d,-40000d,1600d)})
        foreach(var sign in new[]{-1d,1d})Case("Tail","accelerating/decelerating accepted "+data+" sign="+sign,()=>{
            var e=Curve(data.Item1,data.Item2,sign);var r=e.Estimate(100000);
            Check(r.Reason==ReleaseFailure.None,"reliable curved release still refused: "+r.Reason);
            // Linear least squares on [70,100]ms of a quadratic equals its
            // derivative at 85ms (hand derived), not velocity at the oldest point.
            Near(r.Velocity.X,sign*data.Item3,.00001);Near(r.Velocity.Y,0);
            var d=Diagnostic(e);Branch(e,"InlierMajority");
            Check((string?)TailField(d,"VelocitySource")=="tail","tail source missing");
            Check(TailField(d,"FallbackAttempted") is true,"fallback not attributed");
            Check((int)Field(d,"InlierSamples")! <33,"full rejection overwritten");
            Check(TailField(d,"FullOutcome")?.ToString()=="InsufficientSamples","full outcome lost");
            Check(TailField(d,"TailActiveSamples") is 31 && TailField(d,"TailInlierSamples") is 31,"tail counts");
            Near((double)TailField(d,"TailActiveSpanMs")!,30);
            Check(TailField(d,"DirectionConsistent") is true && TailField(d,"FallbackFailureBranch")==null,"tail direction");
            var old=d;var again=e.Estimate(100000);Check(again==r,"repeat estimate changed outcome");
            Check((long)Field(Diagnostic(e),"Sequence")! > (long)Field(old,"Sequence")!,"sequence reused");
            e.Reset();Check(e.LastDiagnostic==null && e.BufferedSamples==0,"Reset leaves snapshot/buffer");
            Check(TailField(old,"VelocitySource")?.ToString()=="tail","immutable snapshot changed");
            Check(e.Estimate(100000).Reason==ReleaseFailure.InsufficientSamples,"old snapshot resurrected");
        });
        foreach(bool outlier in new[]{false,true})Case("Tail","full success priority "+outlier,()=>{
            var e=new ReleaseVelocityEstimator(1000000);
            for(int ms=0;ms<=100;ms+=10)e.Add(ms*1000,outlier&&ms==50?new(900000,-800000):new(ms,-ms/2d));
            var r=e.Estimate(100000);Near(r.Velocity.X,1000);Near(r.Velocity.Y,-500);
            var d=Diagnostic(e);Check((string?)TailField(d,"VelocitySource")=="full","full source missing");
            Check(TailField(d,"FallbackAttempted") is false && TailField(d,"TailActiveSamples")==null &&
                TailField(d,"DirectionConsistent")==null,"full path evaluated tail");
        });
        foreach(int age in new[]{100,101,110,119,120,200})foreach(bool sampledStop in new[]{false,true})
            Case("Tail","gentle stop "+age+" sampled="+sampledStop,()=>{
                var e=Curve(0,40000);
                if(sampledStop)for(int ms=101;ms<=100+age;ms++)e.Add(ms*1000,new(200,0));
                var r=e.Estimate((100+age)*1000);
                Check(r.Reason==(age>=120?ReleaseFailure.Stationary:sampledStop?ReleaseFailure.None:ReleaseFailure.InsufficientSamples),"gentle classification changed");
                Check(r.Velocity==default,"stopped release flung");
                Check(TailField(Diagnostic(e),"FallbackAttempted") is false,"gentle path attempted tail");
            });
        foreach(string shape in new[]{"reversal","subpixel-reversal","oscillation","jitter","stop-tail","bad-tick","bad-point","short","two"})
            Case("Tail","unsafe history refuses tail "+shape,()=>{
                var e=new ReleaseVelocityEstimator(1000000);
                int count=shape=="short"?10:shape=="two"?1:100;
                for(int ms=0;ms<=count;ms++){
                    double t=ms/1000d;
                    var x=shape switch{
                        "reversal"=>ms<=75?20000*t*t:112.5-2000*(t-.075),
                        "subpixel-reversal"=>ms<=60?.49*(60-ms):20000*(t-.060)*(t-.060),
                        "oscillation"=>40*Math.Sin(ms*Math.PI/20),
                        "jitter"=>.4*Math.Sin(ms),
                        "stop-tail"=>20000*Math.Min(t,.08)*Math.Min(t,.08),
                        _=>20000*t*t};
                    e.Add(ms*1000,new(x,0));
                }
                if(shape=="bad-tick")e.Add(100000,new(200,0));
                if(shape=="bad-point")e.Add(101000,new(double.NaN,0));
                var r=e.Estimate(count*1000);
                // Existing full zero-speed jitter successes remain valid. The
                // new path must never promote an unsafe track to a moving tail.
                Check(TailField(Diagnostic(e),"VelocitySource")?.ToString()!="tail","unsafe history promoted");
                if(shape is not ("jitter" or "stop-tail"))Check(r.Reason!=ReleaseFailure.None,"unsafe release accepted "+r);
            });
        foreach(int step in new[]{4,8})Case("Tail","high-speed staircase remains bounded "+step,()=>{
            var e=new ReleaseVelocityEstimator(1000000);
            for(int ms=0;ms<=100;ms++)e.Add(ms*1000,new(ms/step*step*3d,0));
            var r=e.Estimate(100000);Console.WriteLine($"D1 STAIR step={step} result={r} diagnostic={System.Text.Json.JsonSerializer.Serialize(Diagnostic(e))}");
            if(step==4){Check(r.Reason==ReleaseFailure.None,"4ms staircase tail refused");Near(r.Velocity.X,3000,450);}
            else Check(r.Reason==ReleaseFailure.InsufficientSamples,"8ms staircase bypassed robust gate");
        });
        Case("Tail","tail vector hard cap",()=>{
            var e=Curve(6000,40000);var r=e.Estimate(100000);
            Check(r.Reason==ReleaseFailure.None && e.LastDiagnostic!.VelocitySource=="tail","high curved release refused");
            Near(r.Velocity.X,6000);Near(r.Velocity.Y,0);
        });
        Case("Tail","integer oblique reversal rejects cumulative backtracking",()=>{
            var e=new ReleaseVelocityEstimator(1000000);
            for(int ms=0;ms<=100;ms++){
                double u=(ms-60)/1000d;
                e.Add(ms*1000,ms<=60?new(0,60-ms):new(Math.Round(40000*u*u),Math.Round(24000*u*u)));
            }
            Check(e.Estimate(100000).Reason==ReleaseFailure.InsufficientSamples,"integer oblique reversal promoted");
            Check(e.LastDiagnostic!.FallbackAttempted && e.LastDiagnostic.FallbackFailureBranch=="Backtracking" &&
                e.LastDiagnostic.DirectionConsistent is false,"cumulative reverse not attributed");
        });
        foreach(double seconds in new[]{.024,.036,double.NaN,double.PositiveInfinity})Case("Tail","invalid tail duration "+seconds,()=>{
            var e=new ReleaseVelocityEstimator(1000000,new(){TailSeconds=seconds});
            Check(!e.Add(0,new(0,0))&&e.Estimate(0).Reason==ReleaseFailure.InvalidTuning,"invalid tail tuning admitted");
        });
    }
}
