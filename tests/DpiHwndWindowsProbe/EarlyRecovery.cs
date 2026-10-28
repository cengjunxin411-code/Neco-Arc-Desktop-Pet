namespace DpiHwndProbe;

internal enum RecoveryMode { Legacy, Candidate }
internal static class RecoveryAdmission
{
    internal static bool UserTrialAllowed => false;
    internal const string Blocker = "Ordinary Candidate CLI locked. Experimental manual trial requires explicit --manual --policy constant --recovery-mode candidate --experimental-candidate-trial. Mixed-DPI hardware acceptance PENDING; Legacy retained.";
    internal static RecoveryEntry Evaluate(string[] args)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string,string>(StringComparer.Ordinal);
        var switches = new[] { "--manual", "--sample", "--ordinary", "--inspect", "--recovery-self-test", "--experimental-candidate-trial", "--experimental-visual-constant", "--diagnostics", "--trace-stall" };
        var valued = new[] { "--policy", "--recovery-mode", "--manifest", "--target-exe", "--pid", "--hwnd", "--log" };
        string? error = null;
        for (int i=0; i<args.Length; i++) {
            var key=args[i];
            if (!switches.Contains(key) && !valued.Contains(key)) { error??="Unknown argument: "+key; continue; }
            if (!flags.Add(key)) error??="Duplicate argument: "+key;
            if (valued.Contains(key)) {
                if (i+1==args.Length || args[i+1].StartsWith("--") || string.IsNullOrWhiteSpace(args[i+1])) { error??=key+" requires a value"; continue; }
                values.TryAdd(key,args[++i]);
            }
        }
        string? Value(string key) => values.GetValueOrDefault(key);
        bool Has(string key) => flags.Contains(key);
        var policy=Value("--policy")??"constant";
        var mode=Value("--recovery-mode")??"legacy";
        var optIn=Has("--experimental-candidate-trial");
        int exit=1;
        if (error==null && policy is not ("constant" or "baseline")) error="policy must be constant or baseline";
        if (error==null && mode is not ("legacy" or "candidate")) error="recovery-mode must be legacy or candidate";
        if (error==null && mode=="candidate" && !optIn && !UserTrialAllowed) { error=Blocker; exit=3; }
        int commands=new[]{"--manual","--sample","--inspect","--manifest","--recovery-self-test"}.Count(Has);
        if (error==null && commands!=1) error="Exactly one command is required: manual/sample/inspect/manifest/recovery-self-test";
        if (error==null && Has("--ordinary") && !Has("--manual") && !Has("--sample")) error="--ordinary requires manual or sample";
        if (error==null && new[]{"--target-exe","--pid","--hwnd"}.Any(Has) && !Has("--inspect")) error="target-exe/pid/hwnd require --inspect";
        if (error==null && optIn && (!Has("--manual") || !Has("--policy") || policy!="constant" || !Has("--recovery-mode") || mode!="candidate" || Has("--ordinary")))
            error="Experimental trial requires --manual --policy constant --recovery-mode candidate, without ordinary/sample/inspect/manifest/recovery-self-test";
        if(error==null && Has("--experimental-visual-constant") && !optIn)
            error="--experimental-visual-constant requires the complete explicit experimental Candidate manual Constant entry";
        return new(error==null ? 0 : exit,error??(optIn ? "EXPERIMENTAL opt-in; live runtime guards required" : "Legacy / diagnostic entry"),
            mode,optIn,Has("--manual"),Has("--sample"),Has("--ordinary"),Has("--inspect"),Has("--recovery-self-test"),
            policy,Value("--log"),Has("--diagnostics"),Has("--trace-stall"),Value("--manifest"),Value("--target-exe"),Value("--pid"),Value("--hwnd"),Has("--experimental-visual-constant"));
    }
}

internal sealed record RecoveryEntry(int ExitCode,string Reason,string RecoveryModeRequested,bool ExperimentalOptIn,
    bool Manual,bool Sample,bool Ordinary,bool Inspect,bool SelfTest,string Policy,string? LogPath,bool Diagnostics,bool TraceStall,
    string? Manifest,string? TargetExe,string? Pid,string? Hwnd,bool VisualConstant);

internal sealed record RecoveryExperimentResult(string Name,string Verdict,string? Reason)
{
    internal static string? Blocker(IEnumerable<RecoveryExperimentResult> results)
    {
        var failures=results.Where(x=>x.Verdict=="FAIL").ToArray();
        if(failures.Length>0) return string.Join("; ",failures.Select(x=>x.Name+": "+x.Reason));
        var blocked=results.Where(x=>x.Verdict=="BLOCKED").ToArray();
        return blocked.Length==0 ? null : string.Join("; ",blocked.Select(x=>x.Name+": "+x.Reason));
    }
}

