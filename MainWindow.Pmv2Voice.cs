using System;
using NecoArcDesktopPet.Pilot.Audio;
using NecoArcDesktopPet.Pilot.Physics;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private ActionVoiceService? _pilotActionVoice;
    private EventHandler? _pilotActionVoicePendingFrame;
    private long _pilotActionVoiceSequence, _pilotActionVoicePendingId;
    private long _pilotClickVoiceId, _pilotRandomVoiceId, _pilotRocketVoiceId;
    private long _pilotFallVoiceId, _pilotLandVoiceId;
    private long _pilotMoveVoiceId, _pilotMoveVoiceRevision;
    private long _pilotDragVoiceId, _pilotThrowVoiceId;
    private (long Generation, long Gesture, long Token, object? Source, nint Hwnd)? _pilotDragVoiceGesture;
    private PilotGravityThrowVisual? _pilotThrowVoiceVisual;
    private long _pilotAttachVoiceId, _pilotGravityLandVoiceId, _pilotPlanarThrowVoiceId;
    private long _pilotRestVoiceId, _pilotRestExitVoiceId, _pilotStretchVoiceId, _pilotWakeVoiceId;
    // Separate from the ambient schedulers: a voice draw cannot move their deadlines.
    private Random _pilotStretchVoiceRandom = new();
    private Pilot.PilotAttachmentTarget? _pilotAttachVoiceLease;
    private PilotGravityLandingOwner? _pilotGravityLandVoiceOwner;
    private PilotPlanarThrowVisual? _pilotPlanarThrowVoiceVisual;

    private void PilotAttachmentVoiceAdmitted()
    {
        var lease = _pilotAttachmentTarget;
        var path = _pilotAttachmentPoseSide == AttachmentSide.Top ? PilotStandPath : PilotSidePath;
        if (lease == null || _pilotAttachmentPosePending || _gifPlayer?.ResourcePath != path ||
            _pilotVisualSizePath != path || ReferenceEquals(lease, _pilotAttachVoiceLease)) return;
        _pilotAttachVoiceLease = lease; // Consume even when disabled or media fails. Restoration is the same lease.
        var generation = _pilotGeneration;
        var source = _pilotSource;
        var token = _pilotSourceToken;
        var hwnd = PilotHwnd;
        var topology = _pilotTopology?.State.LastKnownGood?.Version;
        PilotActionVoiceStart(ref _pilotAttachVoiceId, path, _gifPlayer.PlaybackGeneration,
            () => ReferenceEquals(lease, _pilotAttachmentTarget) && !_pilotAttachmentPosePending &&
                lease.TryRead(out _) &&
                generation == _pilotGeneration && ReferenceEquals(source, _pilotSource) &&
                token == _pilotSourceToken && hwnd == PilotHwnd && !_pilotTopologyHold &&
                topology == _pilotTopology?.State.LastKnownGood?.Version && _pilotVisualSizePath == path);
    }

    private void PilotGravityLandingVoiceAdmitted()
    {
        var owner = _pilotGravityLanding;
        var session = _pilotGravitySession;
        var token = _pilotPhysicsRunToken;
        if (owner == null || session == null || token == null || _pilotGravityPending ||
            owner.Phase != PilotGravityLandingPhase.Sliding || owner.Path != PilotGravityFallPath ||
            !PilotGravityLandingOwnsClip || ReferenceEquals(owner, _pilotGravityLandVoiceOwner)) return;
        _pilotGravityLandVoiceOwner = owner;
        PilotActionVoiceStart(ref _pilotGravityLandVoiceId, PilotGravityFallPath, owner.Playback,
            () => ReferenceEquals(owner, _pilotGravityLanding) && PilotGravityLandingOwnsClip &&
                !_pilotGravityPending && owner.Path == PilotGravityFallPath &&
                (owner.Phase == PilotGravityLandingPhase.WaitingForAnimation ||
                    ReferenceEquals(session, _pilotGravitySession) && ReferenceEquals(token, _pilotPhysicsRunToken)));
    }

    private void PilotPlanarThrowVoiceAdmitted()
    {
        var visual = _pilotPlanarThrowVisual;
        var session = _pilotPhysicsSession;
        var token = _pilotPhysicsRunToken;
        if (visual == null || session == null || token == null || _pilotPhysicsPending != null ||
            !PilotPlanarThrowOwnsClip || ReferenceEquals(visual, _pilotPlanarThrowVoiceVisual)) return;
        _pilotPlanarThrowVoiceVisual = visual;
        PilotActionVoiceStart(ref _pilotPlanarThrowVoiceId, PilotPlanarThrowPath, visual.Playback,
            () => ReferenceEquals(visual, _pilotPlanarThrowVisual) && ReferenceEquals(session, _pilotPhysicsSession) &&
                ReferenceEquals(token, _pilotPhysicsRunToken) && PilotPlanarThrowOwnsClip && _pilotPhysicsPending == null);
    }

    private void PilotDragVoiceAdmitted()
    {
        if (!_pilotDragVisualActive || !PilotDragClipAdmitted || _gifPlayer == null ||
            _pilotClicks?.Dragging != true ||
            !(_crossScreen ? _crossActive && _crossDragging : _pilotDrag.Active && _pilotDrag.Dragging)) return;
        var key = (_pilotGeneration, _pilotGesture, _pilotSourceToken, (object?)_pilotSource, PilotHwnd);
        if (_pilotDragVoiceGesture == key) return;
        _pilotDragVoiceGesture = key; // Consume disabled/failed attempts; visual retries cannot rearm.
        var revision = _pilotDragVisualRevision;
        var topology = _pilotTopology?.State.LastKnownGood?.Version;
        var path = _pilotDragVisualClipPath!;
        PilotActionVoiceStart(ref _pilotDragVoiceId, path, _gifPlayer.PlaybackGeneration,
            () => key == (_pilotGeneration, _pilotGesture, _pilotSourceToken, (object?)_pilotSource, PilotHwnd) &&
                ReferenceEquals(key.Item4, _pilotSource) && topology == _pilotTopology?.State.LastKnownGood?.Version &&
                revision == _pilotDragVisualRevision && _pilotDragVisualActive && PilotDragClipAdmitted &&
                _pilotClicks?.Dragging == true &&
                (_crossScreen ? _crossActive && _crossDragging : _pilotDrag.Active && _pilotDrag.Dragging));
    }

    private void PilotGravityThrowVoiceAdmitted()
    {
        var visual = _pilotGravityThrowVisual;
        var session = _pilotGravitySession;
        var token = _pilotPhysicsRunToken;
        if (visual == null || session == null || token == null || !PilotGravityThrowOwnsClip ||
            PilotPhysicsEffectiveMode != Pilot.Physics.PhysicsMode.Gravity || ReferenceEquals(visual, _pilotThrowVoiceVisual)) return;
        _pilotThrowVoiceVisual = visual;
        PilotActionVoiceStart(ref _pilotThrowVoiceId, PilotGravityThrowPath, visual.Playback,
            () => ReferenceEquals(visual, _pilotGravityThrowVisual) && ReferenceEquals(session, _pilotGravitySession) &&
                ReferenceEquals(token, _pilotPhysicsRunToken) && PilotGravityThrowOwnsClip &&
                PilotPhysicsEffectiveMode == Pilot.Physics.PhysicsMode.Gravity && !_pilotGravityPending);
    }

    private void PilotActionVoiceClearPending(long id = 0)
    {
        if (id != 0 && id != _pilotActionVoicePendingId) return;
        if (_gifPlayer != null && _pilotActionVoicePendingFrame != null)
            _gifPlayer.PilotFrameAdvanced -= _pilotActionVoicePendingFrame;
        _pilotActionVoicePendingFrame = null;
        _pilotActionVoicePendingId = 0;
    }

    private void PilotActionVoiceEnd(ref long id, bool completed)
    {
        if (id == 0) return;
        PilotActionVoiceClearPending(id);
        _pilotActionVoice?.End(id, completed);
        id = 0;
    }

    private void PilotActionVoiceStart(ref long ownerId, string path, long playback, Func<bool> owns)
    {
        // Caper and Angry1 share a click ID but must get distinct voice IDs.
        PilotActionVoiceEnd(ref ownerId, true);
        if (!_settings.Voice.Enabled) return;
        if (_gifPlayer == null || !ActionVoiceCatalog.TryGet(path, out _, out _)) return;
        PilotActionVoiceClearPending();
        long id = ownerId = ++_pilotActionVoiceSequence;
        var gif = _gifPlayer;
        bool Current() => _settings.Voice.Enabled && PilotClickLive && owns() && ReferenceEquals(gif, _gifPlayer) &&
            playback == gif.PlaybackGeneration && path == gif.ResourcePath;
        if (!Current()) return;
        if (!gif.PilotPresentationHeld && gif.PilotFrameTimerEnabled)
        {
            _pilotActionVoice?.TryStart(id, path);
            return;
        }
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            gif.PilotFrameAdvanced -= handler;
            if (!ReferenceEquals(handler, _pilotActionVoicePendingFrame)) return;
            _pilotActionVoicePendingFrame = null;
            _pilotActionVoicePendingId = 0;
            if (Current() && !gif.PilotPresentationHeld) _pilotActionVoice?.TryStart(id, path);
        };
        _pilotActionVoicePendingId = id;
        _pilotActionVoicePendingFrame = handler;
        gif.PilotFrameAdvanced += handler;
    }

    private void PilotClickVoiceStart(long actionId, string path, long playback) =>
        PilotActionVoiceStart(ref _pilotClickVoiceId, path, playback,
            () => _pilotClickAction && actionId == _pilotClickActionId);

    private void PilotActionVoiceSilence()
    {
        PilotActionVoiceClearPending();
        _pilotActionVoice?.Stop();
        _pilotClickVoiceId = _pilotRandomVoiceId = _pilotRocketVoiceId = 0;
        _pilotFallVoiceId = _pilotLandVoiceId = 0;
        _pilotMoveVoiceId = 0;
        _pilotDragVoiceId = _pilotThrowVoiceId = 0;
        _pilotAttachVoiceId = _pilotGravityLandVoiceId = _pilotPlanarThrowVoiceId = 0;
        _pilotRestVoiceId = _pilotRestExitVoiceId = _pilotStretchVoiceId = _pilotWakeVoiceId = 0;
        // Consumed attachment/landing/planar identities survive mute as well.
        // Gesture/throw latches survive mute just like the movement latch.
        // Keep the consumed movement revision: mute/re-enable must not rearm it.
    }

    private void PilotVoiceApplySettings()
    {
        if (_pilotClosing || _pilotStopped) return;
        // Keep media/event failures inside the new settings boundary, never in Dispatcher.
        // Disabling clears first-frame subscriptions before synchronously stopping audio.
        try
        {
            if (!_settings.Movement.Enabled) PilotActionVoiceEnd(ref _pilotMoveVoiceId, false);
            if (!_settings.Voice.Enabled) PilotStartupVoiceStop();
            else
            {
                PilotStartupVoiceInit();
                _pilotStartupVoice?.Player.SetVolume(_settings.Voice.ToLinearVolume());
            }
        }
        catch (Exception e)
        {
            try { _pilotLog?.Write("voice-settings-failed", new { error = e.ToString() }); } catch { }
        }
    }
}
