using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

// No external positioning APIs. A unique property nonce binds the actual window
// instance, even if USER recycles the same HWND within the same process/thread/class.
internal sealed class PilotAttachmentTarget : IDisposable
{
    internal readonly record struct Snapshot(nint Hwnd,uint Process,uint Thread,string Class,uint Dpi,nint Context,nint Monitor,PhysicalRect Rect);
    private static readonly string PropertyKey="NecoArcDesktopPet.PMv2Attachment."+Guid.NewGuid().ToString("N");
    private static long nextNonce;
    private readonly Snapshot identity;
    private readonly nint nonce;
    private WinEventProc? locationCallback;
    private nint locationHook;
    private bool disposed;
    internal string PropertyName=>PropertyKey;
    internal Snapshot Identity=>identity;
    private PilotAttachmentTarget(Snapshot identity,nint nonce){this.identity=identity;this.nonce=nonce;}

    private enum Observation { Invalid, Stable, Moving, Uncertain }
    internal static bool Observe(nint hwnd,out Snapshot snapshot)=>ObserveCore(hwnd,out snapshot)==Observation.Stable;
    internal static bool ObservePlanning(nint hwnd,out Snapshot snapshot,out bool uncertain)
    {
        var observation=ObserveCore(hwnd,out snapshot);
        uncertain=observation is Observation.Moving or Observation.Uncertain;
        return observation==Observation.Stable;
    }
    private static Observation ClassifyGeometry(PhysicalRect before,PhysicalRect after,bool onMonitor)
    {
        if(before!=after)return Observation.Moving;
        return !onMonitor||!before.IsValid?Observation.Invalid:Observation.Stable;
    }
    private static Observation ObserveCore(nint hwnd,out Snapshot snapshot)
    {
        snapshot=default;
        if(!PilotNative.ThreadV2||hwnd==0||!PilotNative.IsWindow(hwnd)||!IsWindowVisible(hwnd)||IsIconic(hwnd)||GetAncestor(hwnd,2)!=hwnd)return Observation.Invalid;
        var thread=GetWindowThreadProcessId(hwnd,out var process);var name=new StringBuilder(256);
        if(thread==0||process==0||GetClassNameW(hwnd,name,name.Capacity)==0)return Observation.Uncertain;
        var cls=name.ToString();
        if(cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "NotifyIconOverflowWindow" or "Windows.UI.Core.CoreWindow")return Observation.Invalid;
        var dpi=PilotNative.GetDpiForWindow(hwnd);var context=PilotNative.GetWindowDpiAwarenessContext(hwnd);var monitor=MonitorFromWindow(hwnd,0);
        if(dpi==0||context==0||!PilotNative.GetWindowRect(hwnd,out var rect))return Observation.Uncertain;
        // Even zero-size/offscreen windows need a matching second observation:
        // transitioning into eligible geometry must keep planning unresolved.
        // Reject a concurrent identity/visibility/geometry transition. No cached RECT
        // is returned as fresh and no target failure is reported as a pet safety stop.
        var postName=new StringBuilder(256);
        if(!PilotNative.IsWindow(hwnd)||!IsWindowVisible(hwnd)||IsIconic(hwnd)||GetWindowThreadProcessId(hwnd,out var postProcess)!=thread||postProcess!=process||
            GetClassNameW(hwnd,postName,postName.Capacity)==0||postName.ToString()!=cls||MonitorFromWindow(hwnd,0)!=monitor||
            PilotNative.GetDpiForWindow(hwnd)!=dpi||!PilotNative.AreDpiAwarenessContextsEqual(context,PilotNative.GetWindowDpiAwarenessContext(hwnd)))return Observation.Uncertain;
        if(!PilotNative.GetWindowRect(hwnd,out var post))return Observation.Uncertain;
        snapshot=new(hwnd,process,thread,cls,dpi,context,monitor,rect.Physical);
        return ClassifyGeometry(rect.Physical,post.Physical,monitor!=0);
    }
    private static bool SameIdentity(Snapshot a,Snapshot b)=>a.Hwnd==b.Hwnd&&a.Process==b.Process&&a.Thread==b.Thread&&a.Class==b.Class&&
        a.Dpi==b.Dpi&&a.Monitor==b.Monitor&&PilotNative.AreDpiAwarenessContextsEqual(a.Context,b.Context);
    internal static PilotAttachmentTarget? Bind(Snapshot selected)
    {
        if(!Observe(selected.Hwnd,out var fresh)||fresh!=selected||GetPropW(selected.Hwnd,PropertyKey)!=0)return null;
        var nonce=(nint)Interlocked.Increment(ref nextNonce);
        if(nonce==0||!SetPropW(selected.Hwnd,PropertyKey,nonce))return null; // UIPI refused: no attachment.
        var lease=new PilotAttachmentTarget(selected,nonce);
        if(lease.TryRead(out var post)&&post==selected)return lease;
        lease.Dispose();return null;
    }
    internal bool TryRead(out Snapshot snapshot)=>TryRead(out snapshot,out _);
    internal bool WatchLocation(Action wake)
    {
        if(disposed||locationHook!=0)return false;
        // Out-of-context events are delivered through the registering thread's
        // message loop. The callback only requests a later Dispatcher attempt.
        locationCallback=(_,eventType,hwnd,objectId,childId,_,_)=>{
            if(!disposed&&eventType==0x800B&&hwnd==identity.Hwnd&&objectId==0&&childId==0)wake();
        };
        locationHook=SetWinEventHook(0x800B,0x800B,0,locationCallback,identity.Process,identity.Thread,0);
        if(locationHook!=0)return true;
        locationCallback=null;return false; // 50 ms watchdog remains available.
    }
    internal bool TryRead(out Snapshot snapshot,out bool moving)
    {
        snapshot=default;moving=false;
        if(disposed||GetPropW(identity.Hwnd,PropertyKey)!=nonce)return false;
        var observation=ObserveCore(identity.Hwnd,out var fresh);
        if(GetPropW(identity.Hwnd,PropertyKey)!=nonce)return false;
        if(observation==Observation.Moving){moving=SameIdentity(identity,fresh);return false;}
        if(observation!=Observation.Stable||!SameIdentity(identity,fresh))return false;
        snapshot=fresh;return true;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(locationHook!=0){UnhookWinEvent(locationHook);locationHook=0;locationCallback=null;}
        if(GetPropW(identity.Hwnd,PropertyKey)==nonce)RemovePropW(identity.Hwnd,PropertyKey);
    }
    private delegate void WinEventProc(nint hook,uint eventType,nint hwnd,int objectId,int childId,uint eventThread,uint eventTime);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetWinEventHook(uint min,uint max,nint module,WinEventProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    internal static nint Monitor(nint hwnd)=>MonitorFromWindow(hwnd,0);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd,StringBuilder name,int count);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetPropW(nint hwnd,string name,nint value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint GetPropW(nint hwnd,string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint RemovePropW(nint hwnd,string name);
}
