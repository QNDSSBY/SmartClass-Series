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
    /// <summary>应用区目录中的实际文件（.lnk 或 exe）或文件夹，用于移除等管理操作。</summary>
    public string SourcePath { get; set; } = string.Empty;
    /// <summary>系统固定项（如“此电脑”）：不可拖拽排序、不参与应用管理、不出现在设置列表。</summary>
    public bool IsPinned { get; set; }

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

/// <summary>U盘文件浏览器中的一个条目（文件或文件夹）。</summary>
public class UsbFileItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string ModifiedText { get; set; } = string.Empty;
    public string TypeText { get; set; } = string.Empty;
    public string SizeText { get; set; } = string.Empty;
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
}