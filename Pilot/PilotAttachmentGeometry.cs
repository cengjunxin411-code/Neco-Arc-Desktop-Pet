using System;
using System.Windows;
using NecoArcDesktopPet.Coordinates;
using PhysicalScale=NecoArcDesktopPet.Coordinates.DpiScale;

namespace NecoArcDesktopPet.Pilot;

// Only relative geometry is converted to DIP. The world origin never gets scaled.
internal static class PilotAttachmentGeometry
{
    internal static Rect Local(PhysicalRect rect,PhysicalRect origin,PhysicalScale scale)=>
        new((rect.Left-origin.Left)/scale.X,(rect.Top-origin.Top)/scale.Y,rect.Width/scale.X,rect.Height/scale.Y);

    // maximumSnapDistanceDip is a DIP cap applied after the local conversion; null keeps
    // the historical five-argument decision unchanged.
    internal static AttachmentCandidate? Detect(PhysicalRect release,PhysicalRect current,PhysicalRect target,
        PhysicalRect work,PhysicalScale scale,double height,double topVisualHeight,double? maximumSnapDistanceDip=null)
    {
        if(!release.IsValid||!current.IsValid||!target.IsValid||!work.IsValid||!scale.IsValid||
            !double.IsFinite(height)||height<=0||!double.IsFinite(topVisualHeight)||topVisualHeight<=0)return null;
        return AttachmentGeometry.Detect(Local(release,current,scale),Local(target,current,scale),
            Local(work,current,scale),height,topVisualHeight,maximumSnapDistanceDip);
    }

    internal static PhysicalRect? Align(PhysicalRect current,PhysicalRect target,AttachmentSide side,
        double offsetX,double offsetY,Point contact,double height,double width,PhysicalScale scale,PhysicalRect work)
    {
        if(!current.IsValid||!target.IsValid||!work.IsValid||!scale.IsValid||!Enum.IsDefined(side)||
            !double.IsFinite(offsetX)||!double.IsFinite(offsetY)||!double.IsFinite(contact.X)||!double.IsFinite(contact.Y)||
            !double.IsFinite(height)||height<=0||!double.IsFinite(width)||width<=0)return null;
        var p=AttachmentGeometry.Align(side,Local(target,current,scale),new(contact,contact,contact),offsetX,offsetY,height,width);
        var x=Math.Round(current.Left+p.X*scale.X);var y=Math.Round(current.Top+p.Y*scale.Y);
        var aligned=new PhysicalRect(x,y,x+current.Width,y+current.Height);
        // Refuse rather than clamp away from the selected contact. The same complete
        // RECT/sweep gates as existing motion protect the last trusted footprint.
        return CrossScreenGeometry.TranslationSafe(current,aligned,new[]{work})?aligned:null;
    }
}
