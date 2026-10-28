using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace NecoArcDesktopPet.Coordinates.Windows;

internal sealed class WindowsDisplayChangeSource : IDisplayChangeSource
{
    private readonly int _ownerThread;
    private readonly WindowProcedure _procedure;
    private readonly string _className = "NecoArcTopology_" + Guid.NewGuid().ToString("N");
    private readonly IntPtr _instance;
    private GCHandle _callbackRoot;
    private IntPtr _handle;
    private bool _classRegistered;
    private bool _disposed;

    // Caller must own and pump this STA thread. No WPF initialization, Application or global hook.
    // Hidden TOP-LEVEL window: message-only HWNDs do not receive these broadcasts.
    public WindowsDisplayChangeSource()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Display change source requires a caller-owned STA message loop.");
        _ownerThread = Environment.CurrentManagedThreadId;
        _procedure = OnMessage; // Root the native callback for the complete HWND lifetime.
        _instance = GetModuleHandleW(null);
        if (_instance == IntPtr.Zero) throw NativeException("GetModuleHandleW");
        var definition = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = _instance, ClassName = _className,
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure)
        };
        if (RegisterClassExW(ref definition) == 0) throw NativeException("RegisterClassExW");
        _classRegistered = true;
        // An HWND cannot root a managed delegate. Keep it alive even if its caller drops the source reference.
        _callbackRoot = GCHandle.Alloc(_procedure);
        _handle = CreateWindowExW(0x08000080 /* NOACTIVATE | TOOLWINDOW */, _className, "", 0 /* hidden top-level */,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
        {
            var error = NativeException("CreateWindowExW");
            UnregisterClassW(_className, _instance);
            _classRegistered = false;
            _callbackRoot.Free();
            throw error;
        }
    }
    public event EventHandler? Invalidated;
    public TopologyDiagnostic? LastSubscriberFailure { get; private set; }
    internal IntPtr Handle => _disposed ? IntPtr.Zero : _handle;

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // WM_SETTINGCHANGE includes SPI_SETWORKAREA, plus broad best-effort invalidation.
        // Never infer geometry from 16-bit WM_DISPLAYCHANGE payload dimensions.
        if (!_disposed && (message == 0x007E || message == 0x001A) && Invalidated is { } handlers)
        {
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                if (_disposed) break;
                try { handler(this, EventArgs.Empty); }
                catch (Exception e)
                {
                    LastSubscriberFailure = TopologyDiagnostic.FromException("WindowsInvalidatedSubscriber", e);
                    System.Diagnostics.Trace.WriteLine(LastSubscriberFailure);
                }
            }
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Dispose display source on its owning message-loop thread.");
        if (_disposed && _handle == IntPtr.Zero && !_classRegistered) return;
        _disposed = true;
        Invalidated = null;
        if (_handle != IntPtr.Zero)
        {
            if (!DestroyWindow(_handle)) throw NativeException("DestroyWindow");
            _handle = IntPtr.Zero;
        }
        if (_classRegistered)
        {
            if (!UnregisterClassW(_className, _instance)) throw NativeException("UnregisterClassW");
            _classRegistered = false;
        }
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        GC.KeepAlive(_procedure);
    }

    private static Win32Exception NativeException(string stage)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{stage} failed; Win32={error}.");
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass definition);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClassW(string className, IntPtr instance);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
