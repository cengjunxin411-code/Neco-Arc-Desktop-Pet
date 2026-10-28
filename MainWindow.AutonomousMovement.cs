using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private MovementDefinition[] _availableMovements = Array.Empty<MovementDefinition>();
    private readonly Random _movementRandom = new();
    private readonly Stopwatch _movementClock = new();
    private bool _movementAvailable;
    private AutonomousMovementSession? _autonomousMovement;

    private void BeginAutonomousMovement()
    {
        if (!_settings.Movement.Enabled || !_movementAvailable || !IsVisible || !IsNormalIdle) return;
        _randomIdleScheduler.Reset();
        while (MovementDefinitions.ChooseConfigured(_availableMovements, _movementRandom, _settings.Movement) is { } definition)
        {
            var session = AutonomousMovementSession.TryCreate(Left, Width, GetMovementWorkArea(), _movementRandom, definition);
            if (session == null) break;
            // All candidates were decoded at startup; switch before allowing position updates.
            if (_gifPlayer?.TrySwitchTo(definition.ResourcePath) != true)
            {
                _availableMovements = Array.FindAll(_availableMovements, item => item.Type != definition.Type);
                _movementAvailable = _availableMovements.Length > 0;
                continue;
            }
            _autonomousMovement = session;
            _movementClock.Restart();
            ApplyPetLayout();
            _gifPlayer.Resume();
            return;
        }
        _randomIdleScheduler.Update();
    }

    private void AdvanceAutonomousMovement(double elapsedSeconds)
    {
        var session = _autonomousMovement;
        if (session == null) return;
        if (!IsVisible || _isDragging || _leftPressed || _contextMenuOpen || _clickReactionActive ||
            _landingActive || _ambientGifActive || _attachmentMode != AttachmentMode.None ||
            (_animationTimer?.IsEnabled ?? false) || _gifPlayer?.ResourcePath != session.Definition.ResourcePath)
        {
            StopAutonomousMovement();
            return;
        }
        var area = GetMovementWorkArea();
        if (area.IsEmpty)
        {
            StopAutonomousMovement();
            return;
        }
        session.Advance(elapsedSeconds, area.Left, area.Right - Width);
        // Keep the existing horizontal anchor/layout pipeline; never touch the Y anchor.
        _anchorCenterX += session.X - Left;
        ApplyPetLayout();
        if (session.IsComplete) StopAutonomousMovement();
    }

    private void StopAutonomousMovement()
    {
        if (_autonomousMovement == null) return;
        _autonomousMovement = null;
        _movementClock.Reset();
        RestoreIdleGif();
    }

    private Rect GetMovementWorkArea()
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2 /* nearest */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return Rect.Empty;
        // Win32 RECT is device pixels. Reuse the existing window's DIP conversion.
        return new Rect(DeviceToDip(new Point(info.Work.Left, info.Work.Top)),
            DeviceToDip(new Point(info.Work.Right, info.Work.Bottom)));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
