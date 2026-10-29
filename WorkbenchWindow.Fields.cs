using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using NecoArcDesktopPet.Settings;
#if PMV2_PILOT
using NecoArcDesktopPet.Pilot.Physics;
#endif

namespace NecoArcDesktopPet;

public partial class WorkbenchWindow
{
    private enum NumberKind { Decimal, Integer, Seconds, Milliseconds, Percent }

    private StackPanel Group(StackPanel page, string title)
    {
        var body = new StackPanel();
        page.Children.Add(new GroupBox { Header = title, Content = body });
        return body;
    }

    private void Note(Panel body, string text) => body.Children.Add(new TextBlock
    {
        Text = text, Style = (Style)FindResource("HintTextStyle")
    });

    private void Subheading(Panel body, string text) => body.Children.Add(new TextBlock
    {
        Text = text, Style = (Style)FindResource("SubheadingStyle")
    });

    private void Identify(FrameworkElement control, string path, string label)
    {
        control.Name = path.Replace('.', '_');
        RegisterName(control.Name, control);
        AutomationProperties.SetName(control, label);
    }

    private CheckBox Switch(Panel body, int page, string path, string label,
        Func<PetSettings, bool> read, Action<PetSettings, bool> write)
    {
        var control = new CheckBox { Content = label };
        Identify(control, path, label);
        body.Children.Add(control);
        _fields.Add(new(path, label, page, control,
            s => control.IsChecked = read(s), s => { write(s, control.IsChecked == true); return null; }));
        control.Checked += OnEditableChanged;
        control.Unchecked += OnEditableChanged;
        return control;
    }

    private StackPanel Dependents(Panel parent, Func<bool> enabled)
    {
        var panel = new StackPanel();
        parent.Children.Add(panel);
        _enabledStates.Add((panel, enabled));
        return panel;
    }

    private TextBox Number(Panel body, int page, string path, string label, string unit,
        Func<PetSettings, double> read, Action<PetSettings, double> write, NumberKind kind = NumberKind.Decimal)
    {
        var row = FieldRow();
        var input = new TextBox { Style = (Style)FindResource("NumericTextBoxStyle") };
        var caption = new Label { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Target = input, Style = (Style)FindResource("FieldLabelStyle") };
        var units = new TextBlock { Text = unit, Style = (Style)FindResource("UnitTextStyle") };
        Grid.SetColumn(input, 1); Grid.SetColumn(units, 2);
        row.Children.Add(caption); row.Children.Add(input); row.Children.Add(units); body.Children.Add(row);
        Identify(input, path, string.IsNullOrEmpty(unit) ? label : label + "（" + unit + "）");
        _fields.Add(new(path, label, page, input,
            s => input.Text = read(s).ToString("R", CultureInfo.CurrentCulture),
            s =>
            {
                double value;
                if (kind == NumberKind.Integer)
                {
                    if (!int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var integer))
                        return "请输入有效整数（不能包含小数，且须在整数范围内）。";
                    value = integer;
                }
                else if (!TryNumber(input.Text, out value)) return "请输入有效数字。";
                try { write(s, kind == NumberKind.Percent ? value / 100 : value); }
                catch (Exception e) when (e is OverflowException or ArgumentException)
                {
                    // TimeSpan conversion can fail before the model validator can inspect it.
                    return "请输入可表示的有限时长。";
                }
                return null;
            }));
        input.TextChanged += OnEditableChanged;
        return input;
    }

    private static Grid FieldRow()
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 200, SharedSizeGroup = "FieldLabel" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132), SharedSizeGroup = "FieldInput" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64), SharedSizeGroup = "FieldUnit" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return row;
    }

    private void Seconds(Panel body, int page, string path, string label,
        Func<PetSettings, TimeSpan> read, Action<PetSettings, TimeSpan> write) =>
        Number(body, page, path, label, "秒", s => read(s).TotalSeconds, (s, v) => write(s, TimeSpan.FromSeconds(v)), NumberKind.Seconds);

    private void Milliseconds(Panel body, int page, string path, string label,
        Func<PetSettings, TimeSpan> read, Action<PetSettings, TimeSpan> write) =>
        Number(body, page, path, label, "ms", s => read(s).TotalMilliseconds, (s, v) => write(s, TimeSpan.FromMilliseconds(v)), NumberKind.Milliseconds);

    private void Integer(Panel body, int page, string path, string label, string unit,
        Func<PetSettings, int> read, Action<PetSettings, int> write) =>
        Number(body, page, path, label, unit, s => read(s), (s, v) => write(s, (int)v), NumberKind.Integer);

    private void CreateFields()
    {
#if PMV2_PILOT
        CreatePhysicsFields();
        // Created before the behavior groups so it is the first group on the page.
        CreateAttachmentFields();
#endif
        CreateBehaviorFields();
        CreateMovementFields();
        CreateInteractionFields();
        CreateDiagnosticsFields();
#if PMV2_PILOT
        CreateVoiceFields();
#endif
    }

