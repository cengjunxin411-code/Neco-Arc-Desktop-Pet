using System;
using System.Diagnostics;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

// Owns inactivity semantics; the window owns animation and one deadline timer.
internal sealed class RestSession
{
    public static readonly TimeSpan RestInactivityThreshold = PetSettingsDefaults.Create().Rest.EnterAfter;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<RestSettings> _settings;
    private TimeSpan _enterAfter;
    public bool IsResting { get; private set; }
    public bool RestPending { get; private set; }
    public bool TimerPaused { get; private set; } = true;
    public TimeSpan? InactivityStartedAt { get; private set; }
    public TimeSpan Elapsed => InactivityStartedAt is { } start ? _clock() - start : TimeSpan.Zero;
    public TimeSpan Remaining => _enterAfter - Elapsed;

    public RestSession() : this(CreateClock()) { }
    public RestSession(Func<TimeSpan> clock) : this(clock, () => PetSettingsDefaults.Create().Rest) { }
    public RestSession(Func<RestSettings> settings) : this(CreateClock(), settings) { }
    public RestSession(Func<TimeSpan> clock, Func<RestSettings> settings)
    {
        _clock = clock; _settings = settings; _enterAfter = settings().EnterAfter;
    }
    private static Func<TimeSpan> CreateClock()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }
    public void Start()
    {
        _enterAfter = _settings().EnterAfter;
        IsResting = RestPending = TimerPaused = false;
        InactivityStartedAt = _clock();
    }
    public bool UserInteraction()
    {
        var wasResting = IsResting;
        IsResting = RestPending = false;
        TimerPaused = true;
        InactivityStartedAt = null;
        return wasResting;
    }
    public void ResumeAtBaseIdle()
    {
        if (TimerPaused) Start();
    }
    public bool Poll(bool atBaseIdle)
    {
        if (!_settings().Enabled) { RestPending = false; return false; }
        if (TimerPaused || IsResting) return false;
        if (Elapsed >= _enterAfter) RestPending = true;
        return RestPending && atBaseIdle;
    }
    public void BeginRest()
    {
        IsResting = true;
        RestPending = false;
    }
    public void RetryAfterFailure() => Start();
    public void InterruptRest()
    {
        // A system interruption ends this Rest. Start a fresh period at Base Idle
        // instead of immediately restoring an old pose after Fall/Hide.
        if (IsResting) UserInteraction();
    }
}
