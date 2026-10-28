using DpiHwndProbe;
using NecoArcDesktopPet.Coordinates;

internal static partial class Program
{
    private static void RecoveryTests()
    {
        Case("G shared monitor dense in-flight signals await current target", () => {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();
            f.State=f.State with{PlaceDepth=1,NativeDepth=1,Geometry=false};
            for(int i=0;i<200;i++) f.Protocol.Observe();
            Check(f.Unsafe==0 && f.Protocol.Active,"native in-flight signal burst must not synchronously stop the trial");
            Check(f.Commits==1,"no native repair within nested Place");
            f.State=f.State with{PlaceDepth=0,NativeDepth=0,Geometry=true};f.Protocol.Observe();f.Run();
            Check(f.Unsafe==0 && f.Revocations==0,"latest completed target must be checked without revoking good mapping");
        });
        Case("Candidate protocol exists on the shared probe path", () => {
            Check(typeof(ProbeSession).Assembly.GetType("DpiHwndProbe.EarlyRecovery") != null,
                "Candidate missing: no cancellable early recovery or late overwrite protocol exists");
        });
        Case("PURE MODEL Candidate waits for dirty layout and full synchronous stack exit", () => {
            var f=new RecoveryFixture(); f.Begin();
            f.State=f.State with { Layout=false }; f.Protocol.Observe(); Check(f.Queue.Count==0,"matrix alone cannot recover");
            f.State=f.State with { Layout=true, NativeDepth=1, PlaceDepth=1 }; f.Protocol.Observe();
            Check(f.Queue.Count==0 && f.Commits==0,"clean native checkpoint only signals");
            f.State=f.State with { NativeDepth=0 }; f.Protocol.Observe(); Check(f.Queue.Count==0,"whole Place stack must exit");
            f.State=f.State with { PlaceDepth=0, HookDepth=1 }; f.Protocol.Observe(); Check(f.Queue.Count==0,"DPI hook cannot place");
            f.State=f.State with { HookDepth=0 }; f.Protocol.Observe();
            Check(f.Queue.Count==1 && f.Commits==0,"single separate queued callback");
            f.Run(); Check(f.Commits==1 && f.Protocol.Accepted,"current live checkpoint commits");
        });
        Case("PURE MODEL Candidate revalidates all conditions before committing", () => {
            foreach(var mutation in new Func<RecoveryCheckpoint,RecoveryCheckpoint>[] {
                s=>s with{Alive=false}, s=>s with{Matrix=false}, s=>s with{Layout=false},s=>s with{Sizes=false},
                s=>s with{Topology=false},s=>s with{Geometry=false},s=>s with{TargetSafe=false},s=>s with{Dragging=true,Captured=false},
                s=>s with{PlaceDepth=1},s=>s with{NativeDepth=1},s=>s with{HookDepth=1},
                s=>s with{Owner=s.Owner with{Generation=4}},s=>s with{Owner=s.Owner with{Gesture=9}},
                s=>s with{Owner=s.Owner with{Source=8}},s=>s with{Owner=s.Owner with{Hwnd=0}} }) {
                var f=new RecoveryFixture(); f.Begin();f.Protocol.Observe(); Check(f.Queue.Count==1,"valid checkpoint must queue candidate before mutation"); f.State=mutation(f.State);f.Run();
                Check(f.Commits==0 && !f.Protocol.Accepted,"live mutated checkpoint declines");
                for(int i=0;i<100;i++) f.Protocol.Observe(); Check(f.Queue.Count==0,"one attempt only, Legacy fallback remains");
            }
        });
        Case("PURE MODEL late overwrite revokes Accepted and finite repairs cannot loop", () => {
            var f=new RecoveryFixture(); f.Begin();f.Protocol.Observe();f.Run();
            Check(f.Protocol.Accepted,"candidate accepted before overwrite");
            f.State=f.State with{Geometry=false}; f.Protocol.Observe();
            Check(!f.Protocol.Accepted && f.Revocations==1 && f.Queue.Count==1,"incorrect accepted revoked before repair");
            f.Run(); Check(f.Protocol.Accepted && f.Protocol.Repairs==1,"first protected repair");
            f.State=f.State with{Layout=false}; f.Protocol.Observe();f.State=f.State with{Layout=true};f.Run();
            Check(f.Protocol.Repairs==2 && f.Protocol.Accepted,"late invalidation second repair");
            f.State=f.State with{Geometry=false};f.Protocol.Observe();
            Check(!f.Protocol.Active && !f.Protocol.Accepted && f.Unsafe==1 && f.Queue.Count==0,"third overwrite terminates, blocks user");
            for(int i=0;i<10000;i++) f.Protocol.Observe(); Check(f.Queue.Count==0 && f.Commits==3,"no polling/reentry loop");
        });
        Case("PURE MODEL stale dispatch native completion release and close cannot revive owners", () => {
            var f=new RecoveryFixture(); f.Begin();f.Protocol.Observe();Check(f.Queue.Count==1,"old generation callback queued");var old=f.Queue[0].Body;
            f.State=f.State with{Owner=f.State.Owner with{Generation=3}};f.Begin();f.Protocol.Observe();
            f.State=f.State with{Owner=f.State.Owner with{Generation=4}};f.Begin();
            old(); Check(f.Commits==0,"gN callback cannot move gN+2");
            foreach(var why in new[]{"release","capture loss","new same-generation gesture","close"}) {
                f.Protocol.Observe();var callback=f.Queue.Last().Body;f.Protocol.Cancel(why);callback();
                Check(!f.Protocol.Pending && f.Commits==0,"canceled callback no WPF/native access");f.Begin();
            }
            f.Protocol.Observe();f.OnCommit=()=>f.Protocol.Cancel("native nested new owner");f.Run();
            Check(!f.Protocol.Accepted,"native completion cannot revive canceled owner");
        });
        Case("PURE MODEL Legacy fallback retained and expiry terminates observer", () => {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Protocol.LegacyCompleted();
            Check(!f.Protocol.Pending && f.Protocol.Accepted && f.Commits==0,"Legacy wins before candidate dispatch");
            f.Protocol.Expire();Check(!f.Protocol.Active,"finite observation window");
            var bad=new RecoveryFixture();bad.Begin();bad.Protocol.Observe();bad.Run();bad.State=bad.State with{Geometry=false};
            bad.Protocol.Expire();Check(bad.Unsafe==1 && !bad.Protocol.Accepted,"terminal checkpoint does not hide late corruption");
        });
        Case("PURE MODEL capture loss rejects forced stale Candidate and repair before checkpoint/native access",()=> {
            foreach(var repair in new[]{false,true}) {
                var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();
                if(repair) {f.Run();f.State=f.State with{Geometry=false};f.Protocol.Observe();}
                var callback=f.Queue.Last().Body;var oldOwner=f.Protocol.Owner;
                f.Protocol.Cancel("capture loss");var commits=f.Commits;var reads=f.Reads;
                callback();f.Protocol.LegacyCompleted();
                Check(f.Commits==commits && f.Reads==reads && !f.Protocol.Pending && !f.Protocol.Accepted,
                    "even forcible delivery after abort must not read or commit canceled owner");
                f.State=f.State with{Owner=oldOwner with{Gesture=oldOwner.Gesture+1},Geometry=true};
                f.Begin();f.Protocol.Observe();var newPending=f.Protocol.Pending;
                callback();Check(f.Protocol.Pending==newPending && f.Commits==commits,"old callback cannot clear new owner's operation");
                f.Run();Check(f.Protocol.Accepted && f.Commits==commits+1,"new live owner still recovers");
            }
        });
        Case("PURE MODEL experiment blocker reports actual failure before capture blockers",()=> {
            var fail=new RecoveryExperimentResult("B4 old owner","FAIL","post-loss native Place");
            var blocked=new RecoveryExperimentResult("B4 capture","BLOCKED","Mouse.Capture returned false");
            var pass=new RecoveryExperimentResult("B4 release","PASS",null);
            Check(RecoveryExperimentResult.Blocker(new[]{fail})=="B4 old owner: post-loss native Place","real user fail cannot inherit environment blocker");
            Check(RecoveryExperimentResult.Blocker(new[]{blocked,fail,pass})=="B4 old owner: post-loss native Place","FAIL dominates summary; blocked item remains separately listed");
            Check(RecoveryExperimentResult.Blocker(new[]{blocked,pass})=="B4 capture: Mouse.Capture returned false","capture blocker only when actually blocked");
            Check(RecoveryExperimentResult.Blocker(new[]{pass})==null,"all green has no experiment blocker; admission stays independently locked");
        });
        Case("PURE MODEL dense signals have finite observer cost",()=> {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();
            for(int i=0;i<10000;i++)f.Protocol.Observe();
            Check(f.Reads<=132 && !f.Protocol.Active && f.Unsafe==0 && f.Revocations==0,"clean budget exhaustion stops observing without suspending good mapping");
            var bad=new RecoveryFixture();bad.Begin();bad.Protocol.Observe();bad.Run();
            for(int i=0;i<126;i++)bad.Protocol.Observe();bad.State=bad.State with{Geometry=false};bad.Protocol.Observe();
            Check(bad.Revocations==1 && !bad.Protocol.Accepted,"stable divergent budget boundary cannot silently pass");
            bad.Run();Check(bad.Unsafe==0 && bad.Protocol.Repairs==1,"stable divergent boundary retains bounded repair");
            Check(bad.Protocol.CoherentCompletions==1 && !bad.Protocol.Active,"successful budget-boundary repair then completes coherently");
        });
        Case("G stable unaccepted divergence at signal budget must repair or fail",()=> {
            var f=new RecoveryFixture();f.Begin();f.State=f.State with{Geometry=false};f.CommitSucceeds=false;
            for(int i=0;i<200;i++) f.Protocol.Observe();
            f.Run();Check(f.Protocol.Repairs==1 && !f.Protocol.Accepted,"terminal stable divergence queues protected repair");
            f.Run();Check(f.Unsafe==1 && f.Revocations>0 && !f.Protocol.Active,"failed real protocol repair cannot complete coherent");
        });
        Case("G superseded placement is deferred and newest accepted target completes",()=> {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();
            f.State=f.State with{RequestedPlacementId=12,AcceptedPlacementId=11,InFlightPlacementId=12,PlacementOwner=f.State.Owner,Geometry=false};
            for(int i=0;i<200;i++) f.Protocol.Observe();
            Check(f.Unsafe==0 && f.Protocol.DecidableChecks==1 && f.Protocol.Deferred==200,"busy target never consumes decidable budget");
            var old=f.Queue.Last().Body;
            f.State=f.State with{RequestedPlacementId=13,AcceptedPlacementId=13,InFlightPlacementId=null,Geometry=true};
            f.Run();Check(f.Protocol.CoherentCompletions==1 && f.Unsafe==0 && !f.Protocol.Active,"callback re-reads latest target, not scheduled target");
            var reads=f.Reads;old();Check(f.Reads==reads,"completed revision rejects forced old monitor callback before reads");
        });
        Case("G deadline and deferred dispatch remain finite during native stack",()=> {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();f.State=f.State with{NativeDepth=1,Geometry=false};
            for(int i=0;i<200;i++)f.Protocol.Observe();f.Run();
            for(int i=0;i<200;i++)f.Protocol.Observe();f.Run();
            for(int i=0;i<200;i++)f.Protocol.Observe();
            Check(f.Queue.Count==0 && f.Commits==1 && f.Unsafe==0,"at most two deferred callbacks; no native repair/requeue loop");
            f.Now=501;f.Protocol.Expire();Check(f.Unsafe==0,"expiry does not synchronously stop/place in native stack");
            f.State=f.State with{NativeDepth=0,Geometry=true};f.Protocol.Observe();
            Check(f.Unsafe==1 && f.Protocol.FatalStops==1 && f.Revocations==1,"expired in-flight cannot masquerade as late coherence");
        });
        Case("G rapid generations cannot renew unresolved deadline or repair allowance",()=> {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();
            f.State=f.State with{Geometry=false};f.Protocol.Observe();f.Run();
            f.Now=300;f.State=f.State with{Owner=f.State.Owner with{Generation=3},NativeDepth=1,Geometry=false};f.Begin();
            f.Now=499;f.State=f.State with{Owner=f.State.Owner with{Generation=4}};f.Begin();
            Check(f.Protocol.Repairs==1 && f.Protocol.RemainingMilliseconds<=1,"generation replacements inherit original deadline and repair debt");
            f.Now=501;f.Protocol.Observe();f.State=f.State with{NativeDepth=0};f.Protocol.Observe();
            Check(f.Unsafe==1 && !f.Protocol.Active,"fast reverse cannot evade expired unresolved state");
        });
        Case("G stale-owner signal cannot spend new budget and monitor cancellation aborts final operation",()=> {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();f.State=f.State with{NativeDepth=1};
            for(int i=0;i<200;i++)f.Protocol.Observe();var callback=f.Queue.Last().Body;
            foreach(var why in new[]{"release","capture loss","new gesture","close"}) {
                f.Protocol.Cancel(why);var reads=f.Reads;callback();Check(f.Reads==reads,"aborted final callback cannot read stale owner");
            }
            f.State=f.State with{Owner=f.State.Owner with{Generation=5},NativeDepth=0};f.Begin();
            f.State=f.State with{Owner=f.State.Owner with{Generation=4}};f.Protocol.Observe();
            Check(f.Protocol.DecidableChecks==0 && f.Unsafe==0,"owner check precedes any decidable budget accounting");
        });
        Case("G missing acceptance or invalid stable checkpoint cannot expire coherent",()=> {
            foreach(var mutate in new Func<RecoveryCheckpoint,RecoveryCheckpoint>[] {
                s=>s with{Matrix=false},s=>s with{Topology=false},s=>s with{TargetSafe=false},s=>s with{Dragging=true,Captured=false},
                s=>s with{Sizes=false},s=>s with{Layout=false},s=>s with{Geometry=false}}) {
                var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();f.State=mutate(f.State);f.Now=501;f.Protocol.Expire();
                Check(f.Unsafe==1 && f.Protocol.CoherentCompletions==0,"invalid stable state remains fail closed");
            }
            var unaccepted=new RecoveryFixture();unaccepted.Begin();unaccepted.Protocol.Observe();unaccepted.Now=501;unaccepted.Protocol.Expire();
            Check(unaccepted.Unsafe==1 && !unaccepted.Protocol.Pending,"uncompleted candidate is not coherent acceptance");
        });
        Case("G stable unresolved placement association cannot defer beyond deadline",()=> {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();
            f.State=f.State with{RequestedPlacementId=9,AcceptedPlacementId=8,PlacementOwner=f.State.Owner};
            f.Now=501;f.Protocol.Expire();f.Run();
            Check(f.Unsafe==1 && !f.Protocol.Active && !f.Protocol.Pending,"no native stack: unresolved target association must fail closed at deadline");
        });
        Case("G final repair supersedes queued candidate before forced late delivery",()=> {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();var old=f.Queue[0];
            f.State=f.State with{Geometry=false};for(int i=0;i<200;i++)f.Protocol.Observe();
            f.Queue.Last().Body();
            Check(old.Cancelled && f.Protocol.Repairs==1,"final repair aborts previous pending candidate operation");
            var reads=f.Reads;old.Body();Check(f.Reads==reads && f.Commits==0 && f.Protocol.Pending,"forced superseded candidate cannot read, commit or clear new repair");
            f.Run();Check(f.Protocol.Accepted && f.Commits==1,"only current protected repair executes");
        });
        Case("PURE MODEL detached source revokes current Accepted before cancelling",()=> {
            var f=new RecoveryFixture();f.Begin();f.Protocol.Observe();f.Run();f.State=f.State with{Alive=false};
            f.Protocol.Observe();Check(!f.Protocol.Accepted && f.Revocations==1,"detached source cannot leave accepted evidence/mapping behind");
        });
        Case("PURE MODEL same-generation capture loss during shared MoveGrab cannot commit anchor",()=> {
            var s=new ProbeSession(new(-1500,-300),1,10,true);Check(s.Accept(Map(1.5,1.5)),"Candidate map");
            Check(s.BeginGrab(new(-1530,-340)),"begin gesture");var anchor=s.Anchor;
            Check(!s.MoveGrab(new(-900,-100),_=>{s.CancelGrab();return true;}) && s.Anchor==anchor,
                "native callback lost owner; shared MoveGrab must not publish canceled anchor");
        });
        Case("PURE MODEL Candidate common commit uses current DPI physical offset and union safety",()=> {
            var monitors=new[]{new MonitorSnapshot("left",new(-2560,0,0,1600),new(-2560,0,0,1600)),
                new MonitorSnapshot("right",new(0,0,2560,1440),new(0,0,2560,1440))};
            foreach(var direction in new[]{(2.0,1.5),(1.5,2.0)}) foreach(var hole in new[]{false,true}) {
                var state=new RecoveryCheckpoint(new(2,1,1,10),true,true,true,true,true,true,true,true,0,0,0);
                var s=new ProbeSession(new(-130,400),1,10,true);Check(s.Accept(Map(direction.Item1,direction.Item1)),"initial local mapping");
                var cursor=new PhysicalPoint(-150,370);Check(s.BeginGrab(cursor),"physical offset (-20,-30)");
                var g=s.Invalidate();Check(g==state.Owner.Generation,"model generation");
                var queued=new Queue<Action>();int placed=0;
                var p=new EarlyRecovery(()=>state,(_,_)=> {
                    Check(s.Accept(Map(direction.Item2,direction.Item2,g)),"only current DPI accepted");
                    return s.MoveGrab(new(-20,470),anchor=> {
                        Check(ProbeGeometry.TryLayout(anchor,1,s.Mapping!,out var l,out _),"real shared physical layout");
                        Check(ProbeGeometry.TryPixels(l!,s.Mapping!.Scale,out var pixels),"real shared pixel conversion");
                        Near(pixels.Width,240,"Constant canvas width");Near(pixels.Height,180,"Constant canvas height");
                        Near(l!.BodyDip.X*direction.Item2,180,"Constant body width");Near(l.BodyDip.Y*direction.Item2,120,"Constant body height");
                        Near(l.AnchorDip.X*direction.Item2,120,"physical anchor X");Near(l.AnchorDip.Y*direction.Item2,150,"physical anchor Y");
                        var areas=hole ? new[]{monitors[0],new MonitorSnapshot("right",new(100,0,2660,1440),new(100,0,2660,1440))} : monitors;
                        if(!ProbeGeometry.IsSafe(pixels,areas))return false;placed++;return true;
                    });
                },(_,body)=>{queued.Enqueue(body);return ()=>{};},(_,_)=>{},()=>s.SuspendMapping(),_=>{});
                p.Begin(state.Owner);p.Observe();queued.Dequeue()();
                Check(p.Accepted==!hole && placed==(hole?0:1),"same Candidate commit rejects hole, accepts adjacent union");
                Check(s.Anchor==(hole ? new PhysicalPoint(-130,400) : new PhysicalPoint(0,500)),"saved physical offset preserved; declined anchor unchanged");
            }
        });
        Case("Candidate user CLI is fail closed until captured-release experiments pass",()=> {
            Check(!RecoveryAdmission.UserTrialAllowed && RecoveryAdmission.Evaluate(new[]{"--manual","--recovery-mode","candidate"}).ExitCode==3,"no ordinary Candidate admission without explicit experimental consent");
        });
    }

    private sealed class RecoveryFixture
    {
        internal RecoveryCheckpoint State=new(new(2,1,1,10),true,true,true,true,true,true,false,false,0,0,0);
        internal sealed record Operation(string Priority,Action Body) { internal bool Cancelled; }
        internal List<Operation> Queue=new();
        internal int Commits,Revocations,Unsafe,Reads;
        internal double Now;
        internal bool CommitSucceeds=true;
        internal Action? OnCommit;
        internal EarlyRecovery Protocol;
        internal RecoveryFixture() {
            Protocol=new(()=>{Reads++;return State;},(_,_)=>{Commits++;OnCommit?.Invoke();State=State with{Geometry=true};return CommitSucceeds;},
                (priority,body)=>{var op=new Operation(priority,body);Queue.Add(op);return ()=>op.Cancelled=true;},
                (_,_)=>{},()=>Revocations++, _=>Unsafe++,()=>Now);
        }
        internal void Begin()=>Protocol.Begin(State.Owner);
        internal void Run() { var pending=Queue.ToArray();Queue.Clear();foreach(var op in pending)if(!op.Cancelled)op.Body(); }
    }
}
