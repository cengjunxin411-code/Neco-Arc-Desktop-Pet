using System;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

// Limited 4A slice, not G recovery. The window adapter is the sole native writer.
// All absolute coordinates are physical px; DPI converts only the local threshold.
internal sealed class SingleMonitorDrag
{
    private long generation, gesture, source, hwnd;
    private PhysicalPoint down;
    private PhysicalVector grab;
    private MonitorSnapshot monitor;
    private Coordinates.DpiScale scale;
    private double threshold;
    private PhysicalRect? requested;
    public bool Active { get; private set; }
    public bool Dragging { get; private set; }
    public PhysicalRect Trusted { get; private set; }
    public bool Begin(long generation, long gesture, long source, long hwnd, PhysicalPoint cursor,
        PhysicalRect window, MonitorSnapshot monitor, Coordinates.DpiScale scale, double thresholdDip, bool captured)
    {
        Cancel();
        if (!captured || generation < 1 || gesture < 1 || source == 0 || hwnd == 0 ||
            !cursor.IsFinite || !window.IsValid || !monitor.IsValid || !scale.IsValid ||
            !double.IsFinite(thresholdDip) || thresholdDip < 0 || !window.ContainsClosed(cursor) ||
            !Fits(window, monitor.WorkArea)) return false;
        this.generation=generation; this.gesture=gesture; this.source=source; this.hwnd=hwnd;
        this.monitor=monitor; this.scale=scale; threshold=thresholdDip; down=cursor;
        grab=new(cursor.X-window.Left,cursor.Y-window.Top); Trusted=window; Active=true;
        return true;
    }
    public PhysicalRect? Target(long generation, long gesture, long source, long hwnd, PhysicalPoint cursor)
    {
        if (!Owns(generation,gesture,source,hwnd)) return null;
        if (!cursor.IsFinite || !monitor.Bounds.ContainsClosed(cursor) ||
            !PhysicalVector.TryBetween(down,cursor,out var delta) || !scale.TryToLocalDip(delta,out var local))
        { Cancel(); return null; }
        if (!Dragging && Math.Abs(local.X)+Math.Abs(local.Y)<=threshold) return null;
        if (!PhysicalRect.TryFromOriginAndSize(new(cursor.X-grab.X,cursor.Y-grab.Y),Trusted.Width,Trusted.Height,out var target) ||
            !Fits(target,monitor.WorkArea)) { Cancel(); return null; }
        Dragging=true; requested=target; return target;
    }
    public bool Accept(long generation, long gesture, long source, long hwnd, PhysicalRect placed)
    {
        if (!Owns(generation,gesture,source,hwnd) || requested!=placed) return false;
        Trusted=placed; requested=null; return true;
    }
    // The adapter already proved the cursor-only Hold threshold. Confirm only
    // this fully admitted owner, without a synthetic Target/sample/placement.
    public bool ConfirmDrag(long generation,long gesture,long source,long hwnd)
    {
        if(!Owns(generation,gesture,source,hwnd))return false;
        Dragging=true;return true;
    }
    // A drag visual admission resizes the footprint while captured. Re-anchor the
    // grab point to the accepted footprint so the cursor stays on the same screen pixel.
    public void Rebase(PhysicalPoint cursor, PhysicalRect placed)
    {
        if (!Active || !cursor.IsFinite || !placed.IsValid) return;
        grab=new(cursor.X-placed.Left,cursor.Y-placed.Top);
        Trusted=placed; requested=null;
    }
    private bool Owns(long generation,long gesture,long source,long hwnd) => Active &&
        this.generation==generation && this.gesture==gesture && this.source==source && this.hwnd==hwnd;
    internal static bool Fits(PhysicalRect rect, PhysicalRect work) => rect.IsValid && work.IsValid &&
        rect.Left>=work.Left && rect.Top>=work.Top && rect.Right<=work.Right && rect.Bottom<=work.Bottom;
    public void Cancel() { Active=Dragging=false; requested=null; grab=default; }
}
