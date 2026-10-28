using System;
using System.IO;
using Microsoft.Win32;

namespace NecoArcDesktopPet.Product;

public interface IAutostartStore
{
    string? Read();
    void Write(string value);
    void Delete();
}

public sealed class CurrentUserRunStore : IAutostartStore
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "NecoArcDesktopPet.SourceOnly";
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value != null && value is not string)
            throw new InvalidOperationException("同名自启动项的类型不受支持，未修改它。");
        return (string?)value;
    }
    public void Write(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true) ?? throw new InvalidOperationException("无法打开当前用户的自启动注册表。");
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
        key?.DeleteValue(ValueName, false);
    }
}

public sealed class AutostartManager
{
    private readonly IAutostartStore _store;
    private readonly string _executable;
    public AutostartManager(IAutostartStore store, string executable)
    {
        _store = store;
        _executable = Path.GetFullPath(executable);
        if (!string.Equals(Path.GetFileName(_executable), "NecoArcDesktopPet.Product.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("自启动目标不是 Product 程序。", nameof(executable));
    }
    public bool IsEnabled() => string.Equals(_store.Read(), Quote(_executable), StringComparison.OrdinalIgnoreCase);
    public void SetEnabled(bool enabled)
    {
        var existing = _store.Read();
        if (existing != null && !IsProductEntry(existing))
            throw new InvalidOperationException("同名自启动项不属于本程序，未修改它。");
        if (enabled) _store.Write(Quote(_executable));
        else if (existing != null) _store.Delete();
        if (IsEnabled() != enabled) throw new InvalidOperationException("自启动状态验证失败。");
    }
    private static string Quote(string path) => "\"" + path + "\"";
    private static bool IsProductEntry(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return false;
        var path = value.Substring(1, value.Length - 2);
        return Path.IsPathFullyQualified(path) && string.Equals(Path.GetFileName(path), "NecoArcDesktopPet.Product.exe", StringComparison.OrdinalIgnoreCase);
    }
}
