using System;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Coordinates.Windows;
using NecoArcDesktopPet.Pilot;
using PhysicalScale = NecoArcDesktopPet.Coordinates.DpiScale;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private PilotLog? _pilotLog;
    private HwndSource? _pilotSource;
    private WindowsDisplayChangeSource? _pilotChanges;
    private MonitorTopologyService? _pilotTopology;
    private MonitorSnapshot _pilotMonitor;
    private PhysicalScale _pilotScale;
    private PhysicalRect _pilotRect;
    private readonly SingleMonitorDrag _pilotDrag = new();
    private long _pilotGeneration=1, _pilotGesture, _pilotSourceToken=1;
    private int _pilotPlacementDepth, _pilotHookDepth;
    private bool _pilotReady, _pilotClosing, _pilotStopped;
    private DispatcherTimer? _pilotWatch;
    private nint PilotHwnd => _pilotSource?.Handle ?? 0;
    // Attachment status is part of the title and of the "运行状态" menu. Both are
    // rebuilt from the live field so a settings Apply can never leave a stale line.
    private string _pilotTitleBase="",_pilotTitleTail="";
    private MenuItem? _pilotAttachmentStatusItem;
#if PMV2_PRODUCT
    private const string PilotRunSuffix="";
    private static string PilotAttachmentTitleState(bool enabled)=>
        enabled ? " · 外部依附启用" : " · 外部依附关闭";
#else
    private const string PilotRunSuffix=" · 本次运行";
    private static string PilotAttachmentTitleState(bool enabled)=>
        enabled ? " · 外部依附启用（本次运行）" : " · 外部依附关闭";
