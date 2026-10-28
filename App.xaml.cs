using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
#if PMV2_PILOT
            base.OnStartup(e);
            Pilot.PilotApplication.StartWindow();
#else
            if (!e.Args.Contains(SettingsPersistenceDiagnostics.Switch, StringComparer.Ordinal))
            {
                base.OnStartup(e);
                new MainWindow().Show();
                return;
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            using var diagnostic = SettingsPersistenceDiagnostics.RunOneShot();
            var text = "一次性保存诊断；未读写真实 settings.json / 备份 / 故障副本。\n"
                + "请保留本次启动来源（资源管理器 / 普通 PowerShell / 其他启动器）。\n"
                + "日志：" + (diagnostic.LogPath ?? "写入失败；请在下方 Ctrl+A、Ctrl+C 复制完整报告。")
                + "\n\n" + diagnostic.Report;
            ShowPersistenceReport(text);
            Shutdown();
#endif
        }

        internal static void ShowPersistenceReport(string text, Window? owner = null)
        {
            var window = new Window
            {
                Title = "Neco-Arc 保存诊断", Width = 900, Height = 650, Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new TextBox
                {
                    Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12)
                }
            };
            window.ShowDialog();
        }
    }
}
