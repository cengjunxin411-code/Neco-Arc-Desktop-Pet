using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

internal static class PilotNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left,Top,Right,Bottom; public PhysicalRect Physical => new(Left,Top,Right,Bottom); }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X,Y; }
    internal static bool V2(nint context) => context!=0 && AreDpiAwarenessContextsEqual(context,-4);
    internal static bool ProcessV2 => V2(GetDpiAwarenessContextForProcess(Process.GetCurrentProcess().Handle));
    internal static bool ThreadV2 => V2(GetThreadDpiAwarenessContext());
    internal static bool Place(nint hwnd, PhysicalRect rect) => rect.IsValid &&
        rect.Left>=int.MinValue && rect.Left<=int.MaxValue && rect.Top>=int.MinValue && rect.Top<=int.MaxValue &&
        rect.Width<=int.MaxValue && rect.Height<=int.MaxValue &&
        SetWindowPos(hwnd,0,(int)rect.Left,(int)rect.Top,(int)rect.Width,(int)rect.Height,0x14 /* NOZORDER|NOACTIVATE */);
    [DllImport("user32.dll")] internal static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] internal static extern nint GetDpiAwarenessContextForProcess(nint process);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool AreDpiAwarenessContextsEqual(nint a,nint b);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd,nint insert,int x,int y,int width,int height,uint flags);
}
