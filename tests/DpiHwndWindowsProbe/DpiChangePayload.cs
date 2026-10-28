using System.Runtime.InteropServices;
using NecoArcDesktopPet.Coordinates;

namespace DpiHwndProbe;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct SuggestedRect(int left, int top, int right, int bottom)
{
    public readonly int Left = left, Top = top, Right = right, Bottom = bottom;
}
internal sealed record DpiChangePayload(int DpiX, int DpiY, PhysicalRect Suggested)
{
    // Copies transient WM_DPICHANGED lParam before the hook returns. Caller supplies only its own OS message pointer.
    internal static bool TryRead(nuint wp, nint lp, out DpiChangePayload? payload)
    {
        payload = null; if (lp == 0) return false;
        var x = (int)(wp & 0xffff); var y = (int)((wp >> 16) & 0xffff);
        var raw = Marshal.PtrToStructure<SuggestedRect>(lp);
        var rect = new PhysicalRect(raw.Left, raw.Top, raw.Right, raw.Bottom);
        if (x == 0 || y == 0 || !rect.IsValid) return false;
        payload = new(x,y,rect); return true;
    }
}
