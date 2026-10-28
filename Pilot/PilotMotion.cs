using System;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

internal enum PilotMotionState { Idle, Falling, Landing, Grounded }

// Physical world coordinates only. Contact is a local length, never an absolute
// screen coordinate. Refuse partial/straddling/oversized footprints, no auto crossing.
internal static class PilotMotionGeometry
{
    internal static PhysicalRect? GroundTarget(PhysicalRect rect,PhysicalRect work,double contactPx)
    {
        if(Math.Abs(contactPx-rect.Height)<1e-6)contactPx=rect.Height; // arithmetic round-off only
        if(!rect.IsValid||!work.IsValid||!double.IsFinite(contactPx)||contactPx<=0||contactPx>rect.Height||
            rect.Left<work.Left||rect.Right>work.Right||rect.Top<work.Top||rect.Bottom>work.Bottom)return null;
        var y=Math.Round(work.Bottom-contactPx);
        var target=new PhysicalRect(rect.Left,y,rect.Right,y+rect.Height);
        // A transparent margin below the shoe cannot extend outside the WorkArea.
        return target.Top>=rect.Top&&target.Top>=work.Top&&target.Bottom<=work.Bottom ? target : null;
    }
    internal static double Duration(double distancePx,double dpiY)=>Math.Clamp(distancePx/dpiY/280+1.25,1.8,4.2);
    internal static PhysicalRect Step(PhysicalRect start,PhysicalRect end,double progress)
    {
        var p=Math.Clamp(progress,0,1);var eased=(1-Math.Cos(Math.PI*p))/2;
        var y=Math.Round(start.Top+(end.Top-start.Top)*eased);
        return new(start.Left,y,start.Right,y+start.Height);
    }
}
