using System;
using System.IO;
using System.Reflection;

namespace SmartClassNight;

public static class ShellLinkHelper
{
    public static string? GetTargetPath(string lnkPath)
    {
        // 1) WScript.Shell 解析
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell != null)
                {
                    dynamic shortcut = shell.CreateShortcut(lnkPath);
                    string? target = shortcut.TargetPath as string;
                    if (!string.IsNullOrWhiteSpace(target))
                        return target;
                }
            }
        }
        catch { }

        // 2) 手动解析 .lnk 中的 LinkInfo 绝对路径（目标不存在时 WScript.Shell 可能解析失败）
        return ParseLnkTargetPath(lnkPath);
    }

    /// <summary>
    /// 直接解析 .lnk 二进制文件 LinkInfo 块中的本地绝对路径。
    /// 适用于 WScript.Shell 无法解析（如目标程序不在本机）但仍存储了绝对路径的快捷方式。
    /// </summary>
    public static string? ParseLnkTargetPath(string lnkPath)
    {
        try
        {
            byte[] b = File.ReadAllBytes(lnkPath);
            if (b.Length < 76 || b[0] != 0x4C) return null;   // "L" 魔数

            uint flags = BitConverter.ToUInt32(b, 20);
            int off = 76;

            // HasLinkTargetIDList
            if ((flags & 0x01) != 0)
            {
                ushort idSize = BitConverter.ToUInt16(b, off);
                off += 2 + idSize;
            }

            // HasLinkInfo
            if ((flags & 0x02) != 0 && off + 28 <= b.Length)
            {
                int li = off;
                uint liFlags = BitConverter.ToUInt32(b, li + 8);
                uint localBasePathOffset = BitConverter.ToUInt32(b, li + 16);
                bool unicode = (liFlags & 0x02) != 0;

                int baseOff = li + (int)localBasePathOffset;
                if (baseOff >= 0 && baseOff < b.Length)
                {
                    if (unicode)
                    {
                        int end = baseOff;
                        while (end + 1 < b.Length && !(b[end] == 0 && b[end + 1] == 0)) end += 2;
                        string p = System.Text.Encoding.Unicode.GetString(b, baseOff, end - baseOff);
                        return string.IsNullOrEmpty(p) ? null : p;
                    }
                    else
                    {
                        int end = baseOff;
                        while (end < b.Length && b[end] != 0) end++;
                        string p = System.Text.Encoding.ASCII.GetString(b, baseOff, end - baseOff);
                        return string.IsNullOrEmpty(p) ? null : p;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>创建一个指向 targetPath 的 .lnk 快捷方式。</summary>
    public static bool CreateShortcut(string lnkPath, string targetPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "";
            shortcut.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }
}