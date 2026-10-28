using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NecoArcDesktopPet.Product;

internal enum ProductFailure { Entry, Dpi, Startup, Runtime, MissingIdleGif }

// Native UI keeps the pre-WPF DPI/JIT boundary intact. Called only before WPF
// starts or after Run has unwound, never inside Dispatcher exception recovery.
internal sealed class ProductFailureNotice
{
    private int shown;
    internal void Show(ProductFailure failure)
    {
        if (Interlocked.Exchange(ref shown, 1) != 0) return;
        var message = failure switch
        {
            ProductFailure.Entry => "启动入口不合法。请直接运行 NecoArcDesktopPet.Product.exe，不要添加命令行参数。",
            ProductFailure.Dpi => "DPI 初始化失败，桌宠无法安全启动。请检查 Windows 显示兼容性设置后重试。",
            ProductFailure.MissingIdleGif => "缺少可加载且已校准的待机 GIF：素材缺失、角色配置缺失或无效、画布不匹配都会导致安全停止。请按 ASSETS 和 CHARACTER-PROFILE 文档准备合法素材与配置，再重新构建或启动。中性占位图不能用于 Product/Pilot 运行。",
            ProductFailure.Runtime => "桌宠遇到运行异常或安全检查未通过，已停止运行。请重新启动；若再次出现，请启用诊断日志后反馈。",
            _ => "桌宠启动失败。请确认程序文件完整后重试；若再次出现，请启用诊断日志后反馈。"
        };
        try { MessageBoxW(0, message, "Neco-Arc 桌宠", 0x10 /* MB_OK | MB_ICONERROR */); }
        catch (Exception) { /* Error reporting must not throw a second exception. */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
