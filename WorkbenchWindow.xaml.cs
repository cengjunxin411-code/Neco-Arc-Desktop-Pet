using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NecoArcDesktopPet.Settings;
#if PMV2_PILOT
using NecoArcDesktopPet.Pilot.Physics;
#endif

namespace NecoArcDesktopPet;

public partial class WorkbenchWindow : Window
{
    private static string WorkbenchReason(string reason) =>
#if PMV2_PRODUCT
        reason.Replace("Pilot", "桌宠", StringComparison.Ordinal);
#else
        reason;
#endif
    private MainWindow _mainWindow = null!;
    private PetSettings _workingCopy = null!;
    private bool _workingTopmost;
    private string[] _lastAppliedUiState = Array.Empty<string>();
    private bool _appliedTopmost;
    private bool _isLoading;
    private bool _hasApplied;
    private readonly List<EditorField> _fields = new();
    private readonly List<(FrameworkElement Control, Func<bool> Enabled)> _enabledStates = new();
    private StackPanel[] _pages = Array.Empty<StackPanel>();
    private TextBlock _ambientShare = null!;
    private TextBox _movementShare = null!;
    private TextBox _diagnosticsPath = null!;
#if PMV2_PILOT
    private ComboBox _physicsMode = null!;
    private TextBlock _physicsEffective = null!, _physicsHint = null!;
    private PhysicsMode _workingPhysicsMode, _appliedPhysicsMode, _effectivePhysicsMode;
    private TextBox _attachmentSnap = null!;
    private TextBlock _attachmentEffective = null!, _attachmentThresholds = null!;
#endif

    private sealed record EditorField(string Path, string Label, int Page, FrameworkElement Control,
        Action<PetSettings> Load, Func<PetSettings, string?> Write);

