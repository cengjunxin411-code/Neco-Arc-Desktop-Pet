// Portions derived from YoneDesktopPet, maintained upstream by DreamingBetter.
// https://github.com/DreamingBetter/YoneDesktopPet
// Audit reference: 3c410d002e4c6b8ed2cbcb7689dc2b531a48653c; original import not uniquely established.
// Upstream MIT and original notices: LICENSE and THIRD_PARTY_NOTICES.md.
// Inherited window/drag/bubble/detection/image code coexists with later extensions.
// ZINC licenses only modifications it is entitled to license; game assets are excluded.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Settings;
using NecoArcDesktopPet.CharacterProfiles;

namespace NecoArcDesktopPet
{
    public partial class MainWindow : Window
    {
        private const double MinPetHeight = 140;
        private const double MaxPetHeight = 560;
        private const double DragFacingThreshold = 6;
        private const string BaseIdlePath = "NecoPetMaterials/idle/base_idle.gif";
        private const string FallbackIdlePath = "NecoPetMaterials/idle/sway.gif";
        private const string FallbackPngPath = "Assets/default_pet.png";
        private const string TopAttachGifPath = BaseIdlePath;
        // Logical window geometry remains independent of the active GIF canvas.
        private static double GifLayoutAspect => CharacterProfileRuntime.Current?.LogicalAspect ?? 1;
        private const string DraggedGifPath = "NecoPetMaterials/drag/dragged.gif";
        // Phase 19C.4-D P2: Gravity grabs the scruff pose. Every other mode keeps dragged.gif.
        private const string ScruffGifPath = "NecoPetMaterials/drag/scruff.gif";
        private static readonly TimeSpan DragEntryFirstFrameDuration = TimeSpan.FromMilliseconds(120);
        private const string UprightFallingGifPath = "NecoPetMaterials/fall/falling_down2.gif";
        private const string SidewaysFallingGifPath = "NecoPetMaterials/fall/falling_down.gif";
        private const string LandingGifPath = "NecoPetMaterials/fall/landing.gif";

        private enum AttachmentMode
        {
            None,
            FloatingOnTop,
            ClingingLeft,
            ClingingRight,
            Falling
        }

        private enum AmbientAction
        {
            None,
            SlowTilt,
            DrowsyDip,
            TinyLook
        }

        // Scale values are deltas from 1; all other values are in DIPs or degrees.
        private readonly record struct MotionLayer(
            double OffsetX, double OffsetY, double ScaleX, double ScaleY, double Rotation);

        private readonly Random _random = new();
        private readonly Random _fallRandom = new();
        private readonly string[] _voiceLines =
        {
            "……",
            "别一直盯着我看。",
            "有事吗？",
            "真麻烦。",
            "别乱碰。"
        };
        private readonly string[] _attachLines =
        {
            "有事吗？"
        };
        private readonly string[] _descentLines =
        {
            "……",
            "真麻烦。"
        };

        private SpeechBubbleWindow? _bubbleWindow;
        private DispatcherTimer? _motionTimer;
        private DispatcherTimer? _animationTimer;
        private DispatcherTimer? _ambientSpeechTimer;
        private DispatcherTimer? _delayedClickSpeechTimer;
        private readonly GifFramePlayer? _gifPlayer;
        private readonly string? _effectiveBaseIdlePath;
        private readonly RandomIdleScheduler _randomIdleScheduler;
        private bool _ambientGifActive;
        private bool _contextMenuOpen;
        private double _imageAspect = 1;
        private double _petHeight = 170;
        private double _anchorCenterX;
        private double _anchorBottomY;
        private double _animScaleX = 1;
        private double _animScaleY = 1;
        private double _animOffsetX;
        private double _animOffsetY;
        private double _animRotate;
        private MotionLayer _breathingMotion;
        private MotionLayer _mouseAwareMotion;
        private MotionLayer _ambientMotion;
        private double _ambientAmount;
        private AmbientAction _ambientAction;
        private DateTimeOffset _ambientStartedAt;
        private DateTimeOffset _nextAmbientDecisionAt;
        private int _ambientDirection;
        private double _lookX;
        private double _lookY;
        private double _dragVelocityX;
        private double _dragVelocityY;
        private double _dragPoseDirection = 1;
        private double _dragFacingExtremeX;
        private double _attachOffsetX;
        private double _attachOffsetY;
        private Rect _attachmentRect = Rect.Empty;
        private double _descentStartBottomY;
        private double _descentTargetBottomY;
        private double _descentDurationSeconds;
        private IntPtr _attachedWindow;
        private AttachmentMode _attachmentMode = AttachmentMode.None;
        private DateTimeOffset _idleStarted = DateTimeOffset.UtcNow;
        private DateTimeOffset _descentStartedAt;
        private DateTimeOffset _dragStartedAt;
        private DateTimeOffset _lastDragMotionAt;
        private DateTimeOffset _lastMotionFrameAt;
        private bool _isDragging;
        private bool _dragGifActive;
        private bool _clickReactionActive;
        private string? _currentFallAnimation;
        private bool _landingActive;
        private bool _suppressClickOnRelease;
        private bool _dragGifFacingRight = true;
        private bool _leftPressed;
        private Point _mouseDownScreen;
        private Point _lastDragScreen;
        private double _dragStartCenterX;
        private double _dragStartBottomY;

        private readonly PetSettings _settings;

#if PMV2_PILOT
        public MainWindow() : this(Pilot.PilotApplication.CreateSettings()) { }
#else
        public MainWindow() : this(new PetSettingsStore()) { }
#endif

        public MainWindow(PetSettings settings) : this(settings, true, null, null) { }

        public MainWindow(PetSettingsStore store) : this(store.Load(), store) { }

        internal MainWindow(PetSettingsLoadResult startup, PetSettingsStore store)
            : this(startup.Settings, startup.Topmost, store, startup.Message, startup.PetHeightDip) { }

        private MainWindow(PetSettings settings, bool topmost, PetSettingsStore? store, string? startupMessage, double? startupHeightDip = null)
        {
#if PMV2_PILOT
            _pilotHeightForSave = startupHeightDip;
            if (startupHeightDip is {} height) _petHeight = height;
#endif
            _settingsStore = store;
            _settingsStartupMessage = startupMessage;
            SettingsPersistenceStatus = startupMessage;
            _settings = new PetSettings();
            _settings.ApplyFrom(settings);
            _restSession = new RestSession(() => _settings.Rest);
            _sleepSession = new SleepSession(() => _settings.Sleep);
            _clickResolver = new MultiClickResolver(() => _settings.Interaction);
            InitializeComponent();
            Topmost = topmost;

            foreach (var path in new[] { BaseIdlePath, FallbackIdlePath })
            {
                try
                {
                    _gifPlayer = new GifFramePlayer(PetImage, path);
                    _effectiveBaseIdlePath = path;
                    break;
                }
                catch (Exception exception) when (exception is System.IO.IOException or System.IO.InvalidDataException or FormatException or
                    NotSupportedException or InvalidOperationException or ArgumentException or COMException)
                {
                    // Missing or unsupported local Base Idle uses the next existing fallback.
                }
            }
            if (_gifPlayer != null)
            {
                _gifPlayer.Completed += OnGifCompleted;
                _imageAspect = GifLayoutAspect;
            }
            else
            {
                var petBitmap = PetImageLoader.LoadFallback(FallbackPngPath);
                PetImage.Source = petBitmap;
                _imageAspect = petBitmap.PixelWidth / (double)petBitmap.PixelHeight;
            }
            PetImage.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(),
                    new RotateTransform(),
                    new TranslateTransform(),
                    new ScaleTransform(),
                    new TranslateTransform()
                }
            };
            if (_gifPlayer != null)
            {
                _gifPlayer.ClipChanged += OnGifClipChanged;
            }

            _availableMovements = MovementDefinitions.Available(_gifPlayer);
            InitializeClickInteractions();
            _movementAvailable = _availableMovements.Length > 0;
            _randomIdleScheduler = new RandomIdleScheduler(RandomIdleActions.Available(_gifPlayer),
                () => IsVisible && IsNormalIdle, PlayRandomIdleAction,
                () => _movementAvailable && MovementDefinitions.CanChoose(_availableMovements, _settings.Movement), BeginAutonomousMovement,
                () => IsVisible && IsNormalBaseIdle);
            _randomIdleScheduler.Configure(
                () => (_settings.Normal.RandomIdleMinDelay, _settings.Normal.RandomIdleMaxDelay),
                () => _settings.Normal.RandomIdleEnabled, () => _settings.Movement.SelectionWeight);
            InitializeRest();
            InitializeSleep();
            PetSurface.ContextMenu = BuildContextMenu();
            Loaded += OnLoaded;
            Closed += OnClosed;
            PetSurface.MouseLeftButtonDown += OnPetMouseLeftButtonDown;
            PetSurface.MouseMove += OnPetMouseMove;
            PetSurface.MouseLeftButtonUp += OnPetMouseLeftButtonUp;
            MouseWheel += OnMouseWheel;
            InitializeClickDiagnostics();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
#if PMV2_PILOT
            if (_pilotLog != null) { LoadPmv2Pilot(); return; }
#endif
            _bubbleWindow = new SpeechBubbleWindow
            {
                Owner = this,
                Topmost = Topmost
            };

