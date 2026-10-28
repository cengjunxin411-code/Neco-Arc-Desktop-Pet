using System;
using System.Collections.Generic;
using System.Linq;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot.Physics;

internal sealed record ReleaseVelocityTuning
{
    internal double WindowSeconds { get; init; } = .1;
    internal double DwellSeconds { get; init; } = .12;
    internal double MinimumSpanSeconds { get; init; } = .015;
    internal double MaximumSpeed { get; init; } = 6000;
    internal double InlierDistance { get; init; } = 4;
    internal double StationaryDistance { get; init; } = .5;
    internal int MaximumSamples { get; init; } = 64;
    internal double TailSeconds { get; init; } = .030;

    internal bool IsValid => PhysicsTuning.In(WindowSeconds,.08,.12) &&
        PhysicsTuning.In(DwellSeconds,.1,.2) && PhysicsTuning.In(MinimumSpanSeconds,.001,WindowSeconds) &&
        PhysicsTuning.In(MaximumSpeed,1,100000) && PhysicsTuning.In(InlierDistance,.01,1000) &&
        PhysicsTuning.In(StationaryDistance,0,10) && MaximumSamples is >= 3 and <= 128 &&
        PhysicsTuning.In(TailSeconds,.025,.035);
}
internal enum ReleaseFailure { None, Stationary, InsufficientSamples, InvalidInput, InvalidTuning }
internal readonly record struct ReleaseEstimate(PhysicalVelocity Velocity, ReleaseFailure Reason, int Samples);

// Immutable, per-Estimate observation. Null means not evaluated (never a fake zero).
// Positions are physical pixels; durations are milliseconds in the caller's tick
// frequency. Duplicates are GLOBAL repeats: ActiveSamples - DistinctPositions.
internal sealed record ReleaseDiagnostic(long Sequence, long ReleaseTimestamp, string Branch,
    int BufferSamples, int? ActiveSamples, int? InlierSamples, double? ActiveSpanMs,
    double? InlierSpanMs, double? LastSampleAgeMs, int? DistinctPositions,
    int? DuplicatePositionCount, double? DuplicatePositionRatio,
    double? MedianVelocityMagnitude, ReleaseFailure EstimatorOutcome)
{
    // Original fields always describe the full-window decision. EstimatorOutcome
    // is the final returned outcome; FullOutcome preserves the original refusal.
    public ReleaseFailure FullOutcome { get; init; } = EstimatorOutcome;
    public string VelocitySource { get; init; } = "none";
    public bool FallbackAttempted { get; init; }
    public string? FallbackFailureBranch { get; init; }
    public int? TailActiveSamples { get; init; }
    public int? TailInlierSamples { get; init; }
    public double? TailActiveSpanMs { get; init; }
    public double? TailInlierSpanMs { get; init; }
    public double? TailVelocityX { get; init; }
    public double? TailVelocityY { get; init; }
    public double? TailSpeed { get; init; }
    public double? DirectionCosine { get; init; }
    public double? RecentDirectionCosine { get; init; }
    public bool? DirectionConsistent { get; init; }
}

