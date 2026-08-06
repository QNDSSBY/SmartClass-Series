using System;
using System.Diagnostics;
using System.IO;

namespace SmartClassNight
{
    public static class PluginRunner
    {
        /// <summary>
        /// 运行 D:\Smart_Class\Plugin\startup 文件夹中的所有 .lnk 快捷方式
        /// </summary>
        public static void RunStartupPlugins()
        {
            string startupDir = @"D:\Smart_Class\Plugin\startup";
            if (!Directory.Exists(startupDir))
                return;

            foreach (var lnk in Directory.GetFiles(startupDir, "*.lnk"))
            {
                try
                {
                    // 解析快捷方式指向的真实路径
                    string? target = ShellLinkHelper.GetTargetPath(lnk);
                    if (!string.IsNullOrEmpty(target) && File.Exists(target))
                    {
                        Process.Start(new ProcessStartInfo(target)
                        {
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"启动插件失败：{lnk}，错误：{ex.Message}");
                }
            }
        }
    }
}