            var workArea = SystemParameters.WorkArea;
            var baseWidth = _petHeight * _imageAspect;
            _anchorCenterX = workArea.Right - baseWidth / 2 - 92;
            _anchorBottomY = workArea.Bottom - 48;
            ApplyPetLayout();
            _gifPlayer?.Resume();
            StartMotionTimer();
            StartAmbientSpeechTimer();
            StartRestInactivity();
            _randomIdleScheduler.Update();
            if (_settingsStartupMessage != null)
                ShowSettingsNotice("配置未能加载，已用默认值。\n详情请打开工作台。", TimeSpan.FromSeconds(8));
        }

        private void OnClosed(object? sender, EventArgs e)
        {
#if PMV2_PILOT
            ClosePmv2Pilot();
#endif
            _workbenchWindow?.Close();
            InterruptRest();
            CancelClickInteraction(restoreRocketPosition: false);
            _clickResolverTimer.Tick -= OnClickResolverTick;
            _autonomousMovement = null;
            _movementClock.Reset();
            _motionTimer?.Stop();
            _animationTimer?.Stop();
            _ambientSpeechTimer?.Stop();
            _delayedClickSpeechTimer?.Stop();
            _randomIdleScheduler.Dispose();
            _restAmbientScheduler.Dispose();
            _restTimer.Stop();
            _restTimer.Tick -= OnRestDeadline;
            _sleepTimer.Stop();
            _sleepTimer.Tick -= OnSleepDeadline;
            _sleepAmbientScheduler.Dispose();
            if (_gifPlayer != null)
            {
                _gifPlayer.Completed -= OnGifCompleted;
                _gifPlayer.ClipChanged -= OnGifClipChanged;
            }
            _gifPlayer?.Dispose();
            _bubbleWindow?.Close();
            TraceClick("Session.Close");
            StopClickDiagnostics();
        }

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            menu.Opened += (_, _) =>
            {
                _contextMenuOpen = true;
                RegisterRestInteraction();
                TraceClick("Transition", "Menu.Open");
                if (_ambientGifActive || _clickResolver.IsPending || _clickReactionActive || _rocketSession != null || _rotationSession != null || _angrySession != null)
                    RestoreIdleGif();
                CancelClickBubble();
                _bubbleWindow?.Hide();
                StopAutonomousMovement();
                _randomIdleScheduler.Update();
            };
            menu.Closed += (_, _) =>
            {
                _contextMenuOpen = false;
                UpdateRestState();
                _randomIdleScheduler.Update();
            };
            var sizeMenu = new MenuItem { Header = "调整大小" };
            sizeMenu.Items.Add(CreateSizeItem("小", 170));
            sizeMenu.Items.Add(CreateSizeItem("中", 260));
            sizeMenu.Items.Add(CreateSizeItem("大", 360));
            sizeMenu.Items.Add(CreateSizeItem("特大", 470));

            var topmostItem = new MenuItem
            {
                Header = "始终置顶",
                IsCheckable = true,
                IsChecked = Topmost
            };
            topmostItem.Click += (_, _) =>
            {
                ApplyTopmost(topmostItem.IsChecked);
                var result = SaveSettingsSnapshot();
                if (!result.Success) ShowSettingsNotice("置顶已生效，但未保存。\n下次启动可能恢复。", TimeSpan.FromSeconds(8));
            };
            menu.Opened += (_, _) => topmostItem.IsChecked = Topmost;

            var workbenchItem = new MenuItem { Header = "打开工作台..." };
            workbenchItem.Click += (_, _) => OpenWorkbench();

            var exitItem = new MenuItem { Header = "退出程序" };
            exitItem.Click += (_, _) => Close();

            menu.Items.Add(sizeMenu);
            menu.Items.Add(topmostItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(workbenchItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);
            return menu;
        }

        private MenuItem CreateSizeItem(string text, double height)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => SetPetHeight(height);
            return item;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
#if PMV2_PILOT
            if (_pilotLog != null) { PilotWheelStep(e.Delta); e.Handled = true; return; }
#endif
            SetPetHeight(_petHeight * (e.Delta > 0 ? 1.08 : 0.92));
            e.Handled = true;
        }

        private void SetPetHeight(double height)
        {
#if PMV2_PILOT
            if (_pilotLog != null) { PilotSetPetHeight(height, false); return; }
#endif
            SetPetHeightCore(height);
        }

        private void SetPetHeightCore(double height)
        {
#if PMV2_PILOT
            if (_pilotLog != null) PilotMotionScaleBegin();
#endif
            TraceClick("Transition", "SetPetHeight");
            if (_clickResolver.IsPending || _clickReactionActive || _rocketSession != null || _rotationSession != null || _angrySession != null)
                RestoreIdleGif();
            StopAutonomousMovement();
            _petHeight = Math.Clamp(height, MinPetHeight, MaxPetHeight);
            ApplyPetLayout();
#if PMV2_PILOT
            if (_pilotLog != null) PilotMotionScaleEnd();
#endif
            _randomIdleScheduler.Update();
        }

        private void OnPetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
#if PMV2_PILOT
            if (_pilotLog != null) { PilotMouseDown(e); return; }
