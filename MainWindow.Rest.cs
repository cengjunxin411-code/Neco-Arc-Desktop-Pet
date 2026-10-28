using System;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private const string RestGifPath = "NecoPetMaterials/rest/prone_prop_dangle.gif";
    private RestSession _restSession = null!;
    private readonly DispatcherTimer _restTimer = new();
    private bool _restAvailable;
    private string[] _availableRestAmbientActions = Array.Empty<string>();
    private RandomIdleScheduler _restAmbientScheduler = null!;
    private Random _restAmbientRandom = new();
    private bool _restAmbientActive;
    private string? _restAmbientPath;
    private long _restAmbientPlaybackGeneration = -1;

    private bool IsRestBaseIdle => IsVisible && _restSession.IsResting && !_restAmbientActive &&
        !_isDragging && !_leftPressed && !_contextMenuOpen && !_landingActive && !_clickReactionActive &&
        !_clickResolver.IsPending && _rocketSession == null && _rotationSession == null && _angrySession == null &&
        _autonomousMovement == null && _attachmentMode == AttachmentMode.None &&
        !(_animationTimer?.IsEnabled ?? false) && _gifPlayer?.ResourcePath == RestGifPath;

    private void InitializeRest()
    {
        _restAvailable = _gifPlayer?.TryCacheClip(RestGifPath) == true;
        _availableRestAmbientActions = RestAmbientActions.Available(_gifPlayer);
        _restAmbientScheduler = new RandomIdleScheduler(_availableRestAmbientActions,
            () => IsRestBaseIdle, PlayRestAmbientAction);
        _restAmbientScheduler.Configure(() => (_settings.Rest.AmbientMinDelay, _settings.Rest.AmbientMaxDelay),
            () => _settings.Rest.AmbientEnabled);
        _restTimer.Tick += OnRestDeadline;
        IsVisibleChanged += (_, _) =>
        {
#if PMV2_PILOT
            if (_pilotLog != null) return;
#endif
            if (!IsVisible)
            {
                _restTimer.Stop();
                CancelRestAmbient();
                if (_restSession.IsResting)
                {
                    _restSession.InterruptRest();
                    RestoreIdleGif();
                    _gifPlayer?.Pause();
                }
            }
            else if (IsLoaded)
            {
                UpdateRestState();
                ArmRestDeadline();
            }
        };
    }
    private void StartRestInactivity()
    {
        _restSession.Start();
        ArmRestDeadline();
    }
    private void ArmRestDeadline()
    {
        _restTimer.Stop();
        if (!_settings.Rest.Enabled) return;
        if (HasSleepOwnership) return;
        if (!IsVisible || _restSession.TimerPaused || _restSession.IsResting || _restSession.RestPending) return;
        _restTimer.Interval = TimeSpan.FromTicks(Math.Max(1, _restSession.Remaining.Ticks));
        _restTimer.Start();
    }
    private void OnRestDeadline(object? sender, EventArgs e)
    {
        _restTimer.Stop();
        if (!IsVisible || HasSleepOwnership) return;
        if (_restSession.Poll(IsNormalIdle)) BeginRest();
        else ArmRestDeadline();
    }
    private void UpdateRestState()
    {
        if (HasSleepOwnership)
        {
            if (_sleepSession.IsSleeping) _sleepAmbientScheduler.Update();
            return;
        }
        if (_restSession.IsResting)
        {
            if (_settings.Sleep.Enabled && _sleepSession.SleepPending && IsRestBaseIdle)
            {
                BeginSleep();
                return;
            }
            _restAmbientScheduler.Update();
            return;
        }
        if (!_settings.Rest.Enabled || !IsVisible || !IsNormalIdle) return;
        if (_restSession.TimerPaused)
        {
            _restSession.ResumeAtBaseIdle();
            ArmRestDeadline();
        }
        if (_restSession.RestPending && _restSession.Poll(true)) BeginRest();
    }
    // Accepted Click, actual Dragging threshold, and Menu.Open only; never Wheel.
    private bool RegisterRestInteraction()
    {
        var sleeping = HasSleepOwnership;
        InterruptSleep();
        _restTimer.Stop();
        CancelRestAmbient();
        var wake = _restSession.UserInteraction();
        if (wake || sleeping) RestoreIdleGif();
        return wake || sleeping;
    }
    private void BeginRest()
    {
        if (!IsVisible || !IsNormalIdle) return;
        _randomIdleScheduler.Reset();
        CancelClickBubble();
        _bubbleWindow?.Hide();
        if (!_restAvailable || _gifPlayer?.TrySwitchTo(RestGifPath) != true)
        {
            _restAvailable = false;
            _restSession.RetryAfterFailure();
            ArmRestDeadline();
            _randomIdleScheduler.Update();
            return;
        }
        _restSession.BeginRest();
        _restTimer.Stop();
        ApplyPetLayout();
        _gifPlayer.Resume();
        StartSleepDeadline();
        _restAmbientScheduler.Update(); // Entry gets a fresh full 8–14s wait, never an immediate action.
    }
    private void InterruptRest()
    {
        InterruptSleep();
        CancelRestAmbient();
        if (!_restSession.IsResting) return;
        _restSession.InterruptRest();
        _restTimer.Stop();
    }

    private void CancelRestAmbient()
    {
        _restAmbientScheduler.Reset();
        _restAmbientActive = false;
        _restAmbientPath = null;
        _restAmbientPlaybackGeneration = -1;
    }

    private void PlayRestAmbientAction(string path)
    {
        if (!_settings.Rest.AmbientEnabled || !IsRestBaseIdle || !Array.Exists(_availableRestAmbientActions,
            action => string.Equals(action, path, StringComparison.OrdinalIgnoreCase))) return;
        _restAmbientScheduler.Reset();
        if (_gifPlayer?.TrySwitchToLoops(path, RestAmbientActions.LoopsFor(path, _restAmbientRandom, _settings.Rest)) != true)
        {
            _restAmbientScheduler.Update();
            return;
        }
        _restAmbientActive = true;
        _restAmbientPath = path;
        _restAmbientPlaybackGeneration = _gifPlayer.PlaybackGeneration;
        ApplyPetLayout();
        _gifPlayer.Resume();
    }

    private void CompleteRestAmbient(object? sender, EventArgs e)
    {
        if (!_restSession.IsResting || !_restAmbientActive || !IsVisible || _isDragging || _leftPressed ||
            _contextMenuOpen || _attachmentMode != AttachmentMode.None || _landingActive ||
            !ReferenceEquals(sender, _gifPlayer) || _gifPlayer?.IsComplete != true ||
            _gifPlayer.PlaybackGeneration != _restAmbientPlaybackGeneration || _gifPlayer.ResourcePath != _restAmbientPath)
            return;
        CancelRestAmbient();
        if (_sleepSession.Poll(true))
        {
            BeginSleep();
            return;
        }
        if (!_restAvailable || !_gifPlayer.TrySwitchTo(RestGifPath))
        {
            InterruptRest();
            RestoreIdleGif();
            return;
        }
        ApplyPetLayout();
        _gifPlayer.Resume();
        _restAmbientScheduler.Update(); // Completion schedules from now, not the old deadline.
    }
}