#if PMV2_PILOT
    private void CreateVoiceFields()
    {
        var body = Group(Page1, "声音");
        Note(body, "关闭后立即停止语音，动画与交互照常执行；重新开启不会补播已跳过的语音。");
        var enabled = Switch(body, 1, "Voice.Enabled", "启用语音",
            s => s.Voice.Enabled, (s, v) => s.Voice.Enabled = v);
        var volume = Dependents(body, () => enabled.IsChecked == true);
        Integer(volume, 1, "Voice.VolumePercent", "语音音量", "%",
            s => s.Voice.VolumePercent, (s, v) => s.Voice.VolumePercent = v);
        Note(body, "点击应用后，开场语音与动作语音统一采用该音量。");
    }

    private void CreatePhysicsFields()
    {
        var body = Group(Page0, "物理模式");
        _physicsMode = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Left };
        Identify(_physicsMode, "Physics.Mode", "物理模式");
        // UI order deliberately differs from the enum's numeric order.
        foreach (var (mode, label) in new[] { (PhysicsMode.Off, "Off（关闭物理）"),
            (PhysicsMode.PlanarInertia, "PlanarInertia（平面惯性）"), (PhysicsMode.Gravity, "Gravity（重力模式）") })
            _physicsMode.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        body.Children.Add(_physicsMode);
        _physicsEffective = new TextBlock { Style = (Style)FindResource("HintTextStyle") };
        Identify(_physicsEffective, "Physics.Effective", "当前实际生效物理模式");
        body.Children.Add(_physicsEffective);
        _physicsHint = new TextBlock { Style = (Style)FindResource("HintTextStyle") };
        body.Children.Add(_physicsHint);
        _physicsMode.SelectionChanged += (_, _) =>
        {
            if (_isLoading) return;
            if (_physicsMode.SelectedItem is ComboBoxItem { Tag: PhysicsMode mode }) _workingPhysicsMode = mode;
            OnEditableChanged(_physicsMode, new RoutedEventArgs());
        };
        // Pull live status on activation; no independent polling or switching timer.
        Activated += (_, _) => RefreshPhysicsStatus();
    }

    private void LoadPhysicsSelection()
    {
        _physicsMode.SelectedItem = _physicsMode.Items.Cast<ComboBoxItem>()
            .Single(item => item.Tag is PhysicsMode mode && mode == _workingPhysicsMode);
        RefreshPhysicsStatus();
    }

    private void RefreshPhysicsStatus()
    {
        _effectivePhysicsMode = _mainWindow.PilotPhysicsEffectiveMode;
        _physicsMode.IsEnabled = _mainWindow.PilotPhysicsExperimentalEnabled;
        _physicsEffective.Text = _mainWindow.PilotPhysicsExperimentalEnabled
            ? "当前生效：" + PhysicsLabel(_effectivePhysicsMode)
#if PMV2_PRODUCT
            : "当前生效：Off（物理功能不可用）";
        _physicsHint.Text = "当前内存偏好：" + PhysicsLabel(_mainWindow.PilotPhysicsPreference) + "\n" +
            (!_mainWindow.PilotPhysicsExperimentalEnabled ? "物理功能不可用；保留当前偏好。" :
            WorkbenchReason(_mainWindow.PilotPhysicsChangeUnavailableReason(_workingPhysicsMode) ?? "选择后点击“应用”生效并保存。"));
#else
            : "当前生效：Off（未启用 --experimental-physics）";
        _physicsHint.Text = "当前内存偏好：" + PhysicsLabel(_mainWindow.PilotPhysicsPreference) + "\n" +
            (!_mainWindow.PilotPhysicsExperimentalEnabled ? "需以 --experimental-physics 启动；保留当前偏好。" :
            _mainWindow.PilotPhysicsChangeUnavailableReason(_workingPhysicsMode) ?? "选择后点击“应用”生效并保存。");
#endif
    }

    private static string PhysicsLabel(PhysicsMode mode) => mode switch
    {
        PhysicsMode.Off => "Off（关闭物理）",
        PhysicsMode.PlanarInertia => "PlanarInertia（平面惯性）",
        PhysicsMode.Gravity => "Gravity（重力模式）",
        _ => "未知模式"
    };