#endif
            TraceMouse("Handler.MouseDown", e);
            TraceGestureDown();
            if (_landingActive)
            {
                TraceClick("Gesture.Skip", "MouseDown.Landing");
                e.Handled = true;
                return;
            }
            _leftPressed = true;
            StopAutonomousMovement();
            _suppressClickOnRelease = _attachmentMode == AttachmentMode.Falling || _rocketSession != null || _angrySession?.Stage == AngryStage.Angry1;
            if (!HasSleepOwnership && !_suppressClickOnRelease && _rocketSession == null && _rotationSession == null && _angrySession == null)
            {
                _gifPlayer?.Pause();
            }
            _isDragging = false;
            _dragGifActive = false;
            _mouseDownScreen = GetCursorScreenDip();
            _dragGifFacingRight = true;
            _lastDragScreen = _mouseDownScreen;
            _lastDragMotionAt = DateTimeOffset.UtcNow;
            _dragStartedAt = _lastDragMotionAt;
            _dragPoseDirection = 1;
            _dragVelocityX = 0;
            _dragVelocityY = 0;
            _dragStartCenterX = _anchorCenterX;
            _dragStartBottomY = _anchorBottomY;
            PetSurface.CaptureMouse();
            _randomIdleScheduler.Update();
            TraceMouse("Handler.MouseDown.End", e);
            e.Handled = true;
        }

        private void OnPetMouseMove(object sender, MouseEventArgs e)
        {
#if PMV2_PILOT
            if (_pilotLog != null) { PilotMouseMove(e); return; }
#endif
            TraceMouse("Handler.MouseMove", e);
            if (_landingActive || !_leftPressed || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var current = GetCursorScreenDip();
            var delta = current - _mouseDownScreen;
            TraceClick("Gesture.Move", detail: new { deltaX = delta.X, deltaY = delta.Y, thresholdDip = _settings.Interaction.DragThresholdDip });
            if (!_isDragging && ExceedsDragThreshold(delta))
            {
                BeginDragging(current, delta);
            }

            if (!_isDragging)
            {
                return;
            }

            UpdateDragVelocity(current);
            _anchorCenterX = _dragStartCenterX + delta.X;
            _anchorBottomY = _dragStartBottomY + delta.Y;
            ApplyPetLayout();
        }

        private bool ExceedsDragThreshold(Vector delta) =>
            Math.Abs(delta.X) + Math.Abs(delta.Y) > _settings.Interaction.DragThresholdDip;

        private void BeginDragging(Point current, Vector delta)
        {
            if (_landingActive)
            {
                return;
            }
            _isDragging = true;
            RegisterRestInteraction();
            if (_rocketSession != null)
            {
                // Rocket moved since MouseDown: preserve the current grab position.
                _dragStartCenterX = _anchorCenterX - delta.X;
                _dragStartBottomY = _anchorBottomY - delta.Y;
            }
            CancelClickInteraction(restoreRocketPosition: false);
            StopAutonomousMovement();
            ResetIdleWithDiagnostics();
            _ambientGifActive = false;
            _clickReactionActive = false;
            ClearAttachment();
            _delayedClickSpeechTimer?.Stop();
            _animationTimer?.Stop();
            ResetAnimationState();
            _lastDragScreen = current;
            _lastDragMotionAt = DateTimeOffset.UtcNow;
            _dragStartedAt = _lastDragMotionAt;
            _dragPoseDirection = delta.X < 0 ? -1 : 1;
            _dragGifActive = _gifPlayer?.TrySwitchTo(DraggedGifPath) == true;
            if (_dragGifActive)
            {
                _dragFacingExtremeX = _mouseDownScreen.X;
                UpdateDraggedFacing(current.X);
                _gifPlayer?.SetDragEntryFirstFrameDuration(DragEntryFirstFrameDuration);
                _gifPlayer?.Resume();
            }
        }

        private void OnPetMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
#if PMV2_PILOT
            if (_pilotLog != null) { PilotMouseUp(e); return; }
#endif
            TraceMouse("Handler.MouseUp", e);
            TraceGestureUp();
            if (!_leftPressed)
            {
                TraceClick("Gesture.Skip", "MouseUp.NoAcceptedDown");
                return;
            }

            if (PetSurface.IsMouseCaptured)
            {
                PetSurface.ReleaseMouseCapture();
            }

            var wasDragging = _isDragging;
            // Detect where the user saw the dragged character before restoring Idle.
            var releaseBounds = wasDragging ? GetAttachmentDetectionBounds() : Rect.Empty;
            _leftPressed = false;
            _isDragging = false;
            if (wasDragging)
            {
                _dragGifActive = false;
                _dragGifFacingRight = true;
                _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);
                ApplyPetLayout();
            }

            if (!wasDragging)
            {
                if (!_suppressClickOnRelease && RegisterSleepClick())
                {
                    // Sleep/Wake consumes the complete gesture before Normal Click tiers.
                }
                else if (_restSession.IsResting && !_suppressClickOnRelease)
                {
                    RegisterRestInteraction(); // Consume wake gesture before normal tiers.
                }
                else if (_suppressClickOnRelease)
                {
                    TraceClick("Gesture.Skip", "MouseUp.Suppressed");
                    SyncGifPlayback();
                }
                else if (_clickReactionActive || _rocketSession != null || _rotationSession != null || _angrySession != null ||
                         _clickResolver.IsPending || IsNormalIdle || _ambientGifActive)
                {
                    RegisterClickGesture();
                }
                else if (_attachmentMode != AttachmentMode.Falling && !_landingActive &&
                         !(_animationTimer?.IsEnabled ?? false))
                {
                    TraceClick("Gesture.Skip", "MouseUp.BubbleOnly");
                    RegisterRestInteraction();
                    ScheduleDelayedClickBubble(TimeSpan.FromMilliseconds(250));
                }
                else TraceClick("Gesture.Skip", "MouseUp.ActionGuard");
            }
            else
            {
                TraceClick("Gesture.Skip", "MouseUp.Dragged");
                _dragVelocityX = 0;
                _dragVelocityY = 0;
                if (!TryAttachToNearbyWindow(releaseBounds))
                {
                    PlayReleaseFloat();
                }
            }

            _suppressClickOnRelease = false;
            UpdateRestState();
            _randomIdleScheduler.Update();
            e.Handled = true;
            TraceMouse("Handler.MouseUp.End", e);
        }

        private void PlayClickReaction()
        {
            if (_restSession.IsResting || HasSleepOwnership) return;
            if (_angrySession != null) return;
            StopAutonomousMovement();
            ResetIdleWithDiagnostics();
            if (_ambientGifActive)
            {
                _ambientGifActive = false;
                _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);
            }
            var definition = ClickDefinitions.Choose(_availableSingleClicks, _clickRandom);
            if (definition != null && _gifPlayer?.TrySwitchToLoops(definition.ResourcePath, definition.RepeatCount) == true)
            {
                _clickReactionActive = true;
                ApplyPetLayout();
            }

            _gifPlayer?.Resume();
            ScheduleDelayedClickBubble(TimeSpan.FromMilliseconds(250));
            _randomIdleScheduler.Update();
        }

        private void PlayRandomIdleAction(string path)
        {
            if (!_settings.Normal.RandomIdleEnabled || !IsVisible || !IsNormalIdle || !RandomIdleActions.Contains(path)) return;
            ResetIdleWithDiagnostics();
            _ambientGifActive = _gifPlayer?.TrySwitchToForDuration(path, RandomIdleActions.DurationFor(path)) == true;
            if (_ambientGifActive)
            {
                ApplyPetMotion(DateTimeOffset.UtcNow);
                _gifPlayer?.Resume();
            }
            else _randomIdleScheduler.Update();
        }

        private void OnGifCompleted(object? sender, EventArgs e)
        {
#if PMV2_PILOT
            if (_pilotLog != null) { PilotClickCompleted(sender, e); return; }
#endif
            if (e is GifPlaybackCompletedEventArgs completed &&
                (!ReferenceEquals(sender, _gifPlayer) || completed.Generation != _gifPlayer?.PlaybackGeneration)) return;
            if (HasSleepOwnership)
            {
                CompleteSleepPlayback(sender, e);
                return;
            }
            if (_restAmbientActive)
            {
                CompleteRestAmbient(sender, e);
                return;
            }
            if (_angrySession != null)
            {
                if (!_isDragging && _attachmentMode != AttachmentMode.Falling && !_contextMenuOpen && IsVisible)
                    CompleteAngryPlayback(sender, e);
                return;
            }
            if (_rotationSession?.ShouldBecomeAngry == true && _gifPlayer?.IsComplete == true &&
                !_isDragging && _attachmentMode != AttachmentMode.Falling && !_contextMenuOpen && IsVisible)
            {
                BeginCaper();
                return;
            }
            // TrySwitchTo cancels the old cursor/timer on the same Dispatcher.
            // Only the current one-shot state may restore Idle via Completed.
            if (_isDragging || _attachmentMode == AttachmentMode.Falling ||
                (!_landingActive && !_clickReactionActive && !_ambientGifActive && _rotationSession == null))
            {
                return;
            }

            RestoreIdleGif();
        }

        private void OnGifClipChanged(object? sender, EventArgs e)
        {
#if PMV2_PILOT
            if (_pilotLog != null) { PilotRefreshClickImage(); return; }
#endif
            ApplyPetLayout();
        }

        private void RestoreIdleGif()
        {
            InterruptSleep();
            CancelRestAmbient();
            CancelClickInteraction();
            _autonomousMovement = null;
            _movementClock.Reset();
            ResetIdleWithDiagnostics();
            _ambientGifActive = false;
            _landingActive = false;
            _currentFallAnimation = null;
            _clickReactionActive = false;
            _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);
            ApplyPetLayout();
            _gifPlayer?.Resume();
            UpdateRestState();
            _randomIdleScheduler.Update();
        }

        private void UpdateDragVelocity(Point current)
        {
            if (_dragGifActive)
            {
                UpdateDraggedFacing(current.X);
            }

            var now = DateTimeOffset.UtcNow;
            var elapsed = Math.Max(0.016, (now - _lastDragMotionAt).TotalSeconds);
            var instantX = (current.X - _lastDragScreen.X) / elapsed;
            var instantY = (current.Y - _lastDragScreen.Y) / elapsed;

            _dragVelocityX = _dragVelocityX * 0.62 + instantX * 0.38;
            _dragVelocityY = _dragVelocityY * 0.62 + instantY * 0.38;
            if (Math.Abs(instantX) > 45)
            {
                _dragPoseDirection = instantX < 0 ? -1 : 1;
            }

            _lastDragScreen = current;
            _lastDragMotionAt = now;
        }

        private void UpdateDraggedFacing(double currentX)
        {
            if (_dragGifFacingRight)
            {
                if (currentX <= _dragFacingExtremeX - DragFacingThreshold)
                {
                    _dragGifFacingRight = false;
                    _dragFacingExtremeX = currentX;
                }
                else
                {
                    _dragFacingExtremeX = Math.Max(_dragFacingExtremeX, currentX);
                }
            }
            else if (currentX >= _dragFacingExtremeX + DragFacingThreshold)
            {
                _dragGifFacingRight = true;
                _dragFacingExtremeX = currentX;
            }
            else
            {
                _dragFacingExtremeX = Math.Min(_dragFacingExtremeX, currentX);
            }
        }

        private void PlayLiftInteraction()
        {
            ScheduleDelayedClickBubble(TimeSpan.FromMilliseconds(1380));
            RunAnimation(TimeSpan.FromMilliseconds(3200), p =>
            {
                const double peakHeight = 210;
                double height;

                if (p < 0.36)
                {
                    var t = p / 0.36;
                    height = peakHeight * EaseOutCubic(t);
                }
                else if (p < 0.50)
                {
                    var t = (p - 0.36) / 0.14;
                    height = peakHeight + Math.Sin(t * Math.PI * 2) * 5;
                }
                else
                {
                    var t = (p - 0.50) / 0.50;
                    height = peakHeight * (1 - EaseInOutSine(t));
                }

                var highPose = Math.Clamp(height / peakHeight, 0, 1);
                var landing = p > 0.88 ? Math.Sin((p - 0.88) / 0.12 * Math.PI) : 0;
                _animScaleX = 1 + 0.018 * highPose + 0.006 * landing;
                _animScaleY = 1 + 0.014 * highPose - 0.003 * landing;
                _animOffsetX = Math.Sin(p * Math.PI * 1.35) * 9 * highPose;
                _animOffsetY = -height;
                _animRotate = Math.Sin(p * Math.PI * 1.7) * 1.5 * highPose;
            }, useEasing: false, keepDelayedClickSpeech: true);
        }

        private void ScheduleDelayedClickBubble(TimeSpan delay)
        {
            CancelClickBubble();
            var generation = _clickBubbleGeneration;
            var timer = new DispatcherTimer
            {
                Interval = delay
            };
            _delayedClickSpeechTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (generation == _clickBubbleGeneration && IsVisible && !_isDragging &&
                    !_contextMenuOpen && _rocketSession == null && _rotationSession == null && _angrySession == null)
                    ShowClickBubble();
            };
            timer.Start();
        }

        private void PlayReleaseFloat()
        {
            RunAnimation(TimeSpan.FromMilliseconds(460), p =>
            {
                var lift = Math.Sin(Math.PI * p);
                var fade = 1 - p;
                _animScaleX = 1 + 0.01 * lift;
                _animScaleY = 1 + 0.01 * lift;
                _animOffsetX = Math.Sin(Math.PI * 3 * p) * 5 * fade;
                _animOffsetY = -12 * lift;
                _animRotate = Math.Sin(Math.PI * 3 * p) * 1.8 * fade;
            });
        }

        private void RunAnimation(
            TimeSpan duration,
            Action<double> frame,
            bool useEasing = true,
            bool keepDelayedClickSpeech = false)
        {
            CancelClickInteraction(cancelBubble: !keepDelayedClickSpeech);
            ResetIdleWithDiagnostics();
            if (!keepDelayedClickSpeech)
            {
                CancelClickBubble();
            }

            _animationTimer?.Stop();
            var started = DateTimeOffset.UtcNow;
            _animationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _animationTimer.Tick += (_, _) =>
            {
                var progress = (DateTimeOffset.UtcNow - started).TotalMilliseconds / duration.TotalMilliseconds;
                if (progress >= 1)
                {
                    _animationTimer?.Stop();
                    ResetAnimationState();
                    ApplyPetLayout();
                    _randomIdleScheduler.Update();
                    return;
                }

                var boundedProgress = Math.Clamp(progress, 0, 1);
                frame(useEasing ? EaseInOutSine(boundedProgress) : boundedProgress);
                ApplyPetLayout();
            };
            _animationTimer.Start();
        }

        private void ResetAnimationState()
        {
            _animScaleX = 1;
            _animScaleY = 1;
            _animOffsetX = 0;
            _animOffsetY = 0;
            _animRotate = 0;
        }

        private void ShowClickBubble()
        {
            ShowVoiceBubble(TimeSpan.FromMilliseconds(2100));
        }

        private void ShowVoiceBubble(TimeSpan duration)
        {
            ShowVoiceBubble(duration, _voiceLines);
        }

        private void ShowVoiceBubble(TimeSpan duration, string[] lines)
        {
            if (_bubbleWindow == null)
            {
                return;
            }

            PositionBubble();
            var text = lines[_random.Next(lines.Length)];
            _bubbleWindow.ShowMessage(text, duration);
            PositionBubble();
        }

        private void StartAmbientSpeechTimer()
        {
            _ambientSpeechTimer = new DispatcherTimer();
            _ambientSpeechTimer.Tick += (_, _) =>
            {
                _ambientSpeechTimer.Stop();
                ShowAmbientBubble();
                ScheduleNextAmbientSpeech();
            };
            ScheduleNextAmbientSpeech();
        }

        private void ScheduleNextAmbientSpeech()
        {
            if (_ambientSpeechTimer == null)
            {
                return;
            }

            _ambientSpeechTimer.Interval = TimeSpan.FromSeconds(_random.Next(24, 56));
            _ambientSpeechTimer.Start();
        }

        private void ShowAmbientBubble()
        {
            if (_bubbleWindow == null || !IsVisible || _isDragging || _leftPressed ||
                _restSession.IsResting || HasSleepOwnership || _clickReactionActive || _ambientGifActive || _contextMenuOpen || _autonomousMovement != null ||
                _clickResolver.IsPending || _rocketSession != null || _rotationSession != null || _angrySession != null)
            {
                return;
            }

            ShowVoiceBubble(TimeSpan.FromMilliseconds(2600));
        }

        private void ApplyPetLayout()
        {
#if PMV2_PILOT
            if (_pilotLog != null && _pilotReady)
            {
                // IsVisibleChanged may restore idle while the HWND/source is being
                // disposed. A dead source cannot admit layout or native DPI reads.
                if (_pilotClosing || _pilotStopped || _pilotSource is null or { IsDisposed: true } ||
                    !Pilot.PilotNative.IsWindow(PilotHwnd) || Pilot.PilotNative.GetDpiForWindow(PilotHwnd) == 0) return;
                using (PilotLayoutScope())
                {
                    // A user scale keeps the arranged foot contact, not the frozen
                    // top-left. The contact is the pre-arrange anchor of the SAME
                    // transaction; every other ApplyPetLayout keeps the old rule.
                    var scaleContact = PilotScaleAnchorForLayout();
                    PilotVisualArrange();
                    if (!PilotLayoutSizeIsValid(PetSurface.Width, PetSurface.Height) ||
                        !PilotLayoutSizeIsValid(Math.Round(PetSurface.Width * _pilotScale.X), Math.Round(PetSurface.Height * _pilotScale.Y)))
                    { PilotStop("Layout dimensions must be finite and positive before native sizing."); return; }
                    Width = PetSurface.Width; Height = PetSurface.Height;
                    PilotApplyLayout(scaleContact);
                }
                return;
            }
            // Includes Width/Height setters that may synchronously enter native WPF sizing.
            using var pilotLayoutScope = PilotLayoutScope();
#endif
            var petWidth = _petHeight * _imageAspect;
            var petHeight = _petHeight;
            var padding = GetMotionPadding();
#if PMV2_PILOT
            if (_crossScreen) { petWidth *= _crossEffectiveScale; petHeight *= _crossEffectiveScale; padding *= _crossEffectiveScale; }
#endif
            var sourceUnit = petHeight / AnimationVisualProfiles.CanonicalCanvasHeight;
            var imageWidth = _gifPlayer == null ? petWidth : _gifPlayer.PixelWidth * sourceUnit;
            var imageHeight = _gifPlayer == null ? petHeight : _gifPlayer.PixelHeight * sourceUnit;

            PetSurface.Width = petWidth + padding * 2;
            PetSurface.Height = petHeight + padding * 2;
            PetImage.Width = imageWidth;
            PetImage.Height = imageHeight;
            Canvas.SetLeft(PetImage, padding + (petWidth - imageWidth) / 2);
            Canvas.SetTop(PetImage, padding + petHeight - imageHeight);
            ApplyAnimationVisualProfile();

            Width = PetSurface.Width;
            Height = PetSurface.Height;
#if PMV2_PILOT
            if (_pilotLog != null) { PilotApplyLayout(); return; }
#endif
            Left = _anchorCenterX - petWidth / 2 + _animOffsetX - padding;
            Top = _anchorBottomY - petHeight + _animOffsetY - padding;

            ApplyPetMotion(DateTimeOffset.UtcNow);
            AlignAttachedPet(petWidth, petHeight, padding);
            PositionBubble();
        }

        private void ApplyAnimationVisualProfile()
        {
            var group = (TransformGroup)PetImage.RenderTransform;
            var scale = (ScaleTransform)group.Children[3];
            var offset = (TranslateTransform)group.Children[4];
            var profile = _gifPlayer == null
                ? new AnimationVisualProfile(1, 0, 0)
                : AnimationVisualProfiles.For(_gifPlayer.ResourcePath);
            scale.ScaleX = profile.Scale;
            scale.ScaleY = profile.Scale;
            // RenderTransformOrigin belongs to the existing motion transforms.
            // Shift only the profile's pivot to the bottom-center of the source canvas.
            scale.CenterY = PetImage.Height * (1 - PetImage.RenderTransformOrigin.Y);
            var profileFacing = _autonomousMovement?.IsMirrored == true ? -1 : 1;
            offset.X = _petHeight * profile.OffsetXFactor * profileFacing;
            offset.Y = _petHeight * profile.OffsetYFactor;
#if PMV2_PILOT
            if (_crossScreen) { offset.X *= _crossEffectiveScale; offset.Y *= _crossEffectiveScale; }
#endif
        }

        private void StartMotionTimer()
        {
            _idleStarted = DateTimeOffset.UtcNow;
            _lastMotionFrameAt = _idleStarted;
            _motionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _motionTimer.Tick += (_, _) => UpdateMotion();
            _motionTimer.Start();
        }

        private void UpdateMotion()
        {
            if (!IsVisible)
            {
                StopAutonomousMovement();
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var elapsed = Math.Max(0.001, (now - _lastMotionFrameAt).TotalSeconds);
            _lastMotionFrameAt = now;

            AdvanceClickInteractions();

            UpdateAttachedOrFalling(elapsed);
            if (_autonomousMovement != null)
            {
                var movementElapsed = _movementClock.Elapsed.TotalSeconds;
                _movementClock.Restart();
                AdvanceAutonomousMovement(movementElapsed);
            }
            UpdateLookTarget();
            UpdateIdleMotion(now, elapsed);
            ApplyPetLayout();
            SyncGifPlayback();
            UpdateRestState();
            _randomIdleScheduler.Update();
        }

        private void SyncGifPlayback()
        {
            if (HasSleepOwnership || _rocketSession != null || _rotationSession != null || _angrySession != null)
            {
                _gifPlayer?.Resume();
                return;
            }
            if (_isDragging)
            {
                if (_dragGifActive)
                {
                    _gifPlayer?.Resume();
                }
                else
                {
                    _gifPlayer?.Pause();
                }
                return;
            }

            if (_attachmentMode == AttachmentMode.Falling)
            {
                if (_currentFallAnimation != null)
                {
                    _gifPlayer?.Resume();
                }
                else
                {
                    _gifPlayer?.Pause();
                }
                return;
            }

            if (_landingActive)
            {
                _gifPlayer?.Resume();
                return;
            }

            if (_attachmentMode == AttachmentMode.FloatingOnTop && _gifPlayer?.ResourcePath == TopAttachGifPath)
            {
                // Existing canonical standing frame supplies real feet for Top contact.
                _gifPlayer.Pause();
                return;
            }

            if (_clickReactionActive)
            {
                if (_leftPressed)
                {
                    _gifPlayer?.Pause();
                }
                else
                {
                    _gifPlayer?.Resume();
                }
                return;
            }

            if (!_leftPressed &&
                _attachmentMode != AttachmentMode.Falling &&
                !(_animationTimer?.IsEnabled ?? false))
            {
                _gifPlayer?.Resume();
            }
            else
            {
                _gifPlayer?.Pause();
            }
        }

        private bool IsNormalIdle => IsNormalBaseIdle && !_leftPressed && !_contextMenuOpen;

        // Menu and an unclassified press block playback without invalidating the Normal deadline.
        private bool IsNormalBaseIdle =>
#if PMV2_PILOT
            _pilotLog == null &&
#endif
            !HasSleepOwnership && !_restSession.IsResting && !_isDragging && !_clickReactionActive && !_landingActive &&
            !_clickResolver.IsPending && _rocketSession == null && _rotationSession == null && _angrySession == null &&
            !_ambientGifActive && _autonomousMovement == null &&
            _attachmentMode == AttachmentMode.None &&
            !(_animationTimer?.IsEnabled ?? false) &&
            (_gifPlayer == null || _gifPlayer.ResourcePath == _effectiveBaseIdlePath);

        private void UpdateIdleMotion(DateTimeOffset now, double elapsedSeconds)
        {
            if (_gifPlayer != null || !IsNormalIdle)
            {
                _breathingMotion = default;
                _mouseAwareMotion = default;
                _ambientMotion = default;
                _ambientAmount = 0;
                _ambientAction = AmbientAction.None;
                _nextAmbientDecisionAt = DateTimeOffset.MinValue;
                return;
            }

            if (_ambientAction == AmbientAction.None && _nextAmbientDecisionAt == DateTimeOffset.MinValue)
            {
                ScheduleNextAmbientDecision(now);
            }

            if (_ambientAction == AmbientAction.None && now >= _nextAmbientDecisionAt)
            {
                var roll = _random.NextDouble();
                _ambientAction = roll < 0.55 ? AmbientAction.None
                    : roll < 0.75 ? AmbientAction.SlowTilt
                    : roll < 0.90 ? AmbientAction.DrowsyDip
                    : AmbientAction.TinyLook;

                if (_ambientAction == AmbientAction.None)
                {
                    ScheduleNextAmbientDecision(now);
                }
                else
                {
                    _ambientStartedAt = now;
                    _ambientDirection = _random.Next(2) == 0 ? -1 : 1;
                    _nextAmbientDecisionAt = DateTimeOffset.MinValue;
                }
            }

            UpdateAmbientMotion(now);

            var seconds = (now - _idleStarted).TotalSeconds;
            var breath = Math.Sin(seconds * 2 * Math.PI / 6.2);
            var breathStrength = 1 - _ambientAmount * 0.65;
            _breathingMotion = new MotionLayer(
                Math.Sin(seconds * 2 * Math.PI / 10.7) * 1.2 * breathStrength,
                -breath * 2.5 * breathStrength,
                -breath * 0.004 * breathStrength,
                breath * 0.008 * breathStrength,
                Math.Sin(seconds * 2 * Math.PI / 8.9 + 0.7) * 0.35 * breathStrength);

            var targetMouse = default(MotionLayer);
            if (_gifPlayer == null && _ambientAction == AmbientAction.None)
            {
                var cursor = GetCursorScreenDip();
                var bounds = GetVisiblePetBounds();
                var centerX = bounds.Left + bounds.Width * 0.5;
                var centerY = bounds.Top + bounds.Height * 0.45;
                var dx = cursor.X - centerX;
                var dy = cursor.Y - centerY;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < 220)
                {
                    var strength = 1 - distance / 220;
                    var directionScale = strength / Math.Max(distance, 24);
                    targetMouse = new MotionLayer(
                        dx * directionScale * 2.5,
                        dy * directionScale * 1.2,
                        0, 0,
                        dx * directionScale * 0.45);
                }
            }

            // Equivalent to 0.08 per 30 ms frame, including after a delayed UI tick.
            var blend = 1 - Math.Pow(0.92, Math.Clamp(elapsedSeconds, 0, 0.25) / 0.03);
            _mouseAwareMotion = new MotionLayer(
                _mouseAwareMotion.OffsetX + (targetMouse.OffsetX - _mouseAwareMotion.OffsetX) * blend,
                _mouseAwareMotion.OffsetY + (targetMouse.OffsetY - _mouseAwareMotion.OffsetY) * blend,
                0, 0,
                _mouseAwareMotion.Rotation + (targetMouse.Rotation - _mouseAwareMotion.Rotation) * blend);
        }

        private void ScheduleNextAmbientDecision(DateTimeOffset now)
        {
            _nextAmbientDecisionAt = now.AddSeconds(18 + _random.NextDouble() * 14);
        }

        private void UpdateAmbientMotion(DateTimeOffset now)
        {
            if (_ambientAction == AmbientAction.None)
            {
                _ambientMotion = default;
                _ambientAmount = 0;
                return;
            }

            var elapsed = (now - _ambientStartedAt).TotalSeconds;
            var (enter, hold, exit) = _ambientAction switch
            {
                AmbientAction.SlowTilt => (0.55, 0.25, 1.0),
                AmbientAction.DrowsyDip => (0.9, 0.4, 0.9),
                _ => (0.5, 0.2, 0.7)
            };
            var duration = enter + hold + exit;
            if (elapsed >= duration)
            {
                _ambientMotion = default;
                _ambientAmount = 0;
                _ambientAction = AmbientAction.None;
                ScheduleNextAmbientDecision(now);
                return;
            }

            var amount = elapsed < enter ? EaseInOutSine(elapsed / enter)
                : elapsed < enter + hold ? 1
                : 1 - EaseInOutSine((elapsed - enter - hold) / exit);
            _ambientAmount = amount;
            _ambientMotion = _ambientAction switch
            {
                AmbientAction.SlowTilt => new MotionLayer(0, 0, 0, 0, _ambientDirection * 1.1 * amount),
                AmbientAction.DrowsyDip => new MotionLayer(0, 4 * amount, 0.006 * amount, -0.015 * amount, 0),
                _ => new MotionLayer(_ambientDirection * 3 * amount, 0, 0, 0, _ambientDirection * 0.6 * amount)
            };
        }

        private void UpdateLookTarget()
        {
            double targetX;
            double targetY;
            if (_isDragging)
            {
                targetX = Math.Clamp(_dragVelocityX / 900, -1, 1);
                targetY = Math.Clamp(_dragVelocityY / 900, -1, 1);
            }
            else if (_attachmentMode == AttachmentMode.Falling)
            {
                targetX = Math.Sin((DateTimeOffset.UtcNow - _idleStarted).TotalSeconds * 5.5) * 0.7;
                targetY = 0.25;
            }
            else
            {
                var cursor = GetCursorScreenDip();
                var petBounds = GetVisiblePetBounds();
                var bodyCenter = new Point(petBounds.Left + petBounds.Width * 0.5, petBounds.Top + petBounds.Height * 0.45);
                targetX = Math.Clamp((cursor.X - bodyCenter.X) / 360, -1, 1);
                targetY = Math.Clamp((cursor.Y - bodyCenter.Y) / 320, -1, 1);
            }

            _lookX += (targetX - _lookX) * 0.12;
            _lookY += (targetY - _lookY) * 0.12;
        }

        private void ApplyPetMotion(DateTimeOffset now)
        {
            if (PetImage.RenderTransform is not TransformGroup group ||
                group.Children.Count < 3 ||
                group.Children[0] is not ScaleTransform scale ||
                group.Children[1] is not RotateTransform rotate ||
                group.Children[2] is not TranslateTransform translate)
            {
                return;
            }

            var sizeScale = _petHeight / 280;
#if PMV2_PILOT
            // The existing motion transform writer respects the attachment owner.
            // Down cancels that owner before drag takes over this same transform.
            if (_pilotAttachmentTarget != null)
            {
                scale.ScaleX = _pilotAttachmentPoseSide == AttachmentSide.Left ? -1 : 1;
                scale.ScaleY = 1;
                rotate.Angle = 0;
                translate.X = 0;
                translate.Y = 0;
                return;
            }
#endif
            var seconds = (now - _idleStarted).TotalSeconds;
            var floatY = Math.Sin(seconds * 1.35) * 5.8;
            var driftX = Math.Sin(seconds * 0.78) * 3.2;
            var idleTilt = Math.Sin(seconds * 1.08) * 1.4;

            if (_autonomousMovement != null)
            {
                // One centered mirror transform for every movement's native source direction.
                scale.ScaleX = _autonomousMovement.IsMirrored ? -1 : 1;
                scale.ScaleY = 1;
                rotate.Angle = 0;
                translate.X = 0;
                translate.Y = 0;
                return;
            }

            if (_isDragging)
            {
                if (_gifPlayer != null)
                {
                    scale.ScaleX = (_dragGifFacingRight ? 1 : -1) * _animScaleX;
                    scale.ScaleY = _animScaleY;
                    rotate.Angle = 0;
                    translate.X = 0;
                    translate.Y = 0;
                    return;
                }

                var dragSeconds = (now - _dragStartedAt).TotalSeconds;
                var dragX = Math.Clamp(_dragVelocityX / 900, -1, 1);
                var dragY = Math.Clamp(_dragVelocityY / 900, -1, 1);
                var speed = Math.Clamp(Math.Sqrt(_dragVelocityX * _dragVelocityX + _dragVelocityY * _dragVelocityY) / 950, 0, 1);
                var wave = Math.Sin(dragSeconds * 12.0);

                scale.ScaleX = _animScaleX * (1 + speed * 0.012);
                scale.ScaleY = _animScaleY * (1 + speed * 0.008);
                rotate.Angle = Math.Clamp(_dragPoseDirection * (7 + speed * 8) + wave * 5.5 + dragY * 3.0 + _animRotate, -24, 24);
                translate.X = Math.Clamp(_dragPoseDirection * 8 + wave * 4 - dragX * 4, -24, 24) * sizeScale;
                translate.Y = (-20 + Math.Abs(wave) * 3) * sizeScale;
                return;
            }

            if (_currentFallAnimation != null || _landingActive || _rocketSession != null ||
                _rotationSession != null || _clickResolver.IsPending || _angrySession != null)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                rotate.Angle = 0;
                translate.X = 0;
                translate.Y = 0;
                return;
            }

            if (_leftPressed)
            {
                if (_gifPlayer != null)
                {
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                    rotate.Angle = 0;
                    translate.X = 0;
                    translate.Y = 0;
                    return;
                }

                scale.ScaleX = _animScaleX * 1.006;
                scale.ScaleY = _animScaleY * 1.006;
                rotate.Angle = _animRotate;
                translate.X = driftX * 0.35 * sizeScale;
                translate.Y = (-6 + floatY * 0.35) * sizeScale;
                return;
            }

            if (_restSession.IsResting || HasSleepOwnership || _clickReactionActive || _ambientGifActive ||
                (_gifPlayer != null && _gifPlayer.ResourcePath == _effectiveBaseIdlePath &&
                 _attachmentMode == AttachmentMode.None && !(_animationTimer?.IsEnabled ?? false)))
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                rotate.Angle = 0;
                translate.X = 0;
                translate.Y = 0;
                return;
            }

            if (_attachmentMode == AttachmentMode.FloatingOnTop)
            {
                scale.ScaleX = _animScaleX;
                scale.ScaleY = _animScaleY;
                rotate.Angle = idleTilt * 0.55 + _lookX * 1.8 + _animRotate;
                translate.X = (driftX + _lookX * 2.4) * sizeScale;
                translate.Y = (floatY - 4 + _lookY * 1.2) * sizeScale;
                return;
            }

            if (_attachmentMode is AttachmentMode.ClingingLeft or AttachmentMode.ClingingRight)
            {
                var side = _attachmentMode == AttachmentMode.ClingingLeft ? 1 : -1;
                scale.ScaleX = _animScaleX;
                scale.ScaleY = _animScaleY;
                rotate.Angle = side * (7.5 + Math.Sin(seconds * 2.0) * 1.6) + _animRotate;
                translate.X = side * (5.5 + Math.Sin(seconds * 1.3) * 1.5) * sizeScale;
                translate.Y = (floatY * 0.8 + _lookY * 1.4) * sizeScale;
                return;
            }

            if (_attachmentMode == AttachmentMode.Falling)
            {
                scale.ScaleX = _animScaleX;
                scale.ScaleY = _animScaleY;
                rotate.Angle = Math.Sin(seconds * 0.95) * 1.8 + _lookX * 1.2 + _animRotate;
                translate.X = (Math.Sin(seconds * 0.72) * 3.8 + _lookX * 1.4) * sizeScale;
                translate.Y = (Math.Sin(seconds * 1.05) * 4.2 - 8 + _lookY * 1.0) * sizeScale;
                return;
            }

            if (IsNormalIdle)
            {
                scale.ScaleX = _animScaleX * (1 + _breathingMotion.ScaleX + _ambientMotion.ScaleX);
                scale.ScaleY = _animScaleY * (1 + _breathingMotion.ScaleY + _ambientMotion.ScaleY);
                rotate.Angle = _animRotate + _breathingMotion.Rotation + _mouseAwareMotion.Rotation + _ambientMotion.Rotation;
                translate.X = _breathingMotion.OffsetX + _mouseAwareMotion.OffsetX + _ambientMotion.OffsetX;
                translate.Y = _breathingMotion.OffsetY + _mouseAwareMotion.OffsetY + _ambientMotion.OffsetY;
                return;
            }

            scale.ScaleX = _animScaleX * (1 + Math.Sin(seconds * 1.1) * 0.004);
            scale.ScaleY = _animScaleY * (1 + Math.Cos(seconds * 1.1) * 0.004);
            rotate.Angle = idleTilt + _lookX * 3.5 + _lookY * 0.9 + _animRotate;
            translate.X = (driftX + _lookX * 3.8) * sizeScale;
            translate.Y = (floatY + _lookY * 2.2) * sizeScale;
        }

        private bool TryAttachToNearbyWindow(Rect? releaseBounds = null)
        {
            var petBounds = releaseBounds ?? GetAttachmentDetectionBounds();
            if (petBounds.IsEmpty || _gifPlayer == null) return false;
            WindowAttachment? best = null;
            var workArea = SystemParameters.WorkArea;
            var zOrder = 0;
            foreach (var candidate in EnumerateAttachableWindows())
            {
                zOrder++;
                if (!TryGetWindowRectDip(candidate, out var rect)) continue;
                var topVisualHeight = _petHeight * AnimationVisualProfiles.For(TopAttachGifPath).Scale;
                var detected = AttachmentGeometry.Detect(petBounds, rect, workArea, _petHeight, topVisualHeight);
                if (detected == null) continue;
                var match = detected.Value;
                var contactPath = match.Side == AttachmentSide.Top ? _effectiveBaseIdlePath : _gifPlayer.ResourcePath;
                if (!AttachmentContactProfile.HasContact(contactPath, match.Side)) continue;
                var mode = match.Side switch
                {
                    AttachmentSide.Top => AttachmentMode.FloatingOnTop,
                    AttachmentSide.Left => AttachmentMode.ClingingLeft,
                    _ => AttachmentMode.ClingingRight
                };
                best = ChooseBetter(best, new WindowAttachment(candidate, mode,
                    AttachmentGeometry.CandidateScore(match.Side, match.Score, zOrder),
                    match.OffsetX, match.OffsetY));
            }
            if (best == null) return false;
            AttachToWindow(best.Value);
            return true;
        }

        private static WindowAttachment ChooseBetter(WindowAttachment? current, WindowAttachment next)
        {
            return current == null || next.Score < current.Value.Score ? next : current.Value;
        }

        private void AttachToWindow(WindowAttachment attachment)
        {
            CancelClickInteraction();
            ResetIdleWithDiagnostics();
            _ambientGifActive = false;
            if (!TryGetWindowRectDip(attachment.Hwnd, out var rect))
            {
                return;
            }

            _attachedWindow = attachment.Hwnd;
            _attachmentMode = attachment.Mode;
            _attachmentRect = Rect.Empty;
            _attachOffsetX = attachment.OffsetX;
            _attachOffsetY = attachment.OffsetY;
            ResetDescentState();
            _animationTimer?.Stop();
            ResetAnimationState();
            if (attachment.Mode == AttachmentMode.FloatingOnTop)
            {
                // Use the cached effective standing clip, including a rejected/missing base_idle fallback.
                _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);
                _gifPlayer?.Pause();
            }
            FollowAttachedWindow(rect);
            PlayAttachCue(attachment.Mode);
        }

        private void PlayAttachCue(AttachmentMode mode)
        {
            ShowVoiceBubble(TimeSpan.FromMilliseconds(1700), _attachLines);
            RunAnimation(TimeSpan.FromMilliseconds(760), p =>
            {
                var settle = 1 - EaseOutCubic(p);
                var pulse = Math.Sin(Math.PI * p);
                _animScaleX = 1 + 0.020 * pulse;
                _animScaleY = 1 + 0.014 * pulse;

                if (mode == AttachmentMode.FloatingOnTop)
                {
                    _animOffsetX = Math.Sin(Math.PI * 2 * p) * 4 * (1 - p);
                    _animOffsetY = -34 * settle - 7 * pulse;
                    _animRotate = Math.Sin(Math.PI * 2 * p) * 2.2 * (1 - p);
                    return;
                }

                var side = mode == AttachmentMode.ClingingLeft ? 1 : -1;
                _animOffsetX = side * (34 * settle + Math.Sin(Math.PI * 2 * p) * 5 * (1 - p));
                _animOffsetY = -12 * settle - 4 * pulse;
                _animRotate = side * (5.0 * pulse + 2.0 * settle);
            }, useEasing: false);
        }

        private void ClearAttachment()
        {
            _clickResolver.CancelForReason("ClearAttachment");
            _clickResolverTimer.Stop();
            ResetIdleWithDiagnostics();
            _attachedWindow = IntPtr.Zero;
            _attachmentMode = AttachmentMode.None;
            _attachmentRect = Rect.Empty;
            _attachOffsetX = 0;
            _attachOffsetY = 0;
            _currentFallAnimation = null;
            _landingActive = false;
            ResetDescentState();
        }

        private void UpdateAttachedOrFalling(double elapsedSeconds)
        {
            if (_isDragging || (_leftPressed && _attachmentMode != AttachmentMode.Falling))
            {
                return;
            }

            if (_attachmentMode == AttachmentMode.Falling)
            {
                AdvanceFall(elapsedSeconds);
                return;
            }

            if (_attachedWindow == IntPtr.Zero)
            {
                return;
            }

            if (!TryGetWindowRectDip(_attachedWindow, out var rect))
            {
                BeginFall();
                return;
            }

            FollowAttachedWindow(rect);
        }

        private void FollowAttachedWindow(Rect rect)
        {
            _attachmentRect = rect;
            ApplyPetLayout();
        }

        private void AlignAttachedPet(double petWidth, double petHeight, double padding)
        {
            if (_isDragging || _attachmentRect.IsEmpty ||
                _attachmentMode is not (AttachmentMode.FloatingOnTop or AttachmentMode.ClingingLeft or AttachmentMode.ClingingRight))
                return;

            var side = _attachmentMode switch
            {
                AttachmentMode.FloatingOnTop => AttachmentSide.Top,
                AttachmentMode.ClingingLeft => AttachmentSide.Left,
                _ => AttachmentSide.Right
            };
            if (!AttachmentGeometry.TryContact(CharacterProfileRuntime.Current, side, _petHeight,
                new Size(PetImage.Width, PetImage.Height), new Point(Canvas.GetLeft(PetImage), Canvas.GetTop(PetImage)),
                PetImage.RenderTransform.Value, PetImage.RenderTransformOrigin, _gifPlayer?.ResourcePath, out var contact))
            { ClearAttachment(); return; }
            var contacts = new AttachmentContacts(contact, contact, contact);
            var position = AttachmentGeometry.Align(side, _attachmentRect, contacts,
                _attachOffsetX, _attachOffsetY, _petHeight, petWidth);
            Left = position.X;
            Top = position.Y;
            // Keep the existing anchors valid for subsequent dragging / BeginFall.
            _anchorCenterX = position.X + petWidth / 2 + padding - _animOffsetX;
            _anchorBottomY = position.Y + petHeight + padding - _animOffsetY;
        }

        private Rect GetAttachmentDetectionBounds()
        {
            var size = new Size(PetImage.Width, PetImage.Height);
            var sourceSize = _gifPlayer == null ? size : new Size(_gifPlayer.PixelWidth, _gifPlayer.PixelHeight);
            if (!AttachmentContactProfile.TryDetectionSourceBounds(_gifPlayer?.ResourcePath, sourceSize, out var source))
                return Rect.Empty;
            Point Map(Point point) => AttachmentGeometry.TransformPoint(
                new Point(point.X * size.Width / sourceSize.Width, point.Y * size.Height / sourceSize.Height),
                size, new Point(Canvas.GetLeft(PetImage), Canvas.GetTop(PetImage)),
                PetImage.RenderTransform.Value, PetImage.RenderTransformOrigin) + new Vector(Left, Top);
            var bounds = new Rect(Map(source.TopLeft), Map(source.BottomRight));
            bounds.Union(Map(source.TopRight));
            bounds.Union(Map(source.BottomLeft));
            return bounds;
        }

        private void BeginFall()
        {
            var interruptedClick = HasSleepOwnership || _restSession.IsResting || _clickReactionActive || _rocketSession != null || _rotationSession != null || _angrySession != null;
            InterruptRest();
            // Publish the high-priority guard before Movement's Idle restoration can
            // consume pending Rest in the middle of this transition.
            _attachmentMode = AttachmentMode.Falling;
            CancelClickInteraction();
            StopAutonomousMovement();
            ResetIdleWithDiagnostics();
            var interruptedAmbient = _ambientGifActive;
            _ambientGifActive = false;
            _attachedWindow = IntPtr.Zero;
            var floor = SystemParameters.WorkArea.Bottom - 46;
            _descentStartedAt = DateTimeOffset.UtcNow;
            _descentStartBottomY = _anchorBottomY;
            _descentTargetBottomY = floor;
            _descentDurationSeconds = Math.Clamp((floor - _anchorBottomY) / 280 + 1.25, 1.8, 4.2);
            _animationTimer?.Stop();
            _delayedClickSpeechTimer?.Stop();
            ResetAnimationState();
            _clickReactionActive = false;
            _landingActive = false;
            _dragGifActive = false;
            _dragGifFacingRight = true;
            _currentFallAnimation = null;
            // Draw once per fall. A missing first choice uses the other candidate.
            // Successful switches reuse GifFramePlayer's decoded clip cache.
            var sidewaysFirst = _fallRandom.Next(2) == 0;
            var first = sidewaysFirst ? SidewaysFallingGifPath : UprightFallingGifPath;
            var second = sidewaysFirst ? UprightFallingGifPath : SidewaysFallingGifPath;
            if (_gifPlayer?.TrySwitchTo(first) == true)
            {
                _currentFallAnimation = first;
            }
            else if (_gifPlayer?.TrySwitchTo(second) == true)
            {
                _currentFallAnimation = second;
            }

            if (_currentFallAnimation != null)
            {
                _gifPlayer?.Resume();
            }
            else
            {
                if (interruptedAmbient || interruptedClick) _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);
                _gifPlayer?.Pause();
            }
            ApplyPetLayout();
            ShowVoiceBubble(TimeSpan.FromMilliseconds(2100), _descentLines);

            if (_anchorBottomY >= floor - 2)
            {
                _anchorBottomY = floor;
                _attachmentMode = AttachmentMode.None;
                ResetDescentState();
                BeginLanding();
            }
        }

        private void AdvanceFall(double elapsedSeconds)
        {
            var elapsed = (DateTimeOffset.UtcNow - _descentStartedAt).TotalSeconds;
            var progress = Math.Clamp(elapsed / Math.Max(0.1, _descentDurationSeconds), 0, 1);
            var eased = EaseInOutSine(progress);
            _anchorBottomY = _descentStartBottomY + (_descentTargetBottomY - _descentStartBottomY) * eased;

            if (progress >= 1)
            {
                _anchorBottomY = _descentTargetBottomY;
                _attachmentMode = AttachmentMode.None;
                ResetDescentState();
                BeginLanding();
                return;
            }

            ApplyPetLayout();
        }

        private void ResetDescentState()
        {
            _descentStartBottomY = 0;
            _descentTargetBottomY = 0;
            _descentDurationSeconds = 0;
            _descentStartedAt = DateTimeOffset.MinValue;
        }

        private void BeginLanding()
        {
            CancelClickInteraction();
            StopAutonomousMovement();
            ResetIdleWithDiagnostics();
            _ambientGifActive = false;
            // The physics caller has already fixed the anchor and stopped Falling.
            _currentFallAnimation = null;
            _animationTimer?.Stop();
            ResetAnimationState();
            _landingActive = _gifPlayer?.TrySwitchTo(LandingGifPath, loop: false) == true;
            if (_landingActive)
            {
                ApplyPetLayout();
                _gifPlayer?.Resume();
            }
            else
            {
                RestoreIdleGif();
            }
        }

        private void PositionBubble()
        {
            if (_bubbleWindow == null)
            {
                return;
            }

            _bubbleWindow.PositionNear(GetVisiblePetBounds());
        }

        private Rect GetVisiblePetBounds()
        {
            var padding = GetMotionPadding();
            return new Rect(Left + padding, Top + padding, Width - padding * 2, Height - padding * 2);
        }

        private double GetMotionPadding()
        {
            return Math.Max(48, _petHeight * 0.22);
        }

        private Point GetCursorScreenDip()
        {
            GetCursorPos(out var point);
            var screenPoint = new Point(point.X, point.Y);
            return DeviceToDip(screenPoint);
        }

        private Point DeviceToDip(Point screenPoint)
        {
            var source = PresentationSource.FromVisual(this);
            return source?.CompositionTarget == null
                ? screenPoint
                : source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
        }

        private IEnumerable<IntPtr> EnumerateAttachableWindows()
        {
            var ownHandle = new WindowInteropHelper(this).Handle;
            var bubbleHandle = _bubbleWindow == null ? IntPtr.Zero : new WindowInteropHelper(_bubbleWindow).Handle;
            var desktopHandle = GetDesktopWindow();
            var shellHandle = GetShellWindow();
            var handles = new List<IntPtr>();

            EnumWindows((hWnd, _) =>
            {
                if (hWnd == IntPtr.Zero ||
                    hWnd == ownHandle ||
                    hWnd == bubbleHandle ||
                    hWnd == desktopHandle ||
                    hWnd == shellHandle)
                {
                    return true;
                }

                if (!IsWindowCandidate(hWnd))
                {
                    return true;
                }

                handles.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            return handles;
        }

        private bool IsWindowCandidate(IntPtr hWnd)
        {
            if (!IsWindow(hWnd) || !IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                return false;
            }

            var className = GetClassNameText(hWnd);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "NotifyIconOverflowWindow" or "Windows.UI.Core.CoreWindow")
            {
                return false;
            }

            return TryGetWindowRectDip(hWnd, out var rect) && rect.Width >= 140 && rect.Height >= 110;
        }

        private bool TryGetWindowRectDip(IntPtr hWnd, out Rect rect)
        {
            rect = Rect.Empty;
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || !IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                return false;
            }

            if (!GetWindowRect(hWnd, out var nativeRect))
            {
                return false;
            }

            var topLeft = DeviceToDip(new Point(nativeRect.Left, nativeRect.Top));
            var bottomRight = DeviceToDip(new Point(nativeRect.Right, nativeRect.Bottom));
            rect = new Rect(topLeft, bottomRight);
            return rect.Width >= 80 && rect.Height >= 80;
        }

        private static string GetClassNameText(IntPtr hWnd)
        {
            var builder = new StringBuilder(256);
            GetClassName(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        private static double EaseInOutSine(double x)
        {
            return -(Math.Cos(Math.PI * x) - 1) / 2;
        }

        private static double EaseOutCubic(double x)
        {
            return 1 - Math.Pow(1 - x, 3);
        }

        private readonly struct WindowAttachment
        {
            public WindowAttachment(IntPtr hwnd, AttachmentMode mode, double score, double offsetX, double offsetY)
            {
                Hwnd = hwnd;
                Mode = mode;
                Score = score;
                OffsetX = offsetX;
                OffsetY = offsetY;
            }

            public IntPtr Hwnd { get; }
            public AttachmentMode Mode { get; }
            public double Score { get; }
            public double OffsetX { get; }
            public double OffsetY { get; }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    internal sealed class SpeechBubbleWindow : Window
    {
        private readonly TextBlock _textBlock;
        private readonly DispatcherTimer _hideTimer;

        public SpeechBubbleWindow()
        {
            Width = 230;
            Height = 78;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;

            var root = new Grid
            {
                Background = Brushes.Transparent
            };

            var bubble = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 224, 236)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(15, 9, 15, 11),
                Margin = new Thickness(0, 0, 0, 9)
            };

            _textBlock = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(32, 35, 50)),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            bubble.Child = _textBlock;
            root.Children.Add(bubble);

            var tail = new System.Windows.Shapes.Polygon
            {
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(219, 224, 236)),
                StrokeThickness = 1,
                Points = new PointCollection
                {
                    new(106, 66),
                    new(124, 66),
                    new(115, 78)
                }
            };
            root.Children.Add(tail);
            Content = root;

            _hideTimer = new DispatcherTimer();
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                Hide();
            };
        }

        public void ShowMessage(string text, TimeSpan duration)
        {
            _textBlock.Text = text;
            _hideTimer.Stop();
            _hideTimer.Interval = duration;
            if (!IsVisible)
            {
                Show();
            }
            _hideTimer.Start();
        }

        public void PositionNear(Rect petBounds)
        {
            var screen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            const double gap = 10;
            var left = petBounds.Left + petBounds.Width / 2 - Width / 2;
            var top = petBounds.Top - Height - gap;

            if (top < screen.Top + gap)
            {
                top = petBounds.Bottom + gap;
            }

            if (top + Height > screen.Bottom - gap)
            {
                top = petBounds.Top + petBounds.Height * 0.32 - Height / 2;
                left = petBounds.Right + gap;

                if (left + Width > screen.Right - gap)
                {
                    left = petBounds.Left - Width - gap;
                }
            }

            left = Math.Clamp(left, screen.Left + gap, screen.Right - Width - gap);
            top = Math.Clamp(top, screen.Top + gap, screen.Bottom - Height - gap);

            Left = left;
            Top = top;
        }
    }

    internal static class PetImageLoader
    {
        // No disk or game asset is required for the public snapshot's visual.
        public static BitmapSource LoadFallback(string resourcePath)
        {
            try { return LoadPng(resourcePath); }
            catch (Exception e) when (e is System.IO.IOException or FormatException or
                NotSupportedException or InvalidOperationException or ArgumentException or COMException)
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRoundedRectangle(Brushes.SlateBlue, new Pen(Brushes.WhiteSmoke, 2),
                        new Rect(16, 16, 96, 96), 20, 20);
                    drawing.DrawEllipse(null, new Pen(Brushes.WhiteSmoke, 5), new Point(64, 64), 24, 24);
                }
                var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                return bitmap;
            }
        }

        public static BitmapSource LoadPng(string resourcePath)
        {
            var resource = Application.GetResourceStream(
                new Uri($"pack://application:,,,/{typeof(MainWindow).Assembly.GetName().Name};component/{resourcePath}")
            );

            if (resource == null)
            {
                throw new InvalidOperationException($"找不到桌宠素材资源：{resourcePath}");
            }

            using var stream = resource.Stream;

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad
            );

            var bitmap = decoder.Frames[0];
            bitmap.Freeze();

            return bitmap;
        }

        public static BitmapSource LoadTransparentCutout(string resourcePath)
        {
            var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/{resourcePath}"));
            if (resource == null)
            {
                throw new InvalidOperationException($"找不到桌宠素材资源：{resourcePath}");
            }

            using var stream = resource.Stream;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var source = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var transparent = FindConnectedBackground(pixels, width, height);
            for (var i = 0; i < transparent.Length; i++)
            {
                if (transparent[i])
                {
                    pixels[i * 4 + 3] = 0;
                }
            }

            return CropTransparentBounds(pixels, width, height, converted.DpiX, converted.DpiY);
        }

        private static bool[] FindConnectedBackground(byte[] pixels, int width, int height)
        {
            var transparent = new bool[width * height];
            var queue = new Queue<int>();

            void TryAdd(int x, int y)
            {
                if (x < 0 || x >= width || y < 0 || y >= height)
                {
                    return;
                }

                var index = y * width + x;
                if (transparent[index] || !IsBackgroundPixel(pixels, index))
                {
                    return;
                }

                transparent[index] = true;
                queue.Enqueue(index);
            }

            for (var x = 0; x < width; x++)
            {
                TryAdd(x, 0);
                TryAdd(x, height - 1);
            }

            for (var y = 1; y < height - 1; y++)
            {
                TryAdd(0, y);
                TryAdd(width - 1, y);
            }

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                TryAdd(x - 1, y);
                TryAdd(x + 1, y);
                TryAdd(x, y - 1);
                TryAdd(x, y + 1);
            }

            for (var pass = 0; pass < 2; pass++)
            {
                var fringe = new List<int>();
                for (var y = 1; y < height - 1; y++)
                {
                    for (var x = 1; x < width - 1; x++)
                    {
                        var index = y * width + x;
                        if (transparent[index] || !IsFringePixel(pixels, index))
                        {
                            continue;
                        }

                        if (transparent[index - 1] || transparent[index + 1] ||
                            transparent[index - width] || transparent[index + width])
                        {
                            fringe.Add(index);
                        }
                    }
                }

                foreach (var index in fringe)
                {
                    transparent[index] = true;
                }
            }

            return transparent;
        }

        private static bool IsBackgroundPixel(byte[] pixels, int pixelIndex)
        {
            var offset = pixelIndex * 4;
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];
            return Math.Max(r, Math.Max(g, b)) <= 18;
        }

        private static bool IsFringePixel(byte[] pixels, int pixelIndex)
        {
            var offset = pixelIndex * 4;
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];
            return r <= 30 && g <= 30 && b <= 30;
        }

        private static BitmapSource CropTransparentBounds(byte[] pixels, int width, int height, double dpiX, double dpiY)
        {
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var alpha = pixels[(y * width + x) * 4 + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < minX || maxY < minY)
            {
                throw new InvalidOperationException("图片没有可显示的角色内容。");
            }

            minX = Math.Max(0, minX - 2);
            minY = Math.Max(0, minY - 2);
            maxX = Math.Min(width - 1, maxX + 2);
            maxY = Math.Min(height - 1, maxY + 2);

            var cropWidth = maxX - minX + 1;
            var cropHeight = maxY - minY + 1;
            var cropStride = cropWidth * 4;
            var cropped = new byte[cropStride * cropHeight];

            for (var y = 0; y < cropHeight; y++)
            {
                Buffer.BlockCopy(
                    pixels,
                    ((minY + y) * width + minX) * 4,
                    cropped,
                    y * cropStride,
                    cropStride);
            }

            var bitmap = BitmapSource.Create(
                cropWidth,
                cropHeight,
                dpiX,
                dpiY,
                PixelFormats.Bgra32,
                null,
                cropped,
                cropStride);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
