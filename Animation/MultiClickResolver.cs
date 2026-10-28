using System;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet.Animation;

internal enum ClickTier { Single, Double, Triple }

// One deadline, driven by the owning Dispatcher. Only completed gestures enter here.
internal sealed class MultiClickResolver
{
    public static int WindowMilliseconds => (int)PetSettingsDefaults.Create().Interaction.FirstClickWindow.TotalMilliseconds;
    private readonly Func<InteractionSettings> _settings;
    private TimeSpan _firstWindow, _secondWindow;
    private bool _gestureCaptured;
    public TimeSpan FirstWindow => _firstWindow;
    public TimeSpan SecondWindow => _secondWindow;
    public MultiClickResolver() : this(() => PetSettingsDefaults.Create().Interaction) { }
    public MultiClickResolver(Func<InteractionSettings> settings) => _settings = settings;
    private int _count;
    public TimeSpan? Deadline { get; private set; }
    public bool IsPending => _count != 0;
    public int Count => _count;
    public Action<string, int, int, TimeSpan?, string>? DiagnosticTrace { get; set; }

    // Pilot captures at Down, before Workbench can replace the settings object.
    // Ordinary callers still capture on the first completed click in Register.
    public void CaptureGesture()
    {
        if (_count != 0 || _gestureCaptured) return;
        var settings = _settings();
        _firstWindow = settings.FirstClickWindow;
        _secondWindow = settings.SecondClickWindow;
        _gestureCaptured = true;
    }

    public ClickTier? Register(TimeSpan now)
    {
        // A delayed Dispatcher callback must not let an expired click join a combo.
        if (Poll(now) is { } expired) return expired;
        if (_count == 0) CaptureGesture();
        var before = _count++;
        if (_count == 3)
        {
            DiagnosticTrace?.Invoke("Register", before, _count, Deadline, "TripleImmediate");
            CancelForReason("TripleImmediate");
            return ClickTier.Triple;
        }
        Deadline = now + (_count == 1 ? _firstWindow : _secondWindow);
        DiagnosticTrace?.Invoke("Register", before, _count, Deadline, "Pending");
        return null;
    }

    public ClickTier? Poll(TimeSpan now)
    {
        if (Deadline == null || now < Deadline.Value) return null;
        var result = _count == 1 ? ClickTier.Single : ClickTier.Double;
        CancelForReason("DeadlineExpired:" + result);
        return result;
    }

    public void Cancel()
        => CancelForReason("External.Cancel");

    public void CancelForReason(string reason)
    {
        var before = _count;
        var deadline = Deadline;
        _count = 0;
        Deadline = null;
        _gestureCaptured = false;
        DiagnosticTrace?.Invoke("Cancel", before, 0, deadline, reason);
    }
}
