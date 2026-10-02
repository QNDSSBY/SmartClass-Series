using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace SmartClassNight
{
    /// <summary>
    /// 教师通知轮询服务：
    /// - 轮询 api.php 的 get_announcements 获取本班新公告；
    /// - 发现新公告时通过 Windows 通知中心（Toast）显示；
    /// - 记录最后已通知的公告 id，重启后不会重复通知旧公告。
    /// </summary>
    public static class NotificationService
    {
        private const string ApiBase = "https://f18.llt-service.cn/weather/api.php";
        private const string Aumid = "SmartClassNight.ZhihuiKetang";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static int _lastNotifiedId = -1;
        private static bool _initialized = false;

        private static string LastIdFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "lastAnnouncementId.txt");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

        /// <summary>应用启动时调用一次：设置 AUMID 并读取上次已通知的公告 id。</summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                SetCurrentProcessExplicitAppUserModelID(Aumid);
                if (File.Exists(LastIdFile) && int.TryParse(File.ReadAllText(LastIdFile).Trim(), out int last))
                    _lastNotifiedId = last;
            }
            catch { }
        }

        /// <summary>轮询一次：有新公告则通过 Windows 通知中心显示，并返回新公告列表（供灵动岛轮播）。
        /// 轮询方案与首页 WebView 中的 weather 页面保持一致：action=get_announcements&class_id=…，
        /// 字段 id / content / sender_subject / timestamp；content 为 HTML，这里先清洗成纯文本。</summary>
        public static async Task<List<AnnouncementMessage>> PollAsync(int? classId)
        {
            var result = new List<AnnouncementMessage>();
            if (classId == null || classId <= 0) return result;
            try
            {
                string url = $"{ApiBase}?action=get_announcements&class_id={classId}";
                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

                var newItems = new List<(int Id, string Content, string Sender, string? ImageUrl)>();
                int maxId = _lastNotifiedId;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out int id)) continue;
                    if (id > maxId) maxId = id;
                    if (id <= _lastNotifiedId) continue;

                    string rawContent = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? "" : "";
                    string content = CleanHtml(rawContent);
                    if (string.IsNullOrWhiteSpace(content)) content = "收到一条新消息";
                    string sender = el.TryGetProperty("sender_subject", out var s) && s.ValueKind == JsonValueKind.String
                        ? (s.GetString() ?? "").Trim() : "";
                    newItems.Add((id, content, sender, MessageHtml.ExtractImageUrls(rawContent).FirstOrDefault()));
                }

                if (newItems.Count > 0)
                {
                    // 首次运行（无历史记录）：历史公告只作基线，仅提示其中最新的一条，
                    // 既不会一次弹出全部历史公告，也不会漏掉刚收到的新消息。
                    // 基线（历史公告）不发声：提示音只对“运行期间真的收到的新消息”播放。
                    // 首次运行（无历史记录）：历史公告只作基线，仅提示其中最新的一条
                    bool isBaseline = _lastNotifiedId < 0;
                    if (isBaseline)
                        newItems = new List<(int Id, string Content, string Sender, string? ImageUrl)> { newItems.OrderBy(x => x.Id).Last() };

                    // 注意：提示音与通知中心 Toast 由调用方（MainWindow）按“勿扰模式 / 前台全屏 / 主页面隐藏”规则决定是否显示，
                    // 这里只负责把新消息解析出来并记录基线。
                    foreach (var it in newItems.OrderBy(x => x.Id))
                    {
                        result.Add(new AnnouncementMessage
                        {
                            Id = it.Id,
                            Content = it.Content,
                            Sender = it.Sender,
                            ImageUrl = it.ImageUrl
                        });
                    }
                    _lastNotifiedId = maxId;
                    try { File.WriteAllText(LastIdFile, maxId.ToString()); } catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>公告正文是 HTML（可能带图片/换行），转成适合通知与灵动岛显示的纯文本。</summary>
        private static string CleanHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            try
            {
                // 1) 去掉 HTML 标签（raw 形式）
                string text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ");
                // 2) 反转义（有些公告把 <img> 以 &lt;img ...&gt; 的形式存着）
                text = System.Net.WebUtility.HtmlDecode(text);
                // 3) 反转义后可能又出现标签/超长 base64 内联图 → 再清一遍，
                //    否则“最近消息”卡片和灵动岛滚动会显示几百 KB 的 base64 乱码
                text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", " ");
                text = System.Text.RegularExpressions.Regex.Replace(text,
                    @"data:image/[a-zA-Z0-9.+-]+;base64,[A-Za-z0-9+/=\s]+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // 4) 公告正文可能被数据库截断（content 最长 65535，base64 图常常没有结尾的 ">），
                //    这里把残缺的 img 标签也清掉，避免残留 <img src= 这种半截文字
                text = System.Text.RegularExpressions.Regex.Replace(text, @"<\s*img[^>]*", " ",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]*$", " ");
                text = text.Replace("<", " ").Replace(">", " ");
                text = text.Replace('\u00a0', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                text = System.Text.RegularExpressions.Regex.Replace(text, " {2,}", " ");
                return text.Trim();
            }
            catch { return html; }
        }

        /// <summary>通过 Windows 通知中心显示一条 Toast。
        /// 注意：这里把 Toast 自身的声音关掉（&lt;audio silent="true"/&gt;），
        /// 提示音统一由 NotificationSound 播放网页同款 /alarm.mp3，避免“系统通知音 + 提示音”双响。</summary>
        public static void ShowToast(string title, string body)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(title));
                texts[1].AppendChild(xml.CreateTextNode(body));

                var audio = xml.CreateElement("audio");
                audio.SetAttribute("silent", "true");
                xml.DocumentElement.AppendChild(audio);

                var toast = new ToastNotification(xml);
                ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
            }
            catch { }
        }
    }

    /// <summary>轮询到的一条班级公告（供灵动岛滚动与图片预览）。</summary>
    public sealed class AnnouncementMessage
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public string Sender { get; set; } = "";
        /// <summary>公告里的第一张图片地址（无图片为 null）。</summary>
        public string? ImageUrl { get; set; }
    }
}
