using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartClassNight
{
    /// <summary>
    /// 文件选择对话框（Win32 通用对话框 `GetOpenFileNameW`）。
    ///
    /// 为什么不用 WinRT 的 `FileOpenPicker`：本程序是**非打包（unpackaged）**应用，
    /// 且主窗口处于**全屏（FullScreen presenter）+ 无边框**状态，WinRT 选择器在这种组合下
    /// 容易打不开、被全屏窗口盖住（表现为点了没反应/像卡死），个别系统上还会直接抛 COM 异常导致退出。
    /// 换成由主窗口 HWND 持有的经典 Win32 对话框后：**一定显示在软件窗口之上、模态可靠、可选择多文件**。
    /// </summary>
    public static class FileDialogs
    {
        // ==================== 常用筛选器 ====================

        /// <summary>音频文件筛选器（打铃铃声 / 点歌共用）。</summary>
        public const string AudioFilter =
            "音频文件|*.mp3;*.wav;*.m4a;*.flac;*.wma;*.aac;*.ogg|所有文件|*.*";

        /// <summary>图片筛选器（壁纸）。</summary>
        public const string ImageFilter =
            "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*";

        /// <summary>
        /// 弹出“打开文件”对话框。
        /// </summary>
        /// <param name="owner">宿主窗口句柄（对话框会显示在它之上并模态）。</param>
        /// <param name="title">对话框标题。</param>
        /// <param name="filter">筛选器，形如 <c>"音频文件|*.mp3;*.wav|所有文件|*.*"</c>（用 | 分隔，内部转成 \0）。</param>
        /// <param name="multiSelect">是否允许多选。</param>
        /// <returns>选中的文件完整路径列表；用户取消时返回空列表。</returns>
        public static List<string> PickFiles(IntPtr owner, string title, string filter, bool multiSelect)
        {
            var result = new List<string>();
            IntPtr buffer = IntPtr.Zero;
            const int bufferChars = 64 * 1024;   // 多选时可能返回很多文件
            try
            {
                buffer = Marshal.AllocHGlobal(bufferChars * 2);
                // 清空前几个字符即可（返回的是以 \0\0 结尾的字符串）
                Marshal.WriteInt16(buffer, 0, 0);
                Marshal.WriteInt16(buffer, 2, 0);

                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = owner,
                    lpstrFilter = (filter ?? "").Replace('|', '\0') + "\0",
                    nFilterIndex = 0,
                    lpstrFile = buffer,
                    nMaxFile = bufferChars,
                    lpstrTitle = title ?? "选择文件",
                    Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                            | OFN_HIDEREADONLY | OFN_DONTADDTORECENT
                            | (multiSelect ? OFN_ALLOWMULTISELECT : 0)
                };

                if (!GetOpenFileName(ofn)) return result;   // 用户取消

                string raw = Marshal.PtrToStringUni(buffer) ?? "";
                if (raw.Length == 0) return result;

                if (!multiSelect)
                {
                    result.Add(raw);
                    return result;
                }

                // 多选：返回 "目录\0文件1\0文件2\0\0"；只选一个文件时返回完整路径
                string[] parts = raw.Split('\0');
                var nonEmpty = new List<string>();
                foreach (var p in parts)
                    if (!string.IsNullOrEmpty(p)) nonEmpty.Add(p);

                if (nonEmpty.Count <= 1)
                {
                    if (nonEmpty.Count == 1) result.Add(nonEmpty[0]);
                    return result;
                }

                string dir = nonEmpty[0];
                for (int i = 1; i < nonEmpty.Count; i++)
                    result.Add(Path.Combine(dir, nonEmpty[i]));
                return result;
            }
            catch
            {
                return result;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        // ==================== Win32 ====================

        private const int OFN_HIDEREADONLY = 0x00000004;
        private const int OFN_NOCHANGEDIR = 0x00000008;
        private const int OFN_ALLOWMULTISELECT = 0x00000200;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_EXPLORER = 0x00080000;
        private const int OFN_DONTADDTORECENT = 0x02000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFilter;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFileTitle;
            public int nMaxFileTitle;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);
    }
}
