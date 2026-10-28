using System;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet.Pilot;

// Input admission/cancellation only. Aggregation stays in the existing resolver.
internal sealed class PilotClickOwner(MultiClickResolver resolver)
{
    private long generation, source, hwnd;
    public long Gesture { get; private set; }
    public long Revision { get; private set; }
    public bool Held { get; private set; }
    public bool Dragging { get; private set; }
    public void Begin(long generation, long gesture, long source, long hwnd)
    {
        if (Held || this.generation!=generation || this.source!=source || this.hwnd!=hwnd) Cancel();
        this.generation=generation;this.source=source;this.hwnd=hwnd;Gesture=gesture;
        Held=true;Dragging=false;Revision++;
    }
    public void Move(double xDip,double yDip,double thresholdDip)
    {
        if (!Held || Dragging) return;
        if (!double.IsFinite(xDip) || !double.IsFinite(yDip) || Math.Abs(xDip)+Math.Abs(yDip)>thresholdDip) DragWins();
    }
    public void DragWins() { if (!Held || Dragging) return;Dragging=true;resolver.Cancel();Revision++; }
    public ClickTier? Complete(long generation,long source,long hwnd,TimeSpan now)
    {
        if (!Held) return null;
        Held=false;Revision++;
        if (Dragging || this.generation!=generation || this.source!=source || this.hwnd!=hwnd)
        { resolver.Cancel();return null; }
        return resolver.Register(now);
    }
    public ClickTier? Poll(TimeSpan now)=>Held ? null : resolver.Poll(now);
    public void Cancel() {Held=Dragging=false;resolver.Cancel();Revision++;}
}
