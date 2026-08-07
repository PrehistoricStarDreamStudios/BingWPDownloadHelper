using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BingPaper
{
    /// <summary>
    /// 集中管理应用配置，使用 %USERPROFILE%\BingPaper\config.ini。
    /// </summary>
    public class AppConfig
    {
        // 文件夹根：C:\Users\{username}\BingPaper
        public static string AppFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BingPaper");

        // 壁纸目录：BingPaper\Wallpaper
        public static string WallpaperFolder =>
            Path.Combine(AppFolder, "Wallpaper");

        // 配置文件：BingPaper\config.ini
        public static string ConfigFilePath =>
            Path.Combine(AppFolder, "config.ini");

        // 默认值
        public string Language { get; set; } = "auto";
        public string ThemeMode { get; set; } = "System";        // Light / Dark / System
        public string ColorTheme { get; set; } = "System";          // System/Blue/Green/Orange/Purple/Pink
        public bool AutoStart { get; set; } = false;
        public bool AutoStartHide { get; set; } = true;
        public string CloseBehavior { get; set; } = "Tray";        // Tray / Exit
        public bool AskExitConfirm { get; set; } = true;
        public int DownloadThreads { get; set; } = 4;
        public string WallpaperPath { get; set; } = "";
        public string LastSelectedResolution { get; set; } = "1920x1080";
        public bool ShuffleSlideshow { get; set; } = false;
        public int SlideshowIntervalSec { get; set; } = 600;
        public string SlideshowFillMode { get; set; } = "Fill";
        public string DownloadCategory { get; set; } = "All";
        public string FriendlyLinksUrl { get; set; } = "https://www.bing.com/?mkt=zh-CN";
        public bool DailyMottoEnabled { get; set; } = true;

        /// <summary>
        /// 确保应用目录与默认壁纸目录存在。同时执行一次性迁移：
        /// 如果检测到旧版 BingWPDLHelper 的 wallpaper 路径或 AppData 旧配置，则提示用户迁移。
        /// </summary>
        public static void EnsureDirectories()
        {
            try
            {
                if (!Directory.Exists(AppFolder))
                    Directory.CreateDirectory(AppFolder);
                if (!Directory.Exists(WallpaperFolder))
                    Directory.CreateDirectory(WallpaperFolder);
            }
            catch { }
        }

        /// <summary>
        /// 检测旧版本 BingWPDLHelper 是否存在配置/壁纸目录。
        /// 返回旧的壁纸目录路径；若不存在则返回 null。
        /// </summary>
        public static string? DetectOldWallpaperFolder()
        {
            try
            {
                // 旧版本可能在 %USERPROFILE%\Pictures\BingWPDLHelper 或程序目录下
                var candidates = new List<string>
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BingWPDLHelper"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BingWPDLHelper"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BingWPDLHelper", "Wallpaper"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BingWPDLHelper"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BingWPDLHelper", "Wallpaper"),
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(c))
                    {
                        // 验证有图片
                        try
                        {
                            var hasImage = false;
                            foreach (var f in Directory.EnumerateFiles(c, "*.*", SearchOption.AllDirectories))
                            {
                                var ext = Path.GetExtension(f).ToLowerInvariant();
                                if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp")
                                {
                                    hasImage = true; break;
                                }
                            }
                            if (hasImage) return c;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 将旧壁纸目录内容迁移到新的 BingPaper\Wallpaper 目录。
        /// </summary>
        public static bool MigrateOldWallpaper(string oldFolder)
        {
            try
            {
                if (!Directory.Exists(oldFolder)) return false;
                EnsureDirectories();
                foreach (var file in Directory.EnumerateFiles(oldFolder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".bmp") continue;
                    var dst = Path.Combine(WallpaperFolder, Path.GetFileName(file));
                    if (!File.Exists(dst))
                    {
                        try { File.Move(file, dst); } catch { }
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 用户主动迁移壁纸目录到新位置（包括旧壁纸的搬移）。
        /// </summary>
        public static bool RelocateWallpaperFolder(string newPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newPath)) return false;
                if (string.Equals(newPath, WallpaperFolder, StringComparison.OrdinalIgnoreCase)) return true;
                EnsureDirectories();
                if (!Directory.Exists(newPath)) Directory.CreateDirectory(newPath);
                // 复制所有壁纸
                foreach (var file in Directory.EnumerateFiles(WallpaperFolder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".bmp") continue;
                    var dst = Path.Combine(newPath, Path.GetFileName(file));
                    if (!File.Exists(dst))
                    {
                        try { File.Move(file, dst); } catch { }
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>加载配置（INI 格式，简单 key=value）。</summary>
        public static AppConfig Load()
        {
            var cfg = new AppConfig();
            try
            {
                EnsureDirectories();
                if (!File.Exists(ConfigFilePath)) return cfg;
                foreach (var rawLine in File.ReadAllLines(ConfigFilePath, Encoding.UTF8))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim();
                    var v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "Language": cfg.Language = v; break;
                        case "ThemeMode": cfg.ThemeMode = v; break;
                        case "ColorTheme": cfg.ColorTheme = v; break;
                        case "AutoStart": cfg.AutoStart = v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "AutoStartHide": cfg.AutoStartHide = v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "CloseBehavior": cfg.CloseBehavior = v; break;
                        case "AskExitConfirm": cfg.AskExitConfirm = v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "DownloadThreads": if (int.TryParse(v, out int dt)) cfg.DownloadThreads = Math.Max(1, Math.Min(32, dt)); break;
                        case "WallpaperPath": cfg.WallpaperPath = v; break;
                        case "LastSelectedResolution": cfg.LastSelectedResolution = v; break;
                        case "ShuffleSlideshow": cfg.ShuffleSlideshow = v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "SlideshowIntervalSec": if (int.TryParse(v, out int si)) cfg.SlideshowIntervalSec = si; break;
                        case "SlideshowFillMode": cfg.SlideshowFillMode = v; break;
                        case "DownloadCategory": cfg.DownloadCategory = v; break;
                        case "FriendlyLinksUrl": cfg.FriendlyLinksUrl = v; break;
                        case "DailyMottoEnabled": cfg.DailyMottoEnabled = v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
                if (string.IsNullOrEmpty(cfg.WallpaperPath))
                    cfg.WallpaperPath = WallpaperFolder;
            }
            catch { }
            return cfg;
        }

        /// <summary>保存配置到 config.ini。</summary>
        public void Save()
        {
            try
            {
                EnsureDirectories();
                var sb = new StringBuilder();
                sb.AppendLine("; BingPaper configuration");
                sb.AppendLine($"Language={Language}");
                sb.AppendLine($"ThemeMode={ThemeMode}");
                sb.AppendLine($"ColorTheme={ColorTheme}");
                sb.AppendLine($"AutoStart={(AutoStart ? 1 : 0)}");
                sb.AppendLine($"AutoStartHide={(AutoStartHide ? 1 : 0)}");
                sb.AppendLine($"CloseBehavior={CloseBehavior}");
                sb.AppendLine($"AskExitConfirm={(AskExitConfirm ? 1 : 0)}");
                sb.AppendLine($"DownloadThreads={DownloadThreads}");
                sb.AppendLine($"WallpaperPath={WallpaperPath ?? ""}");
                sb.AppendLine($"LastSelectedResolution={LastSelectedResolution ?? ""}");
                sb.AppendLine($"ShuffleSlideshow={(ShuffleSlideshow ? 1 : 0)}");
                sb.AppendLine($"SlideshowIntervalSec={SlideshowIntervalSec}");
                sb.AppendLine($"SlideshowFillMode={SlideshowFillMode}");
                sb.AppendLine($"DownloadCategory={DownloadCategory}");
                sb.AppendLine($"FriendlyLinksUrl={FriendlyLinksUrl}");
                sb.AppendLine($"DailyMottoEnabled={(DailyMottoEnabled ? 1 : 0)}");
                File.WriteAllText(ConfigFilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>检测当前系统语言，返回建议的应用语言文化名。</summary>
        public static string DetectSystemLanguage()
        {
            try
            {
                var cult = System.Globalization.CultureInfo.CurrentUICulture;
                var name = cult.Name;
                if (string.IsNullOrEmpty(name)) return "zh-CN";
                // 简体中文（中国大陆）
                if (name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
                // 繁体中文（香港、澳门）
                if (name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)) return "zh-HK";
                // 繁体中文（台湾）
                if (name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
                // 英文 UK
                if (name.Equals("en-GB", StringComparison.OrdinalIgnoreCase)) return "en-GB";
                // 英文 US / 其他英文
                if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en-US";
                // 其他默认中文
                return "zh-CN";
            }
            catch { return "zh-CN"; }
        }

        /// <summary>支持的显示语言列表（用于设置页下拉）。</summary>
        public static readonly string[] SupportedLanguages =
        {
            "zh-CN", "zh-HK", "zh-TW", "en-US", "en-GB"
        };

        /// <summary>获取语言显示名（本地语言）。</summary>
        public static string GetLanguageDisplayName(string culture)
        {
            return culture switch
            {
                "zh-CN" => "简体中文（中国大陆）",
                "zh-HK" => "繁體中文（中國香港、中國澳門）",
                "zh-TW" => "繁體中文（中國台灣）",
                "en-US" => "English (United States)",
                "en-GB" => "English (United Kingdom)",
                _ => culture
            };
        }
    }
}
