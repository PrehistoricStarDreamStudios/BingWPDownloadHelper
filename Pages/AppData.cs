using System;
using System.Collections.Generic;

namespace BingPaper
{
    /// <summary>
    /// 壁纸数据项，支持 XAML 数据绑定。
    /// </summary>
    public class WallpaperItem
    {
        public string Url { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string TagsDisplay => string.Join(", ", Tags);
    }

    /// <summary>
    /// 静态共享数据类，供各页面访问应用级配置和状态。
    /// </summary>
    public static class AppData
    {
        public static string AppFolderPath { get; set; } = string.Empty;
        public static string WallpaperFolderPath { get; set; } = string.Empty;
        public static string ConfigFilePath { get; set; } = string.Empty;
        public static Dictionary<string, string> Config { get; } = new(StringComparer.OrdinalIgnoreCase);
        public static List<WallpaperItem> AllWallpapers { get; } = new();
        public static List<WallpaperItem> FilteredWallpapers { get; set; } = new();
        public static Dictionary<string, Dictionary<string, string>> AssetFileMap { get; } = new(StringComparer.OrdinalIgnoreCase);
        public static double AnimationMs { get; set; } = 200;
        public static bool AnimationEnabled { get; set; } = true;
    }
}