internal sealed class ReleaseVelocityEstimator
{
    private readonly long frequency;
    private readonly ReleaseVelocityTuning tuning;
    private readonly List<(long Tick, PhysicalPoint Point)> samples = new();
    private long? lastTick;
    private long lastMotion;
    private PhysicalPoint stationaryAnchor;
    private ReleaseFailure fault;
    private long diagnosticSequence;
    internal ReleaseDiagnostic? LastDiagnostic { get; private set; }
    internal int BufferedSamples => samples.Count;
    internal ReleaseVelocityEstimator(long frequency, ReleaseVelocityTuning? tuning = null)
    {
        this.frequency = frequency;
        this.tuning = tuning ?? new();
    }
    private bool Valid => frequency is > 0 and <= 1000000000000 && tuning.IsValid;
    private double Seconds(long newer, long older) => (newer-older)/(double)frequency;
    internal void Reset()
    {
        LastDiagnostic = null;
        samples.Clear(); lastTick = null; lastMotion = 0; stationaryAnchor = default;
        fault = ReleaseFailure.None;
    }
    private ReleaseEstimate Reject(ReleaseFailure reason)
    {
        Reset(); fault = reason;
        return new(default,reason,0);
    }
    internal bool Add(long timestamp, PhysicalPoint position)
    {
        LastDiagnostic = null;
        if (!Valid) { Reject(ReleaseFailure.InvalidTuning); return false; }
        if (timestamp < 0 || (lastTick is {} tick && timestamp <= tick) || !position.IsFinite ||
            Math.Abs(position.X) > 1e9 || Math.Abs(position.Y) > 1e9) {
            Reject(ReleaseFailure.InvalidInput); return false;
        }
        if (lastTick is null || Distance(position,stationaryAnchor) > tuning.StationaryDistance) {
            lastMotion = timestamp; stationaryAnchor = position;
        }
        fault = ReleaseFailure.None; lastTick = timestamp;
        samples.Add((timestamp,position));
        while (samples.Count > tuning.MaximumSamples || (samples.Count > 1 &&
            Seconds(timestamp,samples[0].Tick) > tuning.WindowSeconds + 1e-12)) samples.RemoveAt(0);
        return true;
    }
    private static double Distance(PhysicalPoint a, PhysicalPoint b)
    {
        var x=a.X-b.X; var y=a.Y-b.Y; return Math.Sqrt(x*x+y*y);
    }
    private static double Median(List<double> values)
    {
        values.Sort(); int middle=values.Count/2;
        return values.Count%2==1 ? values[middle] : (values[middle-1]+values[middle])/2;
    }
    internal ReleaseEstimate Estimate(long releaseTimestamp)
    {
        var bufferCount=samples.Count;
        int? activeCount=null,inlierCount=null,distinct=null;
        double? activeSpan=null,inlierSpan=null,medianSpeed=null;
        TailObservation? tail=null;
        double? age=frequency>0 && lastTick is {} last ? Seconds(releaseTimestamp,last)*1000 : null;
        // Reject may Reset the estimator; publish AFTER it returns, preserving the
        // pre-decision buffer counts. Consumers retain this value, never the owner.
        ReleaseEstimate Finish(ReleaseEstimate result,string branch) {
            int? duplicates=activeCount-distinct;
            LastDiagnostic=new(++diagnosticSequence,releaseTimestamp,branch,bufferCount,activeCount,inlierCount,
                activeSpan,inlierSpan,Finite(age),distinct,duplicates,
                activeCount>0 ? duplicates/(double)activeCount.Value : null,Finite(medianSpeed),result.Reason) {
                FullOutcome=tail==null ? result.Reason : ReleaseFailure.InsufficientSamples,
                VelocitySource=result.Reason==ReleaseFailure.None ? tail==null ? "full" : "tail" : "none",
                FallbackAttempted=tail!=null,FallbackFailureBranch=tail?.Failure,
                TailActiveSamples=tail?.ActiveCount,TailInlierSamples=tail?.InlierCount,
                TailActiveSpanMs=tail?.ActiveSpanMs,TailInlierSpanMs=tail?.InlierSpanMs,
                TailVelocityX=tail?.Velocity?.X,TailVelocityY=tail?.Velocity?.Y,TailSpeed=tail?.Speed,
                DirectionCosine=tail?.DirectionCosine,RecentDirectionCosine=tail?.RecentCosine,
                DirectionConsistent=tail?.DirectionConsistent
            };
            return result;
        }
        static double? Finite(double? value)=>value is {} v && double.IsFinite(v) ? v : null;
        if (!Valid) return Finish(Reject(ReleaseFailure.InvalidTuning),"InvalidTuning");
        if (releaseTimestamp < 0 || (lastTick is {} tick && releaseTimestamp < tick)) return Finish(Reject(ReleaseFailure.InvalidInput),"InvalidTimestamp");
        if (lastTick is null) return Finish(new(default,fault==ReleaseFailure.None ? ReleaseFailure.InsufficientSamples : fault,0),
            fault==ReleaseFailure.None ? "EmptyBuffer" : "AddFault");
        if (Seconds(releaseTimestamp,lastTick.Value) >= tuning.DwellSeconds)
            return Finish(new(default,ReleaseFailure.Stationary,samples.Count),"LastSampleDwell");
        if (Seconds(releaseTimestamp,lastMotion) >= tuning.DwellSeconds)
            return Finish(new(default,ReleaseFailure.Stationary,samples.Count),"LastMotionDwell");

        var active = samples.Where(s => Seconds(releaseTimestamp,s.Tick) <= tuning.WindowSeconds+1e-12).ToArray();
        activeCount=active.Length;
        // Release only, bounded by MaximumSamples. Exact coordinate equality;
        // no set allocation, no deduplication or change to the fitting samples.
        distinct=0;
        for(int i=0;i<active.Length;i++) {
            bool seen=false;
            for(int j=0;j<i;j++) if(active[i].Point==active[j].Point){seen=true;break;}
            if(!seen)distinct++;
        }
        ReleaseEstimate Insufficient(string branch) => Finish(new(default,ReleaseFailure.InsufficientSamples,active.Length),branch);
        if (active.Length < 3) return Insufficient("ActiveCount");
        activeSpan=Seconds(active[^1].Tick,active[0].Tick)*1000;
        if (Seconds(active[^1].Tick,active[0].Tick) < tuning.MinimumSpanSeconds) return Insufficient("ActiveSpan");
        // Robust initial line: median pair slopes, median intercepts. A single
        // last-frame or interior outlier cannot dominate a multi-frame fling.
        var xs=new List<double>(); var ys=new List<double>();
        for (int i=0;i<active.Length;i++) for(int j=i+1;j<active.Length;j++) {
            var dt=Seconds(active[j].Tick,active[i].Tick);
            if (dt < tuning.MinimumSpanSeconds/10) continue;
            xs.Add((active[j].Point.X-active[i].Point.X)/dt);
            ys.Add((active[j].Point.Y-active[i].Point.Y)/dt);
        }
        if(xs.Count==0) return Insufficient("NoUsablePairs");
        var vx=Median(xs); var vy=Median(ys);
        medianSpeed=Math.Sqrt(vx*vx+vy*vy);
        var origin=active[0].Tick;
        var ix=Median(active.Select(s=>s.Point.X-vx*Seconds(s.Tick,origin)).ToList());
        var iy=Median(active.Select(s=>s.Point.Y-vy*Seconds(s.Tick,origin)).ToList());
        var inliers=active.Where(s=>Distance(s.Point,new(ix+vx*Seconds(s.Tick,origin),iy+vy*Seconds(s.Tick,origin)))<=tuning.InlierDistance).ToArray();
        inlierCount=inliers.Length;
        if(inliers.Length>=2)inlierSpan=Seconds(inliers[^1].Tick,inliers[0].Tick)*1000;
        if(inliers.Length < Math.Max(3,active.Length/2+1)) {
            tail=FitTail(active,releaseTimestamp);
            return tail.Failure==null && tail.Velocity is {} velocity
                ? Finish(new(velocity,ReleaseFailure.None,active.Length),"InlierMajority")
                : Insufficient("InlierMajority");
        }
        if(Seconds(inliers[^1].Tick,inliers[0].Tick)<tuning.MinimumSpanSeconds) return Insufficient("InlierSpan");
        // Least squares on the robust inliers. Relative ticks preserve precision
        // even near long.MaxValue. Absolute ticks are never converted to doubles.
        double meanT=inliers.Average(s=>Seconds(s.Tick,origin));
        double meanX=inliers.Average(s=>s.Point.X), meanY=inliers.Average(s=>s.Point.Y);
        double denominator=0,nx=0,ny=0;
        foreach(var s in inliers) {
            var t=Seconds(s.Tick,origin)-meanT;
            denominator+=t*t; nx+=t*(s.Point.X-meanX); ny+=t*(s.Point.Y-meanY);
        }
        if(denominator<=0 || !double.IsFinite(denominator)) return Insufficient("LeastSquaresDenominator");
        vx=nx/denominator; vy=ny/denominator;
        var speed=Math.Sqrt(vx*vx+vy*vy);
        if(!double.IsFinite(speed)) return Finish(Reject(ReleaseFailure.InvalidInput),"NonFiniteSpeed");
        if(speed>tuning.MaximumSpeed) { var scale=tuning.MaximumSpeed/speed; vx*=scale; vy*=scale; }
        return Finish(new(new(vx,vy),ReleaseFailure.None,active.Length),"Accepted");
    }

