using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SmartClassNight
{
    /// <summary>
    /// 全局共享的 WebView2 环境。
    /// 使用 %LOCALAPPDATA%\金华一中科技校园套件\WebView2 作为固定的用户数据目录，
    /// 保证 localStorage / Cookie / IndexedDB 等持久可用（不再依赖 exe 目录，更新/重装也不会丢失）。
    /// 同一进程内所有 WebView2（主页天气、更多功能网页）共用同一个环境。
    /// </summary>
    public static class WebView2EnvironmentHelper
    {
        private static Task<CoreWebView2Environment>? _envTask;

        public static Task<CoreWebView2Environment> GetAsync()
        {
            // 任务型惰性初始化：两个 WebView 都在 UI 线程顺序初始化，无并发竞争
            return _envTask ??= CreateAsync();
        }

        private static async Task<CoreWebView2Environment> CreateAsync()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "金华一中科技校园套件", "WebView2");
            Directory.CreateDirectory(folder);
            return await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, null);
        }
    }
}
