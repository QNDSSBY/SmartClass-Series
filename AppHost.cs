namespace SmartClassNight
{
    /// <summary>
    /// 全局宿主引用：主窗口（WinUI 的 Window 不是静态可取的，文件选择器等需要窗口句柄的
    /// 组件通过这里拿到主窗口）。仅保存引用，不做其他事情。
    /// </summary>
    public static class AppHost
    {
        /// <summary>主窗口实例（程序启动时赋值）。</summary>
        public static MainWindow? Main { get; set; }
    }
}
