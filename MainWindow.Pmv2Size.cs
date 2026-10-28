using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    // Null preserves the optional field in an old Pilot file until the user changes size.
    private double? _pilotHeightForSave;
    private DispatcherTimer? _pilotSizeVerifyTimer, _pilotSizeSaveTimer, _pilotWheelTimer;
    private double? _pilotWheelPendingHeight;
    private long _pilotWheelGeneration, _pilotWheelGesture, _pilotWheelSourceToken;
    private nint _pilotWheelHwnd;
    private int _pilotWheelDelta;
    private double? _pilotSizeQueuedHeight;
    private bool _pilotSizeQueuedImmediate;
    private int _pilotSizeVerifyChecks;
    private bool _pilotSizeSaveDue;
    private MenuItem? _pilotSizeNotice;

    private void PilotSizeMessage(string? message)
    {
        if (_pilotSizeNotice == null) return;
        _pilotSizeNotice.Header = message ?? "";
        _pilotSizeNotice.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static double PilotSizeExtent(double height, double aspect, double effective, bool horizontal)
    {
        var padding = Math.Max(48, height * 0.22);
        return (height * (horizontal ? aspect : 1) + 2 * padding) * effective;
    }

    private void PilotRestoreSizeForViewport()
    {
        if (_pilotHeightForSave is not {} intended) return;
        var width = Math.Round(PilotSizeExtent(intended, _imageAspect, 1, true) * _pilotScale.X);
        var height = Math.Round(PilotSizeExtent(intended, _imageAspect, 1, false) * _pilotScale.Y);
        var work = _pilotMonitor.WorkArea;
        if (width > 0 && height > 0 && width <= work.Width && height <= work.Height) return;
        _petHeight = 170;
        _pilotLog?.Write("size-restore-refused", new { intendedHeightDip = intended,
            appliedHeightDip = _petHeight, workArea = work, reason = "saved footprint does not fit startup WorkArea" }, "BLOCKED");
    }

    private bool PilotSizeAdmission(double target)
    {
        if (!double.IsFinite(target) || !_pilotReady || _pilotStopped || _pilotClosing ||
            _pilotTopologyHold || !PilotFresh || _pilotPlacementDepth != 0 || _pilotHookDepth != 0 ||
            _crossLayoutDepth != 0 || PilotSizeInputBlocked || PilotVisualBusy ||
            !PilotMapping(out var scale) || scale != _pilotScale ||
            !PilotNative.GetWindowRect(PilotHwnd, out var actual) || actual.Physical != _pilotRect) return false;
        if (_crossScreen && (!_crossInitialized || !_crossMapped || _crossRecovery?.Active == true ||
            _crossEffectiveScale != 1 || !CrossCheckpoint().Ready)) return false;
        var width = Math.Round(PilotSizeExtent(target, _imageAspect, 1, true) * scale.X);
        var height = Math.Round(PilotSizeExtent(target, _imageAspect, 1, false) * scale.Y);
        if (width <= 0 || height <= 0) return false;
        if (_crossScreen)
            return CrossScreenGeometry.Fit(new PhysicalRect(_crossOrigin.X, _crossOrigin.Y,
                _crossOrigin.X + width, _crossOrigin.Y + height),
                CrossScreenGeometry.Reachable(_pilotRect, CrossWork)) != null;
        return width <= _pilotMonitor.WorkArea.Width && height <= _pilotMonitor.WorkArea.Height;
    }

    private bool PilotSizeInputBlocked => PilotPhysicsBusy || _pilotStopped || _pilotClosing || _pilotTopologyHold ||
        _pilotDrag.Active || _crossActive || PetSurface.IsMouseCaptured ||
        _pilotClicks?.Held == true || _clickResolver.IsPending || _pilotClickAction ||
        _pilotRocketTimer != null || _pilotRocketSession != null || _pilotRocketAttachment != null ||
        _pilotAttachmentTarget != null || _pilotAttachmentTimer != null ||
        PilotMotionBusy || _pilotMoveActive || _pilotRandomActive;

    private void PilotRejectWheelIntent(int delta, string reason)
    {
        PilotCancelWheelPending();
        _pilotLog?.Write("size-rejected", new { wheelDelta = delta, appliedHeightDip = _petHeight,
            savedIntentDip = _pilotHeightForSave, reason }, "BLOCKED");
        SettingsPersistenceStatus = "尺寸未调整：当前拖动、动作或屏幕几何不允许缩放。";
        PilotSizeMessage(SettingsPersistenceStatus);
        _workbenchWindow?.RefreshPersistenceStatus();
    }

    private void PilotWheelStep(int delta)
    {
        if (PilotSizeInputBlocked) { PilotRejectWheelIntent(delta, "wheel input owner busy"); return; }
        var current = _pilotWheelPendingHeight ?? _pilotSizeQueuedHeight ?? _petHeight;
        var target = Math.Clamp(current * (delta > 0 ? 1.08 : 0.92), MinPetHeight, MaxPetHeight);
        // A newer wheel input supersedes a queued, not-yet-started geometry request.
        _pilotSizeQueuedHeight = null;
        _pilotSizeQueuedImmediate = false;
        _pilotWheelPendingHeight = target;
        _pilotWheelGeneration = _pilotGeneration;
        _pilotWheelGesture = _pilotGesture;
        _pilotWheelSourceToken = _pilotSourceToken;
        _pilotWheelHwnd = PilotHwnd;
        _pilotWheelDelta = delta;
        _pilotLog?.Write("size-wheel-intent", new { delta, baseHeightDip = current,
            targetHeightDip = target, appliedHeightDip = _petHeight,
            verifyActive = _pilotSizeVerifyTimer != null }, "USER INPUT; NOT YET APPROVED");
        if (_pilotWheelTimer == null)
        {
            _pilotWheelTimer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(50) };
            _pilotWheelTimer.Tick += (_, _) =>
            {
                _pilotWheelTimer?.Stop();
                if (_pilotWheelPendingHeight is not {} latest) return;
                _pilotWheelPendingHeight = null;
                if (PilotSizeInputBlocked || _pilotWheelGeneration != _pilotGeneration ||
                    _pilotWheelGesture != _pilotGesture || _pilotWheelSourceToken != _pilotSourceToken ||
                    _pilotWheelHwnd != PilotHwnd)
                { PilotRejectWheelIntent(_pilotWheelDelta, "wheel owner changed before admission"); return; }
                PilotSetPetHeight(latest, false);
            };
        }
        _pilotWheelTimer.Stop();
        _pilotWheelTimer.Start();
    }

    private void PilotCancelWheelPending()
    {
        _pilotWheelTimer?.Stop();
        _pilotWheelPendingHeight = null;
        if (!_pilotSizeQueuedImmediate) _pilotSizeQueuedHeight = null;
    }

    private void PilotSetPetHeight(double requested, bool immediateSave)
    {
        if(PilotPhysicsBusy){PilotRejectWheelIntent(_pilotWheelDelta,"physics owner busy before size admission");return;}
        if (immediateSave)
        {
            PilotCancelWheelPending();
        }
        var target = Math.Clamp(requested, MinPetHeight, MaxPetHeight);
        if (!double.IsFinite(target)) return;
        if (_pilotSizeVerifyTimer != null)
        {
            _pilotSizeQueuedHeight = target;
            _pilotSizeQueuedImmediate = immediateSave;
            return;
        }
        if (target == _petHeight)
        {
            // A saved intent may have fallen back to this legal viewport height.
            // An explicit preset selection replaces that intent after admission.
            if (immediateSave && _pilotHeightForSave != target)
            {
                if (!PilotSizeAdmission(target))
                {
                    PilotSizeMessage("尺寸未调整：当前动作或屏幕几何不允许缩放。");
                    return;
                }
                _pilotHeightForSave = target;
                _pilotSizeSaveDue = true;
                PilotFlushSizeSave();
            }
            return;
        }
        if (!PilotSizeAdmission(target))
        {
            _pilotLog?.Write("size-rejected", new { requestedHeightDip = target,
                appliedHeightDip = _petHeight, rect = _pilotRect, reason = "Pilot owner or geometry admission unavailable" }, "BLOCKED");
            SettingsPersistenceStatus = "尺寸未调整：当前拖动、动作或屏幕几何不允许缩放。";
            PilotSizeMessage(SettingsPersistenceStatus);
            _workbenchWindow?.RefreshPersistenceStatus();
            return;
        }
        _pilotLog?.Write("size-request-start", new { requestedHeightDip = target,
            appliedHeightDip = _petHeight, savedIntentDip = _pilotHeightForSave }, "GUARDED GEOMETRY PENDING");
        SetPetHeightCore(target);
        if (_pilotStopped || _pilotClosing) return;
        _pilotSizeVerifyChecks = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(75) };
        _pilotSizeVerifyTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(timer, _pilotSizeVerifyTimer)) return;
            if (_pilotStopped || _pilotClosing) { PilotStopSizeVerify(); return; }
            _pilotSizeVerifyChecks++;
            var accepted = PilotMapping(out var scale) && scale == _pilotScale &&
                PilotNative.GetWindowRect(PilotHwnd, out var rect) && rect.Physical == _pilotRect &&
                (!_crossScreen || _crossMapped && _crossEffectiveScale == 1 &&
                    _crossRecovery?.Active != true && CrossCheckpoint().Ready) &&
                Math.Abs(rect.Right - rect.Left - Math.Round(PetSurface.Width * scale.X)) <= 2 &&
                Math.Abs(rect.Bottom - rect.Top - Math.Round(PetSurface.Height * scale.Y)) <= 2;
            if (accepted)
            {
                PilotStopSizeVerify();
                _pilotHeightForSave = _petHeight;
                PilotSizeMessage(null);
                _pilotLog?.Write("size-applied", new { appliedHeightDip = _petHeight, rect = _pilotRect }, "ACCEPTED");
                if (immediateSave) { _pilotSizeSaveDue = true; PilotFlushSizeSave(); }
                else PilotScheduleSizeSave();
                if (_pilotSizeQueuedHeight is {} queued)
                {
                    var queuedImmediate = _pilotSizeQueuedImmediate;
                    _pilotSizeQueuedHeight = null;
                    PilotSetPetHeight(queued, queuedImmediate);
                }
            }
            else if (_pilotSizeVerifyChecks >= 10)
            {
                PilotStopSizeVerify();
                _pilotSizeQueuedHeight = null;
                _pilotLog?.Write("size-unverified", new { requestedHeightDip = target,
                    appliedHeightDip = _petHeight, savedIntentDip = _pilotHeightForSave }, "NOT SAVED");
                SettingsPersistenceStatus = "尺寸几何未通过验收；此次尺寸未保存。";
                PilotSizeMessage(SettingsPersistenceStatus);
                _workbenchWindow?.RefreshPersistenceStatus();
            }
        };
        timer.Start();
    }

    private void PilotScheduleSizeSave()
    {
        _pilotSizeSaveDue = true;
        if (_pilotSizeSaveTimer == null)
        {
            _pilotSizeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(350) };
            _pilotSizeSaveTimer.Tick += (_, _) => PilotFlushSizeSaveWhenSettled();
        }
        _pilotSizeSaveTimer.Stop();
        _pilotSizeSaveTimer.Start();
    }

    private void PilotFlushSizeSaveWhenSettled()
    {
        if (_pilotWheelPendingHeight != null || _pilotSizeQueuedHeight != null || _pilotSizeVerifyTimer != null)
        {
            _pilotSizeSaveTimer?.Stop();
            _pilotSizeSaveTimer?.Start();
            return;
        }
        PilotFlushSizeSave();
    }

    private void PilotFlushSizeSave()
    {
        _pilotSizeSaveTimer?.Stop();
        if (!_pilotSizeSaveDue) return;
        _pilotSizeSaveDue = false;
        var result = SaveSettingsSnapshot();
        PilotSizeMessage(result.Success ? null : "尺寸已生效，但保存失败；打开工作台可重试。");
        _pilotLog?.Write("size-save", new { appliedHeightDip = _petHeight,
            savedIntentDip = _pilotHeightForSave, result.Success, result.Failure },
            result.Success ? "SAVED" : "UNSAVED");
    }

    private void PilotStopSizeVerify()
    {
        _pilotSizeVerifyTimer?.Stop();
        _pilotSizeVerifyTimer = null;
    }

    private void PilotStopSizeTimers()
    {
        PilotStopSizeVerify();
        PilotCancelWheelPending();
        _pilotWheelTimer = null;
        _pilotSizeSaveTimer?.Stop();
        _pilotSizeSaveTimer = null;
    }
}
