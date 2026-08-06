using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace SmartClassNight;

public class AppItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;

    public ImageSource? Icon
    {
        get
        {
            if (string.IsNullOrEmpty(IconPath) || !File.Exists(IconPath))
                return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.UriSource = new Uri(IconPath);
                return bitmap;
            }
            catch { return null; }
        }
    }

    public AppItem() { }
    public AppItem(string name, string path, string iconPath = "")
    {
        Name = name;
        Path = path;
        IconPath = iconPath;
    }
}

public class ForecastItem { /* 若未使用可省略 */ }