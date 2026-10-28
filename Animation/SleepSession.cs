using System;
using System.Diagnostics;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

// One Rest lifetime owns this monotonic deadline; Rest Ambient never touches it.
internal sealed class SleepSession
{
    public static readonly TimeSpan SleepAfterRestThreshold = PetSettingsDefaults.Create().Sleep.EnterAfterRest;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<SleepSettings> _settings;
    private TimeSpan _enterAfter;
    private TimeSpan? _restStartedAt;
    private TimeSpan? _pausedAt;
    private TimeSpan _pausedDuration;
    public bool IsSleeping { get; private set; }
    public bool SleepPending { get; private set; }
    public TimeSpan RestElapsed => _restStartedAt is { } start ? (_pausedAt ?? _clock()) - start - _pausedDuration : TimeSpan.Zero;
    public TimeSpan Remaining => _enterAfter - RestElapsed;
    public SleepSession() : this(CreateClock()) { }
    public SleepSession(Func<TimeSpan> clock) : this(clock, () => PetSettingsDefaults.Create().Sleep) { }
    public SleepSession(Func<SleepSettings> settings) : this(CreateClock(), settings) { }
    public SleepSession(Func<TimeSpan> clock, Func<SleepSettings> settings)
    {
        _clock = clock; _settings = settings; _enterAfter = settings().EnterAfterRest;
    }
    private static Func<TimeSpan> CreateClock()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }
    public void BeginRest()
    {
        Cancel();
        _enterAfter = _settings().EnterAfterRest;
        _restStartedAt = _clock();
    }
    public bool Poll(bool canEnter)
    {
        if (!_settings().Enabled) { SleepPending = false; return false; }
        if (_restStartedAt == null || IsSleeping) return false;
        if (RestElapsed >= _enterAfter) SleepPending = true;
        return SleepPending && canEnter;
    }
    public void BeginSleep()
    {
        Cancel();
        IsSleeping = true;
    }
    // Pilot menu suspension preserves both the original deadline and pending bit.
    // Ordinary Rest/Sleep never calls these methods and retains its existing clock.
    public void Pause()
    {
        if (_restStartedAt != null && _pausedAt == null) _pausedAt = _clock();
    }
    public void Resume()
    {
        if (_pausedAt is not { } paused) return;
        _pausedDuration += _clock() - paused;
        _pausedAt = null;
    }
    public void Cancel()
    {
        _restStartedAt = null;
        _pausedAt = null;
        _pausedDuration = TimeSpan.Zero;
        IsSleeping = SleepPending = false;
    }
}
