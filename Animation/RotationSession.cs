using System;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

internal sealed class RotationSession
{
    private readonly TimeSpan _cap;
    private TimeSpan _deadline;
    private readonly TimeSpan _extension;
    public bool ShouldBecomeAngry { get; private set; }

    public RotationSession(TimeSpan now) : this(now, PetSettingsDefaults.Create().Interaction) { }
    public RotationSession(TimeSpan now, InteractionSettings settings)
    {
        _cap = now + settings.RotationMaxDuration;
        _deadline = now + settings.RotationInitialDuration;
        _extension = settings.RotationExtensionPerClick;
    }

    public TimeSpan Remaining(TimeSpan now) => now >= _deadline ? TimeSpan.Zero : _deadline - now;

    public void Extend(TimeSpan now)
    {
        if (now >= _cap) return;
        var next = (now > _deadline ? now : _deadline) + _extension;
        _deadline = next < _cap ? next : _cap;
        if (_deadline == _cap) ShouldBecomeAngry = true;
    }

    // The player checks this only after displaying a loop's final frame in full.
    public bool ShouldFinish(TimeSpan now) => now >= _deadline;
}