    public WorkbenchWindow(MainWindow mainWindow)
#if PMV2_PRODUCT
        : this(mainWindow, null)
#endif
    {
#if !PMV2_PRODUCT
        InitializeWorkbench(mainWindow);
#endif
    }
#if PMV2_PRODUCT
    internal WorkbenchWindow(MainWindow mainWindow, Product.AutostartManager? autostart)
    {
        _autostart = autostart;
        InitializeWorkbench(mainWindow);
    }
#endif
    private void InitializeWorkbench(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        _workingCopy = mainWindow.GetSettingsSnapshot();
        _workingTopmost = mainWindow.Topmost;
#if PMV2_PILOT
        _workingPhysicsMode = mainWindow.PilotPhysicsPreference;
#endif
        InitializeComponent();
        InitializeAutostart();
        _pages = new[] { Page0, Page1, Page2, Page3, Page4 };
        CreateFields();
        ApplyPilotFieldAvailability();
        LoadFromSettings();
        CaptureAppliedUiState();
        RecalculateDirtyState();
    }

#if PMV2_PRODUCT
    private Product.AutostartManager? _autostart;
    private bool _loadingAutostart;
    private CheckBox AutostartSetting = null!;
    private TextBlock AutostartStatus = null!;
#endif
    private void InitializeAutostart()
    {
#if PMV2_PRODUCT
        AutostartSetting = new CheckBox { Name = "AutostartSetting", Content = "开机自启动", Style = (Style)FindResource("ImmediateToggleStyle") };
        AutostartStatus = new TextBlock { Name = "AutostartStatus", Style = (Style)FindResource("HintTextStyle") };
        RegisterName(AutostartSetting.Name, AutostartSetting);
        RegisterName(AutostartStatus.Name, AutostartStatus);
        WindowBehaviorSettings.Children.Add(AutostartSetting);
        WindowBehaviorSettings.Children.Add(AutostartStatus);
        AutostartSetting.Checked += OnAutostartChanged;
        AutostartSetting.Unchecked += OnAutostartChanged;
        try
        {
            _autostart ??= new Product.AutostartManager(new Product.CurrentUserRunStore(),
                System.IO.Path.Combine(AppContext.BaseDirectory, "NecoArcDesktopPet.Product.exe"));
            RefreshAutostart();
        }
        catch (Exception e)
        {
            AutostartSetting.IsEnabled = false;
            AutostartStatus.Text = "无法读取开机自启动状态：" + e.Message;
        }
#endif
    }
#if PMV2_PRODUCT
    private void RefreshAutostart()
    {
        _loadingAutostart = true;
        try { AutostartSetting.IsChecked = _autostart!.IsEnabled(); }
        finally { _loadingAutostart = false; }
        AutostartStatus.Text = "登录当前 Windows 用户时启动；更改立即生效。";
    }
#endif
    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
#if PMV2_PRODUCT
        if (_loadingAutostart || _autostart == null) return;
        try { _autostart.SetEnabled(AutostartSetting.IsChecked == true); RefreshAutostart(); }
        catch (Exception error)
        {
            try { RefreshAutostart(); }
            catch { AutostartSetting.IsEnabled = false; }
            AutostartStatus.Text = "更改开机自启动失败：" + error.Message;
        }
#endif
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages.Length == 0 || Navigation.SelectedIndex < 0) return;
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == Navigation.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = ((ListBoxItem)Navigation.SelectedItem).Content.ToString();
        PageScroller.ScrollToTop();
    }

    private void LoadFromSettings()
    {
        _isLoading = true;
        try
        {
            foreach (var field in _fields) field.Load(_workingCopy);
            TopmostSetting.IsChecked = _workingTopmost;
#if PMV2_PILOT
            LoadPhysicsSelection();
#endif
        }
        finally { _isLoading = false; }
        UpdateEnabledStates();
        UpdateAmbientShare();
        ShowValidationErrors(Array.Empty<SettingsValidationError>());
    }

    // Compare raw values so invalid or differently formatted text still counts as an edit.
    // Navigation, validation messages, scroll offsets and window geometry are excluded.
    private string[] ReadUiState() => _fields.Select(field => field.Control switch
    {
        TextBox text => text.Text,
        CheckBox toggle => toggle.IsChecked == true ? "true" : "false",
        _ => throw new InvalidOperationException("Unsupported Workbench editor control.")
    }).ToArray();

    private void CaptureAppliedUiState()
    {
        _lastAppliedUiState = ReadUiState();
        _appliedTopmost = TopmostSetting.IsChecked == true;
#if PMV2_PILOT
        _appliedPhysicsMode = _workingPhysicsMode;
#endif
    }

    private void OnEditableChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _lastAppliedUiState.Length == 0) return;
        UpdateEnabledStates();
        RecalculateDirtyState();
    }

    private void RecalculateDirtyState()
    {
        if (_isLoading) return;
#if PMV2_PILOT
        RefreshPhysicsStatus();
        RefreshAttachmentStatus();
#endif
        var dirty = HasUnappliedEdits();
        ApplyButton.IsEnabled = dirty || _mainWindow.SettingsSavePending;
        var persistence = _mainWindow.SettingsPersistenceStatus;
        if (!_mainWindow.SettingsPersistenceAvailable)
        {
            var notice = persistence ?? "设置仅在本次运行有效；尚未启用保存，重启后会恢复默认值。";
            StatusText.Text = dirty ? "● 有未应用的更改\n" + notice : notice;
        }
        else StatusText.Text = (_mainWindow.WorkbenchUnavailableReason == null ? "" : "PMv2 独立设置\n") +
            (dirty ? "● 有未应用的更改" + (_mainWindow.SettingsSavePending ? "\n" + persistence : "")
            : persistence ?? (_hasApplied ? "已应用。" : "修改后点击“应用”生效。"));
    }

    private bool HasUnappliedEdits() => (TopmostSetting.IsChecked == true) != _appliedTopmost ||
#if PMV2_PILOT
        _workingPhysicsMode != _appliedPhysicsMode ||
#endif
        !ReadUiState().SequenceEqual(_lastAppliedUiState, StringComparer.Ordinal);

    internal void RefreshPersistenceStatus() => RecalculateDirtyState();

#if PMV2_PILOT
    // "当前生效" always comes from the applied runtime owner; the staged number box
    // only feeds a separately labelled preview line, so the two can never be mixed.
    private void RefreshAttachmentStatus()
    {
        if (_attachmentEffective == null || _attachmentThresholds == null || _attachmentSnap == null) return;
        var experimental = _mainWindow.PilotAttachmentExperimentalEnabled;
        var effective = _mainWindow.PilotAttachmentEffectiveEnabled;
        var preference = _mainWindow.PilotAttachmentPreference;
        _attachmentEffective.Text = (experimental
                ? "当前生效：" + (effective ? "已启用" : "已禁用")
#if PMV2_PRODUCT
                : "当前生效：已禁用") +
#else
                : "当前生效：已禁用（未启用 --experimental-attachment）") +
