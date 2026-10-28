using System.Windows;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private WorkbenchWindow? _workbenchWindow;

    private void OpenWorkbench()
    {
        if (_workbenchWindow != null)
        {
            if (_workbenchWindow.WindowState == WindowState.Minimized)
                _workbenchWindow.WindowState = WindowState.Normal;
            _workbenchWindow.Activate();
            _workbenchWindow.Focus();
            return;
        }

        var window = new WorkbenchWindow(this) { Owner = this };
        _workbenchWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_workbenchWindow, window)) _workbenchWindow = null;
        };
        window.Show();
    }

    // Both menu and Workbench keep the speech bubble's existing Topmost behavior.
    internal void ApplyTopmost(bool value)
    {
        Topmost = value;
        if (_bubbleWindow != null) _bubbleWindow.Topmost = value;
    }
}