#endif

#if PMV2_PILOT
    // Pilot-only preference for the experimental external attachment. Both fields are
    // ordinary PetSettings values, so the existing EditorField dirty/Apply/Reset/save
    // path carries them; there is no second working copy.
    private void CreateAttachmentFields()
    {
        var body = Group(Page1, "外部依附");
        PilotNote(body, "外部依附需要 --experimental-attachment；应用后从下一次合法拖拽释放开始采用设置。关闭时已建立的依附会立即解除并回到待机，位置保持不变。");
        var enabled = Switch(body, 1, "Attachment.Enabled", "启用外部依附",
            s => s.Attachment.Enabled, (s, v) => s.Attachment.Enabled = v);
        var snapPanel = Dependents(body, () => enabled.IsChecked == true);
        _attachmentSnap = Number(snapPanel, 1, "Attachment.SnapDistanceDip", "最大吸附检测距离", "DIP",
            s => s.Attachment.SnapDistanceDip, (s, v) => s.Attachment.SnapDistanceDip = v, NumberKind.Integer);
        _attachmentEffective = new TextBlock { Style = (Style)FindResource("HintTextStyle") };
        Identify(_attachmentEffective, "Attachment.Effective", "当前外部依附状态");
        body.Children.Add(_attachmentEffective);
        _attachmentThresholds = new TextBlock { Style = (Style)FindResource("HintTextStyle"), TextWrapping = TextWrapping.Wrap };
        Identify(_attachmentThresholds, "Attachment.Thresholds", "当前已应用检测阈值");
        body.Children.Add(_attachmentThresholds);
        // Pull live status on activation; no independent polling or switching timer.
        Activated += (_, _) => RefreshAttachmentStatus();
    }