#endif
            "；已保存偏好：" + (preference ? "启用" : "禁用") + "。";
        var height = _mainWindow.PilotAttachmentHeightDip;
        var applied = _mainWindow.PilotAttachmentSnapDistanceDip;
        var (top, side) = _mainWindow.PilotAttachmentThresholds(applied);
        var text = "已应用阈值（猫姬身高 " + DipText(height) + " DIP，检测上限 " + DipText(applied) +
            " DIP）：顶面 " + DipText(top) + " DIP · 侧面 " + DipText(side) + " DIP";
        if (TryNumber(_attachmentSnap.Text, out var staged) && double.IsFinite(staged) && Math.Abs(staged - applied) > 1e-9)
        {
            var (stagedTop, stagedSide) = _mainWindow.PilotAttachmentThresholds(staged);
            text += "\n未应用预览（点击“应用”后生效，检测上限 " + DipText(staged) + " DIP）：顶面 " +
                DipText(stagedTop) + " DIP · 侧面 " + DipText(stagedSide) + " DIP";
        }
        _attachmentThresholds.Text = text;
    }
    private static string DipText(double value) =>
        double.IsFinite(value) ? value.ToString("0.###", CultureInfo.CurrentCulture) : "—";
#endif

    internal void RefreshTopmostFromOwner()
    {
        // Preserve a staged Topmost edit while reflecting a menu toggle in an otherwise untouched field.
        if ((TopmostSetting.IsChecked == true) != _appliedTopmost) return;
        _isLoading = true;
        try
        {
            _workingTopmost = _mainWindow.Topmost;
            _appliedTopmost = _workingTopmost;
            TopmostSetting.IsChecked = _workingTopmost;
        }
        finally { _isLoading = false; }
        RecalculateDirtyState();
    }

    private List<SettingsValidationError> WriteToSettings(PetSettings candidate)
    {
        var errors = new List<SettingsValidationError>();
        foreach (var field in _fields)
        {
            if (_mainWindow.WorkbenchUnavailableReason?.Invoke(field.Path) != null) continue;
            var error = field.Write(candidate);
            if (error != null) errors.Add(new(field.Path, error));
        }
        return errors;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_mainWindow.SettingsSavePending && !HasUnappliedEdits())
        {
            // Retry the newest live snapshot; do not republish stale controls after a menu change.
            _mainWindow.SaveSettingsSnapshot();
            return;
        }
        // Parse and validate a candidate before publishing either settings or Topmost.
        // Pilot starts from the live snapshot so Reset never publishes fields
        // that this phase deliberately keeps unavailable for editing.
        var candidate = _mainWindow.Pmv2OwnsSettings
            ? _mainWindow.GetSettingsSnapshot() : _workingCopy.DeepClone();
        var errors = WriteToSettings(candidate);
        if (errors.Count == 0) errors.AddRange(PetSettingsValidator.Validate(candidate).Errors);
#if PMV2_PILOT
        if (_mainWindow.PilotPhysicsChangeUnavailableReason(_workingPhysicsMode) is {} physicsReason)
            errors.Add(new("Physics.Mode", WorkbenchReason(physicsReason)));
        // Attachment is admitted here, before anything is published. A refusal must
        // leave every other staged field unpublished and the live relation untouched.
        if (_mainWindow.PilotAttachmentChangeUnavailableReason(candidate.Attachment.Enabled) is {} attachmentReason)
            errors.Add(new("Attachment.Enabled", WorkbenchReason(attachmentReason)));
#endif
        ShowValidationErrors(errors);
        if (errors.Count != 0)
        {
            RecalculateDirtyState();
            return;
        }

        _workingTopmost = TopmostSetting.IsChecked == true;
#if PMV2_PILOT
        // Complete the fallible safety admission before publishing other settings.
        // ApplySettings can update owners; Topmost can synchronously invoke native hooks.
        // Neither may run between the precheck and Physics Apply's repeated check.
        using (Dispatcher.DisableProcessing())
        {
            try { _mainWindow.PilotPhysicsApplyPreference(_workingPhysicsMode); }
            catch (InvalidOperationException exception)
            {
                ShowValidationErrors(new[] { new SettingsValidationError("Physics.Mode", WorkbenchReason(exception.Message)) });
                RecalculateDirtyState();
                return;
            }
            _mainWindow.ApplySettings(candidate);
            _mainWindow.ApplyTopmost(_workingTopmost);
        }
#else
        _mainWindow.ApplySettings(candidate);
        _mainWindow.ApplyTopmost(_workingTopmost);
