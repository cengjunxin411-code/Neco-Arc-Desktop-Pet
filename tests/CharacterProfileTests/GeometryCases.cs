using System.Reflection;
using System.Windows;
using System.Windows.Media;
using NecoArcDesktopPet;
using NecoArcDesktopPet.CharacterProfiles;

static class GeometryCases
{
    public static void Run(CharacterProfile profile, Action<bool,string> check)
    {
        var method = typeof(AttachmentGeometry).GetMethod("TryContact", BindingFlags.Public|BindingFlags.Static);
        check(method != null,"contact calculation has an explicit refusal result");
        bool Contact(CharacterProfile? p,AttachmentSide side,double height,Matrix matrix,string? key,out Point result) {
            object?[] args={p,side,height,new Size(160,240),new Point(10,20),matrix,new Point(0.5,0.5),key,null};
            var success=(bool)method!.Invoke(null,args)!;result=(Point)args[8]!;return success;
        }
        const string key="NecoPetMaterials/idle/base_idle.gif";
        check(Contact(profile,AttachmentSide.Top,240,Matrix.Identity,key,out var top)&&top==new Point(90,236),"neutral top contact matches independent pixel oracle");
        check(Contact(profile,AttachmentSide.Left,240,Matrix.Identity,key,out var left)&&left==new Point(146,140),"neutral left selects screen-right source contact");
        check(Contact(profile,AttachmentSide.Right,240,Matrix.Identity,key,out var right)&&right==new Point(34,140),"neutral right selects screen-left source contact");
        var mirror=new Matrix(-1.5,0,0,1.5,3,4);
        check(Contact(profile,AttachmentSide.Top,240,mirror,key,out top)&&top==new Point(93,288),"non-unit transform/pivot top oracle");
        check(Contact(profile,AttachmentSide.Left,240,mirror,key,out left)&&left==new Point(177,144),"asymmetric mirror reorders left contact once");
        check(Contact(profile,AttachmentSide.Right,240,mirror,key,out right)&&right==new Point(9,144),"asymmetric mirror reorders right contact once");
        check(!Contact(null,AttachmentSide.Top,240,Matrix.Identity,key,out _),"absent profile cannot produce a contact");
        check(!Contact(profile,AttachmentSide.Top,240,Matrix.Identity,"NecoPetMaterials/idle/unknown.gif",out _),"unknown animation cannot inherit character contact");
        foreach(var height in new[]{double.NaN,double.PositiveInfinity,0d,-1d})
            check(!Contact(profile,AttachmentSide.Top,height,Matrix.Identity,key,out _),"unsafe height never produces a contact");
        check(!Contact(profile,AttachmentSide.Top,double.MaxValue,new Matrix(2,0,0,2,0,0),key,out _),"finite inputs whose transformed contact overflows refuse");
        check(!Contact(profile,AttachmentSide.Top,240,new Matrix(double.NaN,0,0,1,0,0),key,out _),"invalid transform never produces a contact");
        var overlay=typeof(AttachmentGeometry).GetMethod("TrySideOverlayContact",BindingFlags.Public|BindingFlags.Static)!;
        foreach(var side in new[]{AttachmentSide.Left,AttachmentSide.Right}) {
            object?[] args={profile,key,side,240d,new Rect(10,20,300,450),null};
            var success=(bool)overlay.Invoke(null,args)!;var contact=(Point)args[5]!;
            check(success&&contact==new Point(side==AttachmentSide.Left?304:16,422),"side overlay uses canonical correction AFTER non-unit transformed union: "+side);
        }
    }
}
