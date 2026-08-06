using System;
using System.IO;
using System.Reflection;

namespace SmartClassNight;

public static class ShellLinkHelper
{
    public static string? GetTargetPath(string lnkPath)
    {
        try
        {
            // 通过 COM 创建 WScript.Shell 对象
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;

            dynamic shortcut = shell.CreateShortcut(lnkPath);
            return shortcut.TargetPath as string;
        }
        catch
        {
            return null;
        }
    }
}