using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NecoArcDesktopPet.Coordinates.Windows;

internal readonly record struct NativeMonitorRect(int Left, int Top, int Right, int Bottom)
{
    // Continuous edges: never subtract one from Win32's exclusive pixel-index edges.
    public PhysicalRect ToPhysicalEdges() => new(Left, Top, Right, Bottom);
}
internal sealed record NativeMonitorSample(string Device, NativeMonitorRect Bounds, NativeMonitorRect WorkArea,
    uint ModeFields, int ModeX, int ModeY, uint ModeWidth, uint ModeHeight, uint Orientation);
internal sealed record NativeDpiEvidence(int ThreadAwareness, string Context, int ProcessAwareness, int ProcessHResult);
internal sealed record NativeMonitorCapture(TopologyFailure Failure, TopologyDiagnostic Diagnostic,
    NativeDpiEvidence Dpi, IReadOnlyList<NativeMonitorSample> Samples);
internal interface IWindowsMonitorApi { NativeMonitorCapture Capture(); }

// Does not change process/thread awareness. Only verified PM-aware calls may publish.
internal sealed class WindowsMonitorProvider : IMonitorProvider
{
    internal const int MaximumCaptures = 4;
    internal const uint RequiredModeFields = 0x20 /* DM_POSITION */ | 0x80000 /* DM_PELSWIDTH */ | 0x100000 /* DM_PELSHEIGHT */;
    private readonly IWindowsMonitorApi _api;
    public WindowsMonitorProvider() : this(new WindowsMonitorApi()) { }
    internal WindowsMonitorProvider(IWindowsMonitorApi api) { _api = api ?? throw new ArgumentNullException(nameof(api)); }

    public MonitorQueryResult Query()
    {
        MonitorQueryResult? previous = null;
        try
        {
            for (var attempt = 1; attempt <= MaximumCaptures; attempt++)
            {
                var capture = _api.Capture();
                var current = Validate(capture);
                if (!current.Success) return current;
                if (previous != null && MonitorQueryResult.SameContent(previous.Monitors, current.Monitors))
                    return MonitorQueryResult.Create(current.Monitors, MonitorCoordinateSpace.PhysicalPixels,
                        current.Diagnostic with { Message = current.Diagnostic.Message + $"; stable consecutive captures={attempt - 1}/{attempt}" });
                previous = current;
            }
            return MonitorQueryResult.Failed(TopologyFailure.UnstableTopology,
                new("Continuity", $"No two consecutive equal captures within {MaximumCaptures} calls.", DpiContext: previous?.Diagnostic.DpiContext));
        }
        catch (Exception e)
        {
            return MonitorQueryResult.Failed(TopologyFailure.ProviderException, TopologyDiagnostic.FromException("Windows.Query", e));
        }
    }

    internal static MonitorQueryResult Validate(NativeMonitorCapture capture)
    {
        if (capture.Failure != TopologyFailure.None) return MonitorQueryResult.Failed(capture.Failure, capture.Diagnostic);
        var diagnostic = capture.Diagnostic with { DpiContext = capture.Dpi.ToString(),
            Message = capture.Diagnostic.Message + "; samples: " + string.Join("; ", capture.Samples) };
        if (capture.Dpi.ThreadAwareness != 2)
            return MonitorQueryResult.Failed(TopologyFailure.UnverifiedCoordinateSpace,
                diagnostic with { Stage = "DpiVerification", Message = "Requires per-monitor thread awareness (2); raw samples are diagnostic only. " + diagnostic.Message });
        var monitors = new List<MonitorSnapshot>();
        foreach (var sample in capture.Samples)
        {
            if ((sample.ModeFields & RequiredModeFields) != RequiredModeFields || sample.ModeWidth == 0 || sample.ModeHeight == 0)
                return MonitorQueryResult.Failed(TopologyFailure.UnverifiedCoordinateSpace,
                    diagnostic with { Stage = "ModeVerification", Message = $"{sample.Device}: current physical mode lacks valid position/size fields. " + diagnostic.Message });
            var bounds = sample.Bounds.ToPhysicalEdges();
            // uint dimensions and signed int origins are exactly representable in double.
            // Current rotated mode already describes desktop width/height; no guessed swap.
            var modeBounds = new PhysicalRect(sample.ModeX, sample.ModeY,
                (double)sample.ModeX + sample.ModeWidth, (double)sample.ModeY + sample.ModeHeight);
            if (bounds != modeBounds)
                return MonitorQueryResult.Failed(TopologyFailure.UnverifiedCoordinateSpace,
                    diagnostic with { Stage = "ModeVerification", Message = $"{sample.Device}: monitor bounds {bounds} differ from physical mode {modeBounds}. " + diagnostic.Message });
            monitors.Add(new(sample.Device?.ToUpperInvariant() ?? "", bounds, sample.WorkArea.ToPhysicalEdges()));
        }
        return MonitorQueryResult.Create(monitors, MonitorCoordinateSpace.PhysicalPixels,
            diagnostic with { Stage = "VerifiedPhysicalPixels", Message = "Per-monitor thread; every Bounds matches EnumDisplaySettingsExW physical current mode; WorkArea in same monitor virtual-screen space. " + diagnostic.Message });
    }
}

