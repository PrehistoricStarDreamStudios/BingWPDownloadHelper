using System;
using System.Collections.Generic;

namespace BingPaper
{
    /// <summary>
    /// 静态共享数据类，供各页面访问应用级配置和状态。
    /// </summary>
    public static class AppData
    {
        public static string AppFolderPath { get; set; } = string.Empty;
        public static string WallpaperFolderPath { get; set; } = string.Empty;
        public static string ConfigFilePath { get; set; } = string.Empty;
        public static Dictionary<string, string> Config { get; } = new(StringComparer.OrdinalIgnoreCase);
        public static List<(string url, List<string> tags)> AllWallpapers { get; } = new();
        public static List<(string url, List<string> tags)> FilteredWallpapers { get; set; } = new();
        public static Dictionary<string, Dictionary<string, string>> AssetFileMap { get; } = new(StringComparer.OrdinalIgnoreCase);
        public static double AnimationMs { get; set; } = 200;
        public static bool AnimationEnabled { get; set; } = true;
    }
}