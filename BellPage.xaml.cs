using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SmartClassNight
{
    /// <summary>铃声库里的一行（界面绑定用，仅含显示所需信息）。</summary>
    public sealed class SoundRow
    {
        public SoundRow(string path)
        {
            Path = path ?? "";
            try { Name = System.IO.Path.GetFileName(Path); } catch { Name = Path; }
            if (string.IsNullOrWhiteSpace(Name)) Name = Path;
        }

        /// <summary>铃声文件完整路径。</summary>
        public string Path { get; }
        /// <summary>文件名（含扩展名）。</summary>
        public string Name { get; }
    }

    /// <summary>
    /// 校园自动打铃系统界面（大屏第 2 屏）。
    /// 只做界面与交互，定时、播放、持久化全部交给 BellService。
    /// </summary>
    public sealed partial class BellPage : UserControl
    {
        // 时间输入校验：支持 "8:00"、"08:00"，内部统一归一化为 "HH:mm"
        private static readonly Regex TimeRegex = new(@"^\s*(\d{1,2})\s*[:：]\s*(\d{1,2})\s*$", RegexOptions.Compiled);
        // 导入铃声允许的扩展名
        private static readonly string[] SoundExtensions = { ".mp3", ".wav", ".m4a", ".flac", ".wma", ".aac", ".ogg" };

        private readonly DispatcherTimer _statusTimer;   // 30 秒刷新“下一次打铃”
        private bool _loading;                           // 程序化赋值开关/滑块时抑制回写，避免循环

        public BellPage()
        {
            InitializeComponent();

            // 每 30 秒刷新一次“下一次打铃”提示（跨过时间点后自动顺延）
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();

            RefreshAll();

            BellService.Changed += OnServiceChanged;
            Loaded += BellPage_Loaded;
            Unloaded += BellPage_Unloaded;
        }

        // ==================== 生命周期 / 刷新 ====================

        private void BellPage_Loaded(object sender, RoutedEventArgs e)
        {
            try { RefreshAll(); } catch { }
        }

        private void BellPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // 离开页面必须退订，避免服务持有界面导致泄漏
            try
            {
                BellService.Changed -= OnServiceChanged;
                _statusTimer.Stop();
                _statusTimer.Tick -= StatusTimer_Tick;
            }
            catch { }
        }

        /// <summary>BellService 配置变化时整体刷新界面（服务写操作都会触发）。</summary>
        private void OnServiceChanged()
        {
            try
            {
                if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
                {
                    DispatcherQueue.TryEnqueue(RefreshAll);
                    return;
                }
            }
            catch { }
            RefreshAll();
        }

        private void StatusTimer_Tick(object? sender, object e)
        {
            try { UpdateStatusTexts(); } catch { }
        }

        /// <summary>重设各列表数据源与开关/滑块状态。</summary>
        private void RefreshAll()
        {
            RefreshTimeList();
            RefreshSoundList();
            RefreshNewSoundCombo();
            RefreshSwitches();
            UpdateStatusTexts();
        }

        /// <summary>时间表：清空再赋新列表（Items 每次返回新 List）。</summary>
        private void RefreshTimeList()
        {
            try
            {
                var items = BellService.Items;
                TimeList.ItemsSource = null;
                TimeList.ItemsSource = items;
                EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                TimeCountText.Text = $"共 {items.Count} 项";
            }
            catch { }
        }

        /// <summary>铃声库列表 + 默认铃声说明。</summary>
        private void RefreshSoundList()
        {
            try
            {
                var rows = BellService.SoundLibrary.Select(p => new SoundRow(p)).ToList();
                SoundList.ItemsSource = null;
                SoundList.ItemsSource = rows;
                SoundEmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                string def = BellService.DefaultSound;
                string defName = "";
                if (!string.IsNullOrWhiteSpace(def))
                {
                    try { defName = Path.GetFileName(def); } catch { defName = def; }
                }
                DefaultSoundText.Text = string.IsNullOrWhiteSpace(defName)
                    ? "默认铃声：未设置（打铃时不会响）"
                    : $"默认铃声：{defName}";
            }
            catch { }
        }

        /// <summary>新增行的铃声下拉框：第 0 项 = 默认铃声，其余为铃声库文件。</summary>
        private void RefreshNewSoundCombo()
        {
            try
            {
                string keep = (NewSoundCombo.SelectedItem as SoundRow)?.Path ?? "";
                var rows = new List<SoundRow> { new("") };
                rows.AddRange(BellService.SoundLibrary.Select(p => new SoundRow(p)));

                NewSoundCombo.ItemsSource = null;
                NewSoundCombo.ItemsSource = rows;

                int idx = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (string.Equals(rows[i].Path, keep, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                }
                NewSoundCombo.SelectedIndex = idx;
            }
            catch { }
        }

        /// <summary>同步总开关与音量（_loading 防止把界面赋值写回服务）。</summary>
        private void RefreshSwitches()
        {
            _loading = true;
            try
            {
                EnableSwitch.IsOn = BellService.Enabled;
                VolumeSlider.Value = Math.Round(Math.Clamp(BellService.Volume, 0, 1) * 100);
                VolumeText.Text = $"音量：{(int)VolumeSlider.Value}%";
            }
            catch { }
            finally { _loading = false; }
        }

        /// <summary>底部状态：最近一次打铃 + 下一次打铃。</summary>
        private void UpdateStatusTexts()
        {
            try
            {
                string last = BellService.LastRingInfo;
                LastRingText.Text = "最近：" + (string.IsNullOrWhiteSpace(last) ? "暂无记录" : last);

                if (!BellService.Enabled)
                {
                    NextRingText.Text = "下一次打铃：打铃系统已关闭";
                    return;
                }

                var items = BellService.Items.Where(i => i.Enabled).ToList();
                if (items.Count == 0)
                {
                    NextRingText.Text = "下一次打铃：没有启用的时间（请勾选「启用」）";
                    return;
                }

                // Time 均为 "HH:mm"，可直接字符串比较
                string now = DateTime.Now.ToString("HH:mm");
                BellItem? next = items.FirstOrDefault(i => string.CompareOrdinal(i.Time, now) > 0);

                if (next != null)
                {
                    NextRingText.Text = $"下一次打铃：{next.Time} {next.Label}";
                }
                else
                {
                    // 今天已全部过去 → 取最早的一条，提示“明天”
                    var first = items.OrderBy(i => i.Time, StringComparer.Ordinal).First();
                    NextRingText.Text = $"下一次打铃：明天 {first.Time} {first.Label}";
                }
            }
            catch { }
        }

        // ==================== 时间表操作 ====================

        /// <summary>「添加」：校验时间 → 归一化 HH:mm → BellService.AddItem。</summary>
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string raw = TimeInput.Text ?? "";
                if (!TryNormalizeTime(raw, out string time))
                {
                    // 内联红字提示（例如输入“8点”或空）
                    TimeCountText.Text = "时间格式不正确，请输入如 08:00";
                    TimeCountText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Crimson);
                    return;
                }

                string label = (LabelInput.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(label)) label = time + " 打铃";

                string sound = (NewSoundCombo.SelectedItem as SoundRow)?.Path ?? "";
                BellService.AddItem(time, label, sound, true);

                // 清空输入，方便连续添加（服务已触发 Changed → 界面自动刷新）
                TimeInput.Text = "";
                LabelInput.Text = "";
                if (NewSoundCombo.Items.Count > 0) NewSoundCombo.SelectedIndex = 0;
                ResetInlineHint();
            }
            catch (Exception ex)
            {
                ShowInlineHint("添加失败：" + ex.Message);
            }
        }

        /// <summary>勾选/取消「启用」：DataContext 为该行 BellItem。</summary>
        private void ItemEnabled_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement fe || fe.DataContext is not BellItem item) return;
                if (sender is not CheckBox box) return;
                bool? on = box.IsChecked;   // Click 时 TwoWay 绑定可能尚未回写，优先取控件状态
                bool enabled = on ?? item.Enabled;
                BellService.UpdateItem(item, item.Time, item.Label, item.Sound, enabled);
            }
            catch { }
            finally { ResetInlineHint(); }
        }

        /// <summary>「试听」某一行：item 为空时 BellService 会用默认铃声。</summary>
        private void ItemPreview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement fe || fe.DataContext is not BellItem item) return;
                BellService.Ring(item);
            }
            catch { }
        }

        /// <summary>「删除」某一行。</summary>
        private void ItemDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement fe || fe.DataContext is not BellItem item) return;
                BellService.RemoveItem(item);
            }
            catch { }
        }

        /// <summary>用默认铃声试听（右上与音量卡片共用）。</summary>
        private void PreviewDefault_Click(object sender, RoutedEventArgs e)
        {
            try { BellService.Ring(null); } catch { }
        }

        // ==================== 开关 / 音量 ====================

        private void EnableSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            // 程序化同步时直接返回，避免刷新回写造成循环
            if (_loading) return;
            try { BellService.Enabled = EnableSwitch.IsOn; } catch { }
        }

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try { VolumeText.Text = $"音量：{(int)Math.Round(e.NewValue)}%"; } catch { }

            if (_loading) return;
            try { BellService.Volume = Math.Clamp(e.NewValue / 100.0, 0, 1); } catch { }
        }

        // ==================== 铃声库 ====================

        /// <summary>多选导入铃声文件到铃声库（Win32 对话框，稳定且一定显示在软件窗口之上）。</summary>
        private void AddSounds_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IntPtr owner = IntPtr.Zero;
                try { if (AppHost.Main != null) owner = WindowNative.GetWindowHandle(AppHost.Main); } catch { }

                var paths = FileDialogs.PickFiles(owner, "选择铃声文件（可多选）", FileDialogs.AudioFilter, multiSelect: true);
                if (paths.Count == 0) return;

                BellService.AddSounds(paths);   // 触发 Changed → RefreshAll
            }
            catch (Exception ex)
            {
                ShowInlineHint("添加铃声失败：" + ex.Message);
            }
        }

        /// <summary>把某一行铃声设为默认铃声。</summary>
        private void SetDefault_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement fe || fe.DataContext is not SoundRow row) return;
                BellService.DefaultSound = row.Path;
            }
            catch { }
        }

        /// <summary>从铃声库移除某一行铃声。</summary>
        private void RemoveSound_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement fe || fe.DataContext is not SoundRow row) return;
                BellService.RemoveSound(row.Path);   // 服务会自动换默认铃声、清掉引用该文件的打铃项
            }
            catch { }
        }

        // ==================== 小工具 ====================

        /// <summary>校验并归一化时间为 HH:mm（支持全角冒号）。</summary>
        private static bool TryNormalizeTime(string raw, out string time)
        {
            time = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;

            // 优先 Regex：能识别 "8:5"、"08：00" 这类写法
            var m = TimeRegex.Match(raw);
            if (m.Success)
            {
                if (!int.TryParse(m.Groups[1].Value, out int h)) return false;
                if (!int.TryParse(m.Groups[2].Value, out int mi)) return false;
                if (h < 0 || h > 23 || mi < 0 || mi > 59) return false;
                time = $"{h:D2}:{mi:D2}";
                return true;
            }

            if (TimeSpan.TryParse(raw.Trim(), out var ts) && ts.TotalHours < 24 && ts.TotalMinutes >= 0)
            {
                time = $"{ts.Hours:D2}:{ts.Minutes:D2}";
                return true;
            }
            return false;
        }

        /// <summary>把复用为提示的计数文本恢复成正常状态。</summary>
        private void ResetInlineHint()
        {
            try
            {
                TimeCountText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
                TimeCountText.Text = $"共 {BellService.Items.Count} 项";
            }
            catch { }
        }

        /// <summary>用左上角计数位置显示一行错误提示（不弹对话框，适合大屏）。</summary>
        private void ShowInlineHint(string message)
        {
            try
            {
                TimeCountText.Text = message;
                TimeCountText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Crimson);
            }
            catch { }
        }
    }
}
