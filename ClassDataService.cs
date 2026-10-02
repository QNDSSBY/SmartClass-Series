using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SmartClassNight
{
    // ==================== 数据模型 ====================

    /// <summary>实时天气 + 今日高低温。</summary>
    public sealed class WeatherNow
    {
        public string City { get; set; } = "";
        public string Temp { get; set; } = "";
        public string Weather { get; set; } = "";
        public string Wind { get; set; } = "";        // 风向
        public string WindLevel { get; set; } = "";   // 风力
        public string Humidity { get; set; } = "";
        public string Aqi { get; set; } = "";
        public string Rain24h { get; set; } = "";
        public string High { get; set; } = "";
        public string Low { get; set; } = "";
        public string UpdateTime { get; set; } = "";
        public string TodayHint { get; set; } = "";   // 例如 阴转多云
    }

    /// <summary>未来 2 小时逐分钟降水。</summary>
    public sealed class MinutelyPrecip
    {
        public List<double> Values { get; set; } = new();
        public int FirstRainMinute { get; set; } = -1;   // 从第几分钟开始有降水（-1 = 全程无降水）
        public double MaxValue { get; set; }

        /// <summary>一句话摘要，例如“未来2小时无降水”“约 35 分钟后开始降水”。</summary>
        public string Summary
        {
            get
            {
                if (Values.Count == 0) return "暂无降水数据";
                if (FirstRainMinute < 0) return "未来 2 小时无降水";
                if (FirstRainMinute <= 2) return "正在降水，注意带伞";
                return $"约 {FirstRainMinute} 分钟后开始降水";
            }
        }
    }

    /// <summary>值日生条目（weekday：1=周一 … 7=周日）。</summary>
    public sealed class DutyItem
    {
        public int Weekday { get; set; }
        public string Item { get; set; } = "";
        public string People { get; set; } = "";
    }

    /// <summary>课表条目（weekday：1=周一 … 7=周日；lesson_no：1 起）。</summary>
    public sealed class LessonItem
    {
        public int Weekday { get; set; }
        public int LessonNo { get; set; }
        public string Course { get; set; } = "";
        public string Teacher { get; set; } = "";
    }

    public sealed class ClassInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>晚自习作业条目（字段与班级大屏 dashboard 的 get_homework 一致）。</summary>
    public sealed class HomeworkItem
    {
        public int Id { get; set; }
        /// <summary>科目，如“数学”。</summary>
        public string Subject { get; set; } = "";
        /// <summary>作业内容（已按 dashboard 规则拼接“其他：xxx”）。</summary>
        public string Content { get; set; } = "";
        /// <summary>备注。</summary>
        public string Note { get; set; } = "";
        /// <summary>时间段：晚一 / 晚二 / 晚三 / 其他。</summary>
        public string TimeType { get; set; } = "";
        public string TimeOther { get; set; } = "";
        /// <summary>时间显示文本（其他时段为“其他：xxx”）。</summary>
        public string TimeDisplay { get; set; } = "";
        /// <summary>当前是否正处于该作业对应的晚自习时段（用于高亮）。</summary>
        public bool IsCurrentPeriod { get; set; }
    }

    /// <summary>
    /// 班级/天气数据服务（主页 WebView 里的模块改为本地实现，接口与网页保持一致）：
    /// - 天气：/weather/api.php?action=get_wx_weather&amp;cityid=…
    /// - 未来2小时降水：/weather/api.php?action=get_precip&amp;cityid=…
    /// - 值日生：/weather/api.php?action=get_duty&amp;class_id=…
    /// - 班级列表：/weather/api.php?action=get_classes
    /// - 今日课表：/class/api.php?action=get_schedule&amp;class_id=…
    /// 本地配置（班级 id、城市）保存在 %LOCALAPPDATA%\金华一中科技校园套件\classroom.json。
    /// </summary>
    public static class ClassDataService
    {
        private const string WeatherApi = "https://f18.llt-service.cn/weather/api.php";
        private const string ClassApi = "https://f18.llt-service.cn/class/api.php";
        private const string DefaultCityId = "101210901";     // 金华市
        private const string DefaultCityName = "金华市";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "classroom.json");

        // ==================== 本地配置 ====================

        private sealed class LocalConfig
        {
            public int ClassId { get; set; }
            public string CityId { get; set; } = DefaultCityId;
            public string CityName { get; set; } = DefaultCityName;
            /// <summary>收到新消息时是否播放提示音（设置 → 个性化）。旧配置无此字段时按 true 处理。</summary>
            public bool NotifySound { get; set; } = true;
            /// <summary>主页面壁纸（图片文件路径）。为空时使用内置默认壁纸。</summary>
            public string Wallpaper { get; set; } = "";
            /// <summary>勿扰模式：开启后不做消息提醒（打铃不受影响），关闭后补显示未显示过的消息。</summary>
            public bool Dnd { get; set; }
            /// <summary>主页面（第一屏）显示时隐藏灵动岛消息提醒（右上角“最近消息”卡片仍会显示）。默认开。</summary>
            public bool HideAlertOnHome { get; set; } = true;
        }

        /// <summary>内置默认壁纸（随程序安装：Assets\background.jpg）。</summary>
        public static string BundledWallpaper => Path.Combine(AppContext.BaseDirectory, "Assets", "background.jpg");

        /// <summary>出厂默认壁纸路径（老版本用的 E:\background.JPG，仅作为内置文件的备选）。</summary>
        public const string LegacyDefaultWallpaper = @"E:\background.JPG";

        /// <summary>默认壁纸：优先内置文件，缺失时退回 E:\background.JPG。</summary>
        public static string DefaultWallpaper
            => File.Exists(BundledWallpaper) ? BundledWallpaper : LegacyDefaultWallpaper;

        private static LocalConfig _config = new();
        private static bool _loaded;

        // 注意：getter 里先 Load()，保证在任何时机（含窗口构造期间）读取都能拿到上次保存的班级/城市
        public static int SelectedClassId { get { Load(); return _config.ClassId; } }
        public static string CityId { get { Load(); return string.IsNullOrWhiteSpace(_config.CityId) ? DefaultCityId : _config.CityId; } }
        public static string CityName { get { Load(); return string.IsNullOrWhiteSpace(_config.CityName) ? DefaultCityName : _config.CityName; } }
        /// <summary>班级选择变化时触发（灵动岛课表、消息轮询据此刷新）。</summary>
        public static event Action? ClassChanged;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<LocalConfig>(File.ReadAllText(ConfigPath));
                    if (cfg != null) _config = cfg;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config));
            }
            catch { }
        }

        public static void SetClass(int classId)
        {
            Load();
            if (_config.ClassId == classId) return;
            _config.ClassId = classId;
            Save();
            try { ClassChanged?.Invoke(); } catch { }
        }

        public static void SetCity(string cityId, string cityName)
        {
            Load();
            _config.CityId = cityId;
            _config.CityName = cityName;
            Save();
        }

        /// <summary>新消息提示音开关（默认开）。</summary>
        public static bool NotifySoundEnabled { get { Load(); return _config.NotifySound; } }

        public static void SetNotifySound(bool enabled)
        {
            Load();
            if (_config.NotifySound == enabled) return;
            _config.NotifySound = enabled;
            Save();
        }

        // ==================== 壁纸 ====================

        /// <summary>当前壁纸路径：优先用户自定义，未设置则用默认 E:\background.JPG。</summary>
        public static string WallpaperPath
        {
            get
            {
                Load();
                return string.IsNullOrWhiteSpace(_config.Wallpaper) ? DefaultWallpaper : _config.Wallpaper;
            }
        }

        /// <summary>是否使用默认壁纸（未自定义）。</summary>
        public static bool IsDefaultWallpaper { get { Load(); return string.IsNullOrWhiteSpace(_config.Wallpaper); } }

        public static void SetWallpaper(string path)
        {
            Load();
            _config.Wallpaper = path ?? "";
            Save();
        }

        // ==================== 勿扰模式 / 消息提醒 ====================

        /// <summary>勿扰模式（默认关）。开启后不进行消息提醒，打铃系统不受影响。</summary>
        public static bool DndEnabled { get { Load(); return _config.Dnd; } }

        public static void SetDnd(bool enabled)
        {
            Load();
            if (_config.Dnd == enabled) return;
            _config.Dnd = enabled;
            Save();
            try { DndChanged?.Invoke(enabled); } catch { }
        }

        /// <summary>勿扰模式变化时触发（用于补显示未显示过的消息、刷新界面图标）。</summary>
        public static event Action<bool>? DndChanged;

        /// <summary>主页面显示时是否隐藏灵动岛消息提醒（默认开）。</summary>
        public static bool HideAlertOnHome { get { Load(); return _config.HideAlertOnHome; } }

        public static void SetHideAlertOnHome(bool enabled)
        {
            Load();
            if (_config.HideAlertOnHome == enabled) return;
            _config.HideAlertOnHome = enabled;
            Save();
        }

        // ==================== 接口 ====================

        public static async Task<List<ClassInfo>> GetClassesAsync()
        {
            var list = new List<ClassInfo>();
            try
            {
                string json = await Http.GetStringAsync($"{WeatherApi}?action=get_classes");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out int id)) continue;
                    string name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "" : "";
                    list.Add(new ClassInfo { Id = id, Name = name });
                }
            }
            catch { }
            return list;
        }

        public static async Task<WeatherNow?> GetWeatherAsync(string? cityId = null)
        {
            try
            {
                Load();
                string json = await Http.GetStringAsync(
                    $"{WeatherApi}?action=get_wx_weather&cityid={Uri.EscapeDataString(cityId ?? CityId)}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("realtime", out var rt) || rt.ValueKind != JsonValueKind.Object) return null;

                string todayHint = "";
                string high = "", low = "";
                if (root.TryGetProperty("tonight", out var tn) && tn.ValueKind == JsonValueKind.Object)
                    todayHint = Str(tn, "weather");
                if (root.TryGetProperty("forecast", out var fc) && fc.ValueKind == JsonValueKind.Array && fc.GetArrayLength() > 0)
                {
                    var f0 = fc[0];
                    high = Str(f0, "fc");
                    low = Str(f0, "fd");
                    if (string.IsNullOrWhiteSpace(todayHint)) todayHint = Str(f0, "fa");
                }

                return new WeatherNow
                {
                    City = string.IsNullOrWhiteSpace(Str(rt, "cityname")) ? CityName : Str(rt, "cityname"),
                    Temp = Str(rt, "temp"),
                    Weather = Str(rt, "weather"),
                    Wind = Str(rt, "WD"),
                    WindLevel = Str(rt, "WS"),
                    Humidity = Str(rt, "SD"),
                    Aqi = Str(rt, "aqi"),
                    Rain24h = Str(rt, "rain24h"),
                    UpdateTime = Str(rt, "time"),
                    High = high,
                    Low = low,
                    TodayHint = todayHint
                };
            }
            catch { return null; }
        }

        public static async Task<MinutelyPrecip?> GetPrecipAsync(string? cityId = null)
        {
            try
            {
                Load();
                string json = await Http.GetStringAsync(
                    $"{WeatherApi}?action=get_precip&cityid={Uri.EscapeDataString(cityId ?? CityId)}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return null;

                var result = new MinutelyPrecip();
                int idx = 0;
                foreach (var el in list.EnumerateArray())
                {
                    double v = 0;
                    if (el.TryGetProperty("precip", out var p))
                    {
                        if (p.ValueKind == JsonValueKind.Number) v = p.GetDouble();
                        else if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), out double pv)) v = pv;
                    }
                    result.Values.Add(v);
                    if (v > 0)
                    {
                        if (result.FirstRainMinute < 0) result.FirstRainMinute = idx;
                        result.MaxValue = Math.Max(result.MaxValue, v);
                    }
                    idx++;
                    if (idx >= 120) break;   // 只看未来 2 小时
                }
                return result;
            }
            catch { return null; }
        }

        public static async Task<List<DutyItem>> GetDutyAsync(int classId)
        {
            var list = new List<DutyItem>();
            if (classId <= 0) return list;
            try
            {
                string json = await Http.GetStringAsync($"{WeatherApi}?action=get_duty&class_id={classId}");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(new DutyItem
                    {
                        Weekday = Int(el, "weekday"),
                        Item = Str(el, "item"),
                        People = Str(el, "people")
                    });
                }
            }
            catch { }
            return list;
        }

        public static async Task<List<LessonItem>> GetScheduleAsync(int classId)
        {
            var list = new List<LessonItem>();
            if (classId <= 0) return list;
            try
            {
                string json = await Http.GetStringAsync($"{ClassApi}?action=get_schedule&class_id={classId}");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(new LessonItem
                    {
                        Weekday = Int(el, "weekday"),
                        LessonNo = Int(el, "lesson_no"),
                        Course = Str(el, "course"),
                        Teacher = Str(el, "teacher")
                    });
                }
            }
            catch { }
            return list;
        }

        // ==================== 晚自习作业（与班级大屏 dashboard 一致） ====================

        /// <summary>作业时间段顺序（dashboard 的 timeOrder）。</summary>
        private static readonly string[] HomeworkTimeOrder = { "晚一", "晚二", "晚三", "其他" };

        /// <summary>各时间段对应的分钟区间（dashboard 的 timePeriods），用于高亮“当前正在进行的时段”。</summary>
        private static readonly Dictionary<string, (int Start, int End)> HomeworkPeriods = new()
        {
            ["晚一"] = (18 * 60 + 30, 19 * 60 + 20),
            ["晚二"] = (19 * 60 + 30, 20 * 60 + 20),
            ["晚三"] = (20 * 60 + 30, 21 * 60 + 25)
        };

        /// <summary>
        /// 晚自习作业：<c>/class/api.php?action=get_homework&amp;class_id=…</c>
        /// 返回 <c>[{id, subject, content, other_text, note, time_type, time_other}]</c>，
        /// 按 dashboard 的规则排序（晚一 → 晚二 → 晚三 → 其他）并整理好显示文本。
        /// </summary>
        public static async Task<List<HomeworkItem>> GetHomeworkAsync(int classId)
        {
            var list = new List<HomeworkItem>();
            if (classId <= 0) return list;
            try
            {
                string json = await Http.GetStringAsync($"{ClassApi}?action=get_homework&class_id={classId}");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string timeType = Str(el, "time_type");
                    string timeOther = Str(el, "time_other");
                    string content = Str(el, "content");
                    string otherText = Str(el, "other_text");

                    // 与 dashboard 完全一致：content 为“其他”时拼上 other_text；time_type 为“其他”时拼上 time_other
                    string contentDisplay = content;
                    if (content == "其他" && !string.IsNullOrWhiteSpace(otherText))
                        contentDisplay = $"其他：{otherText}";
                    string timeDisplay = string.IsNullOrWhiteSpace(timeType) ? "其他" : timeType;
                    if (timeType == "其他" && !string.IsNullOrWhiteSpace(timeOther))
                        timeDisplay = $"其他：{timeOther}";

                    list.Add(new HomeworkItem
                    {
                        Id = Int(el, "id"),
                        Subject = Str(el, "subject"),
                        Content = contentDisplay,
                        Note = Str(el, "note"),
                        TimeType = timeType,
                        TimeOther = timeOther,
                        TimeDisplay = timeDisplay
                    });
                }

                int nowMinutes = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
                foreach (var hw in list)
                {
                    if (HomeworkPeriods.TryGetValue(hw.TimeType, out var period))
                        hw.IsCurrentPeriod = nowMinutes >= period.Start && nowMinutes <= period.End;
                }

                list = list
                    .OrderBy(h => Array.IndexOf(HomeworkTimeOrder, h.TimeType) is int i && i >= 0 ? i : HomeworkTimeOrder.Length)
                    .ThenBy(h => h.TimeOther, StringComparer.CurrentCulture)   // 与 dashboard 的 localeCompare 一致
                    .ToList();
            }
            catch { }
            return list;
        }

        public static async Task<(string Id, string Name)?> SearchCityAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                string json = await Http.GetStringAsync(
                    $"{WeatherApi}?action=search_wx_city&city={Uri.EscapeDataString(name.Trim())}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                string id = Str(root, "cityid");
                if (string.IsNullOrWhiteSpace(id)) return null;
                string cityName = Str(root, "name");
                return (id, string.IsNullOrWhiteSpace(cityName) ? name.Trim() : cityName);
            }
            catch { return null; }
        }

        // ==================== 今日数据（统一“今天”的口径） ====================

        /// <summary>今天对应的 weekday（1=周一 … 7=周日）。</summary>
        public static int TodayWeekday()
        {
            int d = (int)DateTime.Now.DayOfWeek;   // 0=周日
            return d == 0 ? 7 : d;
        }

        /// <summary>
        /// 数据里的 weekday 是 0 基（0=周一…6=周日）还是 1 基（1=周一…7=周日）：
        /// 只要出现过 weekday == 0 就说明是 0 基存法。
        /// </summary>
        private static bool IsZeroBasedWeekday<T>(IEnumerable<T> rows, Func<T, int> getWeekday)
            => rows.Any(x => getWeekday(x) == 0);

        /// <summary>今日课表（按数据实际的 weekday 基准换算，周日无课就是无课，不会回退到周六）。</summary>
        public static List<LessonItem> TodayLessons(IEnumerable<LessonItem> all)
            => LessonsForWeekday(all, TodayWeekday());

        /// <summary>明日课表（明天是周日就按周日算，同样不会错位到别的天）。</summary>
        public static List<LessonItem> TomorrowLessons(IEnumerable<LessonItem> all)
        {
            int tomorrow = TodayWeekday() + 1;
            if (tomorrow > 7) tomorrow = 1;      // 周日 → 下周一
            return LessonsForWeekday(all, tomorrow);
        }

        /// <summary>
        /// 取指定 weekday（1=周一 … 7=周日）的课表：自动识别数据是 0 基还是 1 基存法。
        /// </summary>
        public static List<LessonItem> LessonsForWeekday(IEnumerable<LessonItem> all, int weekday)
        {
            var list = all as IList<LessonItem> ?? all.ToList();
            if (list.Count == 0) return new List<LessonItem>();

            int key = IsZeroBasedWeekday(list, x => x.Weekday) ? weekday - 1 : weekday;
            return list.Where(x => x.Weekday == key).OrderBy(x => x.LessonNo).ToList();
        }

        /// <summary>今日值日（同样按实际 weekday 基准换算）。</summary>
        public static List<DutyItem> TodayDuty(IEnumerable<DutyItem> all)
        {
            var list = all as IList<DutyItem> ?? all.ToList();
            if (list.Count == 0) return new List<DutyItem>();

            int today = TodayWeekday();
            int key = IsZeroBasedWeekday(list, x => x.Weekday) ? today - 1 : today;
            return list.Where(x => x.Weekday == key).ToList();
        }

        private static string Str(JsonElement el, string name)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return "";
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Number => v.ToString(),
                    _ => ""
                };
            }
            catch { return ""; }
        }

        private static int Int(JsonElement el, string name)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return 0;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
            }
            catch { }
            return 0;
        }
    }
}