    // Release-only, bounded by the existing buffer. No Reset/Add, clock reads,
    // cursor input, ownership changes, native writes or asynchronous work.
    private sealed class TailObservation
    {
        internal int ActiveCount;
        internal int? InlierCount;
        internal double? ActiveSpanMs,InlierSpanMs,Speed,DirectionCosine,RecentCosine;
        internal PhysicalVelocity? Velocity;
        internal bool? DirectionConsistent;
        internal string? Failure;
    }
    private TailObservation FitTail((long Tick,PhysicalPoint Point)[] full,long releaseTimestamp)
    {
        var d=new TailObservation();
        TailObservation Refuse(string branch){d.Failure=branch;return d;}
        var span=Math.Max(.015,tuning.MinimumSpanSeconds);
        var band=Math.Min(4,tuning.InlierDistance);
        var active=full.Where(s=>Seconds(releaseTimestamp,s.Tick)<=tuning.TailSeconds+1e-12).ToArray();
        d.ActiveCount=active.Length;
        if(active.Length<3)return Refuse("ActiveCount");
        d.ActiveSpanMs=Seconds(active[^1].Tick,active[0].Tick)*1000;
        if(d.ActiveSpanMs<span*1000)return Refuse("ActiveSpan");
        // A held-still tail must not reawaken previous movement. The original
        // 120ms dwell and full-window decision have already run, unchanged.
        if(Seconds(releaseTimestamp,lastMotion)>=.015)return Refuse("LastMotionQuiet");
        var xs=new List<double>();var ys=new List<double>();
        for(int i=0;i<active.Length;i++)for(int j=i+1;j<active.Length;j++){
            var dt=Seconds(active[j].Tick,active[i].Tick);
            if(dt<span/10)continue;
            xs.Add((active[j].Point.X-active[i].Point.X)/dt);
            ys.Add((active[j].Point.Y-active[i].Point.Y)/dt);
        }
        if(xs.Count==0)return Refuse("NoUsablePairs");
        var vx=Median(xs);var vy=Median(ys);var origin=active[0].Tick;
        var ix=Median(active.Select(s=>s.Point.X-vx*Seconds(s.Tick,origin)).ToList());
        var iy=Median(active.Select(s=>s.Point.Y-vy*Seconds(s.Tick,origin)).ToList());
        var inliers=active.Where(s=>Distance(s.Point,new(ix+vx*Seconds(s.Tick,origin),iy+vy*Seconds(s.Tick,origin)))<=band).ToArray();
        d.InlierCount=inliers.Length;
        if(inliers.Length>=2)d.InlierSpanMs=Seconds(inliers[^1].Tick,inliers[0].Tick)*1000;
        if(inliers.Length<Math.Max(3,active.Length/2+1))return Refuse("InlierMajority");
        if(Seconds(inliers[^1].Tick,inliers[0].Tick)<span)return Refuse("InlierSpan");
        var meanT=inliers.Average(s=>Seconds(s.Tick,origin));
        var meanX=inliers.Average(s=>s.Point.X);var meanY=inliers.Average(s=>s.Point.Y);
        double denominator=0,nx=0,ny=0;
        foreach(var s in inliers){
            var t=Seconds(s.Tick,origin)-meanT;
            denominator+=t*t;nx+=t*(s.Point.X-meanX);ny+=t*(s.Point.Y-meanY);
        }
        if(denominator<=0||!double.IsFinite(denominator))return Refuse("LeastSquaresDenominator");
        vx=nx/denominator;vy=ny/denominator;
        var speed=Math.Sqrt(vx*vx+vy*vy);
        if(!double.IsFinite(speed))return Refuse("NonFiniteSpeed");
        var cap=Math.Min(6000,tuning.MaximumSpeed);
        if(speed>cap){var scale=cap/speed;vx*=scale;vy*=scale;speed=cap;}
        d.Velocity=new(vx,vy);d.Speed=speed;

        // Endpoint medians resist a single outlier. A direction below twice the
        // unchanged 4px uncertainty is not trustworthy. This only gates fallback;
        // ordinary slow full-window releases retain the adapter's 4px/s dead zone.
        PhysicalPoint Endpoint((long Tick,PhysicalPoint Point)[] track,bool end){
            var group=end?track.TakeLast(3):track.Take(3);
            return new(Median(group.Select(s=>s.Point.X).ToList()),Median(group.Select(s=>s.Point.Y).ToList()));
        }
        var first=Endpoint(full,false);var last=Endpoint(full,true);
        var dx=last.X-first.X;var dy=last.Y-first.Y;var length=Math.Sqrt(dx*dx+dy*dy);
        d.DirectionConsistent=false;
        if(length<=8 || speed<=4)return Refuse("UnreliableDirection");
        d.DirectionCosine=(dx*vx+dy*vy)/(length*speed);
        if(d.DirectionCosine<.9)return Refuse("OverallDirection");
        // Reject backtracking anywhere in the accepted full buffer, including a
        // recent reversal that happens to agree with an otherwise noisy tail fit.
        double furthest=0;
        for(int i=1;i<full.Length;i++){
            var projection=((full[i].Point.X-full[0].Point.X)*dx+(full[i].Point.Y-full[0].Point.Y)*dy)/length;
            if(furthest-projection>.5)return Refuse("Backtracking");
            furthest=Math.Max(furthest,projection);
        }
        var recent=active.Where(s=>Seconds(releaseTimestamp,s.Tick)<=.015+1e-12).ToArray();
        if(recent.Length<3)return Refuse("RecentCount");
        var recentFirst=Endpoint(recent,false);var recentLast=Endpoint(recent,true);
        var rx=recentLast.X-recentFirst.X;var ry=recentLast.Y-recentFirst.Y;
        var recentLength=Math.Sqrt(rx*rx+ry*ry);
        if(recentLength<=.5)return Refuse("RecentStationary");
        d.RecentCosine=(rx*vx+ry*vy)/(recentLength*speed);
        if(d.RecentCosine<.9)return Refuse("RecentDirection");
        d.DirectionConsistent=true;
        return d;
    }
}
