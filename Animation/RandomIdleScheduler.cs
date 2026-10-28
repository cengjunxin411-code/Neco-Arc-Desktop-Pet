using System;
using System.Diagnostics;
using System.Windows.Threading;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

// Owns one scheduling timer, never a GIF playback clock or a pet state.
internal sealed class RandomIdleScheduler : IDisposable
{
    private readonly string[] _actions;
    private readonly Func<bool> _canTrigger;
    private readonly Action<string> _trigger;
    private readonly Random _random;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<bool>? _canMove;
    private readonly Action? _triggerMove;
    private readonly Func<bool>? _preserveDeadlineWhenBlocked;
    private readonly DispatcherTimer _timer = new();
    private TimeSpan? _dueAt;
    private int _lastAction = -1;
    private bool _disposed;
    private Func<(TimeSpan Min, TimeSpan Max)> _delayRange = DefaultDelayRange;
    private Func<bool> _ambientEnabled = () => true;
    private Func<double> _movementShare = () => PetSettingsDefaults.Create().Movement.SelectionWeight;

    private static (TimeSpan, TimeSpan) DefaultDelayRange()
    {
        var settings = PetSettingsDefaults.Create().Normal;
        return (settings.RandomIdleMinDelay, settings.RandomIdleMaxDelay);
    }

    public void Configure(Func<(TimeSpan Min, TimeSpan Max)> delayRange,
        Func<bool> ambientEnabled, Func<double>? movementShare = null)
    {
        _delayRange = delayRange;
        _ambientEnabled = ambientEnabled;
        if (movementShare != null) _movementShare = movementShare;
    }

    public RandomIdleScheduler(string[] actions, Func<bool> canTrigger, Action<string> trigger,
        Random random, Func<TimeSpan> clock)
        : this(actions, canTrigger, trigger, random, clock, null, null) { }

    public RandomIdleScheduler(string[] actions, Func<bool> canTrigger, Action<string> trigger,
        Random random, Func<TimeSpan> clock, Func<bool>? canMove, Action? triggerMove)
        : this(actions, canTrigger, trigger, random, clock, canMove, triggerMove, null) { }

    public RandomIdleScheduler(string[] actions, Func<bool> canTrigger, Action<string> trigger,
        Random random, Func<TimeSpan> clock, Func<bool>? canMove, Action? triggerMove,
        Func<bool>? preserveDeadlineWhenBlocked)
    {
        _actions = (string[])actions.Clone();
        _canTrigger = canTrigger;
        _trigger = trigger;
        _random = random;
        _clock = clock;
        _canMove = canMove;
        _triggerMove = triggerMove;
        _preserveDeadlineWhenBlocked = preserveDeadlineWhenBlocked;
        _timer.Tick += OnTick;
    }

    public RandomIdleScheduler(string[] actions, Func<bool> canTrigger, Action<string> trigger)
        : this(actions, canTrigger, trigger, new Random(), CreateClock()) { }

    public RandomIdleScheduler(string[] actions, Func<bool> canTrigger, Action<string> trigger,
        Func<bool> canMove, Action triggerMove)
        : this(actions, canTrigger, trigger, new Random(), CreateClock(), canMove, triggerMove) { }

    public RandomIdleScheduler(string[] actions, Func<bool> canTrigger, Action<string> trigger,
        Func<bool> canMove, Action triggerMove, Func<bool>? preserveDeadlineWhenBlocked)
        : this(actions, canTrigger, trigger, new Random(), CreateClock(), canMove, triggerMove,
            preserveDeadlineWhenBlocked) { }

    private static Func<TimeSpan> CreateClock()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }

    public void Update()
    {
        if (_disposed) return;
        if ((!_ambientEnabled() || _actions.Length == 0) && !(_canMove?.Invoke() ?? false))
        {
            Reset();
            return;
        }
        if (!_canTrigger())
        {
            SuspendOrReset();
            return;
        }
        if (_dueAt != null && _timer.IsEnabled) return;
        if (_dueAt == null)
        {
            var (min, max) = _delayRange();
            var delay = TimeSpan.FromSeconds(min.TotalSeconds + _random.NextDouble() * (max - min).TotalSeconds);
            _dueAt = _clock() + delay;
            _timer.Interval = delay;
        }
        else
        {
            // Resuming a UI block keeps elapsed time, including an already overdue deadline.
            _timer.Interval = TimeSpan.FromTicks(Math.Max(1, (_dueAt.Value - _clock()).Ticks));
        }
        _timer.Start();
    }

    private void SuspendOrReset()
    {
        if (_preserveDeadlineWhenBlocked?.Invoke() == true) _timer.Stop();
        else Reset();
    }

    public void Reset()
    {
        _timer.Stop();
        _dueAt = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || _dueAt == null) return;
        if ((!_ambientEnabled() || _actions.Length == 0) && !(_canMove?.Invoke() ?? false))
        {
            Reset();
            return;
        }
        if (!_canTrigger())
        {
            SuspendOrReset();
            return;
        }
        var remaining = _dueAt.Value - _clock();
        if (remaining > TimeSpan.Zero)
        {
            // A queued old tick after Reset must honor the new full deadline.
            _timer.Interval = remaining;
            return;
        }
        Reset();
        // One deadline chooses exactly one activity. Missing movement preserves Ambient.
        if (_triggerMove != null && (_canMove?.Invoke() ?? false) &&
            (!_ambientEnabled() || _actions.Length == 0 || _random.NextDouble() < _movementShare()))
        {
            _triggerMove();
            return;
        }
        var count = _actions.Length;
        if (count == 0 || !_ambientEnabled()) return;
        var index = _random.Next(count > 1 && _lastAction >= 0 ? count - 1 : count);
        if (count > 1 && _lastAction >= 0 && index >= _lastAction) index++;
        _lastAction = index;
        _trigger(_actions[index]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _timer.Tick -= OnTick;
        _disposed = true;
    }
}