internal readonly record struct RecoveryOwner(long Generation, long Gesture, long Source, long Hwnd);
internal sealed record RecoveryCheckpoint(RecoveryOwner Owner, bool Alive, bool Matrix, bool Layout,
    bool Sizes, bool Topology, bool Geometry, bool Dragging, bool Captured, int PlaceDepth, int NativeDepth, int HookDepth,
    bool TargetSafe = true, long? RequestedPlacementId = null, long? AcceptedPlacementId = null,
    long? InFlightPlacementId = null, RecoveryOwner? PlacementOwner = null)
{
    internal bool StackBusy => PlaceDepth!=0 || NativeDepth!=0 || HookDepth!=0 || InFlightPlacementId!=null;
    internal bool Decidable => !StackBusy &&
        (RequestedPlacementId==null || RequestedPlacementId==AcceptedPlacementId && PlacementOwner==Owner);
    internal bool Ready => Alive && Matrix && Layout && Sizes && Topology && Geometry && TargetSafe &&
        (!Dragging || Captured) && Decidable;
}

// UI-thread protocol shared by the real probe and deterministic tests. No WPF/native calls here.
internal sealed class EarlyRecovery
{
    private readonly Func<RecoveryCheckpoint> read;
    private readonly Func<RecoveryOwner,string,bool> commit;
    private readonly Func<string,Action,Action> enqueue;
    private readonly Action<string,string> trace;
    private readonly Action revoke;
    private readonly Action<string> unsafeTrial;
    private Action? abort;
    private Action? finalAbort;
    private readonly Func<double> milliseconds;
    private double started;
    private bool finalRequired, deadlineWhileBusy;
    private int finalDispatches;
    private long revision;
    private int observations;
    internal const int CheckBudget=128;
    internal const double WindowMilliseconds=500;
    internal long Signals { get; private set; }
    internal long Deferred { get; private set; }
    internal int CoherentCompletions { get; private set; }
    internal int FatalStops { get; private set; }
    internal int DecidableChecks => observations;
    internal double ElapsedMilliseconds => Math.Max(0,milliseconds()-started);
    internal double RemainingMilliseconds => Math.Max(1,WindowMilliseconds-ElapsedMilliseconds);
    internal RecoveryOwner Owner { get; private set; }
    internal bool Active { get; private set; }
    internal bool Accepted { get; private set; }
    internal int Attempts { get; private set; }
    internal int Repairs { get; private set; }
    internal bool Pending => abort != null || finalAbort != null;
    internal EarlyRecovery(Func<RecoveryCheckpoint> read, Func<RecoveryOwner,string,bool> commit,
        Func<string,Action,Action> enqueue, Action<string,string> trace, Action revoke, Action<string> unsafeTrial,
        Func<double>? milliseconds=null)
    { this.read=read; this.commit=commit; this.enqueue=enqueue; this.trace=trace; this.revoke=revoke; this.unsafeTrial=unsafeTrial;
        this.milliseconds=milliseconds??(()=>System.Diagnostics.Stopwatch.GetTimestamp()*1000.0/System.Diagnostics.Stopwatch.Frequency); }
    internal void Begin(RecoveryOwner owner) {
        // A rapid reversal may replace generations, but cannot renew an unresolved gesture's deadline/repairs.
        var carry=Active && Owner.Gesture==owner.Gesture && Owner.Source==owner.Source && Owner.Hwnd==owner.Hwnd;
        var repairs=carry ? Repairs : 0;
        var expiredBusy=carry && deadlineWhileBusy;
        Cancel("new generation");Owner=owner;Active=true;Attempts=observations=finalDispatches=0;Repairs=repairs;
        Signals=Deferred=0;finalRequired=false;deadlineWhileBusy=expiredBusy;
        if(!carry) started=this.milliseconds();
    }
    internal void Observe()
    {
        if (!Active) return;
        Signals++;
        var s=read();
        if(!Live(s)) return;
        // Own synchronous placement can temporarily invalidate layout; never repair inside that stack.
        if(!s.Decidable) { Undecidable(s,ElapsedMilliseconds>=WindowMilliseconds);return; }
        observations=Math.Min(CheckBudget,observations+1);
        if(ElapsedMilliseconds>=WindowMilliseconds) { Finish(s,true);return; }
        if(finalRequired) { QueueFinal();return; }
        if (Accepted && !s.Ready) {
            Repair();
        } else if (!Accepted && abort==null && Attempts==0 && s.Ready) Schedule("candidate","Loaded");
        if(Active && observations>=CheckBudget) {
            if(s.Ready && Accepted && abort==null) CompleteCoherent("128 decidable checkpoints ended coherent");
            else {finalRequired=true;QueueFinal();}
        }
    }
    private bool Live(RecoveryCheckpoint s) {
        if(s.Owner==Owner && s.Alive) return true;
        if(s.Owner==Owner) revoke();
        Cancel("owner/source no longer live");return false;
    }
    private void Defer() {
        Deferred++;
        if(ElapsedMilliseconds>=WindowMilliseconds) deadlineWhileBusy=true;
        if(Signals>=CheckBudget || deadlineWhileBusy) {
            if(!finalRequired) trace("monitor-deferred","in-flight or superseded target; finite UI-boundary recheck");
            finalRequired=true;QueueFinal();
        }
    }
    private void Undecidable(RecoveryCheckpoint s,bool expired) {
        if(!s.StackBusy && (expired || deadlineWhileBusy)) {
            revoke();StopUnsafe("500 ms current-target association remained unresolved outside native stack");return;
        }
        if(expired) deadlineWhileBusy=true;
        Defer();
    }
    private void QueueFinal() {
        if(!Active || finalAbort!=null || finalDispatches>=2) return;
        var owner=Owner;var token=revision;
        finalDispatches++;
        finalAbort=enqueue("ContextIdle",()=> {
            if(!Active || token!=revision || owner!=Owner) return;
            finalAbort=null;
            var s=read();if(!Live(s)) return;
            if(!s.Decidable) {
                // This operation has already crossed a UI boundary. Do not immediately re-enqueue itself.
                Deferred++;
                if(!s.StackBusy && ElapsedMilliseconds>=WindowMilliseconds) {revoke();StopUnsafe("500 ms current-target association remained unresolved outside native stack");}
                else if(ElapsedMilliseconds>=WindowMilliseconds) deadlineWhileBusy=true;
                return;
            }
            Finish(s,ElapsedMilliseconds>=WindowMilliseconds);
        });
    }
    private void Finish(RecoveryCheckpoint s,bool expired) {
        if(deadlineWhileBusy) {revoke();StopUnsafe("500 ms in-flight completion deadline exceeded");return;}
        if(s.Ready && Accepted) {CompleteCoherent(expired ? "500 ms coherent completion" : "deferred current-target coherent completion");return;}
        if(expired) {revoke();StopUnsafe("500 ms stable checkpoint remained incoherent");return;}
        // Budget exhaustion at a stable divergent boundary still uses the original finite protected repair.
        finalRequired=false;Repair();
    }
    private void Repair() {
        // A final monitor callback may win over a queued Candidate. Supersede its operation/token
        // before installing the repair handle, including forcibly delivered delegates after Abort.
        revision++;abort?.Invoke();abort=null;finalAbort?.Invoke();finalAbort=null;
        Accepted=false;revoke();trace("late-overwrite","live layout/RECT/matrix no longer accepted");
        if(Repairs>=2) {StopUnsafe("two repairs exhausted by repeated overwrite");return;}
        Repairs++;Schedule("repair","ContextIdle");
    }
    private void CompleteCoherent(string reason) {CoherentCompletions++;trace("coherent-completion",reason);Cancel(reason);}
    private void Schedule(string kind,string priority)
    {
        var owner=Owner;var token=revision;
        trace(kind=="candidate" ? "scheduled" : "repair-scheduled",priority);
        abort=enqueue(priority,()=> {
            // Check ownership BEFORE reading WPF/native. Old completion must not clear new abort handle.
            if (!Active || token!=revision || owner!=Owner) return;
            abort=null;
            if (kind=="candidate") Attempts++;
            trace("attempted",kind);
            var s=read();
            if(ElapsedMilliseconds>=WindowMilliseconds || deadlineWhileBusy) {
                if(!Live(s)) return;
                if(!s.Decidable) {Undecidable(s,true);return;}
                Finish(s,true);return;
            }
            if (s.Owner!=owner || !(kind=="repair" ? (s with {Geometry=true}).Ready : s.Ready)) {
                trace("rejected","live checkpoint changed");
                if(kind=="repair") StopUnsafe("repair checkpoint invalid");
                return;
            }
            var ok=commit(owner,kind);
            if (!Active || token!=revision || owner!=Owner) { trace("cancelled","native completion superseded");return; }
            var post=read();
            if (!ok || post.Owner!=owner || !post.Ready) {
                revoke();trace("rejected","commit or native postcheck failed");
                if(kind=="repair") StopUnsafe("protected repair failed");
                return;
            }
            Accepted=true;trace(kind=="candidate" ? "accepted" : "repair","native RECT accepted; layout checked on subsequent signals");
        });
    }
    internal void LegacyCompleted()
    {
        if (!Active) return;
        abort?.Invoke();abort=null;finalAbort?.Invoke();finalAbort=null;Attempts=Math.Max(Attempts,1);revision++;
        trace("fallback","original ContextIdle chain completed");
        var s=read();
        Accepted=s.Owner==Owner && s.Ready;
        if(!Accepted) { revoke();StopUnsafe("Legacy completed without coherent postcheck"); }
        else if(finalRequired || deadlineWhileBusy || ElapsedMilliseconds>=WindowMilliseconds) Finish(s,ElapsedMilliseconds>=WindowMilliseconds);
    }
    internal void Expire()
    {
        if (!Active) return;
        var s=read();
        if(!Live(s)) return;
        if(!s.Decidable) {Undecidable(s,true);return;}
        Finish(s,true);
    }
    private void StopUnsafe(string why) { FatalStops++;Cancel(why);trace("safety-blocked",why);unsafeTrial(why); }
    internal void Cancel(string why)
    {
        var wasActive=Active;revision++;abort?.Invoke();abort=null;finalAbort?.Invoke();finalAbort=null;Active=Accepted=false;
        if(wasActive) trace("cancelled",why);
    }
}