#endif
        _workingCopy = _mainWindow.GetSettingsSnapshot();
        _workingTopmost = _mainWindow.Topmost;
#if PMV2_PILOT
        _workingPhysicsMode = _mainWindow.PilotPhysicsPreference;
#endif
        LoadFromSettings();
        CaptureAppliedUiState();
        _hasApplied = true;
        _mainWindow.SaveSettingsSnapshot();
        RecalculateDirtyState();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        _workingCopy = PetSettingsDefaults.Create();
        _workingTopmost = true; // Matches MainWindow's existing startup default.
#if PMV2_PILOT
        _workingPhysicsMode = _mainWindow.PilotPhysicsExperimentalEnabled
            ? PhysicsMode.Off : _mainWindow.PilotPhysicsPreference;
#endif
        LoadFromSettings();
        RecalculateDirtyState();
    }

    private void UpdateEnabledStates()
    {
        foreach (var state in _enabledStates) state.Control.IsEnabled = state.Enabled();
        if (_mainWindow.WorkbenchUnavailableReason is {} unavailable)
            foreach (var field in _fields) field.Control.IsEnabled = unavailable(field.Path) == null;
        foreach (var state in _enabledStates)
        {
            // Fade only the first disabled panel in a branch, avoiding compounded opacity.
            var parentDisabled = (state.Control.Parent as UIElement)?.IsEnabled == false;
            state.Control.Opacity = !state.Control.IsEnabled && !parentDisabled ? 0.65 : 1;
        }
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void UpdateAmbientShare()
    {
        _ambientShare.Text = TryNumber(_movementShare.Text, out var percent) && double.IsFinite(percent) && percent >= 0 && percent <= 100
            ? (100 - percent).ToString("0.############", CultureInfo.CurrentCulture)
            : "—";
    }

    private void ShowValidationErrors(IReadOnlyList<SettingsValidationError> errors)
    {
        ErrorArea.Visibility = errors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = string.Join(Environment.NewLine, errors.Select(error =>
        {
            var field = _fields.FirstOrDefault(f => f.Path == error.Path);
            return (field?.Label ?? (error.Path == "Movement.Profiles" ? "移动权重合计" :
#if PMV2_PILOT
                error.Path == "Physics.Mode" ? "物理模式" :
#endif
                error.Path)) + "：" + LocalizeError(error.Message);
        }));
        if (errors.Count == 0) return;
        var firstPath = errors[0].Path == "Movement.Profiles" ? "Movement.Lope.Weight" : errors[0].Path;
#if PMV2_PILOT
        if (firstPath == "Physics.Mode")
        {
            Navigation.SelectedIndex = 0;
            _physicsMode.BringIntoView();
            _physicsMode.Focus();
            return;
        }
#endif
        var first = _fields.FirstOrDefault(f => f.Path == firstPath);
        if (first == null) return;
        Navigation.SelectedIndex = first.Page;
        UpdateLayout();
        first.Control.BringIntoView();
        first.Control.Focus();
        if (first.Control is TextBox text) text.SelectAll();
    }

    // Translate existing validator messages; semantic validation remains solely in Settings.
    private static string LocalizeError(string message) => message switch
    {
        "Volume must be between 0 and 100 percent." => "音量必须在 0～100% 之间。",
        "Must be greater than zero and within the supported timer range (2147483647 ms)." => "必须大于 0，且不能超过计时器范围（2147483647 ms）。",
        "Maximum cannot be less than minimum." => "最大值不能小于最小值。",
        "Must be finite and greater than zero." => "必须是大于 0 的有限数值。",
        "Must be finite and nonnegative." => "必须是大于或等于 0 的有限数值。",
        "Maximum must be finite and at least minimum." => "最大值必须是有限数值，且不能小于最小值。",
        "Must be nonnegative." => "不能小于 0。",
        "Must be at least one." => "必须至少为 1。",
        "Must be a probability from zero to one." => "概率必须在 0%–100% 之间。",
        "Total weight exceeds the random selection limit." => "权重合计不能超过 2147483647。",
        "Maximum must leave room for an inclusive random bound." => "最大循环次数必须小于 2147483647。",
        "Must be nonnegative and within the supported duration range." => "不能小于 0，且不能超过计时器范围。",
        "Maximum must be at least initial duration and within the supported duration range." => "最大时长不能小于初始时长，且不能超过计时器范围。",
        _ => message
    };
}