#endif
    private static string PilotAttachmentMenuState(bool enabled)=>
        $"外部依附：{(enabled ? "已启用" : "已禁用")}"+PilotRunSuffix;
    private void PilotRefreshAttachmentStatus()
    {
        if(_pilotTitleBase.Length!=0)Title=_pilotTitleBase+PilotAttachmentTitleState(_pilotAttachmentEnabled)+_pilotTitleTail;
        if(_pilotAttachmentStatusItem!=null)_pilotAttachmentStatusItem.Header=PilotAttachmentMenuState(_pilotAttachmentEnabled);
    }

    internal void EnablePmv2Pilot(PilotLog log)
    {
        if(IsLoaded || (_settingsStore?.FilePath != null &&
            !string.Equals(Path.GetFileName(_settingsStore.FilePath), "pmv2-settings.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Pilot requires its independent pmv2-settings.json store before load.");
        _pilotLog=log;
        _crossScreen=PilotApplication.CrossScreen;
        _seamHandoff=_crossScreen&&PilotApplication.SeamHandoff;
        _pilotRocketEnabled=PilotApplication.Rocket;
        // Preference is loaded before this call; the process flag stays the master gate.
        _pilotAttachmentEnabled=PilotApplication.Attachment&&_settings.Attachment.Enabled;
        _pilotRandomEnabled=PilotApplication.RandomIdle;
        _pilotMoveEnabled=PilotApplication.AutonomousMovement;
        _pilotPhysicsEnabled=PilotApplication.Physics;
        _pilotDragFrameDiagnosticsEnabled=Environment.GetEnvironmentVariable("NECO_PMV2_DRAG_FRAME_DIAGNOSTICS")=="1";
        if(_pilotDragFrameDiagnosticsEnabled&&_gifPlayer!=null)_gifPlayer.PilotFrameAdvanced+=PilotDragFrameAdvanced;
#if PMV2_PRODUCT
        WorkbenchUnavailableReason=null;
        Title="Neco-Arc Desktop Pet · 猫姬 · Esc 退出";
#else
        WorkbenchUnavailableReason=PilotWorkbenchUnavailableReason;
        Title=_crossScreen ? "EXPERIMENTAL · 猫姬 PMv2 · 跨屏 Guarded Recovery · Esc 退出" : "EXPERIMENTAL · 猫姬 PMv2 · 仅当前屏幕拖动";
#endif
        if(_crossScreen) Closing+=CrossClosing;
        _pilotTitleBase=Title;
#if !PMV2_PRODUCT
        _pilotTitleTail=(PilotApplication.RestSleep ? " · 休息/睡眠能力开放（本次运行）" : " · 休息/睡眠关闭")
            +(_pilotRandomEnabled?" · 随机待机能力开放（本次运行）":"")
            +(_pilotMoveEnabled?" · 自主移动能力开放（本次运行）":"");
#endif
        Title+=PilotAttachmentTitleState(_pilotAttachmentEnabled)+_pilotTitleTail;
        Focusable=true; // Keyboard focus is requested only by an accepted user gesture.
        Closing += (_, _) => PilotFlushSizeSave();
        // Constructor initialized real sessions/assets. Only this limited pilot pauses
        // legacy positional/interaction subsystems; ordinary Release has none of this code.
        PetSurface.LostMouseCapture+=PilotLostCapture;
        PreviewKeyDown+=PilotKeyDown;
        var menu=new ContextMenu{MaxWidth=320};
        PetSurface.ContextMenuOpening+=(_,e)=>{if(PilotPhysicsBusy)e.Handled=true;};
        // Defense for programmatic opens which bypass ContextMenuOpening. Do this
        // before any normal Opened handler is allowed to cancel a gesture.
        menu.Opened+=(_,_)=>{if(PilotPhysicsBusy)menu.IsOpen=false;};
        var status=new MenuItem{Header="运行状态",MaxWidth=320};
        foreach(var state in new[]{
            ("跨屏",_crossScreen),("接力",_seamHandoff),("Rocket",_pilotRocketEnabled)})
            status.Items.Add(new MenuItem{Header=$"{state.Item1}：{(state.Item2 ? "已启用" : "已禁用")}"+PilotRunSuffix,IsEnabled=false});
        _pilotAttachmentStatusItem=new MenuItem{Header=PilotAttachmentMenuState(_pilotAttachmentEnabled),IsEnabled=false};
        status.Items.Add(_pilotAttachmentStatusItem);
        foreach(var state in new[]{
            ("休息/睡眠",PilotApplication.RestSleep),("随机待机",_pilotRandomEnabled),
            ("自主移动",_pilotMoveEnabled)})
            status.Items.Add(new MenuItem{Header=$"{state.Item1}：{(state.Item2 ? "已启用" : "已禁用")}"+PilotRunSuffix,IsEnabled=false});
        menu.Items.Add(status);
        var top=new MenuItem{Header="始终置顶",IsCheckable=true,IsChecked=Topmost};
        var sizeMenu=new MenuItem{Header="调整大小"};
        _pilotSizeNotice=new MenuItem{IsEnabled=false,Visibility=Visibility.Collapsed};
        foreach(var choice in new[]{("小",170d),("中",260d),("大",360d),("特大",470d)}) {
            var size=new MenuItem{Header=choice.Item1,IsCheckable=true};
            size.Click+=(_,_)=>PilotSetPetHeight(choice.Item2,true);
            sizeMenu.Items.Add(size);
        }
        top.Click+=(_,_)=>{ApplyTopmost(top.IsChecked);_workbenchWindow?.RefreshTopmostFromOwner();
            var result=SaveSettingsSnapshot();
            if(!result.Success)ShowSettingsNotice("置顶已生效，但未保存。\n下次启动可能恢复。",TimeSpan.FromSeconds(8));};
        var workbench=new MenuItem{Header="打开工作台..."};workbench.Click+=(_,_)=>OpenWorkbench();
        var exit=new MenuItem{Header="退出程序"};exit.Click+=(_,_)=>Close();
        var fall=new MenuItem{Header="下落到当前屏幕地面"};
        fall.Click+=(_,_)=>PilotMotionBegin("explicit ground menu; release remains floating");
        menu.Items.Add(sizeMenu);menu.Items.Add(_pilotSizeNotice);menu.Items.Add(top);menu.Items.Add(workbench);menu.Items.Add(exit);menu.Items.Add(fall);
        menu.Opened+=(_,_)=>{top.IsChecked=Topmost;foreach(MenuItem item in sizeMenu.Items)
            item.IsChecked=(string)item.Header switch {"小"=>_petHeight==170,"中"=>_petHeight==260,"大"=>_petHeight==360,"特大"=>_petHeight==470,_=>false};};
        menu.Opened+=(_,_)=>{if(PilotPhysicsBusy||!menu.IsOpen)return;_contextMenuOpen=true;if(_pilotRandomEnabled||_pilotMoveEnabled){PilotRandomCancel("Menu.Open");PilotMoveCancel("Menu.Open");}
            PilotRestMenuOpen();PilotCancelGestureCore("Menu.Open",true);PilotRandomUpdate();};
        menu.Closed+=(_,_)=>{_contextMenuOpen=false;PilotAttachmentMenuResume();PilotRestMenuClose();PilotRestUpdate();PilotRandomUpdate();};
        PetSurface.ContextMenu=menu;
        IsVisibleChanged+=PilotMoveVisibleChanged;
        _pilotLog.Write("settings-isolation",new{storeAttached=_settingsStore!=null,
            persistence="pmv2-settings.json only; ordinary settings untouched",legacyPositionBehaviors="paused for entire single-monitor slice"});
    }

    private string? PilotWorkbenchUnavailableReason(string path)
    {
        if(path.StartsWith("Normal.RandomIdle",StringComparison.Ordinal))
            return !_pilotRandomEnabled ? "需以 --experimental-random-idle 启动。" : null;
        if(path.StartsWith("Rest.",StringComparison.Ordinal) || path.StartsWith("Sleep.",StringComparison.Ordinal))
            return !PilotApplication.RestSleep ? "需以 --experimental-rest-sleep 启动。" : null;
        if(path.StartsWith("Movement.",StringComparison.Ordinal))
            return !_pilotMoveEnabled ? "需以 --experimental-autonomous-movement 启动。" : null;
        if(path.StartsWith("Interaction.Rocket",StringComparison.Ordinal))
            return !_pilotRocketEnabled ? "需以 --experimental-rocket 启动。" : null;
        if(path.StartsWith("Attachment.",StringComparison.Ordinal))
            return !PilotApplication.Attachment ? "需以 --experimental-attachment 启动。" : null;
        return null;
    }

    private void PilotApplySettings(bool previousRandomEnabled, bool previousRestEnabled, bool previousSleepEnabled)
    {
        // Settings are already published into _settings; this only re-derives the
        // runtime owner and performs the one safe detach when it was turned off.
        PilotAttachmentApplySettings();
        PilotRestApplySettings(previousRestEnabled, previousSleepEnabled);
        PilotRandomApplySettings(previousRandomEnabled);
        PilotVoiceApplySettings();
    }

    private void LoadPmv2Pilot()
    {
        try
        {
            _pilotSource=PresentationSource.FromVisual(this) as HwndSource;
            if(_pilotSource==null || !PilotNative.ProcessV2 || !PilotNative.ThreadV2 ||
                !PilotNative.V2(PilotNative.GetWindowDpiAwarenessContext(PilotHwnd)))
                throw new InvalidOperationException("Real MainWindow process/thread/HWND is not PMv2.");
            // Required assets must be admitted before native layout reads an unarranged surface.
            // A neutral WPF fallback does not supply the real animation/geometry contract.
            if(_gifPlayer==null || _effectiveBaseIdlePath==null) {
                PilotApplication.MissingIdleGif=true;
                throw new InvalidOperationException("Missing required idle GIF: Product/Pilot needs a loadable, calibrated idle GIF before native layout; static fallback is not a successful pilot. Character Profile: " + CharacterProfiles.CharacterProfileRuntime.Result.Code + ".");
            }
            _pilotSource.AddHook(PilotHook);
            if(!PilotMapping(out _pilotScale)) throw new InvalidOperationException("Initial source/visual DPI matrix incoherent.");
            _pilotChanges=new WindowsDisplayChangeSource();
            _pilotTopology=new MonitorTopologyService(new WindowsMonitorProvider(),_pilotChanges);
            _pilotTopology.RefreshRequested+=PilotTopologyInvalidated;
            if(!_pilotTopology.Refresh().Success || !PilotFresh) throw new InvalidOperationException("Physical topology could not be verified.");
            if(!PilotNative.GetWindowRect(PilotHwnd,out var initial)) throw new InvalidOperationException("Initial GetWindowRect failed: "+Marshal.GetLastPInvokeError());
            var center=new PhysicalPoint((initial.Left+(double)initial.Right)/2,(initial.Top+(double)initial.Bottom)/2);
            _pilotMonitor=_pilotTopology.State.LastKnownGood!.Monitors.FirstOrDefault(m=>m.Bounds.ContainsClosed(center));
            if(!_pilotMonitor.IsValid) throw new InvalidOperationException("Initial real window is outside verified topology.");
            _pilotRect=initial.Physical;
            _pilotReady=true;
            PilotRestoreSizeForViewport();
            ApplyPetLayout();
            if(_pilotStopped) return;
            var width=_crossScreen ? Math.Round(CrossSize.X*_pilotScale.X) : _pilotRect.Width;
            var height=_crossScreen ? Math.Round(CrossSize.Y*_pilotScale.Y) : _pilotRect.Height;
            var origin=new PhysicalPoint(Math.Max(_pilotMonitor.WorkArea.Left,_pilotMonitor.WorkArea.Right-width-92*_pilotScale.X),
                Math.Max(_pilotMonitor.WorkArea.Top,_pilotMonitor.WorkArea.Bottom-height-48*_pilotScale.Y));
            var target=new PhysicalRect(Math.Round(origin.X),Math.Round(origin.Y),Math.Round(origin.X)+width,Math.Round(origin.Y)+height);
            if(!PilotPlace(target,"startup")) throw new InvalidOperationException("Initial native placement rejected.");
            UpdateLayout();
            if(_gifPlayer==null || _effectiveBaseIdlePath==null) throw new InvalidOperationException("Pilot needs real idle GIF; static fallback is not a successful pilot.");
            _gifPlayer.Resume();
            _pilotLog!.Write("product-ready",new{processV2=PilotNative.ProcessV2,threadV2=PilotNative.ThreadV2,
                hwndV2=PilotNative.V2(PilotNative.GetWindowDpiAwarenessContext(PilotHwnd)),hwnd=(long)PilotHwnd,
                dpi=PilotNative.GetDpiForWindow(PilotHwnd),matrix=_pilotSource.CompositionTarget!.TransformToDevice,
                rect=_pilotRect,monitor=_pilotMonitor,Title,Topmost,AllowsTransparency,WindowStyle,
                gif=_gifPlayer.ResourcePath,gifWidth=_gifPlayer.PixelWidth,gifHeight=_gifPlayer.PixelHeight,
                imageWidth=PetImage.ActualWidth,imageHeight=PetImage.ActualHeight,userHeightDip=_petHeight,
                loadedHeightIntentDip=_pilotHeightForSave,
                crossScreen=_crossScreen ? "EXPERIMENTAL opt-in Candidate" : "DISABLED; default 4A",finalScreenPixels="USER HARDWARE PENDING"},"REAL WPF OS OBSERVED");
            _pilotWatch=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(100)};
            _pilotWatch.Tick+=PilotWatchTick;_pilotWatch.Start();
            if(_crossScreen) CrossLoaded();
            PilotRestStart();
            PilotRandomStart();
            PilotStartupVoiceTrigger();
            PilotGravityScheduleStart("stable startup; zero initial velocity");
        }
        catch(Exception e) { PilotStop("Startup: "+e); }
    }

    private bool PilotFresh => _pilotTopology is {} t && !t.State.IsDirty && !t.State.IsStale &&
        !t.State.IsDisposed && t.State.LastKnownGood!=null;

    private bool PilotMapping(out PhysicalScale scale)
    {
        scale=default;
        if(_pilotSource==null || _pilotSource.IsDisposed || _pilotSource.CompositionTarget is not {} target ||
            !ReferenceEquals(PresentationSource.FromVisual(this),_pilotSource) ||
            !ReferenceEquals(PresentationSource.FromVisual(PetImage),_pilotSource) ||
            !PilotNative.IsWindow(PilotHwnd) || !PilotNative.ThreadV2 ||
            !PilotNative.V2(PilotNative.GetWindowDpiAwarenessContext(PilotHwnd))) return false;
        var dpi=PilotNative.GetDpiForWindow(PilotHwnd)/96d;
        var to=target.TransformToDevice;var from=target.TransformFromDevice;
        bool Close(double a,double b)=>double.IsFinite(a) && Math.Abs(a-b)<1e-8;
        if(dpi<=0 || !Close(to.M11,dpi) || !Close(to.M22,dpi) || !Close(to.M12,0) || !Close(to.M21,0) ||
            !Close(to.OffsetX,0) || !Close(to.OffsetY,0) || !Close(from.M11,1/dpi) || !Close(from.M22,1/dpi) ||
            !Close(from.M12,0) || !Close(from.M21,0) || !Close(from.OffsetX,0) || !Close(from.OffsetY,0)) return false;
        foreach(var visual in new Visual[]{this,PetSurface,PetImage})
        { var local=VisualTreeHelper.GetDpi(visual);if(!Close(local.DpiScaleX,dpi) || !Close(local.DpiScaleY,dpi)) return false; }
        scale=new(dpi,dpi);return true;
    }

    // One user scale transaction's arranged foot contact, in physical px.
    internal readonly record struct PilotScaleAnchor(double ContactX,double Bottom,bool Grounded);
    private PilotScaleAnchor? PilotScaleAnchorForLayout()
    {
        if(!_pilotScaleActive || !_pilotReady || _pilotClosing || _pilotStopped) return null;
        if(_pilotRect!=_pilotScaleRect) return null; // the window moved after capture; no stale anchor
        return new(_pilotScaleContactX,_pilotScaleRect.Bottom,_pilotScaleGrounded);
    }
    private static bool PilotLayoutSizeIsValid(double width,double height)=>
        double.IsFinite(width)&&double.IsFinite(height)&&width>0&&height>0;

    private void PilotApplyLayout(PilotScaleAnchor? scaleContact=null)
    {
        PilotMotionAlignImage();
        if(_crossScreen) {CrossApplyLayout(scaleContact);return;}
        if(!_pilotReady || _pilotClosing || _pilotStopped) return;
        PilotCancelGesture("layout/scale changed");
        if(!PilotMapping(out var scale) || scale!=_pilotScale || !PilotFresh)
        { PilotStop("Layout mapping/topology changed; cross-DPI slice disabled.");return; }
        var width=Math.Round(Width*scale.X);var height=Math.Round(Height*scale.Y);
        var work=_pilotMonitor.WorkArea;
        if(!PilotLayoutSizeIsValid(width,height)) {PilotStop("Layout dimensions must be finite and positive.");return;}
        if(width<=0 || height<=0 || width>work.Width || height>work.Height) {PilotStop("Sprite layout cannot fit locked WorkArea.");return;}
        // The arranged visual bottom edge is the contact the viewport is fitted to
        // (PilotVisualArrange crops the all-frame union there). A scale keeps that
        // edge and the arranged horizontal anchor instead of the frozen top-left.
        double x=_pilotRect.Left,y=_pilotRect.Top;
        if(scaleContact is {} contact)
        {
            x=contact.ContactX-PilotVisualFootCenterX*scale.X;
            y=contact.Bottom-height;
        }
        x=Math.Round(Math.Clamp(x,work.Left,work.Right-width));
        y=Math.Round(Math.Clamp(y,work.Top,work.Bottom-height));
        if(!PilotPlace(new(x,y,x+width,y+height),"layout/scale")) PilotStop("Native layout placement failed.");
    }

    private bool PilotPlace(PhysicalRect target,string reason)
    {
        if(_crossScreen) return CrossPlace(target,reason);
        if(!_pilotReady || _pilotClosing || _pilotStopped || _pilotPlacementDepth!=0 || _pilotHookDepth!=0 ||
            !PilotFresh || !SingleMonitorDrag.Fits(target,_pilotMonitor.WorkArea) ||
            !PilotMapping(out var scale) || scale!=_pilotScale) return false;
        var generation=_pilotGeneration;var gesture=_pilotGesture;var source=_pilotSource;
        _pilotPlacementDepth++;
        try
        {
            var ok=PilotNative.Place(PilotHwnd,target);var error=ok ? 0 : Marshal.GetLastPInvokeError();
            var read=PilotNative.GetWindowRect(PilotHwnd,out var actual);
            var accepted=ok && read && !_pilotStopped && !_pilotClosing && generation==_pilotGeneration && gesture==_pilotGesture &&
                ReferenceEquals(source,_pilotSource) && actual.Physical==target && PilotMapping(out var post) && post==_pilotScale;
            _pilotLog!.Write("native-placement",new{reason,generation,gesture,source=_pilotSourceToken,hwnd=(long)PilotHwnd,
                requested=target,actual=read ? (PhysicalRect?)actual.Physical : null,error,accepted},accepted ? "ACCEPTED" : "REJECTED");
            if(accepted) _pilotRect=actual.Physical;
            return accepted;
        }
        finally { _pilotPlacementDepth--; }
    }

    private bool PilotCursor(out PhysicalPoint cursor)
    {
        cursor=default;
        var succeeded=PilotNative.GetPhysicalCursorPos(out var native);
        if(succeeded)cursor=new(native.X,native.Y);
        if(CrossScreenGeometry.CursorUsable(succeeded,cursor))return true;
        _pilotLog!.Write("cursor-refused",new{error=Marshal.GetLastPInvokeError(),substitution="none"},"BLOCKED");
        return false;
    }
    private void PilotMouseDown(MouseButtonEventArgs e)
    {
        if(_pilotPhysicsHold!=null){e.Handled=true;return;} // duplicate Down cannot renew/rebase Hold
        _pilotPhysicsIgnoredUp=false;
        if(PilotPhysicsBusy){e.Handled=true;PilotPhysicsHoldBegin();return;}
        PilotSeatReset(); // a new gesture can never inherit a release-time correction
        PilotPhysicsReset();
        PilotCancelWheelPending();
        PilotMotionAbortFall();
        PilotMoveCancel("Down priority");
        PilotRandomCancel("Down priority");
        if(_pilotAttachmentTarget==null)PilotAttachmentCancel("Down priority"); // pending release planning only
        // Restore a finite action's idle footprint before sampling the native grab.
        // Admission waits for this independent callback; no held grab is resized.
        if(PilotNeedsFootprintAdmission()) {
            e.Handled=true;
            // A Down on an already admitted single/Rotation action keeps its
            // original suppression/extension rules. Only a real drag interrupts it.
            if(!_pilotClickAction)PilotCancelGestureCore("Down before footprint admission",true);
            else PilotVisualCancelPending();
            PilotVisualQueue();var generation=_pilotGeneration;var token=_pilotSourceToken;var hwnd=PilotHwnd;
            var gesture=_pilotGesture;var revision=_pilotVisualRevision;
            _pilotVisualCompleted=()=>{
                if(generation==_pilotGeneration&&gesture==_pilotGesture&&revision==_pilotVisualRevision&&token==_pilotSourceToken&&hwnd==PilotHwnd&&
                    Mouse.LeftButton==MouseButtonState.Pressed)PilotMouseDown(e);
            };
            return;
        }
        if(_pilotTopologyHold){_pilotTopologyReflowAttempts=0;_pilotTopologyBudgetReported=false;if(!PilotMotionTryLeaveTopologyHold()){e.Handled=true;return;}}
        if(_crossScreen) {
            CrossMouseDown(e);
            if(_pilotClicks?.Held!=true) {PilotClickCancel("Down refused by gesture owner");PilotClickFact("click-refused","Down cursor/capture/geometry/focus guard");}
            return;
        }
        e.Handled=true;PilotCancelGestureCore("new gesture",true);
        if(!_pilotReady || _pilotStopped || !PilotFresh || !PilotMapping(out var scale) || scale!=_pilotScale ||
            !PilotCursor(out var cursor) || !PilotNative.GetWindowRect(PilotHwnd,out var rect))
        {PilotClickCancel("Down mapping/cursor guard");PilotClickFact("click-refused","Down mapping/cursor guard");return;}
        var captured=PetSurface.CaptureMouse();
        _pilotGesture++;
        var accepted=_pilotDrag.Begin(_pilotGeneration,_pilotGesture,_pilotSourceToken,(long)PilotHwnd,
            cursor,rect.Physical,_pilotMonitor,_pilotScale,_settings.Interaction.DragThresholdDip,captured && PetSurface.IsMouseCaptured);
        _pilotLog!.Write("gesture-down",new{captured,accepted,generation=_pilotGeneration,gesture=_pilotGesture,
            source=_pilotSourceToken,hwnd=(long)PilotHwnd,cursor,rect=rect.Physical,thresholdDip=_settings.Interaction.DragThresholdDip},accepted ? "ACCEPTED" : "BLOCKED");
        if(!accepted) PilotCancelGesture("capture/start guard rejected");
        else
        {
            var focused=Focus() && ReferenceEquals(Keyboard.Focus(this),this);
            _pilotLog!.Write("gesture-keyboard-focus",new{focused},focused ? "ACCEPTED" : "BLOCKED");
            if(!focused) PilotCancelGesture("Esc keyboard focus unavailable");
            else {_pilotClickDown=cursor;PilotClickBegin();}
        }
    }
    private sealed record PilotPhysicsHoldState(PhysicalPoint Down, PhysicalScale Scale, double Threshold,
        long Generation,long Gesture,long SourceToken,nint Hwnd,long Topology,HwndSource Source);
    private PilotPhysicsHoldState? _pilotPhysicsHold;
    // A lost/failed capture still consumes the matching Up, even after settle.
    // This latch never blocks a fresh Down and owns neither capture nor physics.
    private bool _pilotPhysicsIgnoredUp;

    private bool PilotPhysicsHoldValid(PilotPhysicsHoldState hold) => PilotClickLive && IsVisible &&
        !_pilotTopologyHold && _pilotPlacementDepth==0 && _pilotHookDepth==0 && _crossLayoutDepth==0 &&
        hold.Generation==_pilotGeneration && hold.Gesture==_pilotGesture && hold.SourceToken==_pilotSourceToken &&
        hold.Hwnd==PilotHwnd && ReferenceEquals(hold.Source,_pilotSource) &&
        hold.Topology==_pilotTopology?.State.LastKnownGood?.Version &&
        PilotMapping(out var scale) && scale==hold.Scale && scale==_pilotScale &&
        PilotNative.GetWindowRect(PilotHwnd,out var native) && native.Physical==_pilotRect &&
        (_crossScreen ? _crossInitialized && _crossMapped && _crossRecovery?.Active!=true &&
            !_crossBoundaryPreparing && _crossBoundary==null && _crossMove==null && _seamPlan==null &&
            CrossScreenGeometry.Covered(native.Physical,CrossWork) &&
            CrossScreenGeometry.NaturalFootprint(native.Physical,CrossSize,scale)
            : SingleMonitorDrag.Fits(native.Physical,_pilotMonitor.WorkArea));

    private void PilotPhysicsHoldBegin()
    {
        _pilotPhysicsIgnoredUp=true;
        var threshold=_settings.Interaction.DragThresholdDip;
        if(!PilotClickLive || !double.IsFinite(threshold) || threshold<0 ||
            !PilotCursor(out var cursor) || !_pilotRect.ContainsClosed(cursor) || _pilotSource==null)return;
        var hold=new PilotPhysicsHoldState(cursor,_pilotScale,threshold,_pilotGeneration,_pilotGesture,
            _pilotSourceToken,PilotHwnd,_pilotTopology!.State.LastKnownGood!.Version,_pilotSource);
        if(!PilotPhysicsHoldValid(hold)){PilotStop("Candidate Down mapping/native identity rejected.");return;}
        _pilotPhysicsHold=hold; // before CaptureMouse can synchronously enter input callbacks
        if(!PetSurface.CaptureMouse() || !PetSurface.IsMouseCaptured ||
            !(Focus()&&ReferenceEquals(Keyboard.Focus(this),this)))PilotPhysicsHoldClear();
    }
    private void PilotPhysicsHoldClear()
    {
        if(_pilotPhysicsHold==null)return;
        _pilotPhysicsHold=null;PilotInputStopReleaseWait();
        var releasing=_pilotClickReleasing;_pilotClickReleasing=true;
        try {if(PetSurface.IsMouseCaptured)PetSurface.ReleaseMouseCapture();}
        finally {_pilotClickReleasing=releasing;}
    }
    private void PilotPhysicsHoldMove(PhysicalPoint cursor)
    {
        if(_pilotPhysicsHold is not {} hold)return;
        if(!PetSurface.IsMouseCaptured){PilotPhysicsHoldClear();return;}
        if(!cursor.IsFinite || !PilotPhysicsHoldValid(hold)) {PilotStop("Candidate Move mapping/native identity rejected.");return;}
        if(!PhysicalVector.TryBetween(hold.Down,cursor,out var delta) ||
            !CrossScreenGeometry.Threshold(delta,hold.Scale,hold.Threshold))return;
        // Existing drag admission requires a grab inside the current window.
        // Refuse an impossible regrab before ending the still-valid physics.
        if(!_pilotRect.ContainsClosed(cursor)){PilotPhysicsHoldClear();return;}
        // Revoke physics only after a legitimate threshold crossing. All native
        // reads below are current: the window may have moved since Hold.Down.
        _pilotPhysicsHold=null;_pilotPhysicsIgnoredUp=false;
        PilotCancelGestureCore("confirmed physics drag",true);
        if(_pilotStopped||_pilotClosing)return;
        // Reset may switch throw/fall back to Idle and invalidate measure while
        // retaining the admitted viewport. Re-measure that viewport before the
        // existing cross checkpoint; do not bless or write a different RECT.
        UpdateLayout();
        if(_crossScreen)CrossMouseAdmit(cursor,true,hold.Threshold);
        else {
            if(!PilotMapping(out var scale)||scale!=hold.Scale||!PilotNative.GetWindowRect(PilotHwnd,out var rect)||rect.Physical!=_pilotRect)
            {PilotStop("Takeover native mapping changed.");return;}
            var captured=PetSurface.CaptureMouse();_pilotGesture++;
            if(!_pilotDrag.Begin(_pilotGeneration,_pilotGesture,_pilotSourceToken,(long)PilotHwnd,
                cursor,rect.Physical,_pilotMonitor,scale,hold.Threshold,captured&&PetSurface.IsMouseCaptured) ||
                !_pilotDrag.ConfirmDrag(_pilotGeneration,_pilotGesture,_pilotSourceToken,(long)PilotHwnd) ||
                !(Focus()&&ReferenceEquals(Keyboard.Focus(this),this)))
            {PilotCancelGesture("confirmed drag admission refused");return;}
            _pilotClickDown=cursor;PilotClickBegin();_pilotGestureDragThresholdDip=hold.Threshold;
            PilotClickDragWins();PilotDragVisualEnsure(cursor);
        }
    }
    private bool PilotNeedsFootprintAdmission()=>_pilotRocketTimer!=null||_pilotRocketSession!=null||PilotMotionBusy||PilotVisualBusy;
    private void PilotMouseMove(MouseEventArgs e)
    {
        if(_pilotPhysicsHold!=null){e.Handled=true;if(PilotInputMoveAllowed(e,_crossScreen)) {
            if(PilotCursor(out var heldCursor))PilotPhysicsHoldMove(heldCursor);
            else PilotStop("Candidate physical cursor unavailable.");
        }return;}
        if(_crossScreen) {CrossMouseMove(e);return;}
        e.Handled=true;
        if(!_pilotDrag.Active) return;
        if(!PilotInputMoveAllowed(e,false))return;
        if(!PilotMapping(out var scale) || scale!=_pilotScale || !PilotCursor(out var cursor))
        {PilotCancelGesture("input/capture/mapping rejected");return;}
        PilotMovePhysical(cursor);
    }
    private long _pilotInputGuardGesture=-1, _pilotInputEventSequence;
    private DispatcherTimer? _pilotInputReleaseWait;
    private Action? _pilotInputReleaseWaitBody;
    private void PilotInputStopReleaseWait()
    {
        _pilotInputReleaseWait?.Stop();_pilotInputReleaseWait=null;_pilotInputReleaseWaitBody=null;
    }
    private void PilotInputAwaitRelease()
    {
        if(_pilotInputReleaseWait!=null)return; // repeated Released Moves cannot renew the deadline
        var gesture=_pilotGesture;var generation=_pilotGeneration;var source=_pilotSource;
        var token=_pilotSourceToken;var hwnd=PilotHwnd;
        var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(250)};
        _pilotInputReleaseWait=timer;
        var body=new Action(()=>{
            if(!ReferenceEquals(timer,_pilotInputReleaseWait))return;
            PilotInputStopReleaseWait();
            if(gesture!=_pilotGesture||generation!=_pilotGeneration||token!=_pilotSourceToken||
                !ReferenceEquals(source,_pilotSource)||hwnd!=PilotHwnd)return;
            _pilotLog?.Write("input-release-deadline",new{gesture,generation,sequence=++_pilotInputEventSequence,
                ticks=System.Diagnostics.Stopwatch.GetTimestamp(),reason="Released Move without MouseUp; cancel only"});
            if(_pilotPhysicsHold!=null)PilotPhysicsHoldClear();
            else PilotCancelGesture("Released Move without MouseUp deadline");
        });
        _pilotInputReleaseWaitBody=body;timer.Tick+=(_,_)=>body();timer.Start();
    }
    private bool PilotInputMoveAllowed(MouseEventArgs e,bool cross)
    {
        var sequence=++_pilotInputEventSequence;
        // A Released event is allowed to wait only for an otherwise valid Hold.
        // Source/DPI/native loss remains a real safety stop, not input cleanup.
        if(_pilotPhysicsHold is {} hold&&!PilotPhysicsHoldValid(hold))
        {PilotStop("Candidate input identity/mapping rejected.");return false;}
        var live=cross?CrossLive:_pilotReady&&!_pilotStopped&&!_pilotClosing&&_pilotSource is {IsDisposed:false};
        var captured=PetSurface.IsMouseCaptured;var pressed=e.LeftButton==MouseButtonState.Pressed;var fresh=PilotFresh;
        if(live&&captured&&pressed&&fresh){PilotInputStopReleaseWait();return true;}
        if(_pilotInputGuardGesture!=_pilotGesture){
            _pilotInputGuardGesture=_pilotGesture;
            _pilotLog?.Write("input-move-guard",new{gesture=_pilotGesture,generation=_pilotGeneration,sequence,
                ticks=System.Diagnostics.Stopwatch.GetTimestamp(),cross,notLive=!live,notCaptured=!captured,
                buttonNotPressed=!pressed,notFresh=!fresh,source=_pilotSourceToken,hwnd=(long)PilotHwnd});
        }
        // A Released Move is neither a drag sample nor a synthetic Up. Preserve
        // only an otherwise legal owner for the actual routed button release.
        if(live&&captured&&!pressed&&fresh&&!_pilotTopologyHold&&
            PilotMapping(out var scale)&&scale==_pilotScale&&
            (!cross||_crossMapped&&_crossRecovery?.Active!=true)){
            if(cross)Abort(ref _crossMove);
            PilotInputAwaitRelease();
            return false;
        }
        if(_pilotPhysicsHold!=null){PilotPhysicsHoldClear();if(!live||!fresh)PilotStop("Candidate input/source/topology rejected.");return false;}
        if(cross)CrossCancelGesture("input/capture/topology rejected");
        else PilotCancelGesture("input/capture/mapping rejected");
        return false;
    }
    // Sampling is an OS boundary; the same production owner/placement consumer can
    // receive controlled physical samples in a real-HWND integration harness.
    private void PilotMovePhysical(PhysicalPoint cursor)
    {
        if(_pilotPhysicsHold!=null){PilotPhysicsHoldMove(cursor);return;}
        if(_crossScreen) {CrossMovePhysical(cursor);return;}
        if(!_pilotDrag.Active) return;
        if(!PetSurface.IsMouseCaptured || !PilotFresh || !PilotMapping(out var scale) || scale!=_pilotScale)
        {PilotCancelGesture("physical sample owner/capture/mapping rejected");return;}
        // Real drag input, sampled before any placement/clamp decision: the seat
        // classifier must see the hand, not only the moves that moved the window.
        PilotSeatInputSample(cursor,System.Diagnostics.Stopwatch.GetTimestamp());
        var generation=_pilotGeneration;var gesture=_pilotGesture;var source=_pilotSourceToken;var hwnd=(long)PilotHwnd;
        var target=_pilotDrag.Target(generation,gesture,source,hwnd,cursor);
        if(_pilotDrag.Dragging){PilotClickDragWins();PilotDragVisualEnsure(cursor);}
        if(target==null) {if(!_pilotDrag.Active) PilotCancelGesture("cross-screen/outside WorkArea disabled");return;}
        if(!PilotPlace(target.Value,"physical drag") || !_pilotDrag.Accept(generation,gesture,source,hwnd,target.Value))
            PilotStop("Native drag completion invalid; no late placement permitted.");
        else
            PilotPhysicsAcceptedSample(cursor,System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void PilotCancelGesture(string reason)=>PilotCancelGestureCore(reason,false);
    private void PilotCancelGestureCore(string reason,bool keepAttachment)
    {
        PilotPhysicsHoldClear();
        PilotInputStopReleaseWait();
        // Cancel/LostCapture/Esc revoke a pending seat intent before anything else.
        PilotSeatReset();
        PilotPhysicsReset();
        PilotMotionAbortFall();
        PilotDragVisualEnd(reason);
        PilotReleaseFloatCancel(reason);
        if(!keepAttachment)PilotAttachmentCancel(reason);
        var visual=PilotVisualBusy;
        // External cancellation revokes a pending Down/return continuation before
        // any GIF switch, capture release or new recovery owner can requeue layout.
        PilotVisualCancelPending();
        // MouseUp revokes its old visual continuation without changing the
        // admitted click clip. Keep that same overlay revision under the lease.
        if(keepAttachment&&_pilotAttachmentTarget!=null&&_pilotClickAction)
            _pilotAttachmentOverlayVisualRevision=_pilotVisualRevision;
        PilotClickCancelCore(reason,keepAttachment);
        void RequeueVisual()
        {
            if(!visual&&!PilotVisualBusy)return;
            // Re-register a pending pose at the final revision, including after
            // CrossCancelGesture's own queue. No clip or playback reset is needed.
            if(keepAttachment&&_pilotAttachmentTarget!=null&&
                _pilotAttachmentPosePending&&PilotAttachmentPoseVisible)
                _pilotAttachmentPosePending=false;
            PilotVisualQueue();
        }
        if(_crossScreen) {CrossCancelGestureCore(reason,keepAttachment);RequeueVisual();return;}
        var active=_pilotDrag.Active;
        _pilotDrag.Cancel();_pilotGesture++;
        // Revoke before ReleaseMouseCapture invokes LostMouseCapture synchronously.
        if(PetSurface.IsMouseCaptured) PetSurface.ReleaseMouseCapture();
        if(active) _pilotLog?.Write("gesture-cancel",new{reason,generation=_pilotGeneration,gesture=_pilotGesture,rect=_pilotRect});
        RequeueVisual();
    }
    private void PilotLostCapture(object sender,MouseEventArgs e)
    {
        // Candidate capture loss is already fully handled here. Do not route it
        // on to the throw visual's ordinary capture-loss cancellation handler.
        if(_pilotPhysicsHold!=null){e.Handled=true;PilotPhysicsHoldClear();return;}
        if(!_pilotClickReleasing && (_pilotGravitySession!=null || _pilotGravityPending)) PilotPhysicsReset();
        // Both adapters revoke the gesture before their synchronous capture release.
        if(_pilotClickReleasing || (!_crossActive && !_pilotDrag.Active && _pilotRocketSession==null && e.OriginalSource!=PetSurface))return;
        if(_crossActive || _pilotDrag.Active || _pilotClicks?.Held==true || _clickResolver.IsPending||_pilotRocketSession!=null)
            PilotCancelGesture("LostMouseCapture");
    }
    private void PilotKeyDown(object sender,KeyEventArgs e) {if(e.Key==Key.Escape){PilotCancelGesture("Esc");Close();e.Handled=true;}}
    private void PilotTopologyInvalidated(object? sender,EventArgs e) => PilotMotionTopologyChanged();
    private nint PilotHook(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        _pilotHookDepth++;
        try
        {
            if(message==0x02E0 /* WM_DPICHANGED */)
            {
                PilotPhysicsReset();
                if(_crossScreen) {CrossDpiChanged(hwnd,wParam,lParam);return 0;}
                _pilotGeneration++;
                _pilotLog?.Write("dpi-transition-disabled",new{generation=_pilotGeneration,wParam=(long)wParam,hwnd=(long)hwnd,
                    policy="single-monitor slice; no G/H recovery; cancel and close"},"BLOCKED; 4B");
                PilotStop("WM_DPICHANGED: cross-screen migration disabled in 4A slice.");
            }
        }
        finally {_pilotHookDepth--;if(_crossScreen && (message==0x02E0 || message==0x0047)) CrossSignal();}
        return 0; // Never decode fabricated/suggested RECT as a trusted product target.
    }
    private void PilotWatchTick(object? sender,EventArgs e)
    {
        if(_pilotPhysicsHold!=null&&Mouse.LeftButton==MouseButtonState.Released)PilotInputAwaitRelease();
        PilotRestUpdate();
        PilotRandomUpdate();
        if(_crossScreen) {CrossWatch();return;}
        if(_pilotClosing || _pilotStopped || !_pilotReady || _pilotPlacementDepth!=0 || _pilotHookDepth!=0) return;
        if(_pilotTopologyHold){PilotMotionWatchTopologyHold();return;}
        UpdateLayout();
        if(!PilotFresh || !PilotMapping(out var scale) || scale!=_pilotScale ||
            !PilotNative.GetWindowRect(PilotHwnd,out var rect) || rect.Physical!=_pilotRect ||
            !IsMeasureValid || !IsArrangeValid || !PetImage.IsMeasureValid || !PetImage.IsArrangeValid ||
            Math.Abs(ActualWidth*_pilotScale.X-_pilotRect.Width)>2 || Math.Abs(ActualHeight*_pilotScale.Y-_pilotRect.Height)>2)
            PilotStop("Late native/layout/source overwrite; limited pilot has no repair protocol.");
    }
    private void PilotStop(string reason)
    {
        PilotPhysicsReset();
        if(_pilotStopped || _pilotClosing) return;
        PilotStartupVoiceStop();
        PilotMotionAbortFall();
        PilotRestCancel("safety stop",false);
        PilotRandomClose("safety stop");
        PilotSeatReset();_pilotScaleActive=false;
        _pilotStopped=true;_pilotGeneration++;PilotApplication.Failed=true;
        PilotMoveClose("safety stop");
        PilotCancelGesture("safety stop");_pilotWatch?.Stop();
        _pilotLog?.Write("safety-stop",new{reason,generation=_pilotGeneration,rect=_pilotRect},"FAIL-CLOSED");
        Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>{if(!_pilotClosing) Close();}));
    }
    private void ClosePmv2Pilot()
    {
        PilotPhysicsReset();
        if(_pilotDragFrameDiagnosticsEnabled&&_gifPlayer!=null)_gifPlayer.PilotFrameAdvanced-=PilotDragFrameAdvanced;
        PilotStopSizeTimers();
        PilotMotionAbortFall();
        PilotRestClose();
        PilotRandomClose("Close");
        PilotStartupVoiceClose();
        PilotSeatReset();_pilotScaleActive=false;
        _pilotClosing=true;_pilotGeneration++;PilotCancelGesture("Close");_pilotWatch?.Stop();
        PilotMoveClose("Close");IsVisibleChanged-=PilotMoveVisibleChanged;
        PilotVisualCancelPending();
        Abort(ref _pilotTopologyRefresh);
        PilotVisualClearHeld();_pilotVisualDirty=false;_pilotVisualResume=false;
        _pilotVisualArrangedFrame=null;_pilotVisualPresentationDeferred=false;
        if(_crossScreen) CrossClosed();
        if(_pilotWatch!=null) _pilotWatch.Tick-=PilotWatchTick;
        if(_pilotSource is {IsDisposed:false}) _pilotSource.RemoveHook(PilotHook);
        if(_pilotTopology!=null) _pilotTopology.RefreshRequested-=PilotTopologyInvalidated;
        _pilotTopology?.Dispose();_pilotChanges?.Dispose();
        PetSurface.LostMouseCapture-=PilotLostCapture;PreviewKeyDown-=PilotKeyDown;
        _pilotLog?.Write("product-close",new{generation=_pilotGeneration,settingsStoreAttached=_settingsStore!=null,stopped=_pilotStopped});
    }
}
