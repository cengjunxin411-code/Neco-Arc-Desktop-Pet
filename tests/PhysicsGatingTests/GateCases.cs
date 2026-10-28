using NecoArcDesktopPet.Pilot.Physics;

internal static partial class Program
{
    static partial void GateCases()
    {
        var identity = new PhysicsOwnerIdentity(1,2,3,4,5,6);
        var ready = new PhysicsOwnershipSnapshot(identity,true,false,false,false);
        foreach (var mode in Enum.GetValues<PhysicsMode>()) foreach (bool flag in new[]{false,true})
            Case($"gate effective mode flag={flag} preference={mode}",()=>{
                var gate=new PhysicsOwnershipGate();gate.Configure(flag,mode);
                Check(gate.EffectiveMode==(flag?mode:PhysicsMode.Off),"effective mode mismatch");
                Check((gate.TryAcquire(ready)!=null)==(flag&&mode!=PhysicsMode.Off),"mode bypassed opt-in");
            });
        PhysicsOwnershipGate Enabled() {var g=new PhysicsOwnershipGate();g.Configure(true,PhysicsMode.PlanarInertia);return g;}
        foreach(var busy in new[]{ready with{Stable=false},ready with{HigherPriorityOwner=true},ready with{AutonomousMovement=true},ready with{VisualTransaction=true}})
            Case("gate refuses busy " + busy,()=>{
                var gate=Enabled();var token=gate.TryAcquire(ready)!;
                Check(gate.TryAcquire(busy)==null&&!gate.IsCurrent(token,busy),"busy owner allowed a token");
                Check(!gate.IsCurrent(token,ready),"stale token resurrected after busy cleared");
            });
        foreach(var owner in new[]{identity with{Generation=9},identity with{Gesture=9},identity with{SourceToken=9},
            identity with{Hwnd=9},identity with{TopologyVersion=9},identity with{GeometryRevision=9}})
            Case("identity transition " + owner,()=>{
                var gate=Enabled();var token=gate.TryAcquire(ready)!;var next=ready with{Identity=owner};
                Check(!gate.IsCurrent(token,next),"identity mismatch accepted");
                var replacement=gate.TryAcquire(next)!;
                Check(!ReferenceEquals(token,replacement)&&!gate.IsCurrent(token,next)&&gate.IsCurrent(replacement,next),"old callback stole new lease");
            });
        Case("visual Yield revokes lease but retains state revision; never auto resumes",()=>{
            var gate=Enabled();var old=gate.TryAcquire(ready)!;var revision=gate.StateRevision;
            gate.Yield();gate.Yield();
            Check(gate.StateRevision==revision&&!gate.IsCurrent(old,ready),"visual pause discarded state or revived token");
            var next=gate.TryAcquire(ready)!;Check(!ReferenceEquals(next,old),"new visual lease reused stale token");
        });
        Case("visual geometry revision revokes lease without discarding paused integration identity",()=>{
            var gate=Enabled();var old=gate.TryAcquire(ready)!;var revision=gate.StateRevision;
            var visual=ready with{VisualTransaction=true,Identity=identity with{GeometryRevision=7}};
            Check(!gate.IsCurrent(old,visual)&&gate.StateRevision==revision,"visual revision treated as DPI/source loss");
        });
        Case("Reset discards identity state and is idempotent",()=>{
            var gate=Enabled();var old=gate.TryAcquire(ready)!;var revision=gate.StateRevision;gate.Reset();var reset=gate.StateRevision;gate.Reset();
            Check(reset>revision&&gate.StateRevision==reset&&!gate.IsCurrent(old,ready),"Reset failed revocation/idempotence");
        });
        Case("mode change invalidates previous token",()=>{
            var gate=Enabled();var old=gate.TryAcquire(ready)!;gate.Configure(true,PhysicsMode.Gravity);
            Check(!gate.IsCurrent(old,ready),"mode change revived old state");gate.Configure(false,PhysicsMode.Gravity);
            Check(gate.TryAcquire(ready)==null,"disabled lease admitted");
        });
        Case("invalid enum fails closed",()=>{
            var gate=Enabled();gate.Configure(true,(PhysicsMode)99);Check(gate.EffectiveMode==PhysicsMode.Off&&gate.TryAcquire(ready)==null,"invalid mode active");
        });
        Case("null and foreign gate token never validate",()=>{
            var a=Enabled();var b=Enabled();var token=a.TryAcquire(ready)!;b.TryAcquire(ready);
            Check(!b.IsCurrent(token,ready)&&!a.IsCurrent(null,ready),"foreign/null lease accepted");
        });
    }
}
