using System;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

internal sealed class RocketSession
{
    private readonly TimeSpan _startedAt;
    private readonly double _screenTop;
    private TimeSpan? _invisibleAt;
    private readonly double _speed;
    private readonly TimeSpan _hiddenDuration;
    public double OriginalCenterX { get; }
    public double OriginalBottomY { get; }
    public double BottomY { get; private set; }
    public bool IsInvisible => _invisibleAt != null;
    public bool IsComplete { get; private set; }

    public RocketSession(double centerX, double bottomY, double screenTop, TimeSpan now)
        : this(centerX, bottomY, screenTop, now, PetSettingsDefaults.Create().Interaction) { }
    public RocketSession(double centerX, double bottomY, double screenTop, TimeSpan now, InteractionSettings settings)
    {
        OriginalCenterX = centerX;
        OriginalBottomY = BottomY = bottomY;
        _screenTop = screenTop;
        _startedAt = now;
        _speed = settings.RocketVerticalSpeed;
        _hiddenDuration = settings.RocketHiddenDuration;
    }

    // Lower extent includes the entire transformed source canvas (also the flames).
    public void Advance(TimeSpan now, double lowerExtentFromAnchor)
    {
        if (IsComplete) return;
        if (_invisibleAt is { } invisible)
        {
            IsComplete = now - invisible >= _hiddenDuration;
            return;
        }
        BottomY = OriginalBottomY - _speed * Math.Max(0, (now - _startedAt).TotalSeconds);
        if (BottomY + lowerExtentFromAnchor <= _screenTop) _invisibleAt = now;
    }
}
