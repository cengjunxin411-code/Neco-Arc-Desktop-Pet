using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    // Single entry point for the future application configuration layer. Off by default.
    private bool EnableMultiClickDiagnostics => _settings.Diagnostics.MultiClickDiagnosticsEnabled;

    private StreamWriter? _clickDiagnosticWriter;
    private readonly List<(UIElement Owner, RoutedEvent Event, Delegate Handler)> _clickDiagnosticObservers = new();
    private readonly string _clickDiagnosticSession = Guid.NewGuid().ToString("N");
    private long _clickDiagnosticLine;
    private TimeSpan? _diagnosticDownAt;
    private TimeSpan? _diagnosticUpAt;
    private TimeSpan? _diagnosticValidUpAt;

    private void InitializeClickDiagnostics()
    {
        if (!EnableMultiClickDiagnostics) return;
        try
        {
            var path =
#if PMV2_PRODUCT
                Product.ProductDiagnostics.NewDiagnosticPath("click");
#else
                _settings.Diagnostics.MultiClickDiagnosticsPath ??
                Path.Combine(AppContext.BaseDirectory, "phase9-1-input-diagnostics.log");
#endif
            _clickDiagnosticWriter = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return; // Diagnostics must never prevent the pet from running.
        }
        _clickResolver.DiagnosticTrace = (kind, before, after, deadline, reason) =>
            TraceClick("Resolver." + kind, reason, new { before, after, previousDeadlineMs = deadline?.TotalMilliseconds,
                windowMs = kind == "Register" && after < 3
                    ? (after == 1 ? _clickResolver.FirstWindow : _clickResolver.SecondWindow).TotalMilliseconds : (double?)null });
        TraceClick("Session.Start", detail: new {
            processId = Environment.ProcessId, executable = Environment.ProcessPath,
            assembly = typeof(MainWindow).Assembly.Location,
            assemblyModifiedUtc = File.GetLastWriteTimeUtc(typeof(MainWindow).Assembly.Location),
            firstWindowMs = _settings.Interaction.FirstClickWindow.TotalMilliseconds,
            secondWindowMs = _settings.Interaction.SecondClickWindow.TotalMilliseconds, rotationAvailable = _rotationAvailable,
            logFormat = "JSON lines; wall time plus monotonic clock; append across sessions"
        });
        if (_clickDiagnosticWriter == null) return;
        ObserveClickEvent(this, Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) => TraceMouse("Preview.MouseDown", e)));
        ObserveClickEvent(this, Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) => TraceMouse("Preview.MouseUp", e)));
        ObserveClickEvent(this, Mouse.MouseDownEvent, new MouseButtonEventHandler((_, e) => TraceMouse("Raw.MouseDown", e)));
        ObserveClickEvent(this, Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) => TraceMouse("Raw.MouseUp", e)));
        ObserveClickEvent(this, Mouse.MouseMoveEvent, new MouseEventHandler((_, e) => TraceMouse("Raw.MouseMove", e)));
        ObserveClickEvent(PetSurface, Mouse.GotMouseCaptureEvent, new MouseEventHandler((_, e) => TraceMouse("Capture.Got", e)));
        ObserveClickEvent(PetSurface, Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, e) => TraceMouse("Capture.Lost", e)));
    }

    private void ObserveClickEvent(UIElement owner, RoutedEvent routedEvent, Delegate handler)
    {
        owner.AddHandler(routedEvent, handler, true);
        _clickDiagnosticObservers.Add((owner, routedEvent, handler));
    }

    private void TraceMouse(string eventType, MouseEventArgs e, string reason = "")
    {
        if (_clickDiagnosticWriter == null) return;
        TraceClick(eventType, reason, new {
            inputTimestamp = e.Timestamp, handled = e.Handled,
            routedEvent = e.RoutedEvent.Name, source = e.Source?.GetType().Name,
            originalSource = e.OriginalSource?.GetType().Name,
            leftButton = e.LeftButton.ToString(), changedButton = (e as MouseButtonEventArgs)?.ChangedButton.ToString(),
            clickCount = (e as MouseButtonEventArgs)?.ClickCount
        });
    }

    private void TraceClick(string eventType, string reason = "", object? detail = null)
    {
        if (_clickDiagnosticWriter == null) return;
        try
        {
            var now = _clickClock();
            var point = GetCursorScreenDip();
            _clickDiagnosticWriter.WriteLine(JsonSerializer.Serialize(new {
                timestamp = DateTimeOffset.Now, session = _clickDiagnosticSession, line = ++_clickDiagnosticLine,
                clockMs = now.TotalMilliseconds, eventType, reason, detail,
                mouseX = point.X, mouseY = point.Y, mouseDown = _leftPressed, dragging = _isDragging,
                count = _clickResolver.Count, deadlineMs = _clickResolver.Deadline?.TotalMilliseconds,
                remainingMs = (_clickResolver.Deadline - now)?.TotalMilliseconds,
                gif = _gifPlayer?.ResourcePath, suppression = _suppressClickOnRelease,
                single = _clickReactionActive, rocket = _rocketSession != null, rotation = _rotationSession != null,
                attachment = _attachmentMode.ToString(), landing = _landingActive, menu = _contextMenuOpen,
                ambient = _ambientGifActive, movement = _autonomousMovement != null,
                normalIdle = IsNormalIdle,
                visible = IsVisible, captured = PetSurface.IsMouseCaptured,
                animationTimer = _animationTimer?.IsEnabled ?? false,
                resolverTimer = _clickResolverTimer.IsEnabled, bubbleGeneration = _clickBubbleGeneration
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StopClickDiagnostics();
        }
    }

    private void TraceGestureDown()
    {
        if (_clickDiagnosticWriter != null) _diagnosticDownAt = _clickClock();
    }

    private void ResetIdleWithDiagnostics([CallerMemberName] string reason = "")
    {
        _randomIdleScheduler.Reset();
        TraceClick("Idle.Reset", reason);
    }

    private void TraceGestureUp()
    {
        if (_clickDiagnosticWriter == null) return;
        var now = _clickClock();
        _diagnosticUpAt = now;
        var down = _leftPressed ? _diagnosticDownAt : null;
        TraceClick("Gesture.Up", detail: new { acceptedDown = _leftPressed, downMs = down?.TotalMilliseconds,
            upMs = now.TotalMilliseconds, durationMs = (now - down)?.TotalMilliseconds });
    }

    private void TraceValidGesture()
    {
        if (_clickDiagnosticWriter == null) return;
        var now = _diagnosticUpAt ?? _clickClock();
        TraceClick("ValidGesture", detail: new { downMs = _diagnosticDownAt?.TotalMilliseconds,
            upMs = now.TotalMilliseconds, durationMs = (now - _diagnosticDownAt)?.TotalMilliseconds,
            upIntervalMs = (now - _diagnosticValidUpAt)?.TotalMilliseconds });
        _diagnosticValidUpAt = now;
    }

    private void StopClickDiagnostics()
    {
        var writer = _clickDiagnosticWriter;
        _clickDiagnosticWriter = null;
        _clickResolver.DiagnosticTrace = null;
        foreach (var observer in _clickDiagnosticObservers)
            observer.Owner.RemoveHandler(observer.Event, observer.Handler);
        _clickDiagnosticObservers.Clear();
        try { writer?.Dispose(); } catch (IOException) { }
    }
}
