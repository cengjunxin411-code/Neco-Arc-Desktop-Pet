using System;

namespace NecoArcDesktopPet.Pilot.Physics;

// Admission only. No integration state, clock, callbacks, geometry repair or writer.
internal readonly record struct PhysicsOwnerIdentity(long Generation, long Gesture, long SourceToken,
    long Hwnd, long TopologyVersion, long GeometryRevision);

internal readonly record struct PhysicsOwnershipSnapshot(PhysicsOwnerIdentity Identity, bool Stable,
    bool VisualTransaction, bool HigherPriorityOwner, bool AutonomousMovement);

internal sealed class PhysicsRunToken
{
    internal PhysicsRunToken() { }
}

internal sealed class PhysicsOwnershipGate
{
    private PhysicsOwnerIdentity? _identity;
    internal PhysicsRunToken? CurrentToken { get; private set; }
    internal PhysicsMode EffectiveMode { get; private set; }
    // A temporary visual pause keeps this revision. Identity/owner loss discards it.
    // Future consumers must bind their integration state to this revision as well.
    internal long StateRevision { get; private set; }

    internal void Configure(bool experimental, PhysicsMode requested)
    {
        var effective = experimental && Enum.IsDefined(requested) ? requested : PhysicsMode.Off;
        if (effective == EffectiveMode) return;
        Reset();
        EffectiveMode = effective;
    }

    internal void Yield() => CurrentToken = null;

    internal void Reset()
    {
        if (_identity != null || CurrentToken != null) StateRevision++;
        CurrentToken = null;
        _identity = null;
    }

    private bool Observe(PhysicsOwnershipSnapshot snapshot)
    {
        if (EffectiveMode == PhysicsMode.Off || !snapshot.Stable || snapshot.HigherPriorityOwner || snapshot.AutonomousMovement)
        {
            Reset();
            return false;
        }
        if (_identity != snapshot.Identity)
        {
            // Footprint revision is a short geometry transaction, unlike a new
            // gesture/source/DPI/topology identity. Both revoke the running lease.
            if (_identity is {} previous &&
                previous with { GeometryRevision = snapshot.Identity.GeometryRevision } == snapshot.Identity)
                Yield();
            else Reset();
            _identity = snapshot.Identity;
        }
        if (snapshot.VisualTransaction) { Yield(); return false; }
        return true;
    }

    internal PhysicsRunToken? TryAcquire(PhysicsOwnershipSnapshot snapshot) =>
        Observe(snapshot) ? CurrentToken ??= new PhysicsRunToken() : null;

    // Validation can only revoke; it never acquires, resumes or renews a lease.
    internal bool IsCurrent(PhysicsRunToken? token, PhysicsOwnershipSnapshot snapshot) =>
        Observe(snapshot) && token != null && ReferenceEquals(token, CurrentToken);
}