internal sealed class WindowsMonitorApi : IWindowsMonitorApi
{
    public NativeMonitorCapture Capture()
    {
        var samples = new List<NativeMonitorSample>();
        var dpi = new NativeDpiEvidence(-1, "unavailable", -1, 0);
        NativeMonitorCapture Failed(TopologyFailure failure, TopologyDiagnostic diagnostic) =>
            new(failure, diagnostic with { DpiContext = dpi.ToString() }, dpi, samples.AsReadOnly());
        if (!OperatingSystem.IsWindows()) return Failed(TopologyFailure.UnsupportedPlatform, new("Platform", "Windows only."));
        string stage = "DpiAwareness";
        try
        {
            dpi = ReadDpi();
            TopologyDiagnostic? callbackFailure = null;
            MonitorEnum callback = (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
            {
                try
                {
                    stage = "GetMonitorInfoW";
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
                    Marshal.SetLastPInvokeError(0);
                    if (!GetMonitorInfoW(monitor, ref info)) { callbackFailure = Error(stage); return false; }
                    stage = "EnumDisplaySettingsExW";
                    var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>(), DeviceName = "", FormName = "" };
                    Marshal.SetLastPInvokeError(0);
                    if (!EnumDisplaySettingsExW(info.Device, uint.MaxValue /* ENUM_CURRENT_SETTINGS */, ref mode, 0))
                    { callbackFailure = Error(stage); return false; }
                    samples.Add(new(info.Device, info.Bounds.ToValue(), info.Work.ToValue(), mode.Fields,
                        mode.PositionX, mode.PositionY, mode.PelsWidth, mode.PelsHeight, mode.DisplayOrientation));
                    return true;
                }
                catch (Exception e) { callbackFailure = TopologyDiagnostic.FromException(stage, e); return false; }
            };
            stage = "EnumDisplayMonitors";
            Marshal.SetLastPInvokeError(0);
            bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            var enumerationError = Marshal.GetLastPInvokeError();
            GC.KeepAlive(callback);
            if (callbackFailure != null) return Failed(TopologyFailure.NativeApiError, callbackFailure);
            if (!enumerated) return Failed(TopologyFailure.NativeApiError,
                new("EnumDisplayMonitors", "API returned FALSE. Last-error captured; API does not guarantee an extended error.", enumerationError));
            stage = "DpiAwarenessAfter";
            var after = ReadDpi();
            if (after.ThreadAwareness != dpi.ThreadAwareness)
                return Failed(TopologyFailure.UnverifiedCoordinateSpace, new(stage, "Thread awareness changed during enumeration."));
            return new(TopologyFailure.None, new("NativeCapture", "EnumDisplayMonitors(NULL,NULL) + GetMonitorInfoW + EnumDisplaySettingsExW(current,flags=0)"),
                dpi, samples.AsReadOnly());
        }
        catch (EntryPointNotFoundException e) { return Failed(TopologyFailure.UnverifiedCoordinateSpace, TopologyDiagnostic.FromException(stage, e)); }
        catch (DllNotFoundException e) { return Failed(TopologyFailure.UnsupportedPlatform, TopologyDiagnostic.FromException(stage, e)); }
        catch (Exception e) { return Failed(TopologyFailure.NativeApiError, TopologyDiagnostic.FromException(stage, e)); }
    }

    private static NativeDpiEvidence ReadDpi()
    {
        var context = GetThreadDpiAwarenessContext();
        var awareness = GetAwarenessFromDpiAwarenessContext(context);
        var hr = GetProcessDpiAwareness(IntPtr.Zero, out var process);
        // Token logged only; opaque context tokens are never interpreted as device IDs.
        return new(awareness, $"0x{context.ToInt64():X}", process, hr);
    }
    private static TopologyDiagnostic Error(string stage) => new(stage,
        "API returned FALSE. Last-error captured; API does not guarantee an extended error.", Marshal.GetLastPInvokeError());

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly NativeMonitorRect ToValue() => new(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    // DEVMODEW's 16-byte union uses the display arm; full public layout is 220 bytes.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsExW(string device, uint mode, ref DevMode info, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("shcore.dll", ExactSpelling = true)] private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);
}
