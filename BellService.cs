using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace SmartClassNight
{
    /// <summary>一条打铃设置。</summary>
    public sealed class BellItem
    {
        /// <summary>时间，格式 HH:mm。</summary>
        public string Time { get; set; } = "08:00";
        /// <summary>名称（如“第 1 节上课”“课间操”）。</summary>
        public string Label { get; set; } = "";
        /// <summary>铃声文件路径；为空则使用默认铃声。</summary>
        public string Sound { get; set; } = "";
        public bool Enabled { get; set; } = true;

        public string TimeDisplay => string.IsNullOrWhiteSpace(Time) ? "--:--" : Time;
        public string SoundDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Sound)) return string.IsNullOrWhiteSpace(DefaultLabel) ? "默认铃声" : "默认铃声（" + DefaultLabel + "）";
                try { return Path.GetFileNameWithoutExtension(Sound); } catch { return Sound; }
            }
        }

        /// <summary>由 BellService 在返回列表时填充，仅用于界面显示。</summary>
        internal string DefaultLabel { get; set; } = "";
    }

    /// <summary>打铃配置（bell.json）。</summary>
    public sealed class BellConfig
    {
        public bool Enabled { get; set; } = false;
        /// <summary>音量 0…1。</summary>
        public double Volume { get; set; } = 0.8;
        /// <summary>默认铃声文件（未单独指定铃声的打铃项使用它）。</summary>
        public string DefaultSound { get; set; } = "";
        /// <summary>铃声库（“添加铃声”导入的文件，可在打铃项里选择）。</summary>
        public List<string> Sounds { get; set; } = new();
        /// <summary>打铃时间表。</summary>
        public List<BellItem> Items { get; set; } = new();
    }

    /// <summary>
    /// 校园自动打铃服务：与“打铃系统”界面解耦的全局定时器，
    /// 保证不论当前显示哪一屏（或界面在后台），到点都会打铃。
    /// 配置保存在 %LOCALAPPDATA%\金华一中科技校园套件\bell.json。
    /// </summary>
    public static class BellService
    {
        private static readonly object Lock = new();
        private static BellConfig _config = new();
        private static bool _loaded;
        private static DispatcherTimer? _timer;
        private static readonly HashSet<string> _firedKeys = new();     // 已响过的“日期+时间+序号”，避免同一分钟重复
        private static AudioEngine? _engine;
        private static DispatcherTimer? _bellInfoTimer;      // 打铃信息条的兜底/最短显示计时
        private static DateTime _bellInfoShownAt = DateTime.MinValue;

        /// <summary>打铃配置变化（界面据此刷新）。</summary>
        public static event Action? Changed;

        public static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "bell.json");

        /// <summary>最近一次打铃的说明（供界面显示，如“12:00 已打铃”）。</summary>
        public static string LastRingInfo { get; private set; } = "";

        // ==================== 配置读写 ====================

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<BellConfig>(File.ReadAllText(ConfigPath));
                    if (cfg != null) _config = cfg;
                }
            }
            catch { }

            // 首次运行：给一套常见的作息时间，方便直接使用（默认关闭，需要用户手动开启）
            if (_config.Items.Count == 0)
            {
                _config.Items = new List<BellItem>
                {
                    new() { Time = "08:00", Label = "第 1 节上课" },
                    new() { Time = "08:45", Label = "第 1 节下课" },
                    new() { Time = "09:45", Label = "第 2 节下课" },
                };
            }
        }

        public static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static void NotifyChanged()
        {
            try { Changed?.Invoke(); } catch { }
        }

        // ==================== 对外属性 ====================

        public static bool Enabled
        {
            get { Load(); return _config.Enabled; }
            set { Load(); _config.Enabled = value; Save(); NotifyChanged(); }
        }

        /// <summary>音量 0…1。</summary>
        public static double Volume
        {
            get { Load(); return _config.Volume; }
            set
            {
                Load();
                _config.Volume = Math.Clamp(value, 0, 1);
                Save();
                if (_engine != null) _engine.Volume = _config.Volume;
                NotifyChanged();
            }
        }

        public static string DefaultSound
        {
            get { Load(); return _config.DefaultSound; }
            set { Load(); _config.DefaultSound = value ?? ""; Save(); NotifyChanged(); }
        }

        public static List<string> SoundLibrary
        {
            get { Load(); return _config.Sounds.ToList(); }
        }

        public static List<BellItem> Items
        {
            get
            {
                Load();
                string defLabel = string.IsNullOrWhiteSpace(_config.DefaultSound)
                    ? "" : SafeName(_config.DefaultSound);
                foreach (var it in _config.Items) it.DefaultLabel = defLabel;
                return _config.Items.OrderBy(x => x.Time).ToList();
            }
        }

        private static string SafeName(string path)
        {
            try { return Path.GetFileNameWithoutExtension(path); } catch { return path; }
        }

        // ==================== 铃声库 / 打铃项编辑 ====================

        public static void AddSounds(IEnumerable<string> files)
        {
            Load();
            foreach (var f in files)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                if (!_config.Sounds.Any(s => string.Equals(s, f, StringComparison.OrdinalIgnoreCase)))
                    _config.Sounds.Add(f);
                if (string.IsNullOrWhiteSpace(_config.DefaultSound)) _config.DefaultSound = f;
            }
            Save();
            NotifyChanged();
        }

        public static void RemoveSound(string file)
        {
            Load();
            _config.Sounds.RemoveAll(s => string.Equals(s, file, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_config.DefaultSound, file, StringComparison.OrdinalIgnoreCase))
                _config.DefaultSound = _config.Sounds.FirstOrDefault() ?? "";
            foreach (var it in _config.Items)
                if (string.Equals(it.Sound, file, StringComparison.OrdinalIgnoreCase)) it.Sound = "";
            Save();
            NotifyChanged();
        }

        public static void AddItem(string time, string label, string sound, bool enabled = true)
        {
            Load();
            _config.Items.Add(new BellItem { Time = time, Label = label, Sound = sound ?? "", Enabled = enabled });
            Save();
            NotifyChanged();
        }

        public static void RemoveItem(BellItem item)
        {
            Load();
            _config.Items.RemoveAll(i => i == item);
            Save();
            NotifyChanged();
        }

        public static void UpdateItem(BellItem item, string time, string label, string sound, bool enabled)
        {
            Load();
            item.Time = time;
            item.Label = label;
            item.Sound = sound ?? "";
            item.Enabled = enabled;
            Save();
            NotifyChanged();
        }

        // ==================== 定时打铃 ====================

        /// <summary>应用启动时调用一次：每秒检查一次时间表，到点打铃。</summary>
        public static void Start()
        {
            Load();
            _engine = new AudioEngine(_config.Volume);
            _engine.Failed += msg => DynamicIslandLog("打铃播放失败：" + msg);
            _engine.Ended += () => EndBellInfo();      // 铃声播完 → 灵动岛恢复原状

            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private static void Tick()
        {
            try
            {
                Load();
                if (!_config.Enabled) return;

                var now = DateTime.Now;
                string hhmm = now.ToString("HH:mm");
                string day = now.ToString("yyyy-MM-dd");

                if (_firedKeys.Count > 200) _firedKeys.Clear();

                for (int i = 0; i < _config.Items.Count; i++)
                {
                    var item = _config.Items[i];
                    if (!item.Enabled) continue;
                    if (!string.Equals(item.Time, hhmm, StringComparison.Ordinal)) continue;

                    string key = $"{day} {hhmm} #{i}";
                    if (_firedKeys.Contains(key)) continue;
                    _firedKeys.Add(key);
                    Ring(item);
                }
            }
            catch { }
        }

        /// <summary>立即打铃（界面“试听/立即打铃”按钮用）。</summary>
        public static void Ring(BellItem? item)
        {
            try
            {
                Load();
                string? file = null;
                if (item != null && !string.IsNullOrWhiteSpace(item.Sound)) file = item.Sound;
                if (string.IsNullOrWhiteSpace(file)) file = _config.DefaultSound;

                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    LastRingInfo = $"{DateTime.Now:HH:mm} 未设置铃声文件";
                    DynamicIslandLog("打铃：" + LastRingInfo);
                    NotifyChanged();
                    return;
                }

                _engine ??= new AudioEngine(_config.Volume);
                _engine.Volume = Math.Clamp(_config.Volume, 0, 1);
                _engine.Load(file, autoPlay: true);

                string label = item != null && !string.IsNullOrWhiteSpace(item.Label) ? item.Label : "打铃";
                LastRingInfo = $"{DateTime.Now:HH:mm} {label}（{SafeName(file)}）";
                DynamicIslandLog("打铃：" + LastRingInfo);
                NotifyChanged();

                StartBellInfo(label);      // 灵动岛左右拉长显示「正在播放铃声 | 任务名」
            }
            catch (Exception ex)
            {
                DynamicIslandLog("打铃异常：" + ex.Message);
            }
        }

        // ==================== 灵动岛信息条（打铃中） ====================

        /// <summary>打铃开始：显示信息条；最短显示 8 秒，最长 90 秒兜底恢复。</summary>
        private static void StartBellInfo(string taskName)
        {
            try
            {
                _bellInfoShownAt = DateTime.Now;
                MainWindow.AppHostMain?.ShowIslandBellInfo(string.IsNullOrWhiteSpace(taskName) ? "打铃" : taskName);

                _bellInfoTimer?.Stop();
                _bellInfoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
                _bellInfoTimer.Tick += (_, _) => EndBellInfo();
                _bellInfoTimer.Start();
            }
            catch { }
        }

        /// <summary>铃声播完：至少显示 8 秒后恢复灵动岛原状。</summary>
        private static void EndBellInfo()
        {
            try
            {
                double shown = (DateTime.Now - _bellInfoShownAt).TotalSeconds;
                double minShow = 8;
                if (shown < minShow)
                {
                    // 铃声很短（如提示音）也要看清楚任务名：等满最短时间再恢复
                    _bellInfoTimer?.Stop();
                    _bellInfoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(minShow - shown) };
                    _bellInfoTimer.Tick += (_, _) => EndBellInfoNow();
                    _bellInfoTimer.Start();
                    return;
                }
                EndBellInfoNow();
            }
            catch { }
        }

        private static void EndBellInfoNow()
        {
            try
            {
                _bellInfoTimer?.Stop();
                _bellInfoTimer = null;
                _bellInfoShownAt = DateTime.MinValue;
                MainWindow.AppHostMain?.ClearIslandBellInfo();
            }
            catch { }
        }

        private static void DynamicIslandLog(string message) => MainWindow.IslandLog(message);
    }
}