#endif

    private void CreateBehaviorFields()
    {
        var normal = Group(Page1, "普通待机");
        PilotNote(normal, "随机待机需要 --experimental-random-idle；关闭使能后不再开始新动作。等待时长在下次排程生效。");
        var randomEnabled = Switch(normal, 1, "Normal.RandomIdleEnabled", "启用随机待机动作", s => s.Normal.RandomIdleEnabled, (s, v) => s.Normal.RandomIdleEnabled = v);
        var random = Dependents(normal, () => _mainWindow.WorkbenchUnavailableReason == null
            ? randomEnabled.IsChecked == true : _mainWindow.WorkbenchUnavailableReason("Normal.RandomIdleMinDelay") == null);
        Subheading(random, "随机待机");
        Seconds(random, 1, "Normal.RandomIdleMinDelay", "最短等待", s => s.Normal.RandomIdleMinDelay, (s, v) => s.Normal.RandomIdleMinDelay = v);
        Seconds(random, 1, "Normal.RandomIdleMaxDelay", "最长等待", s => s.Normal.RandomIdleMaxDelay, (s, v) => s.Normal.RandomIdleMaxDelay = v);

        var rest = Group(Page1, "休息");
        PilotNote(rest, "休息需要 --experimental-rest-sleep；关闭后不再进入新的休息周期。无操作时长在下次周期生效，随机动作参数在下次排程生效。");
        var restEnabled = Switch(rest, 1, "Rest.Enabled", "启用休息模式", s => s.Rest.Enabled, (s, v) => s.Rest.Enabled = v);
        var restBody = Dependents(rest, () => _mainWindow.WorkbenchUnavailableReason == null
            ? restEnabled.IsChecked == true : _mainWindow.WorkbenchUnavailableReason("Rest.EnterAfter") == null);
        Subheading(restBody, "进入休息");
        Seconds(restBody, 1, "Rest.EnterAfter", "无操作时长", s => s.Rest.EnterAfter, (s, v) => s.Rest.EnterAfter = v);
        Subheading(restBody, "休息随机动作");
        var restAmbient = Switch(restBody, 1, "Rest.AmbientEnabled", "启用随机动作", s => s.Rest.AmbientEnabled, (s, v) => s.Rest.AmbientEnabled = v);
        var restRandom = Dependents(restBody, () => restAmbient.IsChecked == true);
        Seconds(restRandom, 1, "Rest.AmbientMinDelay", "最短等待", s => s.Rest.AmbientMinDelay, (s, v) => s.Rest.AmbientMinDelay = v);
        Seconds(restRandom, 1, "Rest.AmbientMaxDelay", "最长等待", s => s.Rest.AmbientMaxDelay, (s, v) => s.Rest.AmbientMaxDelay = v);
        Integer(restRandom, 1, "Rest.AmbientMinLoops", "最少循环", "次", s => s.Rest.AmbientMinLoops, (s, v) => s.Rest.AmbientMinLoops = v);
        Integer(restRandom, 1, "Rest.AmbientMaxLoops", "最多循环", "次", s => s.Rest.AmbientMaxLoops, (s, v) => s.Rest.AmbientMaxLoops = v);

        var sleep = Group(Page1, "睡眠");
        PilotNote(sleep, "睡眠需要 --experimental-rest-sleep，且须先进入休息；休息关闭时不会开启新周期。进入睡眠的等待时长在下次休息周期生效，随机动作参数在下次排程生效。");
        var sleepEnabled = Switch(sleep, 1, "Sleep.Enabled", "启用睡眠模式", s => s.Sleep.Enabled, (s, v) => s.Sleep.Enabled = v);
        var sleepBody = Dependents(sleep, () => _mainWindow.WorkbenchUnavailableReason == null
            ? sleepEnabled.IsChecked == true : _mainWindow.WorkbenchUnavailableReason("Sleep.EnterAfterRest") == null);
        Subheading(sleepBody, "进入睡眠");
        Seconds(sleepBody, 1, "Sleep.EnterAfterRest", "进入休息后等待", s => s.Sleep.EnterAfterRest, (s, v) => s.Sleep.EnterAfterRest = v);
        Subheading(sleepBody, "睡眠随机动作");
        var sleepAmbient = Switch(sleepBody, 1, "Sleep.AmbientEnabled", "启用随机动作", s => s.Sleep.AmbientEnabled, (s, v) => s.Sleep.AmbientEnabled = v);
        var sleepRandom = Dependents(sleepBody, () => sleepAmbient.IsChecked == true);
        Seconds(sleepRandom, 1, "Sleep.AmbientMinDelay", "最短等待", s => s.Sleep.AmbientMinDelay, (s, v) => s.Sleep.AmbientMinDelay = v);
        Seconds(sleepRandom, 1, "Sleep.AmbientMaxDelay", "最长等待", s => s.Sleep.AmbientMaxDelay, (s, v) => s.Sleep.AmbientMaxDelay = v);
        Integer(sleepRandom, 1, "Sleep.GrouchyLoops", "起床气循环", "次", s => s.Sleep.GrouchyLoops, (s, v) => s.Sleep.GrouchyLoops = v);
    }

    private void CreateMovementFields()
    {
        PilotNote(Page2, "自主移动需要 --experimental-autonomous-movement；速度、距离、权重在下次选择移动时生效，不改变当前动作。");
        var enabled = Switch(Page2, 2, "Movement.Enabled", "启用自主移动", s => s.Movement.Enabled, (s, v) => s.Movement.Enabled = v);
        var body = Dependents(Page2, () => enabled.IsChecked == true);
        var choice = Group(body, "活动选择概率");
        _movementShare = Number(choice, 2, "Movement.SelectionWeight", "自主移动出现概率", "%", s => s.Movement.SelectionWeight * 100, (s, v) => s.Movement.SelectionWeight = v, NumberKind.Percent);
        _ambientShare = new TextBlock { Name = "AmbientShare", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        RegisterName(_ambientShare.Name, _ambientShare);
        var shareRow = FieldRow();
        shareRow.Children.Add(new Label { Content = "随机待机动作概率", Style = (Style)FindResource("FieldLabelStyle") });
        Grid.SetColumn(_ambientShare, 1); shareRow.Children.Add(_ambientShare);
        var shareUnit = new TextBlock { Text = "%", Style = (Style)FindResource("UnitTextStyle") };
        Grid.SetColumn(shareUnit, 2); shareRow.Children.Add(shareUnit); choice.Children.Add(shareRow);
        Note(choice, "当自主移动和随机待机都可用时生效。");
        _movementShare.TextChanged += (_, _) => UpdateAmbientShare();
        Profile(body, "散步", "Lope", s => s.Movement.Lope);
        Profile(body, "闲步", "TrotRelaxed", s => s.Movement.TrotRelaxed);
        Profile(body, "小跑", "RunExcited", s => s.Movement.RunExcited);
        Profile(body, "冲刺", "SprintPanic", s => s.Movement.SprintPanic);
    }

    private void Profile(StackPanel body, string title, string name, Func<PetSettings, MovementProfileSettings> profile)
    {
        var group = Group(body, title);
        var path = "Movement." + name + ".";
        Number(group, 2, path + "Speed", "速度", "DIP/s", s => profile(s).Speed, (s, v) => profile(s).Speed = v);
        Number(group, 2, path + "MinDistance", "最短距离", "DIP", s => profile(s).MinDistance, (s, v) => profile(s).MinDistance = v);
        Number(group, 2, path + "MaxDistance", "最长距离", "DIP", s => profile(s).MaxDistance, (s, v) => profile(s).MaxDistance = v);
        Integer(group, 2, path + "Weight", "权重", "", s => profile(s).Weight, (s, v) => profile(s).Weight = v);
        _fields[^1].Control.ToolTip = "在可用移动动作中，权重越高，被选中的机会越大；设为 0 可排除该动作。";
    }

    private void CreateInteractionFields()
    {
        PilotNote(Page3, "交互参数在下一次点击或动作生效；Rocket 参数需要 --experimental-rocket。");
        var clicks = Group(Page3, "点击判定");
        Milliseconds(clicks, 3, "Interaction.FirstClickWindow", "第一次到第二次点击间隔", s => s.Interaction.FirstClickWindow, (s, v) => s.Interaction.FirstClickWindow = v);
        Milliseconds(clicks, 3, "Interaction.SecondClickWindow", "第二次到第三次点击间隔", s => s.Interaction.SecondClickWindow, (s, v) => s.Interaction.SecondClickWindow = v);
        var drag = Group(Page3, "拖拽");
        var threshold = Number(drag, 3, "Interaction.DragThresholdDip", "拖拽判定阈值", "DIP", s => s.Interaction.DragThresholdDip, (s, v) => s.Interaction.DragThresholdDip = v);
        threshold.ToolTip = "从按下位置移动超过该距离后视为拖拽。DIP 是不随屏幕缩放改变的逻辑像素。";
        Note(drag, "鼠标移动超过该距离后视为拖拽。");
        var rotation = Group(Page3, "连续旋转");
        Seconds(rotation, 3, "Interaction.RotationInitialDuration", "初始持续时间", s => s.Interaction.RotationInitialDuration, (s, v) => s.Interaction.RotationInitialDuration = v);
        Seconds(rotation, 3, "Interaction.RotationExtensionPerClick", "每次点击增加", s => s.Interaction.RotationExtensionPerClick, (s, v) => s.Interaction.RotationExtensionPerClick = v);
        Seconds(rotation, 3, "Interaction.RotationMaxDuration", "最大持续时间", s => s.Interaction.RotationMaxDuration, (s, v) => s.Interaction.RotationMaxDuration = v);
        var rocket = Group(Page3, "火箭冲天");
        Number(rocket, 3, "Interaction.RocketVerticalSpeed", "垂直速度", "DIP/s", s => s.Interaction.RocketVerticalSpeed, (s, v) => s.Interaction.RocketVerticalSpeed = v);
        Milliseconds(rocket, 3, "Interaction.RocketHiddenDuration", "离屏停留", s => s.Interaction.RocketHiddenDuration, (s, v) => s.Interaction.RocketHiddenDuration = v);
        var angry = Group(Page3, "生气反应");
        Integer(angry, 3, "Interaction.CaperLoops", "闹脾气循环次数", "次", s => s.Interaction.CaperLoops, (s, v) => s.Interaction.CaperLoops = v);
    }

    private void CreateDiagnosticsFields()
    {
        PilotNote(Page4, "诊断开关应用后立即更新；本次运行的设置不会保存。");
        var diagnostics = Group(Page4, "多击诊断");
        var enabled = Switch(diagnostics, 4, "Diagnostics.MultiClickDiagnosticsEnabled", "启用多击输入诊断", s => s.Diagnostics.MultiClickDiagnosticsEnabled, (s, v) => s.Diagnostics.MultiClickDiagnosticsEnabled = v);
        var body = Dependents(diagnostics, () => enabled.IsChecked == true);
        _diagnosticsPath = new TextBox { Margin = new Thickness(0, 0, 0, 8), Style = (Style)FindResource("EditorTextBoxStyle"), ToolTip = "指定诊断日志的输出文件路径；留空时使用默认日志路径。" };
        Identify(_diagnosticsPath, "Diagnostics.MultiClickDiagnosticsPath", "日志路径");
        body.Children.Add(new Label { Content = "日志路径", Target = _diagnosticsPath, Padding = new Thickness(0, 4, 0, 6) });
        body.Children.Add(_diagnosticsPath);
        _fields.Add(new("Diagnostics.MultiClickDiagnosticsPath", "日志路径", 4, _diagnosticsPath,
            s => _diagnosticsPath.Text = s.Diagnostics.MultiClickDiagnosticsPath ?? "",
            s => { s.Diagnostics.MultiClickDiagnosticsPath = string.IsNullOrWhiteSpace(_diagnosticsPath.Text) ? null : _diagnosticsPath.Text; return null; }));
        _diagnosticsPath.TextChanged += OnEditableChanged;
        Note(diagnostics, "留空时使用默认日志路径。");
    }

    private void PilotNote(Panel body, string message)
    {
        if (_mainWindow.WorkbenchUnavailableReason != null) Note(body, message);
    }

    private void ApplyPilotFieldAvailability()
    {
        if (_mainWindow.WorkbenchUnavailableReason is not {} unavailable) return;
        foreach (var field in _fields)
        {
            var reason = unavailable(field.Path);
            var attachment = field.Path.StartsWith("Attachment.", StringComparison.Ordinal);
            var flag = attachment ? "--experimental-attachment" :
                field.Path.StartsWith("Normal.RandomIdle", StringComparison.Ordinal) ? "--experimental-random-idle" :
                field.Path.StartsWith("Rest.", StringComparison.Ordinal) || field.Path.StartsWith("Sleep.", StringComparison.Ordinal) ? "--experimental-rest-sleep" :
                field.Path.StartsWith("Movement.", StringComparison.Ordinal) ? "--experimental-autonomous-movement" :
                field.Path.StartsWith("Interaction.Rocket", StringComparison.Ordinal) ? "--experimental-rocket" : null;
            var timing = attachment
                ? field.Path == "Attachment.SnapDistanceDip"
                ? "检测距离上限（24–140 DIP，默认 140）。实际阈值还受猫姬身高与内置上下限限制，只能收紧、不能放宽。"
                : "关闭时立即解除已建立的依附并按既有视觉事务回到待机；开启时需等待下一次合法拖拽释放。"
                : field.Path is "Normal.RandomIdleEnabled" or "Rest.Enabled" or "Sleep.Enabled"
                ? "应用后控制后续排程或进入；进行中的动作保持当前周期。" :
                field.Path.StartsWith("Interaction.", StringComparison.Ordinal)
                ? "下次点击或动作生效。" : field.Path.StartsWith("Movement.", StringComparison.Ordinal)
                ? "下次移动选择生效。" : field.Path.StartsWith("Diagnostics.", StringComparison.Ordinal)
                ? "应用后立即更新。" : "下次周期或排程生效。";
            field.Control.ToolTip = reason ?? (flag == null ? timing : "启动开关：" + flag + "；" + timing);
        }
    }
}
