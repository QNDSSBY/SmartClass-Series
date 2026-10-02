using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SmartClassNight
{
    /// <summary>公告 HTML 工具：提取图片地址（相对路径按站点根解析，兼容 data-src 懒加载写法）。</summary>
    public static class MessageHtml
    {
        private const string SiteRoot = "https://f18.llt-service.cn";

        public static List<string> ExtractImageUrls(string? html)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(html)) return list;
            try
            {
                foreach (Match tag in Regex.Matches(html, "<img[^>]*>", RegexOptions.IgnoreCase))
                {
                    var attr = Regex.Match(tag.Value,
                        "(?:data-src|data-original|src)\\s*=\\s*[\"']([^\"']+)[\"']",
                        RegexOptions.IgnoreCase);
                    if (!attr.Success) continue;

                    string src = attr.Groups[1].Value.Trim().Replace("&amp;", "&");
                    if (src.Length == 0) continue;

                    // base64 内联图片（data:image/...;base64,xxxx）保留原样，交给 ImagePreview 解码
                    if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (src.Contains("base64", StringComparison.OrdinalIgnoreCase) && !list.Contains(src))
                            list.Add(src);
                        continue;
                    }

                    if (src.StartsWith("//", StringComparison.Ordinal)) src = "https:" + src;
                    else if (src.StartsWith("/", StringComparison.Ordinal)) src = SiteRoot + src;
                    else if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) src = SiteRoot + "/" + src;

                    if (!list.Contains(src)) list.Add(src);
                }

                // 某些公告把 base64 直接写在 img 之外（或用了单引号/无引号），再兜底扫一遍 data:image
                foreach (Match m in Regex.Matches(html, @"data:image/[a-zA-Z0-9.+-]+;base64,[A-Za-z0-9+/=\s]+"))
                {
                    string src = Regex.Replace(m.Value, @"\s+", "");
                    if (src.Length > 64 && !list.Contains(src)) list.Add(src);
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 把 base64 内联图片（data:image/...;base64,xxxx）解码保存成缓存文件，返回本地路径。
        /// 不是有效的 base64 图片时返回 null。同时支持传入**纯 base64 字符串**（无 data: 前缀）。
        /// </summary>
        public static string? TryDecodeBase64Image(string dataOrBase64, string cacheDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dataOrBase64)) return null;

                string text = dataOrBase64.Trim();
                string ext = ".png";
                string base64;

                if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = text.IndexOf(',');
                    if (comma < 0) return null;
                    string meta = text.Substring(5, comma - 5).ToLowerInvariant();   // 例如 image/png;base64
                    if (!meta.Contains("image")) return null;
                    if (meta.Contains("jpeg") || meta.Contains("jpg")) ext = ".jpg";
                    else if (meta.Contains("gif")) ext = ".gif";
                    else if (meta.Contains("bmp")) ext = ".bmp";
                    else if (meta.Contains("webp")) ext = ".webp";
                    else if (meta.Contains("svg")) return null;                       // 不看 svg，避免脚本
                    base64 = text.Substring(comma + 1);
                }
                else
                {
                    // 纯 base64 兜底：只接受看起来像 base64 的长串
                    if (text.Length < 64) return null;
                    if (!Regex.IsMatch(text, @"^[A-Za-z0-9+/=\s]+$")) return null;
                    base64 = text;
                }

                base64 = Regex.Replace(base64, @"\s+", "");
                if (base64.Length < 32) return null;
                base64 = base64.Replace('-', '+').Replace('_', '/');                  // base64url 兼容
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "="; break;
                    case 1: return null;
                }

                byte[] bytes = Convert.FromBase64String(base64);
                if (bytes.Length < 32) return null;

                // 依据文件头修正扩展名（更可靠）
                if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) ext = ".jpg";
                else if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50) ext = ".png";
                else if (bytes.Length > 3 && bytes[0] == 0x47 && bytes[1] == 0x49) ext = ".gif";
                else if (bytes.Length > 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) ext = ".bmp";
                else if (bytes.Length > 12 && bytes[8] == 0x57 && bytes[9] == 0x45) ext = ".webp";

                Directory.CreateDirectory(cacheDir);
                string hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes))[..16];
                string path = Path.Combine(cacheDir, "b64_" + hash + ext);
                if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
                return path;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 消息图片预览：居中弹窗显示公告图片，支持滚轮/按钮缩放与拖拽平移。
    /// 图片先下载到本地缓存（自动尝试多个候选地址），避免相对路径/防盗链导致显示空白。
    /// </summary>
    public sealed partial class MainWindow
    {
        private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>图片缓存目录（下载图片与 base64 解码图片都放这里）。</summary>
        private static string ImageCacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "images");

        private bool _imagePanning;
        private Windows.Foundation.Point _imagePanStart;
        private double _imagePanOffsetX;
        private double _imagePanOffsetY;

        /// <summary>从公告 HTML 中提取图片地址。</summary>
        public static List<string> ExtractImageUrls(string? html) => MessageHtml.ExtractImageUrls(html);

        /// <summary>弹出图片预览（异步下载后显示；加载中/失败都有提示）。</summary>
        public void ShowImagePreview(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _ = LoadAndShowImageAsync(url);
        }

        /// <summary>弹出公告里的图片（若有）。</summary>
        public void ShowImagePreviewIfAny(string? htmlContent)
        {
            var urls = ExtractImageUrls(htmlContent);
            if (urls.Count > 0) ShowImagePreview(urls[0]);
        }

        private async Task LoadAndShowImageAsync(string url)
        {
            try
            {
                string brief = url.Length > 64 ? url[..64] + $"…（共 {url.Length} 字符）" : url;
                IslandLog($"准备弹出图片预览：{brief}");
                ShowImagePreviewShell(null, "图片加载中…");

                // 1) base64 内联图片（data:image/...;base64,xxx）先本地解码
                string? localPath = MessageHtml.TryDecodeBase64Image(url, ImageCacheDir);
                if (localPath != null)
                {
                    var b64Bitmap = new BitmapImage(new Uri(localPath));
                    ShowImagePreviewShell(b64Bitmap, null);
                    IslandLog($"图片预览成功（base64 解码）：{localPath}");
                    return;
                }

                // 2) 普通 URL：下载
                localPath = await DownloadImageAsync(url);
                if (localPath == null)
                {
                    ShowImagePreviewShell(null, $"图片加载失败：{url}");
                    IslandLog($"图片加载失败：{url}");
                    return;
                }

                var bitmap = new BitmapImage(new Uri(localPath));
                ShowImagePreviewShell(bitmap, null);
                IslandLog($"图片预览成功：{url} → {localPath}");
            }
            catch (Exception ex)
            {
                IslandLog($"图片预览异常：{ex.Message}");
                ShowImagePreviewShell(null, "图片加载失败");
            }
        }

        /// <summary>依次尝试候选地址下载图片，成功后缓存到本地并返回文件路径。</summary>
        private static async Task<string?> DownloadImageAsync(string url)
        {
            foreach (var candidate in ImageUrlCandidates(url))
            {
                try
                {
                    using var resp = await ImageHttp.GetAsync(candidate);
                    if (!resp.IsSuccessStatusCode)
                    {
                        IslandLog($"图片 HTTP {(int)resp.StatusCode}：{candidate}");
                        continue;
                    }

                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                    if (bytes.Length == 0) continue;

                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "金华一中科技校园套件", "images");
                    Directory.CreateDirectory(dir);

                    string hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)).Substring(0, 12);
                    string file = Path.Combine(dir, hash + GuessExtension(candidate));
                    if (!File.Exists(file)) await File.WriteAllBytesAsync(file, bytes);
                    return file;
                }
                catch (Exception ex)
                {
                    IslandLog($"图片下载失败：{candidate} → {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>候选地址：原地址 + 常见的 /class/uploads 变体（网页上传接口在 /class 下）。</summary>
        private static List<string> ImageUrlCandidates(string url)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string u)
            {
                if (!string.IsNullOrWhiteSpace(u) && seen.Add(u)) list.Add(u);
            }

            Add(url);
            try
            {
                var uri = new Uri(url);
                string path = uri.AbsolutePath.TrimStart('/');
                string origin = $"{uri.Scheme}://{uri.Authority}";

                if (path.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
                    Add($"{origin}/class/{path}");
                else if (path.StartsWith("class/uploads/", StringComparison.OrdinalIgnoreCase))
                    Add($"{origin}/{path.Substring("class/".Length)}");
                else
                    Add($"{origin}/class/{path}");
            }
            catch { }
            return list;
        }

        private static string GuessExtension(string url)
        {
            try
            {
                string ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp") return ext;
            }
            catch { }
            return ".png";
        }

        /// <summary>设置弹窗内容（source 为 null 时显示提示文字）。</summary>
        private void ShowImagePreviewShell(ImageSource? source, string? message)
        {
            try
            {
                double w = Math.Max(320, ScreenWidth * 0.9);
                double h = Math.Max(240, ScreenHeight * 0.78);

                ImagePreviewScroll.Width = w;
                ImagePreviewScroll.Height = h;
                ImagePreviewImage.MaxWidth = w;
                ImagePreviewImage.MaxHeight = h;
                ImagePreviewImage.Source = source;
                ImagePreviewImage.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;

                ImagePreviewHint.Text = message ?? "";
                ImagePreviewHint.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;

                ImagePreviewScroll.ChangeView(0, 0, 1.0f, true);
                ImagePreviewHost.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void CloseImagePreview()
        {
            try
            {
                ImagePreviewHost.Visibility = Visibility.Collapsed;
                ImagePreviewImage.Source = null;
            }
            catch { }
        }

        // ==================== 事件 ====================

        private void ImagePreviewBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 只有点空白处才关闭（图片/按钮上的点击已被各自处理并标记 Handled）
            if (IsInsideButton(e.OriginalSource)) return;
            CloseImagePreview();
        }

        private void ImagePreviewClose_Click(object sender, RoutedEventArgs e) => CloseImagePreview();

        /// <summary>判断事件源是否位于某个按钮内部（用于“点空白处关闭”不误触发）。</summary>
        private static bool IsInsideButton(object? source)
        {
            var el = source as DependencyObject;
            while (el != null)
            {
                if (el is Button) return true;
                el = VisualTreeHelper.GetParent(el);
            }
            return false;
        }

        private void ImagePreviewZoomIn_Click(object sender, RoutedEventArgs e) => ZoomImage(1.25f);

        private void ImagePreviewZoomOut_Click(object sender, RoutedEventArgs e) => ZoomImage(1 / 1.25f);

        private void ZoomImage(float factor)
        {
            try
            {
                float target = Math.Clamp(ImagePreviewScroll.ZoomFactor * factor,
                    ImagePreviewScroll.MinZoomFactor, ImagePreviewScroll.MaxZoomFactor);
                ImagePreviewScroll.ChangeView(null, null, target, true);
            }
            catch { }
        }

        private void ImagePreviewScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                int delta = e.GetCurrentPoint(ImagePreviewScroll).Properties.MouseWheelDelta;
                ZoomImage(delta > 0 ? 1.15f : 1 / 1.15f);
                e.Handled = true;
            }
            catch { }
        }

        private void ImagePreviewScroll_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;   // 点在图片上不关闭弹窗
        }

        private void ImagePreviewScroll_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                _imagePanning = true;
                _imagePanStart = e.GetCurrentPoint(ImagePreviewScroll).Position;
                _imagePanOffsetX = ImagePreviewScroll.HorizontalOffset;
                _imagePanOffsetY = ImagePreviewScroll.VerticalOffset;
                ImagePreviewScroll.CapturePointer(e.Pointer);
            }
            catch { }
        }

        private void ImagePreviewScroll_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_imagePanning) return;
            try
            {
                var p = e.GetCurrentPoint(ImagePreviewScroll).Position;
                double dx = p.X - _imagePanStart.X;
                double dy = p.Y - _imagePanStart.Y;
                ImagePreviewScroll.ChangeView(_imagePanOffsetX - dx, _imagePanOffsetY - dy, null, true);
                e.Handled = true;
            }
            catch { }
        }

        private void ImagePreviewScroll_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _imagePanning = false;
            try { ImagePreviewScroll.ReleasePointerCapture(e.Pointer); } catch { }
        }
    }
}
