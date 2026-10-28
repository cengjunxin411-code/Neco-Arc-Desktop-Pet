using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private readonly MultiClickResolver _clickResolver;
    private readonly DispatcherTimer _clickResolverTimer = new();
    private Func<TimeSpan> _clickClock = CreateClickClock();
    private Random _clickRandom = new();
    private ClickActionDefinition[] _availableSingleClicks = Array.Empty<ClickActionDefinition>();
    private ClickActionDefinition[] _availableRockets = Array.Empty<ClickActionDefinition>();
    private bool _rotationAvailable;
    private RocketSession? _rocketSession;
    private RotationSession? _rotationSession;
    private int _clickBubbleGeneration;

    private static Func<TimeSpan> CreateClickClock()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }

    private void InitializeClickInteractions()
    {
        _availableSingleClicks = ClickDefinitions.Available(ClickDefinitions.SingleActions, _gifPlayer);
        _availableRockets = ClickDefinitions.Available(ClickDefinitions.RocketActions, _gifPlayer);
        _rotationAvailable = _gifPlayer?.TryCacheClip(ClickDefinitions.RotationPath) == true;
        _caperAvailable = _gifPlayer?.TryCacheClip(AngrySession.CaperPath) == true;
        _angry1Available = _gifPlayer?.TryCacheClip(AngrySession.Angry1Path) == true;
        _clickResolverTimer.Tick += OnClickResolverTick;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            var active = _clickResolver.IsPending || _clickReactionActive || _rocketSession != null || _rotationSession != null || _angrySession != null;
            CancelClickInteraction(reason: "Visibility.Hidden");
            if (active) RestoreIdleGif();
            _gifPlayer?.Pause();
        };
    }

    private void RegisterClickGesture()
    {
        TraceClick("Gesture.Entry");
        if (RegisterSleepClick()) return;
        if (_rocketSession != null || _contextMenuOpen || !IsVisible || _angrySession?.Stage == AngryStage.Angry1)
        { TraceClick("Gesture.Skip", "Rocket/Menu/Hidden/Angry1"); return; }
        if (RegisterRestInteraction()) return;
        if (_angrySession != null)
        {
            _angrySession.RegisterClick();
            return;
        }
        if (_rocketSession != null || _contextMenuOpen || !IsVisible) { TraceClick("Gesture.Skip", "Rocket/Menu/Hidden"); return; }
        if (_rotationSession != null)
        {
            TraceClick("Gesture.Skip", "ExtendRotation");
            _rotationSession.Extend(_clickClock());
            _gifPlayer?.Resume();
            return;
        }
        if (_clickReactionActive) { TraceClick("Gesture.Skip", "SingleActive"); _gifPlayer?.Resume(); return; }
        if (_ambientGifActive)
        {
            _ambientGifActive = false;
            _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);
        }
        ResetIdleWithDiagnostics();
        CancelClickBubble();
        TraceValidGesture();
        TraceClick("Resolver.Call", "Register");
        if (_clickResolver.Register(_clickClock()) is { } tier) ResolveClickTier(tier);
        else ArmClickResolver();
        TraceClick("Resolver.Return", "Register");
        SyncGifPlayback();
    }

    private void ArmClickResolver()
    {
        _clickResolverTimer.Stop();
        if (_clickResolver.Deadline is not { } due) return;
        _clickResolverTimer.Interval = TimeSpan.FromTicks(Math.Max(1, (due - _clickClock()).Ticks));
        _clickResolverTimer.Start();
        TraceClick("Timer.Arm", detail: new { intervalMs = _clickResolverTimer.Interval.TotalMilliseconds });
    }

    private void OnClickResolverTick(object? sender, EventArgs e)
    {
        TraceClick("Timer.Tick");
        if (!_clickResolver.IsPending) return;
        if (HasSleepOwnership || !IsVisible || _isDragging || _landingActive || _contextMenuOpen ||
            _attachmentMode != AttachmentMode.None || _rocketSession != null || _rotationSession != null || _angrySession != null)
        {
            _clickResolver.CancelForReason("Timer.StateGuard");
            _clickResolverTimer.Stop();
            return;
        }
        if (_clickResolver.Poll(_clickClock()) is { } tier) ResolveClickTier(tier);
        else ArmClickResolver(); // A queued old Tick must honor the current deadline.
    }

    private void ResolveClickTier(ClickTier tier)
    {
        if (HasSleepOwnership || _angrySession != null) return;
        TraceClick("Resolve." + tier);
        _clickResolver.CancelForReason("ResolveClickTier:" + tier);
        _clickResolverTimer.Stop();
        switch (tier)
        {
            case ClickTier.Single: PlayClickReaction(); break;
            case ClickTier.Double: BeginRocket(); break;
            case ClickTier.Triple: BeginRotation(); break;
        }
    }

    private void BeginRocket()
    {
        if (_restSession.IsResting || HasSleepOwnership) return;
        if (_angrySession != null) return;
        CancelClickInteraction();
        StopAutonomousMovement();
        ResetIdleWithDiagnostics();
        _ambientGifActive = false;
        var definition = ClickDefinitions.Choose(_availableRockets, _clickRandom);
        if (definition == null || _gifPlayer?.TrySwitchTo(definition.ResourcePath) != true)
        {
            RestoreIdleGif();
            return;
        }
        var center = _anchorCenterX;
        var bottom = _anchorBottomY;
        ClearAttachment();
        _animationTimer?.Stop();
        ResetAnimationState();
        _bubbleWindow?.Hide();
        // VirtualScreenTop also covers a monitor above the current monitor.
        _rocketSession = new RocketSession(center, bottom, SystemParameters.VirtualScreenTop, _clickClock(), _settings.Interaction);
        ApplyPetLayout();
        _gifPlayer.Resume();
    }

    private void BeginRotation()
    {
        if (_restSession.IsResting || HasSleepOwnership) return;
        if (_angrySession != null) return;
        CancelClickInteraction();
        StopAutonomousMovement();
        ResetIdleWithDiagnostics();
        _ambientGifActive = false;
        if (!_rotationAvailable) { RestoreIdleGif(); return; }
        var session = new RotationSession(_clickClock(), _settings.Interaction);
        _rotationSession = session;
        if (_gifPlayer?.TrySwitchToUntil(ClickDefinitions.RotationPath,
            () => ReferenceEquals(_rotationSession, session) && session.ShouldFinish(_clickClock())) != true)
        {
            RestoreIdleGif();
            return;
        }
        _bubbleWindow?.Hide();
        ApplyPetLayout();
        _gifPlayer.Resume();
    }

    private void AdvanceClickInteractions()
    {
        var rocket = _rocketSession;
        if (rocket == null) return;
        // Use all transformed source corners, not a single frame's alpha bounds.
        var size = new Size(PetImage.Width, PetImage.Height);
        double Lower(Point point) => Top + AttachmentGeometry.TransformPoint(point, size,
            new Point(Canvas.GetLeft(PetImage), Canvas.GetTop(PetImage)),
            PetImage.RenderTransform.Value, PetImage.RenderTransformOrigin).Y - _anchorBottomY;
        var lowerExtent = Math.Max(Math.Max(Lower(new Point(0, 0)), Lower(new Point(size.Width, 0))),
            Math.Max(Lower(new Point(0, size.Height)), Lower(new Point(size.Width, size.Height))));
        rocket.Advance(_clickClock(), Math.Max(0, lowerExtent));
        _anchorBottomY = rocket.BottomY;
        if (rocket.IsComplete)
        {
            _anchorCenterX = rocket.OriginalCenterX;
            _anchorBottomY = rocket.OriginalBottomY;
            RestoreIdleGif();
            return;
        }
        PetSurface.Visibility = rocket.IsInvisible ? Visibility.Hidden : Visibility.Visible;
        ApplyPetLayout();
    }

    private void CancelClickInteraction(bool restoreRocketPosition = true, bool cancelBubble = true,
        [CallerMemberName] string reason = "")
    {
        _clickResolver.CancelForReason(reason);
        _clickResolverTimer.Stop();
        if (restoreRocketPosition && _rocketSession is { } rocket)
        {
            _anchorCenterX = rocket.OriginalCenterX;
            _anchorBottomY = rocket.OriginalBottomY;
            if (_leftPressed)
            {
                // A held gesture can outlive either flight or high-priority return.
                // Rebase before clearing the session; its eventual Click stays suppressed.
                var delta = GetCursorScreenDip() - _mouseDownScreen;
                _dragStartCenterX = _anchorCenterX - delta.X;
                _dragStartBottomY = _anchorBottomY - delta.Y;
            }
        }
        _rocketSession = null;
        _rotationSession = null;
        CancelAngrySequence();
        _clickReactionActive = false;
        PetSurface.Visibility = Visibility.Visible;
        if (cancelBubble) CancelClickBubble();
    }

    private void CancelClickBubble()
    {
        _clickBubbleGeneration++;
        TraceClick("Bubble.Cancel", "CancelClickBubble");
        _delayedClickSpeechTimer?.Stop();
    }
}
