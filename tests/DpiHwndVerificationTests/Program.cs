using NecoArcDesktopPet.Coordinates;
using DpiHwndProbe;
using System.Runtime.InteropServices;

internal static partial class Program
{
    private static int checks, cases, failures;
    private static void Check(bool value, string why) { checks++; if (!value) throw new Exception(why); }
    private static void Near(double actual, double expected, string why) => Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= 1e-9, $"{why}: expected={expected}, actual={actual}");
    private static void Case(string name, Action body) { cases++; try { body(); Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static ViewMapping Map(double x, double y, long generation = 1, nint hwnd = 10)
    {
        Check(ProbeGeometry.TryMapping(hwnd, true, generation, new(x, 0, 0, y), new(1 / x, 0, 0, 1 / y), 96 * x, 96 * y, out var map, out var failure), "valid mapping " + failure);
        return map!;
    }
    private static ProbeSession Session()
    { var s = new ProbeSession(new(-1500, -300), 1, 10); Check(s.Accept(Map(1.5, 1.5)), "accept initial map"); return s; }
    private static int Main()
    {
        AdmissionTests();
        RecoveryTests();
        Case("read-only mapping observation is bounded and reserves settle entry", () => {
            var type=typeof(ProbeSession).Assembly.GetType("DpiHwndProbe.MappingObservationBudget");
            Check(type != null,"missing bounded pre-settle matrix observation budget (instrumentation only)");
            var budget=Activator.CreateInstance(type!,System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,new object[]{true,1000L},null)!;
            var flags=System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            type!.GetMethod("Begin",flags)!.Invoke(budget,new object[]{2L,100L});
            bool Take(long requested,long current,int kind,long ticks,bool closed=false) =>
                (bool)type.GetMethod("TryTake",flags)!.Invoke(budget,new object[]{requested,current,kind,ticks,closed})!;
            Check(!Take(1,2,0,100) && !Take(2,3,0,100) && !Take(2,2,0,100,true),"stale/close callbacks cannot sample");
            Check(!Take(2,2,0,99) && !Take(2,2,7,100),"reversed clock/invalid kind rejected");
            int count=0;
            for(int i=0;i<10000;i++) if(Take(2,2,i%6,100+i)) count++;
            Check(count<=12 && count>0,"fixed observation cap even under dense event traffic");
            Check(Take(2,2,6,20000),"settle entry retained even after expiry/cap");
            Check(!Take(2,2,6,20001),"settle entry once only");
            type.GetMethod("End",flags)!.Invoke(budget,new object[]{2L});
            Check(!Take(2,2,1,20002),"completed transition cannot continue observations");
        });
        Case("mapping observation disabled by default and cannot publish a mapping", () => {
            var type=typeof(ProbeSession).Assembly.GetType("DpiHwndProbe.MappingObservationBudget");
            Check(type != null,"read-only observer unavailable (not a drag recovery assertion)");
            var flags=System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var budget=Activator.CreateInstance(type!,flags,null,new object[]{false,1000L},null)!;
            type!.GetMethod("Begin",flags)!.Invoke(budget,new object[]{2L,100L});
            Check(!(bool)type.GetMethod("TryTake",flags)!.Invoke(budget,new object[]{2L,2L,6,101L,false})!,"trace-off does no new work");
            foreach(var pair in new[]{(2.0,1.5),(1.5,2.0)}) {
                var s=Session(); Check(s.BeginGrab(new(-1530,-340)),"MODEL begin grab");
                long g=s.Invalidate();
                Check(!ProbeGeometry.TryMapping(10,true,g,new(pair.Item1,0,0,pair.Item1),new(1/pair.Item1,0,0,1/pair.Item1),
                    pair.Item2*96,pair.Item2*96,out _,out var reason) && reason=="DpiMismatch","old matrix rejected in both directions");
                Check(ProbeGeometry.TryMapping(10,true,g,new(pair.Item2,0,0,pair.Item2),new(1/pair.Item2,0,0,1/pair.Item2),
                    pair.Item2*96,pair.Item2*96,out _,out _),"read-only matrix candidate observed");
                Check(s.Mapping==null && !s.MoveGrab(new(-930,-140)),"observation must not Accept or move; legacy rejection retained");
            }
        });
        Case("MODEL delayed settle classifies missing mapping without inventing placement", () => {
            var outcome=typeof(ProbeSession).GetProperty("LastMoveOutcome",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            Check(outcome != null,"stall diagnostic missing: MoveGrab early returns are not classified");
            foreach(var scale in new[]{1.5,2.0}) {
                var s=Session(); Check(s.BeginGrab(new(-1530,-340)),"begin physical grab");
                var anchor=s.Anchor; var generation=s.Invalidate(); var calls=0;
                for(var i=0;i<8;i++) {
                    Check(!s.MoveGrab(new(-930+i*30,-140),_ => { calls++;return true; }),"pending mapping cannot safely place");
                    Check(outcome!.GetValue(s)!.ToString()=="MappingUnavailable","each produced model input classified before Place");
                }
                Check(calls==0 && s.Anchor==anchor,"no placement/anchor mutation during delayed mapping");
                Check(!s.Accept(Map(scale,scale,generation-1)),"obsolete mapping refused");
                Check(s.Accept(Map(scale,scale,generation)),"current epoch recovers");
                Check(s.MoveGrab(new(-600,-100),_ => {calls++;return true;}),"latest point after valid mapping");
                Check(outcome!.GetValue(s)!.ToString()=="Accepted" && calls==1,"diagnostic records acceptance");
                s.CancelGrab(); var released=s.Anchor;
                Check(!s.MoveGrab(new(900,500)) && s.Anchor==released && outcome.GetValue(s)!.ToString()=="NotDragging","release cannot move late point");
                s.Close(); Check(!s.MoveGrab(new(0,0)) && outcome.GetValue(s)!.ToString()=="Closed","close terminal; no zero fallback");
                Console.WriteLine($"TRACE MODEL scale={scale} generation={generation}; 8 MappingUnavailable / 0 placement; then 1 current-map placement; release/close no position change");
            }
        });
        Case("stall aggregate has bounded storage and retains all repeated early returns", () => {
            var type=typeof(ProbeSession).Assembly.GetType("DpiHwndProbe.DragStallTrace");
            Check(type != null,"stall diagnostic missing: optional bounded aggregate");
            var trace=Activator.CreateInstance(type!,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,new object[]{true},null)!;
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var record=type!.GetMethod("Move",flags)!;var enumType=typeof(ProbeSession).Assembly.GetType("DpiHwndProbe.GrabMoveOutcome")!;
            for(var i=0;i<10000;i++)record.Invoke(trace,new[]{Enum.Parse(enumType,"MappingUnavailable")});
            var summary=type.GetProperty("Summary",flags)!.GetValue(trace);
            using var json=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(summary));
            Check(json.RootElement.GetProperty("moves").GetProperty("MappingUnavailable").GetInt64()==10000,"count every attempt; no per-input log list");
            Check(!type.GetFields(flags).Any(f => typeof(System.Collections.IList).IsAssignableFrom(f.FieldType) && !f.FieldType.IsArray),"no growing per-input list");
            var bounded=new DragStallTrace(true);var tokens=Enumerable.Range(0,17).Select(_ => new object()).ToArray();
            for(var i=0;i<tokens.Length;i++)bounded.DispatcherStart(tokens[i],5,100);
            for(var i=0;i<tokens.Length;i++)bounded.DispatcherEnd(tokens[i],150);
            bounded.NativeEnter();bounded.NativeEnter();bounded.NativeExit(40);bounded.NativeExit(60);
            using var aggregates=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(bounded.Summary));
            Check(!bounded.HasActiveDispatcher && aggregates.RootElement.GetProperty("dispatcherOverflow").GetInt64()==1 &&
                aggregates.RootElement.GetProperty("dispatcherUnmatched").GetInt64()==1,"bounded overflow explicit; completion clears stored ownership");
            Check(aggregates.RootElement.GetProperty("maxNativeDepth").GetInt32()==2 &&
                aggregates.RootElement.GetProperty("nativeDepth").GetInt32()==0 &&
                aggregates.RootElement.GetProperty("maxNativeTicks").GetInt64()==60,"inclusive native reentry duration retained");
            var disabled=new DragStallTrace(false);disabled.Move(GrabMoveOutcome.MappingUnavailable);disabled.NativeMouse();
            using var off=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(disabled.Summary));
            Check(off.RootElement.GetProperty("moves").GetProperty("MappingUnavailable").GetInt64()==0 &&
                off.RootElement.GetProperty("nativeMouseMoves").GetInt64()==0,"default-off does not collect counters");
            var pending=new DragStallTrace(true);pending.PendingMove();
            using var unresolved=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(pending.Summary));
            Check(unresolved.RootElement.GetProperty("pendingMoves").GetInt64()==1 &&
                unresolved.RootElement.GetProperty("moves").GetProperty("Accepted").GetInt64()==0,"pending session target not counted as accepted placement");
        });
        Case("accepted target geometry must not use a later cursor (current mixed L556)", () => {
            Near(ProbeGeometry.AnchorError(new(485,797), new(485,797), new(489,713), new(-2,-84)),
                0, "same accepted target geometry; old late cursor path falsely fails at 6px");
        });
        Case("A1 accepted target geometry must not use a later cursor (L583)", () => {
            Near(ProbeGeometry.AnchorError(new(288,875), new(288,875), new(298,791), new(6,-82)),
                0, "same accepted target geometry; old late cursor path falsely fails at 4px");
        });
        Case("same placement measurement preserves fresh nonzero tracking and timing", () => {
            var evidence = new PlacementEvidence();
            var p = Accepted(evidence);
            var result = evidence.FinishMeasurement(evidence.BeginMeasurement(p,1,true,30),1,new(485,797),0,0,
                new(new(489,713),31,32),33);
            Near(result.PlacementGeometryErrorPx!.Value,0,"geometry independent of late cursor");
            Near(result.CursorTrackingLagPx!.Value,6,"fresh tracking remains nonzero");
            Check(result.PlacementId == p.PlacementId && result.PlacementCompletedTicks == 20 &&
                result.CursorStartedTicks == 31 && result.GeometryCompletedTicks == 33,"associated time samples");
            var failure = evidence.FinishMeasurement(evidence.BeginMeasurement(p,1,true,40),1,new(488,797),0,0,null,41);
            Near(failure.PlacementGeometryErrorPx!.Value,3,"real geometry failure preserved >2px");
        });
        Case("FAKE seven historical audit delta patterns preserve lag without geometry failures", () => {
            // Hand fixtures from 14A.2-3.1 section 5.3. Original seven-event JSONL is absent this round.
            // These are pattern reproductions, not replayed/new-format historical evidence.
            foreach(var row in new[]{(-83.0,458.0,7.0),(-106.0,460.0,16.0),(-86.0,460.0,4.0),
                (-84.0,460.0,6.0),(-116.0,462.0,26.0),(-86.0,460.0,4.0),(-98.0,462.0,8.0)}) {
                Near(ProbeGeometry.AnchorError(new(-100,500),new(-100,500),new(row.Item1,row.Item2),new(10,-40)),0,"same target geometry");
                Near(ProbeGeometry.TrackingLag(new(-100,500),new(row.Item1,row.Item2),new(10,-40)),row.Item3,"historical nonzero lag pattern");
            }
        });
        Case("same generation supersede and native reentry cannot accept older request", () => {
            var e = new PlacementEvidence(); var p=Accepted(e);
            var ticket=e.BeginMeasurement(p,1,true,30); var newer=Accepted(e,new(-500,800));
            var r=e.FinishMeasurement(ticket,1,new(485,797),0,0,null,40);
            Check(newer.PlacementId > p.PlacementId && r.Classification == "superseded" && r.PlacementGeometryErrorPx == null,"no old PASS");
            Check(e.Complete(p,PlacementStatus.Accepted,1,50).Status == PlacementStatus.Stale && e.Accepted == newer,"reentry cannot overwrite newer acceptance");
        });
        Case("synchronous DPI transition is pending until a new generation placement accepts", () => {
            var e=new PlacementEvidence(); var p=e.Request(1,"constant","mouse move",null,new(-2,-84),new(485,797),new(365,647,240,180),Map(2,2),10);
            p=e.Complete(p,PlacementStatus.Accepted,2,20);
            Check(p.Status == PlacementStatus.PendingDpiSettle && e.Accepted == null,"native return not geometry acceptance");
            var r=e.FinishMeasurement(e.BeginMeasurement(p,2,false,30),2,new(485,797),0,0,null,31);
            Check(r.PlacementGeometryErrorPx == null,"pending never PASS");
            var q=e.Request(2,"constant","dpi settle",null,new(-2,-84),new(485,797),new(365,647,240,180),Map(1.5,1.5,2),40);
            q=e.Complete(q,PlacementStatus.Accepted,2,50);
            Near(e.FinishMeasurement(e.BeginMeasurement(q,2,true,60),2,new(485,797),0,0,null,61).PlacementGeometryErrorPx!.Value,0,"settled generation accepts");
            Check(e.FinishMeasurement(e.BeginMeasurement(q,2,true,70),3,new(485,797),0,0,null,71).Classification == "stale","DPI during measure invalidates");
        });
        Case("invalid mapping and rejected/native failed placements remain inconclusive", () => {
            var e=new PlacementEvidence();var p=Accepted(e);
            Check(e.FinishMeasurement(e.BeginMeasurement(p,1,false,30),1,new(485,797),0,0,null,31).Classification == "invalidMapping","invalid map cannot PASS");
            foreach(var state in new[]{PlacementStatus.Rejected,PlacementStatus.NativeFailed}) {
                var q=e.Request(1,"constant","mouse move",null,default,new(-500,800),null,null,40);
                q=e.Complete(q,state,1,50);
                Check(e.FinishMeasurement(e.BeginMeasurement(q,1,true,60),1,new(-500,800),0,0,null,61).PlacementGeometryErrorPx == null,"rejection not PASS");
                Check(e.Accepted == p,"failed target never commits");
            }
            var bad=e.Request(1,"constant","invalid map",null,default,new(0,0),new(-120,-150,240,180),null,70);
            Check(e.Complete(bad,PlacementStatus.Accepted,1,80).Status == PlacementStatus.Rejected,"cannot accept null mapping");
        });
        Case("capture loss, close and late callbacks terminate measurement ownership", () => {
            var e=new PlacementEvidence();var p=Accepted(e);var t=e.BeginMeasurement(p,1,true,30);
            e.Cancel(); Check(e.FinishMeasurement(t,1,new(485,797),0,0,null,31).PlacementGeometryErrorPx == null,"capture loss blocks late geometry");
            p=Accepted(e);t=e.BeginMeasurement(p,1,true,30);e.Dispose();
            Check(e.FinishMeasurement(t,1,new(485,797),0,0,null,31).Classification == "disposed","late disposed callback classified");
            Check(e.Complete(p,PlacementStatus.Accepted,1,40).Status == PlacementStatus.Disposed && e.Accepted == null,"no resurrection on close");
        });
        Case("capture loss rejects completion of an in-flight placement", () => {
            var e=new PlacementEvidence();
            var p=e.Request(1,"constant","mouse move",null,default,new(485,797),new(365,647,240,180),Map(1.5,1.5),10);
            e.Cancel();
            Check(e.Complete(p,PlacementStatus.Accepted,1,20).Status == PlacementStatus.Stale && e.Accepted == null,
                "lost capture cannot resurrect a pending measurement owner");
        });
        Case("late old-gesture cursor is retained raw but cannot enter tracking metrics", () => {
            var e=new PlacementEvidence();var p=Accepted(e);var t=e.BeginMeasurement(p,1,true,30);
            e.Cancel();
            var result=e.FinishMeasurement(t,1,new(485,797),0,0,new(new(600,700),31,32),33);
            Check(result.CursorTrackingLagPx == null,"old grab offset after capture loss cannot count as current tracking");
            Accepted(e,new(-500,800));
            result=e.FinishMeasurement(e.BeginMeasurement(p,1,true,40),1,new(-500,800),0,0,new(new(-500,800),41,42),43);
            Check(result.CursorTrackingLagPx == null,"new gesture cannot reuse old callback grab");
        });
        Case("release observer detects a newer placement in the same generation", () => {
            Check(ProbeGeometry.CallbackClassification(10,2,11,2,false) == "superseded","later drag cannot be release-stable");
            Check(ProbeGeometry.CallbackClassification(10,2,10,3,false) == "stale","DPI invalidates release callback");
            Check(ProbeGeometry.CallbackClassification(10,2,10,2,true) == "disposed","closed callback");
            Check(ProbeGeometry.CallbackClassification(10,2,10,2,false) == "observed","unchanged placement can sample");
        });
        Case("immediate native RECT failure contributes explicit counters even without content measure", () => {
            var e=new PlacementEvidence();var p=Accepted(e);
            e.ObserveNativeRect(p,1,3);e.ObserveNativeRect(p,1,2);e.ObserveNativeRect(p,2,0);
            var summary=System.Text.Json.JsonSerializer.SerializeToElement(e.Summary);
            Check(summary.GetProperty("rectCount").GetInt64() == 2 && summary.GetProperty("rectFailed").GetInt64() == 1 &&
                summary.GetProperty("rectInconclusive").GetInt64() == 1,"rect observation failure never vanishes");
            Near(summary.GetProperty("maxNativeRectErrorPx").GetDouble(),3,"rect max retains failure");
        });
        Case("placement evidence serializes the HWND without unsupported IntPtr", () => {
            var e=new PlacementEvidence();var p=Accepted(e);
            var value=System.Text.Json.JsonSerializer.SerializeToElement(p);
            Check(value.GetProperty("Mapping").GetProperty("Hwnd").GetString() == "0xA","HWND association remains a stable hex string");
            Check(value.GetProperty("PlacementId").GetInt64() == p.PlacementId,"serialized association id");
        });
        Case("measurement changed during sample and reversed timestamps cannot PASS", () => {
            var e=new PlacementEvidence();var p=Accepted(e);var t=e.BeginMeasurement(p,1,true,30);
            var request=e.Request(1,"constant","reentrant",null,default,new(0,0),null,null,31);
            Check(e.FinishMeasurement(t,1,new(485,797),0,0,null,32).PlacementGeometryErrorPx == null,"in-flight request invalidates geometry");
            e.Complete(request,PlacementStatus.Rejected,1,33);
            p=Accepted(e);t=e.BeginMeasurement(p,1,true,30);
            var r=e.FinishMeasurement(t,1,new(485,797),0,0,new(new(489,713),32,31),29);
            Check(r.PlacementGeometryErrorPx == null && r.CursorTrackingLagPx == null,"invalid time no zero substitute");
            Check(e.FinishMeasurement(e.BeginMeasurement(p,1,true,19),1,new(485,797),0,0,null,40).PlacementGeometryErrorPx == null,"measure cannot predate acceptance");
        });
        Case("concurrent measurements use immutable targets and safe counters", () => {
            var e=new PlacementEvidence();var p=Accepted(e);int bad=0;
            Parallel.For(0,100, _ => {
                var r=e.FinishMeasurement(e.BeginMeasurement(p,1,true,30),1,new(485,797),0,0,new(new(489,713),31,32),33);
                if(r.PlacementGeometryErrorPx != 0 || r.CursorTrackingLagPx != 6) Interlocked.Increment(ref bad);
            });
            Check(bad == 0,"parallel snapshots keep target and lag");
        });
        Case("negative world X and 192/144 mappings retain physical measurement targets", () => {
            foreach(var scale in new[]{2.0,1.5}) {
                var e=new PlacementEvidence();var map=Map(scale,scale);
                var p=e.Request(1,"constant","mouse move",new(new(-502,716),1,2),new(-2,-84),new(-500,800),new(-620,650,240,180),map,10);
                p=e.Complete(p,PlacementStatus.Accepted,1,20);
                var r=e.FinishMeasurement(e.BeginMeasurement(p,1,true,30),1,new(-500,800),0,0,new(new(-490,716),31,32),33);
                Near(r.PlacementGeometryErrorPx!.Value,0,"world not scaled");Near(r.CursorTrackingLagPx!.Value,12,"late cursor delta not scaled");
            }
        });
        Case("local px-DIP fixtures and negative world origin", () => {
            foreach (var row in new[] { (1.0, 180.0, 120.0), (1.25, 144.0, 96.0), (1.5, 120.0, 80.0), (1.75, 720.0/7, 480.0/7), (2.0, 90.0, 60.0) }) {
                var map = Map(row.Item1, row.Item1);
                Check(ProbeGeometry.TryLayout(new(-1500, -300), 1, map, out var l, out _), "layout");
                Near(l!.Origin.X, -1620, "absolute X stays physical"); Near(l.Origin.Y, -450, "absolute Y stays physical");
                Near(l.BodyDip.X, row.Item2, "body DIP X"); Near(l.BodyDip.Y, row.Item3, "body DIP Y");
                Check(map.Scale.TryToPhysical(l.BodyDip, out var body), "forward"); Near(body.X, 180, "physical width"); Near(body.Y, 120, "physical height");
                Check(PhysicalVector.TryBetween(l.Origin, new(-1500, -300), out var local), "point difference");
                Near(local.X, 120, "anchor offset"); Near(local.Y, 150, "anchor offset Y");
            }
        });
        Case("unequal axes and canvas whitespace", () => {
            var m = Map(1.25, 2); Check(ProbeGeometry.TryLayout(new(150, 250), 1, m, out var l, out _), "layout");
            Near(l!.Origin.X, 30, "origin X"); Near(l.Origin.Y, 100, "origin Y");
            Near(l.CanvasDip.X, 192, "canvas X"); Near(l.CanvasDip.Y, 90, "canvas Y");
            Near(l.BodyOffsetDip.X, 24, "transparent margin X"); Near(l.BodyOffsetDip.Y, 15, "margin Y");
            Near(l.AnchorDip.X, 96, "anchor local X"); Near(l.AnchorDip.Y, 75, "anchor local Y");
        });
        Case("user multiplier independent of view DPI", () => {
            Check(ProbeGeometry.TryLayout(new(0, 0), 1.25, Map(1.5, 1.5), out var l, out _), "layout");
            Near(l!.BodyDip.X, 150, "225 physical / 1.5"); Near(l.Origin.X, -150, "scaled local anchor X");
            var s = new ProbeSession(new(0, 0), 1.25, 10); s.Invalidate(); Near(s.UserMultiplier, 1.25, "user setting stable");
        });
        Case("physical grab survives repeated DPI changes without accumulation", () => {
            var s = Session(); Check(s.BeginGrab(new(-1530, -340)), "mouse down");
            for (int i = 0; i < 200; i++) {
                var gen = s.Invalidate(new PhysicalRect(-1800, -900, -1440, -630));
                Check(s.Accept(Map(i % 2 == 0 ? 1 : 1.5, i % 2 == 0 ? 1 : 1.5, gen)), "settled map");
                Check(s.MoveGrab(new(-930, -140)), "drag move"); Near(s.Anchor.X, -900, "fixed grab X"); Near(s.Anchor.Y, -100, "fixed grab Y");
                Check(ProbeGeometry.TryLayout(s.Anchor, s.UserMultiplier, s.Mapping!, out var l, out _), "recompute");
                Check(ProbeGeometry.TryPixels(l!, s.Mapping!.Scale, out var px), "integer placement");
                Check(px == new PixelPlacement(-1020, -250, 240, 180), "no cumulative size/origin drift");
            }
        });
        Case("matrix direction, inconsistency and unavailable HWND fail explicitly", () => {
            Check(!ProbeGeometry.TryMapping(0, true, 1, new(1,0,0,1), new(1,0,0,1), 96,96,out _,out var f) && f == "InvalidHwnd", "zero HWND");
            Check(!ProbeGeometry.TryMapping(10,false,1,new(1,0,0,1),new(1,0,0,1),96,96,out _,out _), "dead HWND");
            Check(!ProbeGeometry.TryMapping(10,true,1,new(1.5,0,0,1.5),new(1.5,0,0,1.5),144,144,out _,out f) && f == "MatrixMismatch", "wrong inverse");
            Check(!ProbeGeometry.TryMapping(10,true,1,new(1.5,0,0,1.5),new(2.0/3,0,0,2.0/3),96,96,out _,out f) && f == "DpiMismatch", "HWND/view mismatch");
            foreach(var to in new[] { new Affine(1, .1, 0, 1), new Affine(1,0,0,1,1), new Affine(double.NaN,0,0,1), new Affine(0,0,0,1), new Affine(-1,0,0,1) })
                Check(!ProbeGeometry.TryMapping(10,true,1,to,new(1,0,0,1),96,96,out _,out _), "unsupported matrix rejected");
            Check(!ProbeGeometry.TryMapping(10,true,0,new(1,0,0,1),new(1,0,0,1),96,96,out _,out _), "invalid generation");
        });
        Case("stale generations, rebuild, capture loss and close", () => {
            var s = Session(); var old = s.Mapping!; var gen = s.Invalidate();
            Check(s.Mapping == null && !s.Accept(old), "reject stale map after message");
            Check(!s.BeginGrab(new(1,1)), "no mapping cannot start grab"); Check(s.Accept(Map(1,1,gen)), "new generation");
            Check(s.BeginGrab(new(-1500,-300)), "capture"); s.CancelGrab(); Check(!s.MoveGrab(new(1,1)), "lost capture rejects");
            s.Rebind(20); Check(!s.Accept(Map(1,1,s.Generation,10)), "old source handle rejected");
            Check(s.Accept(Map(1,1,s.Generation,20)), "new source accepted"); s.Close();
            Check(!s.Accept(Map(1,1,s.Generation,20)) && !s.BeginGrab(new(0,0)), "close terminal");
        });
        Case("fake suggested RECT is diagnostic and ordered", () => {
            var s = Session(); var suggested = new PhysicalRect(400,100,760,370); var gen = s.Invalidate(suggested);
            Check(s.Suggested == suggested && s.Anchor == new PhysicalPoint(-1500,-300), "suggested is not world owner");
            Check(!s.Accept(Map(1,1,gen-1)) && s.Accept(Map(1,1,gen)), "settled callback ordering");
            s.Invalidate(new PhysicalRect(10,20,9,21)); Check(s.Suggested == null, "invalid suggested rectangle discarded");
        });
        Case("nonfinite, overflow and int placement reject instead of default target", () => {
            var m = Map(1,1);
            foreach(var a in new[] { new PhysicalPoint(double.NaN,0), new PhysicalPoint(0,double.PositiveInfinity), new PhysicalPoint(double.MaxValue,0) })
                Check(!ProbeGeometry.TryLayout(a,1,m,out _,out _), "invalid/overflow coordinate");
            foreach(var u in new[] { 0, -1, double.NaN, double.MaxValue, double.Epsilon })
                Check(!ProbeGeometry.TryLayout(new(100,100),u,m,out _,out _), "invalid/underflow multiplier");
            Check(ProbeGeometry.TryLayout(new(3e9,100),1,m,out var l,out _), "finite math allowed");
            Check(!ProbeGeometry.TryPixels(l!,m.Scale,out _), "Win32 int overflow fails");
            Check(m.Scale.TryToPhysical(new(double.MaxValue,double.MaxValue),out _), "existing scale 1 keeps finite max");
            Check(!new DpiScale(2,2).TryToPhysical(new(double.MaxValue,1),out _), "existing DPI overflow");
            var s=Session(); Check(!s.BeginGrab(new(double.NaN,0)), "invalid grab");
        });
        Case("integer rounding and workarea safety", () => {
            var m=Map(1.5,1.5); Check(ProbeGeometry.TryLayout(new(-340.5,-200.5),1,m,out var l,out _), "half pixels");
            Check(ProbeGeometry.TryPixels(l!,m.Scale,out var px), "round");
            Check(px == new PixelPlacement(-461,-351,240,180), "round half away from zero; <1px error");
            var monitors=new[] { new MonitorSnapshot("left",new(-1920,-1080,0,0),new(-1920,-1080,0,-40)) };
            Check(ProbeGeometry.IsSafe(px,monitors), "on screen");
            Check(!ProbeGeometry.IsSafe(new(-100,-100,240,180),monitors), "partial outside blocked");
            Check(!ProbeGeometry.IsSafe(new(0,0,0,0),monitors), "invalid rectangle");
        });
        Case("horizontal, vertical and negative mixed-DPI layouts reuse Resolve", () => {
            var monitors=new[] {
                new MonitorSnapshot("left",new(-1920,0,0,1080),new(-1920,0,0,1040)),
                new MonitorSnapshot("right",new(0,0,2560,1440),new(0,0,2560,1380)),
                new MonitorSnapshot("top",new(0,-1080,1920,0),new(0,-1080,1920,-40)) };
            foreach(var row in new[] { (new PhysicalPoint(-1000,500),"left",1.0), (new PhysicalPoint(1000,700),"right",1.5), (new PhysicalPoint(1000,-500),"top",1.25) }) {
                var release=DesktopGeometry.Resolve(row.Item1,monitors,new PhysicalFootprint(90,90,120));
                Check(release.Success && release.MonitorId == row.Item2 && release.GroundAnchor == row.Item1, "original geometry owns selection");
                Check(ProbeGeometry.TryLayout(release.GroundAnchor,1,Map(row.Item3,row.Item3),out var l,out _), "local remap");
                Near(l!.Origin.X,row.Item1.X-120,"world x unchanged by selected view scale");
            }
            var failed=DesktopGeometry.Resolve(new(0,0),monitors,new PhysicalFootprint(5000,5000,5000));
            Check(!failed.Success, "failure not (0,0) safe target");
        });
        Case("safe placement spans adjacent workareas without cancelling cross-screen drag", () => {
            var adjacent=new[] { new MonitorSnapshot("a",new(-1920,0,0,1080),new(-1920,0,0,1040)),
                new MonitorSnapshot("b",new(0,0,2560,1440),new(0,0,2560,1380)) };
            Check(ProbeGeometry.IsSafe(new(-120,300,240,180),adjacent), "continuous seam must allow a straddling window");
            var gap=new[] { adjacent[0],new MonitorSnapshot("b",new(100,0,2660,1440),new(100,0,2660,1380)) };
            Check(!ProbeGeometry.IsSafe(new(-120,300,340,180),gap), "desktop hole is not safe placement");
            var vertical=new[] { new MonitorSnapshot("a",new(0,-1080,1920,0),new(0,-1080,1920,0)),
                new MonitorSnapshot("b",new(0,0,2560,1440),new(0,0,2560,1380)) };
            Check(ProbeGeometry.IsSafe(new(500,-90,240,180),vertical), "vertical seam allowed");
        });
        Case("FAKE WM_DPICHANGED native RECT copy preserves exclusive edges and word order", () => {
            var ptr=Marshal.AllocHGlobal(16);
            try {
                Marshal.StructureToPtr(new SuggestedRect(-1800,-900,-1440,-630),ptr,false);
                Check(DpiChangePayload.TryRead(((nuint)144 << 16) | 120,ptr,out var msg), "valid fake pointer payload");
                Check(msg!.DpiX == 120 && msg.DpiY == 144 && msg.Suggested == new PhysicalRect(-1800,-900,-1440,-630), "word order/RECT edges");
                Marshal.StructureToPtr(new SuggestedRect(10,20,10,21),ptr,false);
                Check(!DpiChangePayload.TryRead(((nuint)144 << 16) | 144,ptr,out _), "zero width rejected");
                Check(!DpiChangePayload.TryRead(0,ptr,out _) && !DpiChangePayload.TryRead(144,0,out _), "zero DPI/pointer fails");
            } finally { Marshal.FreeHGlobal(ptr); }
        });
        Case("rejected physical placement preserves last accepted anchor and next grab", () => {
            var s=Session(); Check(s.BeginGrab(new(-1530,-340)), "start grab");
            Check(!s.MoveGrab(new(-930,-140), _ => false), "offscreen/native rejection propagates");
            Check(s.Anchor == new PhysicalPoint(-1500,-300), "rejected placement does not change owner");
            s.CancelGrab(); Check(s.BeginGrab(new(-1530,-340)), "new grab on displayed marker");
            Check(s.MoveGrab(new(-930,-140), target => target == new PhysicalPoint(-900,-100)), "accepted input commits");
            Check(s.Anchor == new PhysicalPoint(-900,-100), "expected anchor after accepted move");
            Check(!s.MoveGrab(new(double.NaN,1), _ => throw new Exception("must not place invalid target")), "no fallback target callback");
        });
        Case("FAKE synchronous DPI transition accepts physical target then settles new mapping", () => {
            var s=Session(); Check(s.BeginGrab(new(-1530,-340)), "begin");
            var oldGeneration=s.Generation;
            Check(s.MoveGrab(new(-930,-140), _ => { s.Invalidate(new(100,200,460,470)); return true; }), "accepted native input pending DPI is not rejection");
            Check(s.Anchor == new PhysicalPoint(-900,-100) && s.Mapping == null, "world target retained; old view invalidated");
            Check(!s.Accept(Map(1,1,oldGeneration)), "old callback rejected");
            Check(s.Accept(Map(1,1,s.Generation)), "new mapping settles");
            Check(s.MoveGrab(new(-830,-40)) && s.Anchor == new PhysicalPoint(-800,0), "grab resumes without unit jump");
        });
        Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {checks} checks / {cases} cases; failures={failures}; FAKE/SIMULATED (no OS DPI events)");
        return failures == 0 ? 0 : 1;
    }
    private static PlacementAttempt Accepted(PlacementEvidence evidence, PhysicalPoint? anchor=null)
    {
        var a=anchor ?? new PhysicalPoint(485,797);
        var p=evidence.Request(1,"constant","mouse move",new(new(a.X-2,a.Y-84),1,2),new(-2,-84),a,
            new((int)a.X-120,(int)a.Y-150,240,180),Map(1.5,1.5),10);
        return evidence.Complete(p,PlacementStatus.Accepted,1,20);
    }
}
