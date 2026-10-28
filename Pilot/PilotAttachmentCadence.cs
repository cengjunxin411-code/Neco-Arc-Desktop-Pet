using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

// Bounded aggregate only. WinEvent callbacks increment a counter; all other
// measurements and log writes happen on the pet Dispatcher.
internal sealed class PilotAttachmentCadence
{
    private readonly PilotLog? log;
    private readonly List<double> intervals=new(256),callbacks=new(256);
    private long lastFlush=Stopwatch.GetTimestamp(),lastCommit;
    private PhysicalRect lastObserved;
    private int notifications,rectChanges,commits,pending,held,unchanged,consecutivePending,maxConsecutivePending;

    internal PilotAttachmentCadence(PilotLog? log){this.log=log;}
    internal void Notification()=>Interlocked.Increment(ref notifications);
    internal void Rect(PhysicalRect rect){if(rect!=lastObserved){rectChanges++;lastObserved=rect;}}
    internal void Commit(){
        var now=Stopwatch.GetTimestamp();
        if(lastCommit!=0&&intervals.Count<256)intervals.Add(Stopwatch.GetElapsedTime(lastCommit,now).TotalMilliseconds);
        lastCommit=now;commits++;consecutivePending=0;
    }
    internal void Pending(){pending++;consecutivePending++;maxConsecutivePending=Math.Max(maxConsecutivePending,consecutivePending);}
    internal void Held(){held++;}
    internal void Unchanged(){unchanged++;consecutivePending=0;}
    internal void Callback(long began){if(callbacks.Count<256)callbacks.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);}
    internal void MaybeFlush(){if(Stopwatch.GetElapsedTime(lastFlush).TotalSeconds>=2)Flush(null);}
    internal void Flush(string? endReason)
    {
        var seen=Interlocked.Exchange(ref notifications,0);
        if(seen==0&&rectChanges==0&&commits==0&&pending==0&&held==0&&endReason==null){lastFlush=Stopwatch.GetTimestamp();callbacks.Clear();unchanged=0;return;}
        static object Distribution(List<double> source){
            var sorted=source.OrderBy(v=>v).ToArray();
            double? At(double p)=>sorted.Length==0?null:sorted[Math.Min(sorted.Length-1,(int)Math.Ceiling(p*sorted.Length)-1)];
            return new{samples=sorted.Length,p50Ms=At(.5),p90Ms=At(.9),maxMs=sorted.Length==0?(double?)null:sorted[^1]};
        }
        log?.Write("attachment-cadence",new{windowSeconds=Stopwatch.GetElapsedTime(lastFlush).TotalSeconds,
            externalLocationNotifications=seen,observedRectChanges=rectChanges,placementCommits=commits,
            placementIntervals=Distribution(intervals),pendingRefresh=pending,heldCallbacks=held,
            unchangedSamples=unchanged,maxConsecutivePending,dispatcherCallbacks=Distribution(callbacks),endReason});
        lastFlush=Stopwatch.GetTimestamp();rectChanges=commits=pending=held=unchanged=maxConsecutivePending=0;
        intervals.Clear();callbacks.Clear();
    }
}
