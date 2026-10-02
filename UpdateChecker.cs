using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SmartClassNight
{
    /// <summary>
    /// 一次更新检查的结果。
    /// </summary>
    public sealed class UpdateInfo
    {
        /// <summary>是否成功完成检查（网络与解析均成功）。</summary>
        public bool Succeeded { get; set; }
        /// <summary>是否存在可用的新版本。</summary>
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }

    public static class UpdateChecker
    {
        /// <summary>
        /// 检查更新：下载并解析更新清单，与当前版本比较。
        /// 兼容的清单格式：纯文本版本号、key=value、以及 JSON。
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync(
            HttpClient client,
            string currentVersion,
            string updateUrl,
            string fallbackDownloadUrl)
        {
            var info = new UpdateInfo
            {
                CurrentVersion = currentVersion,
                DownloadUrl = fallbackDownloadUrl
            };

            try
            {
                using var response = await client.GetAsync(updateUrl);
                if (!response.IsSuccessStatusCode)
                {
                    info.ErrorMessage = $"服务器返回状态码 {(int)response.StatusCode}";
                    return info;
                }

                string content = await response.Content.ReadAsStringAsync();
                ParseManifest(content, fallbackDownloadUrl, info);

                if (string.IsNullOrWhiteSpace(info.LatestVersion))
                {
                    info.ErrorMessage = "无法从更新信息中解析版本号";
                    return info;
                }

                info.Succeeded = true;
                info.IsUpdateAvailable = CompareVersions(info.LatestVersion, currentVersion) > 0;
                return info;
            }
            catch (TaskCanceledException)
            {
                info.ErrorMessage = "检查更新超时，请稍后重试";
                return info;
            }
            catch (HttpRequestException ex)
            {
                info.ErrorMessage = "无法连接更新服务器：" + ex.Message;
                return info;
            }
            catch (Exception ex)
            {
                info.ErrorMessage = "检查更新失败：" + ex.Message;
                return info;
            }
        }

        private static void ParseManifest(string content, string fallbackDownloadUrl, UpdateInfo info)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            string trimmed = content.Trim();

            // 1) JSON 清单：{ "version": "1.1.4", "url": "...", "notes": "..." }
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                        info.LatestVersion = NormalizeVersion(v.GetString() ?? "");
                    else if (root.TryGetProperty("latest_version", out v) && v.ValueKind == JsonValueKind.String)
                        info.LatestVersion = NormalizeVersion(v.GetString() ?? "");

                    if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
                        info.DownloadUrl = u.GetString()!;
                    else if (root.TryGetProperty("download_url", out u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
                        info.DownloadUrl = u.GetString()!;

                    if (root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String)
                        info.ReleaseNotes = (n.GetString() ?? "").Trim();
                    else if (root.TryGetProperty("changelog", out n) && n.ValueKind == JsonValueKind.String)
                        info.ReleaseNotes = (n.GetString() ?? "").Trim();
                    return;
                }
                catch (JsonException)
                {
                    // 不是合法 JSON，按纯文本继续解析
                }
            }

            // 2) 纯文本 / key=value 清单
            string[] lines = trimmed.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                // key=value 形式
                if (line.Contains('='))
                {
                    int eq = line.IndexOf('=');
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "version":
                        case "latest":
                        case "latest_version":
                            if (string.IsNullOrEmpty(info.LatestVersion) && !string.IsNullOrWhiteSpace(value))
                                info.LatestVersion = NormalizeVersion(value);
                            continue;
                        case "url":
                        case "download":
                        case "download_url":
                            if (!string.IsNullOrWhiteSpace(value))
                                info.DownloadUrl = value;
                            continue;
                        case "notes":
                        case "changelog":
                            if (!string.IsNullOrWhiteSpace(value))
                                AppendNotes(info, value);
                            continue;
                    }
                }

                // 版本号
                string? versionToken = ExtractVersionToken(line);
                if (versionToken != null)
                {
                    if (string.IsNullOrEmpty(info.LatestVersion))
                        info.LatestVersion = NormalizeVersion(versionToken);
                    continue;
                }

                // URL
                if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    if (info.DownloadUrl == fallbackDownloadUrl)
                        info.DownloadUrl = line;
                    continue;
                }

                // 其余内容视为更新说明
                AppendNotes(info, line);
            }
        }

        private static void AppendNotes(UpdateInfo info, string note)
        {
            info.ReleaseNotes = info.ReleaseNotes.Length > 0
                ? info.ReleaseNotes + Environment.NewLine + note
                : note;
        }

        private static string? ExtractVersionToken(string line)
        {
            var m = Regex.Match(line, @"\d+(\.\d+){1,3}");
            return m.Success ? m.Value : null;
        }

        private static string NormalizeVersion(string version)
        {
            string v = version.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase) && v.Length > 1 && char.IsDigit(v[1]))
                v = v.Substring(1);
            var m = Regex.Match(v, @"\d+(\.\d+){1,3}");
            return m.Success ? m.Value : "";
        }

        /// <summary>逐段比较两个版本号：a &gt; b 返回 1，a &lt; b 返回 -1，相等返回 0。</summary>
        public static int CompareVersions(string a, string b)
        {
            int[] va = Parse(a);
            int[] vb = Parse(b);
            int len = Math.Max(va.Length, vb.Length);
            for (int i = 0; i < len; i++)
            {
                int x = i < va.Length ? va[i] : 0;
                int y = i < vb.Length ? vb[i] : 0;
                if (x > y) return 1;
                if (x < y) return -1;
            }
            return 0;
        }

        private static int[] Parse(string version)
        {
            string[] parts = version.Trim().Split('.');
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), out int n)) n = 0;
                nums[i] = n;
            }
            return nums;
        }
    }
}
