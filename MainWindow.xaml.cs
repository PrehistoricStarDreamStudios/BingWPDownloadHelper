using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Windows.UI;
using Microsoft.Win32;
using System.Xml.Linq;
using System.IO.Compression;
using System.Text;
using System.Globalization;
using System.Threading;

namespace BingPaper
{
    public sealed partial class MainWindow : Window
    {
        // application config and folders
        private string AppFolderPath;
        private string WallpaperFolderPath;
        private string ConfigFilePath;
        private readonly Dictionary<string, string> _config = new(StringComparer.OrdinalIgnoreCase);
        private const string AppVersion = "b0.1";
        private bool AnimationEnabled = true;
        private int AnimationMs = 200;
        private bool _initialSizeApplied = false;
        private readonly Dictionary<string, Dictionary<string, string>> _assetFileMap = new(StringComparer.OrdinalIgnoreCase);

        // 官方标签（与 list.xml / GitHub Actions 脚本保持一致）
        private static readonly string[] OfficialTags = { "精选", "人文", "风景", "节日", "动物", "植物", "海洋", "建筑", "景点", "其他" };
        private const string UnclassifiedTag = "未分类";

        // 壁纸列表（url + 标签集合），用于标签筛选与预览
        private readonly List<(string url, List<string> tags)> _allWallpapers = new();
        private List<(string url, List<string> tags)> _filteredWallpapers = new();
        private int _currentWallpaperIndex = 0;

        // 托盘管理器与退出标志（关闭行为：Tray/Exit）
        private TrayManager? _trayManager;
        private bool _isExiting = false;
        // 设置加载标志：防止初始化期间 SelectionChanged/Toggled 事件覆盖配置
        private bool _isLoadingSettings = false;

        // 详细异常记录到日志文件（同时写入临时目录以保证可写性）
        private void LogException(Exception ex)
        {
            try
            {
                if (ex == null) return;

                string BuildExceptionText(Exception e)
                {
                    try
                    {
                        if (e == null) return string.Empty;
                        var sw = new System.Text.StringBuilder();
                        sw.AppendLine("----- EXCEPTION " + DateTime.Now.ToString("o") + " -----");
                        sw.AppendLine("Type: " + e.GetType().FullName);
                        sw.AppendLine("Message: " + e.Message);
                        sw.AppendLine("StackTrace:");
                        sw.AppendLine(e.StackTrace ?? "(no stack)");
                        if (e.InnerException != null)
                        {
                            sw.AppendLine("--- INNER ---");
                            sw.AppendLine(BuildExceptionText(e.InnerException));
                        }
                        return sw.ToString();
                    }
                    catch { return "(failed to build exception text)"; }
                }

                var text = BuildExceptionText(ex);

                // 优先写入应用目录（若可写），否则写入临时目录
                try
                {
                    var appPath = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
                    var p = Path.Combine(appPath, "error.log");
                    File.AppendAllText(p, text + Environment.NewLine);
                }
                catch
                {
                    try
                    {
                        var tmp = Path.Combine(Path.GetTempPath(), "BingWPDLHelper_error.log");
                        File.AppendAllText(tmp, text + Environment.NewLine);
                    }
                    catch { }
                }

                try { System.Diagnostics.Debug.WriteLine(text); } catch { }
            }
            catch { }
        }

        public MainWindow()
        {
            this.InitializeComponent();
            // ensure default wallpaper folder and config file paths use AppFolderPath (initialized in constructor body)

            // 启用 WinUI3 原生窗口外观：ExtendsContentIntoTitleBar + Mica 云母背景
            this.ExtendsContentIntoTitleBar = true;
            // 背景材质在加载 _config 后由 ApplyBackdrop 统一应用（支持 Mica/Acrylic/None + 透明背景开关）

            // 配置 OverlappedPresenter：原生 WinUI3 窗口样式（圆角、系统标题栏按钮）
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsMinimizable = true;
                    presenter.IsMaximizable = true;
                    presenter.IsResizable = true;
                    presenter.IsAlwaysOnTop = false;
                }
                // 关键：AppWindow.TitleBar.ExtendsContentIntoTitleBar=true 让系统绘制 Fluent 风格标题按钮
                try { appWindow.TitleBar.ExtendsContentIntoTitleBar = true; } catch { }
                // Windows 11 圆角：DWM 设置窗口圆角属性
                try
                {
                    var cornerPreference = DWMWCP_ROUND;
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
                }
                catch { }
            }
            catch { }

            // 立即设置标题栏：用整个 AppTitleBar 作为标题栏区域，系统会自动在右侧放置 Fluent 风格按钮
            try
            {
                this.SetTitleBar(this.AppTitleBar);
                UpdateTitleBarColors();
            }
            catch { }

            // 初始化切换按钮图标
            try
            {
                var toggleBtn = this.AppTitleBar?.FindName("ToggleThemeButton") as Button;
                if (toggleBtn != null)
                {
                    var isDark = this.AppTitleBar.ActualTheme == ElementTheme.Dark;
                    var glyph = isDark ? "☀" : "☾";
                    toggleBtn.Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe UI Symbol") };
                }
            }
            catch { }

            // Defer setting window size until Activated to avoid calling window APIs too early
            this.Activated += (_, __) =>
            {
                try
                {
                    try { UpdateTitleBarColors(); } catch { }

                    if (!_initialSizeApplied)
                    {
                        try
                        {
                            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                            // 从配置恢复窗口大小位置
                            int wx = 0, wy = 0, ww = 0, wh = 0;
                            bool hasSavedRect = false;
                            if (_config.TryGetValue("win_x", out var sx) && _config.TryGetValue("win_y", out var sy)
                                && _config.TryGetValue("win_w", out var sw) && _config.TryGetValue("win_h", out var sh))
                            {
                                hasSavedRect = int.TryParse(sx, out wx) && int.TryParse(sy, out wy)
                                    && int.TryParse(sw, out ww) && int.TryParse(sh, out wh);
                            }
                            if (hasSavedRect)
                            {
                                const uint SWP_NOZORDER = 0x0004;
                                SetWindowPos(hwnd, IntPtr.Zero, wx, wy, ww, wh, SWP_NOZORDER);
                            }
                            else
                            {
                                int screenWidth = GetSystemMetrics(0);
                                int screenHeight = GetSystemMetrics(1);
                                int longSide = Math.Max(screenWidth, screenHeight);
                                int shortSide = Math.Min(screenWidth, screenHeight);
                                int targetWidth = (int)Math.Round(longSide * 0.5);
                                int targetHeight = (int)Math.Round(shortSide * 0.6);
                                const uint SWP_NOZORDER = 0x0004;
                                const uint SWP_NOMOVE = 0x0002;
                                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, targetWidth, targetHeight, SWP_NOZORDER | SWP_NOMOVE);
                            }
                        }
                        catch { }
                        _initialSizeApplied = true;

                        // 设置窗口图标（unpackaged 模式需要手动用 Win32 设置任务栏/标题栏图标）
                        try
                        {
                            var hwnd2 = WinRT.Interop.WindowNative.GetWindowHandle(this);
                            var exeDir = AppContext.BaseDirectory;
                            var icoPath = Path.Combine(exeDir, "Assets", "appicon.ico");
                            if (!File.Exists(icoPath)) icoPath = Path.Combine(exeDir, "appicon.ico");
                            if (File.Exists(icoPath))
                            {
                                var hIcon = LoadImage(IntPtr.Zero, icoPath, 1, 0, 0, 0x00000010);
                                if (hIcon != IntPtr.Zero)
                                {
                                    SendMessage(hwnd2, 0x0080, (IntPtr)1, hIcon); // ICON_BIG
                                    SendMessage(hwnd2, 0x0080, (IntPtr)0, hIcon); // ICON_SMALL
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            };

            // 注册全局异常日志并启动初始化（记录 InitializeTodayUIAsync 的异常）
            Application.Current.UnhandledException += (s, e) =>
            {
                try { LogException(e.Exception); e.Handled = true; } catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { LogException(e.Exception); e.SetObserved(); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { LogException(e.ExceptionObject as Exception); } catch { }
            };

            // 使用标题栏按钮控制 NavigationView 折叠/展开，响应式更新列宽
            try
            {
                var rootForBtn = this.Content as FrameworkElement;
                var nav = rootForBtn?.FindName("NavView") as Microsoft.UI.Xaml.Controls.NavigationView;
                if (nav != null && rootForBtn != null)
                {
                    // set initial state on loaded
                    rootForBtn.Loaded += (_, __) =>
                    {
                        try
                        {
                            nav.IsPaneOpen = true;
                            UpdateNavLayout(nav);
                        }
                        catch { }
                    };

                    // when IsPaneOpen changes, update pane width
                    try
                    {
                        nav.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.NavigationView.IsPaneOpenProperty, new DependencyPropertyChangedCallback((dep, args) =>
                        {
                            try
                            {
                                this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                                {
                                    try { UpdateNavLayout(nav); } catch { }
                                });
                            }
                            catch { }
                        }));
                    }
                    catch { }

                    // when window size changes, recompute desired open pane length
                    this.SizeChanged += (_, __) =>
                    {
                        try
                        {
                            UpdateNavLayout(nav);
                        }
                        catch { };
                    };
                }
            }
            catch { }

            // Ensure AppData folders and config

            try
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                AppFolderPath = Path.Combine(roaming, "WPDLHelper");
                WallpaperFolderPath = Path.Combine(AppFolderPath, "Wallpaper");
                ConfigFilePath = Path.Combine(AppFolderPath, "config.ini");
                if (!Directory.Exists(AppFolderPath)) Directory.CreateDirectory(AppFolderPath);
                if (!Directory.Exists(WallpaperFolderPath)) Directory.CreateDirectory(WallpaperFolderPath);

                if (!File.Exists(ConfigFilePath))
                {
                    var defaultCfg = new List<string>
                    {
                        "version=" + AppVersion,
                        "animation_enabled=true",
                        "animation_ms=200",
                        "default_resolution=UHD",
                        "download_folder=Wallpaper"
                    };
                    File.WriteAllLines(ConfigFilePath, defaultCfg);
                }

                var lines = File.ReadAllLines(ConfigFilePath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                    var idx = trimmed.IndexOf('=');
                    if (idx > 0)
                    {
                        var k = trimmed.Substring(0, idx).Trim();
                        var v = trimmed.Substring(idx + 1).Trim();
                        _config[k] = v;
                    }
                }

                if (_config.TryGetValue("animation_enabled", out var aen)) AnimationEnabled = aen.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (_config.TryGetValue("animation_ms", out var ams) && int.TryParse(ams, out var ms)) AnimationMs = ms;

                // 读取 download_folder，并更新 WallpaperFolderPath（支持相对路径）
                if (_config.TryGetValue("download_folder", out var df) && !string.IsNullOrEmpty(df))
                {
                    WallpaperFolderPath = Path.IsPathRooted(df) ? df : Path.Combine(AppFolderPath, df);
                }
                if (!Directory.Exists(WallpaperFolderPath)) Directory.CreateDirectory(WallpaperFolderPath);

                // 应用保存的显示语言（启动时即生效，托盘菜单等后续创建的 UI 都会使用新 culture）
                try
                {
                    var savedLang = _config.TryGetValue("language", out var sl) ? sl : "zh-CN";
                    if (string.IsNullOrEmpty(savedLang) || savedLang == "auto") savedLang = AppConfig.DetectSystemLanguage();
                    try { Strings.Culture = new CultureInfo(savedLang); } catch { }
                }
                catch { }

                // 启动今日壁纸 UI 初始化（必须在 AppFolderPath/config 初始化之后，避免 AutoUpdateListAsync 访问 null）
                InitializeTodayUIAsync().ContinueWith(t =>
                {
                    if (t.Exception != null) { LogException(t.Exception.Flatten()); }
                }, System.Threading.Tasks.TaskScheduler.Default);

                // initialize AppSettings UI (autostart/default list/api)
                try
                {
                    var root = this.Content as FrameworkElement;
                    var autoChk = root?.FindName("AutoStartCheck") as ToggleSwitch;
                    if (autoChk != null)
                    {
                        var aut = _config.TryGetValue("autostart", out var av) && av.Equals("true", StringComparison.OrdinalIgnoreCase);
                        autoChk.IsOn = aut;
                        try { SetAutoStart(aut); } catch { }
                    }

                    // 初始化关闭行为
                    var closeCombo = root?.FindName("CloseBehaviorCombo") as ComboBox;
                    if (closeCombo != null)
                    {
                        var cb = _config.TryGetValue("close_behavior", out var cbv) ? cbv : "Tray";
                        for (int i = 0; i < closeCombo.Items.Count; i++)
                        {
                            if (closeCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == cb)
                            { closeCombo.SelectedIndex = i; break; }
                        }
                    }

                    // 初始化主题模式
                    var themeCombo = root?.FindName("ThemeModeCombo") as ComboBox;
                    if (themeCombo != null)
                    {
                        var tm = _config.TryGetValue("theme_mode", out var tmv) ? tmv : "System";
                        for (int i = 0; i < themeCombo.Items.Count; i++)
                        {
                            if (themeCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == tm)
                            { themeCombo.SelectedIndex = i; break; }
                        }
                        try { ApplyThemeMode(tm); } catch { }
                    }

                    // 初始化颜色主题
                    var colorCombo = root?.FindName("ColorThemeCombo") as ComboBox;
                    if (colorCombo != null)
                    {
                        var ct = _config.TryGetValue("color_theme", out var ctv) ? ctv : "System";
                        for (int i = 0; i < colorCombo.Items.Count; i++)
                        {
                            if (colorCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == ct)
                            { colorCombo.SelectedIndex = i; break; }
                        }
                        try { ApplyColorTheme(ct); } catch { }
                    }

                    // 初始化下载线程数
                    var threadsBox = root?.FindName("DownloadThreadsBox") as NumberBox;
                    if (threadsBox != null)
                    {
                        var dt = _config.TryGetValue("download_threads", out var dtv) && int.TryParse(dtv, out var dti) ? dti : 4;
                        threadsBox.Value = Math.Max(1, Math.Min(32, dt));
                    }

                    // 初始化显示语言
                    var langCombo = root?.FindName("LanguageCombo") as ComboBox;
                    if (langCombo != null)
                    {
                        var lang = _config.TryGetValue("language", out var lv) ? lv : "zh-CN";
                        if (lang == "auto") lang = AppConfig.DetectSystemLanguage();
                        for (int i = 0; i < langCombo.Items.Count; i++)
                        {
                            if (langCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == lang)
                            { langCombo.SelectedIndex = i; break; }
                        }
                    }

                    // 初始化背景材质（Fluent Design：Mica/Acrylic/None + 透明背景开关）
                    _isLoadingSettings = true;
                    try
                    {
                        var backdropType = _config.TryGetValue("backdrop_type", out var btv) ? btv : "Mica";
                        var transparentBg = _config.TryGetValue("transparent_background", out var tgv)
                            ? tgv.Equals("true", StringComparison.OrdinalIgnoreCase) : true;

                        var backdropCombo = root?.FindName("BackdropTypeCombo") as ComboBox;
                        if (backdropCombo != null)
                        {
                            for (int i = 0; i < backdropCombo.Items.Count; i++)
                            {
                                if (backdropCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == backdropType)
                                { backdropCombo.SelectedIndex = i; break; }
                            }
                            if (backdropCombo.SelectedIndex < 0) backdropCombo.SelectedIndex = 0;
                        }

                        var transparentSwitch = root?.FindName("TransparentBackgroundSwitch") as ToggleSwitch;
                        if (transparentSwitch != null) transparentSwitch.IsOn = transparentBg;

                        // 立即应用背景材质（构造函数早期已设置 ExtendsContentIntoTitleBar）
                        ApplyBackdrop(backdropType, transparentBg);
                    }
                    catch { }
                    _isLoadingSettings = false;
                }
                catch { }

            // 初始化托盘管理器并订阅事件
            try
            {
                _trayManager = new TrayManager(this);
                _trayManager.ShowRequested += (s, e) =>
                {
                    try { _trayManager?.ShowFromTray(); } catch { }
                };
                _trayManager.ExitRequested += (s, e) =>
                {
                    try { _isExiting = true; this.Close(); } catch { }
                };

                // 拦截窗口关闭事件：未标记退出时最小化到托盘
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                    appWindow.Closing += (s, e) =>
                    {
                        if (!_isExiting)
                        {
                            e.Cancel = true;
                            try { _trayManager?.HideToTray(); } catch { }
                        }
                        else
                        {
                            // 真正退出时保存窗口大小位置
                            try { SaveConfig(); } catch { }
                        }
                    };
                }
                catch { }
            }
            catch { }

            try
            {
                var root = this.Content as FrameworkElement;
                var defListCombo = root?.FindName("DefaultListCombo") as ComboBox;
                    if (defListCombo != null)
                    {
                        var defList = _config.TryGetValue("default_list", out var dl) ? dl : "local";
                        foreach (ComboBoxItem it in defListCombo.Items)
                        {
                            var tag = it.Tag as string ?? (it.Content as string);
                            if (tag == defList) { defListCombo.SelectedItem = it; break; }
                        }
                    }
                    var apiCombo = root?.FindName("ApiSourceCombo") as ComboBox;
                    if (apiCombo != null)
                    {
                        var api = _config.TryGetValue("api_source", out var ap) ? ap : "bing";
                        if (api == "bing") apiCombo.SelectedIndex = 0;
                    }
                    // 自动更新列表开关（默认开启）
                    var autoUpdateChk = root?.FindName("AutoUpdateListCheck") as ToggleSwitch;
                    if (autoUpdateChk != null)
                    {
                        var auOn = _config.TryGetValue("auto_update_list", out var auv) ? auv.Equals("true", StringComparison.OrdinalIgnoreCase) : true;
                        autoUpdateChk.IsOn = auOn;
                    }
                }
                catch { }
            }
            catch { }

            // 注册 Loaded 事件，用于在布局完成后修正分段指示器位置并绑定尺寸变化以保持对齐
            try { var rootEl = this.Content as FrameworkElement; if (rootEl != null) rootEl.Loaded += MainWindow_Loaded; } catch { }
        }

            // Show window if not open; activate if already open
            public void ShowOrActivate()
            {
                try
                {
                    // Activate the window (if minimized or hidden this will bring it up)
                    this.Activate();
                }
                catch { }
            }

            // Expose method to show settings page
            public void ShowSettings()
            {
                try
                {
                    var root = this.Content as FrameworkElement;
                    var nav = root?.FindName("NavView") as Microsoft.UI.Xaml.Controls.NavigationView;
                    if (nav != null)
                    {
                        foreach (NavigationViewItem it in nav.FooterMenuItems)
                        {
                            if (it.Tag != null && it.Tag.ToString() == "3") { nav.SelectedItem = it; break; }
                        }
                    }
                }
                catch { }
            }

            // Trigger check/download action: switch to 下载页
            public void TriggerCheckDownloads()
            {
                try
                {
                    var root = this.Content as FrameworkElement;
                    var nav = root?.FindName("NavView") as Microsoft.UI.Xaml.Controls.NavigationView;
                    if (nav != null)
                    {
                        foreach (var item in nav.MenuItems)
                        {
                            if (item is Microsoft.UI.Xaml.Controls.NavigationViewItem nvi && nvi.Tag != null && nvi.Tag.ToString() == "1") { nav.SelectedItem = nvi; break; }
                        }
                    }

                    // ensure defaults: 16:9-UHD
                    try
                    {
                        var aspect = root?.FindName("AspectRatioCombo") as ComboBox;
                        var res = root?.FindName("ResolutionCombo") as ComboBox;
                        if (aspect != null && res != null)
                        {
                            for (int i = 0; i < aspect.Items.Count; i++) { if ((aspect.Items[i] as ComboBoxItem)?.Content as string == "16:9") { aspect.SelectedIndex = i; break; } }
                            // trigger selection changed will populate resolutions; then select UHD
                            if (res.Items.Count > 0)
                            {
                                for (int j = 0; j < res.Items.Count; j++) { if ((res.Items[j] as ComboBoxItem)?.Content as string == "3840x2160") { res.SelectedIndex = j; break; } }
                            }
                        }
                    }
                    catch { }
                }
                catch { }
            }

            private void AspectRatioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
            {
                try
                {
                    var aspect = sender as ComboBox;
                    var root = this.Content as FrameworkElement;
                    var res = root?.FindName("ResolutionCombo") as ComboBox;
                    if (aspect == null || res == null) return;
                    res.Items.Clear();
                    var map = aspect.Tag as Dictionary<string, List<string>>;
                    var key = (aspect.SelectedItem as ComboBoxItem)?.Content as string;
                    if (!string.IsNullOrEmpty(key) && map != null && map.ContainsKey(key))
                    {
                        foreach (var rr in map[key].Distinct().OrderBy(x => x))
                        {
                            var tag = "_" + rr + ".jpg";
                            res.Items.Add(new ComboBoxItem { Content = rr, Tag = tag });
                        }
                    }
                    if (res.Items.Count == 0) res.Items.Add(new ComboBoxItem { Content = "3840x2160", Tag = "_3840x2160.jpg" });

                    // determine configured default resolution or pick highest available for this aspect
                    string configuredRes = null;
                    if (_config.TryGetValue("default_resolution", out var dr)) configuredRes = dr;
                    string targetRes = null;
                    if (!string.IsNullOrEmpty(configuredRes))
                    {
                        if (configuredRes == "720") targetRes = "1280x720";
                        else if (configuredRes == "1080") targetRes = "1920x1080";
                        else if (configuredRes.Equals("UHD", StringComparison.OrdinalIgnoreCase)) targetRes = "3840x2160";
                        else targetRes = configuredRes;
                    }

                    int bestIndex = -1;
                    long bestArea = -1;
                    for (int j = 0; j < res.Items.Count; j++)
                    {
                        var it = res.Items[j] as ComboBoxItem;
                        if (it == null) continue;
                        var content = it.Content as string ?? "";
                        if (!string.IsNullOrEmpty(targetRes) && content == targetRes) { res.SelectedIndex = j; bestIndex = -2; break; }
                        var m = System.Text.RegularExpressions.Regex.Match(content, "(\\d{3,4})x(\\d{3,4})");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var w) && int.TryParse(m.Groups[2].Value, out var h))
                        {
                            long area = (long)w * h;
                            if (area > bestArea) { bestArea = area; bestIndex = j; }
                        }
                    }
                    if (res.SelectedIndex < 0 && bestIndex >= 0) res.SelectedIndex = bestIndex;
                    // fallback to UHD if nothing selected
                    if (res.SelectedIndex < 0)
                    {
                        for (int j = 0; j < res.Items.Count; j++) { var it = res.Items[j] as ComboBoxItem; if (it != null && it.Content as string == "3840x2160") { res.SelectedIndex = j; break; } }
                    }
                }
                catch { }
            }

        private void UpdateNavLayout(Microsoft.UI.Xaml.Controls.NavigationView nav)
        {
            try
            {
                if (nav == null) return;
                double openW = 240;
                try { double winW = this.Bounds.Width; if (winW > 0) openW = Math.Max(200, Math.Min(400, winW * 0.4)); } catch { }
                try
                {
                    // compute width based on widest menu item text + icon + padding
                    double maxText = 0;
                    foreach (var mi in nav.MenuItems)
                    {
                        if (mi is NavigationViewItem nvi)
                        {
                            var cp = nvi.Content?.ToString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(cp))
                            {
                                var tb = new TextBlock { Text = cp, FontSize = 14 };
                                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                maxText = Math.Max(maxText, tb.DesiredSize.Width);
                            }
                        }
                    }
                    if (maxText > 0)
                    {
                        double iconEstimate = 24;
                        double paddingEstimate = 32;
                        openW = Math.Max(180, Math.Min(600, Math.Ceiling(iconEstimate + maxText + paddingEstimate)));
                    }
                    nav.OpenPaneLength = openW;
                }
                catch { }
            }
            catch { }
        }

        private void NavView_SelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            try
            {
                var nv = sender as NavigationView;
                // 使用 Tag 控制页面索引（避免 SelectedIndex 受分组影响）
                int idx = 0;
                try { if (args?.SelectedItem is NavigationViewItem it && it.Tag != null) idx = int.Parse(it.Tag.ToString()); }
                catch { idx = 0; }
                var root = this.Content as FrameworkElement;
                var todayHost = root?.FindName("TodayHost") as UIElement;
                var downloadHost = root?.FindName("DownloadHost") as UIElement;
                var settingsHost = root?.FindName("SettingsHost") as UIElement; // may be removed
                // todayHost and downloadHost control main content; 软件设置 and 关于 handled separately
                if (todayHost == null || downloadHost == null) return;

                var appSettings = root?.FindName("AppSettingsHost") as UIElement;
                var aboutHost = root?.FindName("AboutHost") as UIElement;
                var previewHost = root?.FindName("PreviewHost") as UIElement;
                todayHost.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
                downloadHost.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
                if (settingsHost != null) settingsHost.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
                if (appSettings != null) appSettings.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
                if (aboutHost != null) aboutHost.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
                if (previewHost != null) previewHost.Visibility = idx == 5 ? Visibility.Visible : Visibility.Collapsed;

                // 软件设置 (Tag=3) 可直接由 AppSettingsHost 控制显示
                // 关于 (Tag=4) 由 AboutHost 控制显示（替换原来的对话框）

                // 当切换到下载页时，确保显示当前下载目录
                if (idx == 1)
                {
                    try
                    {
                        var dlText = root.FindName("DownloadFolderText") as TextBox;
                        if (dlText != null) dlText.Text = WallpaperFolderPath;
                    }
                    catch { }
                }
                else if (idx == 5)
                {
                    // 切换到壁纸预览页：加载列表并填充平铺预览
                    try
                    {
                        if (_allWallpapers.Count == 0)
                        {
                            LoadWallpapersFromXml();
                            PopulateTagFilter();
                        }
                        if (_allWallpapers.Count > 0 && _filteredWallpapers.Count == 0)
                        {
                            _filteredWallpapers = _allWallpapers.ToList();
                        }
                        FillPreviewGrid();
                    }
                    catch { }
                }
                else if (idx == 2)
                {
                    try
                    {
                        // 切换到设置壁纸页时填充默认值（使用当前下载目录）
                        var sText = root.FindName("SlideshowFolderText") as TextBox;
                        if (sText != null) sText.Text = WallpaperFolderPath;
                        var interval = root.FindName("SlideshowIntervalCombo") as ComboBox;
                        if (interval != null)
                        {
                            var ivv = _config.TryGetValue("slideshow_interval", out var iv) && int.TryParse(iv, out var x) ? x : 1800;
                            for (int i = 0; i < interval.Items.Count; i++)
                            {
                                if (interval.Items[i] is ComboBoxItem ci && (ci.Tag as string) == ivv.ToString()) { interval.SelectedIndex = i; break; }
                            }
                        }
                        var shuffle = root.FindName("ShuffleCheck") as ToggleSwitch; if (shuffle != null && _config.TryGetValue("slideshow_shuffle", out var sh)) shuffle.IsOn = sh.Equals("true", StringComparison.OrdinalIgnoreCase);
                        var fill = root.FindName("FillModeCombo") as ComboBox; if (fill != null && _config.TryGetValue("slideshow_fill", out var f))
                        {
                            foreach (ComboBoxItem it in fill.Items) { if (it.Tag?.ToString() == f) { fill.SelectedItem = it; break; } }
                        }
                        var setacc = root.FindName("SetAccentCheck") as CheckBox; if (setacc != null && _config.TryGetValue("slideshow_setacc", out var sa)) setacc.IsChecked = sa.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task InitializeTodayUIAsync()
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var left = root?.FindName("NavView") as NavigationView;
                if (left != null && left.SelectedItem == null) left.SelectedItem = left.MenuItems[0] as NavigationViewItem;

                // 确保下载目录显示在文本框中（若 UI 已加载）
                try { var dlText = root?.FindName("DownloadFolderText") as TextBox; if (dlText != null) dlText.Text = WallpaperFolderPath; } catch { }
                // 初始化比例与分辨率下拉
                try
                {
                    var aspect = root?.FindName("AspectRatioCombo") as ComboBox;
                    var res = root?.FindName("ResolutionCombo") as ComboBox;
                    if (aspect != null && res != null)
                    {
                        aspect.Items.Clear();
                        res.Items.Clear();
                        // scan Assets/*.xml for aspect and resolution entries
                        var assetDir = Path.Combine(AppContext.BaseDirectory, "Assets");
                        if (Directory.Exists(assetDir))
                        {
                            var files = Directory.GetFiles(assetDir, "list*.xml");
                            var aspectSet = new HashSet<string>();
                            var resMap = new Dictionary<string, List<string>>();
                            foreach (var f in files)
                            {
                                try
                                {
                                    // try to parse XML for resolution/aspect attributes (supports single large list.xml)
                                    var xdoc = System.Xml.Linq.XDocument.Load(f);
                                    var resNodes = xdoc.Descendants("resolution").ToList();
                                    foreach (var resNode in resNodes)
                                    {
                                        var r = (string)resNode.Attribute("name") ?? "";
                                        var ar = (string)resNode.Attribute("aspect_ratio") ?? "";
                                        if (string.IsNullOrEmpty(ar) && !string.IsNullOrEmpty(r) && r.Contains('x'))
                                        {
                                            var parts = r.Split('x');
                                            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                                            {
                                                ar = $"{w / Gcd(w,h)}:{h / Gcd(w,h)}";
                                            }
                                        }
                                        if (!string.IsNullOrEmpty(ar))
                                        {
                                            aspectSet.Add(ar);
                                            if (!resMap.ContainsKey(ar)) resMap[ar] = new List<string>();
                                            if (!string.IsNullOrEmpty(r) && !resMap[ar].Contains(r)) resMap[ar].Add(r);

                                            try
                                            {
                                                if (!_assetFileMap.ContainsKey(ar)) _assetFileMap[ar] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                                if (!string.IsNullOrEmpty(r) && !_assetFileMap[ar].ContainsKey(r)) _assetFileMap[ar][r] = f;
                                            }
                                            catch { }
                                        }
                                    }

                                    // fallback: infer from first wallpaper url if no resolution nodes found
                                    if (!resNodes.Any())
                                    {
                                        var firstWp = xdoc.Descendants("wallpaper").FirstOrDefault();
                                        if (firstWp != null)
                                        {
                                            var wpUrl = firstWp.Element("url")?.Value?.Trim() ?? "";
                                            var m = System.Text.RegularExpressions.Regex.Match(wpUrl, "_(\\d{3,4}x\\d{3,4})\\.jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                            if (m.Success)
                                            {
                                                var r = m.Groups[1].Value;
                                                var parts = r.Split('x');
                                                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                                                {
                                                    var ar = $"{w / Gcd(w,h)}:{h / Gcd(w,h)}";
                                                    aspectSet.Add(ar);
                                                    if (!resMap.ContainsKey(ar)) resMap[ar] = new List<string>();
                                                    if (!resMap[ar].Contains(r)) resMap[ar].Add(r);
                                                    try
                                                    {
                                                        if (!_assetFileMap.ContainsKey(ar)) _assetFileMap[ar] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                                        if (!_assetFileMap[ar].ContainsKey(r)) _assetFileMap[ar][r] = f;
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            // populate aspect combo
                            foreach (var a in aspectSet.OrderBy(x => x)) { aspect.Items.Add(new ComboBoxItem { Content = a }); }

                            // try to select configured default aspect first, then 16:9
                            string configuredAspect = null;
                            if (_config.TryGetValue("default_aspect", out var da)) configuredAspect = da;
                            if (!string.IsNullOrEmpty(configuredAspect))
                            {
                                for (int i = 0; i < aspect.Items.Count; i++) { var it = aspect.Items[i] as ComboBoxItem; if (it != null && it.Content as string == configuredAspect) { aspect.SelectedIndex = i; break; } }
                            }
                            if (aspect.SelectedIndex < 0)
                            {
                                for (int i = 0; i < aspect.Items.Count; i++)
                                {
                                    var it = aspect.Items[i] as ComboBoxItem;
                                    if (it != null && it.Content is string contentStr && contentStr.Trim() == "16:9") { aspect.SelectedIndex = i; break; }
                                }
                            }

                            // fill resolutions for selected aspect
                            try
                            {
                                var sel = (aspect.SelectedItem as ComboBoxItem)?.Content as string;
                                if (!string.IsNullOrEmpty(sel) && resMap.ContainsKey(sel))
                                {
                                    foreach (var rr in resMap[sel].Distinct().OrderBy(x => x))
                                    {
                                        // set Tag to suffix like _3840x2160.jpg so it can be used for building URL
                                        var tag = "_" + rr + ".jpg";
                                        res.Items.Add(new ComboBoxItem { Content = rr, Tag = tag });
                                    }
                                }
                                else
                                {
                                    // fallback: add UHD
                                    res.Items.Add(new ComboBoxItem { Content = "3840x2160", Tag = "_3840x2160.jpg" });
                                }

                                // select configured default resolution if present, otherwise pick highest available for this aspect
                                string configuredRes = null;
                                if (_config.TryGetValue("default_resolution", out var dr)) configuredRes = dr;
                                // map legacy tokens to actual resolution strings
                                string targetRes = null;
                                if (!string.IsNullOrEmpty(configuredRes))
                                {
                                    if (configuredRes == "720") targetRes = "1280x720";
                                    else if (configuredRes == "1080") targetRes = "1920x1080";
                                    else if (configuredRes.Equals("UHD", StringComparison.OrdinalIgnoreCase)) targetRes = "3840x2160";
                                    else targetRes = configuredRes;
                                }

                                int bestIndex = -1;
                                long bestArea = -1;
                                for (int j = 0; j < res.Items.Count; j++)
                                {
                                    var it = res.Items[j] as ComboBoxItem;
                                    if (it == null) continue;
                                    var content = it.Content as string ?? "";
                                    if (!string.IsNullOrEmpty(targetRes) && content == targetRes) { res.SelectedIndex = j; bestIndex = -2; break; }
                                    var m = System.Text.RegularExpressions.Regex.Match(content, "(\\d{3,4})x(\\d{3,4})");
                                    if (m.Success && int.TryParse(m.Groups[1].Value, out var w) && int.TryParse(m.Groups[2].Value, out var h))
                                    {
                                        long area = (long)w * h;
                                        if (area > bestArea) { bestArea = area; bestIndex = j; }
                                    }
                                }
                                if (res.SelectedIndex < 0 && bestIndex >= 0) res.SelectedIndex = bestIndex;
                                // fallback to UHD if nothing selected
                                if (res.SelectedIndex < 0)
                                {
                                    res.Items.Add(new ComboBoxItem { Content = "3840x2160", Tag = "_3840x2160.jpg" });
                                    for (int j = 0; j < res.Items.Count; j++) { var it = res.Items[j] as ComboBoxItem; if (it != null && it.Content as string == "3840x2160") { res.SelectedIndex = j; break; } }
                                }

                            }
                            catch { }

                            // store mapping in Tag for later use (aspect.Tag -> Dictionary<string,List<string>>)
                            aspect.Tag = resMap;

                            // 静态 fallback：若 XML 无比例/分辨率数据，提供 5 种标准比例
                            if (aspectSet.Count == 0)
                            {
                                var staticMap = new Dictionary<string, List<string>>
                                {
                                    ["16:9"] = new List<string> { "3840x2160", "2560x1440", "1920x1080", "1366x768", "1280x720" },
                                    ["16:10"] = new List<string> { "2560x1600", "1920x1200", "1680x1050", "1440x900", "1280x800" },
                                    ["4:3"] = new List<string> { "1600x1200", "1400x1050", "1280x960", "1024x768", "800x600" },
                                    ["3:2"] = new List<string> { "3024x2016", "2496x1664", "1920x1280", "1440x960" },
                                    ["5:4"] = new List<string> { "2560x2048", "1920x1536", "1600x1280", "1280x1024" }
                                };
                                foreach (var ar in staticMap.Keys) aspectSet.Add(ar);
                                foreach (var kv in staticMap)
                                {
                                    if (!resMap.ContainsKey(kv.Key)) resMap[kv.Key] = new List<string>(kv.Value);
                                }
                            }
                        }
                    }
                }
                catch { }
                // 确保设置壁纸页的幻灯片目录默认为下载目录
                try { var sText = root?.FindName("SlideshowFolderText") as TextBox; if (sText != null) sText.Text = WallpaperFolderPath; } catch { }

                // 初始化年/月/日下拉用于历史下载选择（年：2021..当前，月：1..12，日：1..31）
                try
                {
                    var yearCb = root?.FindName("YearCombo") as ComboBox;
                    var monthCb = root?.FindName("MonthCombo") as ComboBox;
                    var dayCb = root?.FindName("DayCombo") as ComboBox;
                    if (yearCb != null && monthCb != null && dayCb != null)
                    {
                        yearCb.Items.Clear(); monthCb.Items.Clear(); dayCb.Items.Clear();
                        int startYear = 2021;
                        int currentYear = DateTime.Now.Year;
                        for (int y = startYear; y <= currentYear; y++) yearCb.Items.Add(new ComboBoxItem { Content = y.ToString() });
                        for (int m = 1; m <= 12; m++) monthCb.Items.Add(new ComboBoxItem { Content = m.ToString("D2") });
                        for (int d = 1; d <= 31; d++) dayCb.Items.Add(new ComboBoxItem { Content = d.ToString("D2") });
                        // 默认值为 7 天之前
                        var defDate = DateTime.Now.Date.AddDays(-7);
                        SelectComboItemByContent(yearCb, defDate.Year.ToString());
                        SelectComboItemByContent(monthCb, defDate.Month.ToString("D2"));
                        SelectComboItemByContent(dayCb, defDate.Day.ToString("D2"));
                    }
                }
                catch { }

                // 加载壁纸列表并填充标签筛选（供壁纸预览页使用）
                try
                {
                    LoadWallpapersFromXml();
                    PopulateTagFilter();
                    if (_allWallpapers.Count > 0)
                    {
                        _filteredWallpapers = _allWallpapers.ToList();
                    }
                }
                catch { }

                // 若开启自动更新列表，则在后台执行（每日一次）
                try
                {
                    var autoUpdateOn = _config.TryGetValue("auto_update_list", out var auv) ? auv.Equals("true", StringComparison.OrdinalIgnoreCase) : true;
                    var lastRun = _config.TryGetValue("auto_update_last_run", out var lr) ? lr : "";
                    var today = DateTime.Now.ToString("yyyy-MM-dd");
                    if (autoUpdateOn && lastRun != today)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await AutoUpdateListAsync(); }
                            catch (Exception exi) { LogException(exi); }
                        });
                    }
                }
                catch { }

                var http = new System.Net.Http.HttpClient();
                var url = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-US";
                var s = await http.GetStringAsync(url);
                using var doc = System.Text.Json.JsonDocument.Parse(s);
                var images = doc.RootElement.GetProperty("images");
                if (images.GetArrayLength() > 0)
                {
                    var first = images[0];
                    var urlBase = first.GetProperty("urlbase").GetString();
                    string title = null;
                    if (first.TryGetProperty("copyright", out var cp)) title = cp.GetString();
                    else if (first.TryGetProperty("title", out var t)) title = t.GetString();

                    if (!string.IsNullOrEmpty(urlBase))
                    {
                        var res = _config.ContainsKey("default_resolution") ? _config["default_resolution"] : "UHD";
                        var suffix = res == "720" || res.Equals("720p", StringComparison.OrdinalIgnoreCase) ? "_1280x720.jpg" : res == "1080" ? "_1920x1080.jpg" : "_UHD.jpg";
                        var full = "https://www.bing.com" + urlBase + suffix;
                        var img = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(full));
                        var mainImg = root?.FindName("MainImage") as Microsoft.UI.Xaml.Controls.Image;
                        if (mainImg != null) mainImg.Source = img;

                        var titleBlock = root?.FindName("ImageTitle") as TextBlock;
                        if (titleBlock != null) titleBlock.Text = title ?? string.Empty;

                        // update selector (select BUHD by default)
                                _ = this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                                {
                                    try
                                    {
                                        var seg = root?.FindName("BUHD") as FrameworkElement;
                                var segGrid = root?.FindName("SegGrid") as Grid;
                                var sel = root?.FindName("SelIndicator") as Border;
                                var selTrans = root?.FindName("SelTransform") as Microsoft.UI.Xaml.Media.TranslateTransform;
                                if (seg != null && sel != null && selTrans != null && segGrid != null)
                                {
                                    var t = seg.TransformToVisual(segGrid).TransformPoint(new Point(0, 0));
                                    double overlapAdjustment = 0;
                                    if (seg == (root?.FindName("B1080") as FrameworkElement)) overlapAdjustment = -2;
                                    if (seg == (root?.FindName("BUHD") as FrameworkElement)) overlapAdjustment = -4;

                                    double innerWidth = seg.ActualWidth;
                                    try
                                    {
                                        var btn = seg.FindName("Btn720") as FrameworkElement;
                                        if (btn == null) btn = seg.FindName("Btn1080") as FrameworkElement;
                                        if (btn == null) btn = seg.FindName("BtnUHD") as FrameworkElement;
                                        if (btn != null) innerWidth = btn.ActualWidth + 24;
                                    }
                                    catch { }

                                    var targetWidth = Math.Max(0, innerWidth - 4);
                                    var targetX = t.X + 2 + overlapAdjustment;

                                    if (AnimationEnabled && AnimationMs > 0)
                                    {
                                        AnimateSelIndicator(sel, selTrans, sel.Width, targetWidth, selTrans.X, targetX, AnimationMs);
                                    }
                                    else
                                    {
                                        sel.Width = targetWidth;
                                        selTrans.X = targetX;
                                        sel.Opacity = 1.0;
                                    }
                                }
                            }
                            catch { }
                        });
                    }
                }
            }
            catch { }
        }

        private void Seg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root == null) return;

                Button btn = sender as Button;
                if (btn != null)
                {
                    // 移动指示器到目标按钮并高亮
                    MoveSelToButton(btn);
                }

                // 加载对应清晰度图片
                var mainImg = root?.FindName("MainImage") as Microsoft.UI.Xaml.Controls.Image;
                if (mainImg != null)
                {
                    string suffix = "_UHD.jpg";
                    if (btn?.Name == "Btn720") suffix = "_1280x720.jpg";
                    else if (btn?.Name == "Btn1080") suffix = "_1920x1080.jpg";

                    try
                    {
                        var http = new System.Net.Http.HttpClient();
                        var url = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-US";
                        var s = http.GetStringAsync(url).Result;
                        using var doc = System.Text.Json.JsonDocument.Parse(s);
                        var images = doc.RootElement.GetProperty("images");
                        if (images.GetArrayLength() > 0)
                        {
                            var first = images[0];
                            var urlBase = first.GetProperty("urlbase").GetString();
                            if (!string.IsNullOrEmpty(urlBase))
                            {
                                var full = "https://www.bing.com" + urlBase + suffix;
                                mainImg.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(full));

                                var titleBlock = root?.FindName("ImageTitle") as TextBlock;
                                if (titleBlock != null && first.TryGetProperty("copyright", out var cp)) titleBlock.Text = cp.GetString();
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private int _currentSegIndex = 2;
        // 将 SelIndicator 移动并居中到指定按钮内部（基于 SegGrid 三等分列计算）
        private void MoveSelToButton(Button btn)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var segGrid = root?.FindName("SegGrid") as Grid;
                var sel = root?.FindName("SelIndicator") as Border;
                var selTrans = root?.FindName("SelTransform") as Microsoft.UI.Xaml.Media.TranslateTransform;
                if (segGrid == null || sel == null || selTrans == null || btn == null) return;

                int colIndex = 2;
                var b720 = root.FindName("Btn720") as Button;
                var b1080 = root.FindName("Btn1080") as Button;
                var buhd = root.FindName("BtnUHD") as Button;
                if (btn == b720) colIndex = 0; else if (btn == b1080) colIndex = 1; else colIndex = 2;

                // 计算列宽并设置指示器宽度与位置
                double segGridWidth = Math.Max(0, segGrid.ActualWidth);
                double columnWidth = segGridWidth / 3.0;
                double targetWidth = Math.Max(0, columnWidth - 12);
                double targetX = columnWidth * colIndex + (columnWidth - targetWidth) / 2.0;

                _currentSegIndex = colIndex;

                if (AnimationEnabled && AnimationMs > 0)
                {
                    AnimateSelIndicator(sel, selTrans, sel.Width, targetWidth, selTrans.X, targetX, AnimationMs);
                }
                else
                {
                    sel.Width = targetWidth;
                    selTrans.X = targetX;
                    sel.Opacity = 1.0;
                }

                // 调整按钮前景色，突出被选中项
                try
                {
                    foreach (var b in new[] { b720, b1080, buhd })
                    {
                        if (b == null) continue;
                        b.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlForegroundBaseHighBrush"];
                    }
                    var selBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                    if (colIndex == 0 && b720 != null) b720.Foreground = selBrush;
                    if (colIndex == 1 && b1080 != null) b1080.Foreground = selBrush;
                    if (colIndex == 2 && buhd != null) buhd.Foreground = selBrush;
                }
                catch { }
            }
            catch { }
        }

        // 递归查找视觉树中第一个可用的 TextBlock 或 ContentPresenter 的实际宽度（保留以备后用）
        private static int Gcd(int a, int b)
        {
            try { a = Math.Abs(a); b = Math.Abs(b); while (b != 0) { var t = a % b; a = b; b = t; } return a == 0 ? 1 : a; } catch { return 1; }
        }

        // 根据所选分辨率调整 URL（仅当非 3840x2160 时替换 UHD 或 3840x2160 后缀）
        private string AdjustUrlToResolution(string url, string selRes)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return url;
                if (string.IsNullOrEmpty(selRes)) return url;
                // 不改变 3840x2160 的默认行为
                if (selRes == "3840x2160") return url;

                // 规范化相对 URL
                if (url.StartsWith("//")) url = "https:" + url;
                else if (url.StartsWith("/")) url = "https://cn.bing.com" + url;

                try
                {
                    // 先替换常见的 UHD 或 3840x2160 后缀
                    url = System.Text.RegularExpressions.Regex.Replace(url, "_(?:UHD|3840x2160)(?:\\.jpg)?", "_" + selRes + ".jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                catch { }

                try
                {
                    // 对于 th?id=..._XXXXxYYYY.jpg 形式，直接重建直链
                    var m = System.Text.RegularExpressions.Regex.Match(url, @"th\?id=([^_]+)_(?:\\d{3,4}x\\d{3,4}|UHD)(?:\\.jpg)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        var idPart = m.Groups[1].Value;
                        if (!string.IsNullOrEmpty(idPart))
                        {
                            url = $"https://cn.bing.com/th?id={idPart}_{selRes}.jpg";
                        }
                    }
                }
                catch { }

                return url;
            }
            catch { return url; }
        }

        private void SelectComboItemByContent(ComboBox cb, string content)
        {
            try
            {
                if (cb == null) return;
                for (int i = 0; i < cb.Items.Count; i++)
                {
                    if ((cb.Items[i] as ComboBoxItem)?.Content as string == content) { cb.SelectedIndex = i; return; }
                }
            }
            catch { }
        }

        private double GetDescendantActualWidth(DependencyObject root)
        {
            try
            {
                if (root == null) return 0;
                var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                    if (child is TextBlock tb && tb.ActualWidth > 0) return tb.ActualWidth;
                    if (child is ContentPresenter cp && cp.ActualWidth > 0) return cp.ActualWidth;
                    var w = GetDescendantActualWidth(child);
                    if (w > 0) return w;
                }
            }
            catch { }
            return 0;
        }


        // animation helper
        private void AnimateSelIndicator(Border sel, Microsoft.UI.Xaml.Media.TranslateTransform selTrans, double fromWidth, double toWidth, double fromX, double toX, int durationMs)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var dq = this.DispatcherQueue;
                        System.Threading.Timer timer = null;
                timer = new System.Threading.Timer(_ =>
                {
                    var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / Math.Max(1, durationMs));
                    var curW = fromWidth + (toWidth - fromWidth) * t;
                    var curX = fromX + (toX - fromX) * t;
                    try
                    {
                        dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                        {
                            sel.Width = curW;
                            selTrans.X = curX;
                            sel.Opacity = 1.0;
                        });
                    }
                    catch { }

                    if (t >= 1.0)
                    {
                        try { timer?.Dispose(); } catch { }
                    }
                }, null, 0, 16);
            }
            catch { }
        }


        /// <summary>打开 Windows 系统个性化设置页面（ms-settings:personalization）。</summary>
        private void OpenWindowsPersonalization_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:personalization",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { LogException(ex); }
        }

        private void ApplySlideshowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var status = root?.FindName("SettingsStatusText") as TextBlock;

                var folder = WallpaperFolderPath;
                var sText = root?.FindName("SlideshowFolderText") as TextBox;
                if (sText != null && !string.IsNullOrWhiteSpace(sText.Text)) folder = sText.Text.Trim();

                if (!Directory.Exists(folder))
                {
                    if (status != null) status.Text = "幻灯片目录不存在: " + folder;
                    return;
                }

                // 读取 UI 参数
                var intervalCombo = root?.FindName("SlideshowIntervalCombo") as ComboBox;
                uint intervalMs = 1800 * 1000; // 默认 30 分钟
                if (intervalCombo?.SelectedItem is ComboBoxItem ivci && uint.TryParse(ivci.Tag as string, out var secs)) intervalMs = secs * 1000;

                var shuffleCheck = root?.FindName("ShuffleCheck") as ToggleSwitch;
                bool shuffle = shuffleCheck?.IsOn == true;

                var fillCombo = root?.FindName("FillModeCombo") as ComboBox;
                string fillMode = "Fill";
                if (fillCombo?.SelectedItem is ComboBoxItem fci) fillMode = fci.Tag?.ToString() ?? "Fill";

                // 调用 App.SetDesktopSlideshow（使用正确的 COM 接口 IShellItemArray）
                bool ok = App.SetDesktopSlideshow(folder, intervalMs, shuffle, fillMode);
                if (status != null) status.Text = ok
                    ? "幻灯片已设置成功。"
                    : "设置幻灯片失败，请查看日志（%TEMP%\\BingWPDLHelper_slideshow.log）。";

                // 保存设置到配置文件
                _config["slideshow_interval"] = intervalMs.ToString();
                _config["slideshow_shuffle"] = shuffle ? "true" : "false";
                _config["slideshow_fill"] = fillMode;
                SaveConfig();
            }
            catch (Exception ex)
            {
                LogException(ex);
                var root2 = this.Content as FrameworkElement;
                var status = root2?.FindName("SettingsStatusText") as TextBlock;
                if (status != null) status.Text = "发生错误: " + ex.Message;
            }
        }
// 保证在窗口加载和大小变化时修正 SelIndicator 位置，避免错位；并恢复选择时使用主题高亮色（SystemControlHighlightAccentBrush）
private void MainWindow_Loaded(object sender, RoutedEventArgs e)
{
    try
    {
        void Recalc()
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var segGrid = root?.FindName("SegGrid") as Grid;
                var sel = root?.FindName("SelIndicator") as Border;
                var selTrans = root?.FindName("SelTransform") as Microsoft.UI.Xaml.Media.TranslateTransform;
                if (segGrid == null || sel == null || selTrans == null) return;

                // 优先使用实际按钮 BtnUHD，若不存在则回退到 Btn1080，或 SegGrid 中第一个 Btn* 子元素
                FrameworkElement target = root?.FindName("BtnUHD") as FrameworkElement;
                if (target == null) target = root?.FindName("Btn1080") as FrameworkElement;
                if (target == null)
                {
                    try
                    {
                        if (segGrid.Children.Count > 0)
                        {
                            foreach (var ch in segGrid.Children)
                            {
                                if (ch is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) && fe.Name.StartsWith("Btn"))
                                {
                                    target = fe;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (target == null) return;

                var t = target.TransformToVisual(segGrid).TransformPoint(new Point(0, 0));
                double overlapAdjustment = -4;
                double innerWidth = target.ActualWidth;
                try
                {
                    var btn = target.FindName("BtnUHD") as FrameworkElement;
                    if (btn != null) innerWidth = btn.ActualWidth + 24;
                    else innerWidth = target.ActualWidth;
                }
                catch { }
                // 将 SelIndicator 宽度与目标按钮宽度匹配，并令其在目标内部居中显示
                sel.Width = Math.Max(0, innerWidth - 4);
                // 计算使 SelIndicator 在按钮内部水平居中：按钮左偏 + (按钮宽 - sel宽)/2
                var centeredX = t.X + (target.ActualWidth - sel.Width) / 2.0;
                selTrans.X = centeredX + overlapAdjustment;
                sel.Opacity = 1.0;
            }
            catch { }
        }

        Recalc();
        var root2 = this.Content as FrameworkElement;
        var segGrid2 = root2?.FindName("SegGrid") as Grid;
        if (segGrid2 != null) segGrid2.SizeChanged += (_, __) => Recalc();
    }
    catch { }
}


// save wallpaper helper
private string SaveImageToWallpaper(Microsoft.UI.Xaml.Controls.Image image, string suggestedName)
{
    try
    {
        if (image?.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bi && bi.UriSource != null)
        {
            var uri = bi.UriSource;
            using var http = new System.Net.Http.HttpClient();
            var data = http.GetByteArrayAsync(uri).Result;
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var fileName = suggestedName ?? ("bing_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext);
            var outPath = Path.Combine(WallpaperFolderPath ?? AppFolderPath, fileName);
            File.WriteAllBytes(outPath, data);
            return outPath;
        }
    }
    catch { }
    return null;
}

        // 保存配置到 config.ini（覆盖写入，保持常见键）
        private void SaveConfig()
        {
            try
            {
                var lines = new List<string>();
                lines.Add("version=" + AppVersion);
                lines.Add("animation_enabled=" + (AnimationEnabled ? "true" : "false"));
                lines.Add("animation_ms=" + AnimationMs.ToString());
                // default_resolution 从 UI 或 _config 决定，同时保存默认比例
                var root = this.Content as FrameworkElement;
                var resCombo = root?.FindName("ResolutionCombo") as ComboBox;
                var aspectCombo = root?.FindName("AspectRatioCombo") as ComboBox;
                string defRes = "UHD";
                if (resCombo != null && resCombo.SelectedItem is ComboBoxItem rcbi && rcbi.Content is string rs)
                {
                    if (rs.StartsWith("1280") || rs.StartsWith("720")) defRes = "720";
                    else if (rs.StartsWith("1920") || rs.StartsWith("1080")) defRes = "1080";
                    else defRes = "UHD";
                }
                else if (_config.TryGetValue("default_resolution", out var dr)) defRes = dr;
                lines.Add("default_resolution=" + defRes);
                // save aspect
                if (aspectCombo != null && aspectCombo.SelectedItem is ComboBoxItem ab && ab.Content is string asp) lines.Add("default_aspect=" + asp);
                else if (_config.TryGetValue("default_aspect", out var da)) lines.Add("default_aspect=" + da);

                lines.Add("download_folder=" + (WallpaperFolderPath ?? "Wallpaper"));

                // 应用设置
                var autoChk = root?.FindName("AutoStartCheck") as ToggleSwitch;
                var autoVal = (autoChk != null && autoChk.IsOn) ? "true" : "false";
                lines.Add("autostart=" + autoVal);

                var defList = "local";
                var dlCombo2 = root?.FindName("DefaultListCombo") as ComboBox;
                if (dlCombo2 != null && dlCombo2.SelectedItem is ComboBoxItem cbi2)
                {
                    defList = cbi2.Tag as string ?? (cbi2.Content as string ?? defList);
                }
                lines.Add("default_list=" + defList);

                var api = "bing";
                var apiCombo = root?.FindName("ApiSourceCombo") as ComboBox;
                if (apiCombo != null && apiCombo.SelectedItem is ComboBoxItem ac && ac.Content is string ap) api = ap == "必应" ? "bing" : "unknown";
                lines.Add("api_source=" + api);

                // 自动更新列表
                var autoUpdateChk = root?.FindName("AutoUpdateListCheck") as ToggleSwitch;
                var autoUpdateVal = (autoUpdateChk != null && autoUpdateChk.IsOn) ? "true" : "false";
                lines.Add("auto_update_list=" + autoUpdateVal);
                if (_config.TryGetValue("auto_update_last_run", out var lr)) lines.Add("auto_update_last_run=" + lr);

                // 关闭行为
                if (_config.TryGetValue("close_behavior", out var cbeh)) lines.Add("close_behavior=" + cbeh);
                else lines.Add("close_behavior=Tray");

                // 主题模式 + 颜色主题
                if (_config.TryGetValue("theme_mode", out var tmod)) lines.Add("theme_mode=" + tmod);
                else lines.Add("theme_mode=System");
                if (_config.TryGetValue("color_theme", out var cth)) lines.Add("color_theme=" + cth);
                else lines.Add("color_theme=System");

                // 下载线程数
                if (_config.TryGetValue("download_threads", out var dth)) lines.Add("download_threads=" + dth);
                else lines.Add("download_threads=4");

                // 显示语言
                if (_config.TryGetValue("language", out var lng)) lines.Add("language=" + lng);
                else lines.Add("language=zh-CN");

                // 背景材质（Fluent Design）
                if (_config.TryGetValue("backdrop_type", out var bdt)) lines.Add("backdrop_type=" + bdt);
                else lines.Add("backdrop_type=Mica");
                if (_config.TryGetValue("transparent_background", out var tbg)) lines.Add("transparent_background=" + tbg);
                else lines.Add("transparent_background=true");

                // 保存窗口大小位置
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    if (GetWindowRect(hwnd, out var rc))
                    {
                        lines.Add("win_x=" + rc.left);
                        lines.Add("win_y=" + rc.top);
                        lines.Add("win_w=" + (rc.right - rc.left));
                        lines.Add("win_h=" + (rc.bottom - rc.top));
                    }
                }
                catch { }

                File.WriteAllLines(ConfigFilePath, lines);
            }
            catch { }
        }

        // 将分段控件切换为通过按钮背景改变选中状态（不移动底色）
        private void SetSegmentSelected(string buttonName)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var btn720 = root?.FindName("Btn720") as Button;
                var btn1080 = root?.FindName("Btn1080") as Button;
                var btnUHD = root?.FindName("BtnUHD") as Button;
                var accent = Application.Current.Resources.TryGetValue("SystemControlHighlightAccentBrush", out var ac) ? ac as Microsoft.UI.Xaml.Media.Brush : null;
                var fgHigh = Application.Current.Resources.TryGetValue("SystemControlForegroundBaseHighBrush", out var fh) ? fh as Microsoft.UI.Xaml.Media.Brush : null;
                var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                if (btn720 != null) { btn720.Background = transparent; btn720.Foreground = fgHigh; }
                if (btn1080 != null) { btn1080.Background = transparent; btn1080.Foreground = fgHigh; }
                if (btnUHD != null) { btnUHD.Background = transparent; btnUHD.Foreground = fgHigh; }

                Button sel = null;
                if (buttonName == "Btn720") sel = btn720;
                else if (buttonName == "Btn1080") sel = btn1080;
                else sel = btnUHD;
                if (sel != null && accent != null)
                {
                    sel.Background = accent;
                    sel.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                }

                // 隐藏移动底色指示器，避免与按钮背景冲突
                var selIndicator = root?.FindName("SelIndicator") as Border;
                if (selIndicator != null) selIndicator.Opacity = 0.0;
            }
            catch { }
        }

        // 打开文件夹选择器（使用 WinRT FolderPicker，桌面应用需初始化窗口句柄）
        private async void OpenFolderPicker_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
                picker.FileTypeFilter.Add("*");
                // 初始化窗口宿主句柄（WinUI 桌面）
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    var path = folder.Path;
                    // 检查写入权限
                    try
                    {
                        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                        var tmp = Path.Combine(path, ".wpdl_test_write.tmp");
                        File.WriteAllText(tmp, "test");
                        File.Delete(tmp);
                    }
                    catch (Exception ex)
                    {
                        var status = (this.Content as FrameworkElement)?.FindName("DownloadStatusText") as TextBlock;
                        if (status != null) status.Text = "没有对所选目录的写入权限：" + ex.Message;
                        return;
                    }

                    WallpaperFolderPath = path;
                    var dlText = (this.Content as FrameworkElement)?.FindName("DownloadFolderText") as TextBox;
                    if (dlText != null) dlText.Text = WallpaperFolderPath;
                    _config["download_folder"] = WallpaperFolderPath;
                    SaveConfig();

                    // 确保 UI 刷新并将列表项选中以触发可视更新
                    try { var left = (this.Content as FrameworkElement)?.FindName("LeftNavList") as ListBox; if (left != null) left.SelectedIndex = left.SelectedIndex; } catch { }
                }
            }
            catch { }
        }

        // 用户在文本框回车后尝试设置目录并检测权限
        private void DownloadFolderText_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            try
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    var tb = sender as TextBox;
                    if (tb == null) return;
                    var newPath = tb.Text?.Trim();
                    if (string.IsNullOrEmpty(newPath)) return;
                    if (!Path.IsPathRooted(newPath)) newPath = Path.GetFullPath(newPath);
                    try
                    {
                        if (!Directory.Exists(newPath)) Directory.CreateDirectory(newPath);
                        var tmp = Path.Combine(newPath, ".wpdl_test_write.tmp");
                        File.WriteAllText(tmp, "test");
                        File.Delete(tmp);
                    }
                    catch (Exception ex)
                    {
                        var status = (this.Content as FrameworkElement)?.FindName("DownloadStatusText") as TextBlock;
                        if (status != null) status.Text = "目录不可用或没有写入权限：" + ex.Message;
                        return;
                    }
                    WallpaperFolderPath = newPath;
                    _config["download_folder"] = WallpaperFolderPath;
                    SaveConfig();
                    var status2 = (this.Content as FrameworkElement)?.FindName("DownloadStatusText") as TextBlock;
                    if (status2 != null) status2.Text = "下载目录已更新：" + WallpaperFolderPath;
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // Windows 11 圆角窗口
        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_SYSCOMMAND = 0x0112;
        private const uint SC_MAXIMIZE = 0xF030;
        private const uint SC_RESTORE = 0xF120;

        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;
        private const int SW_MAXIMIZE = 3;

        private const int GWL_STYLE = -16;
        private const long WS_SYSMENU = 0x00080000L;
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        private bool IsWindowMaximized(IntPtr hwnd)
        {
            try
            {
                WINDOWPLACEMENT wp = new WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf(wp);
                if (GetWindowPlacement(hwnd, ref wp))
                {
                    return wp.showCmd == SW_MAXIMIZE;
                }
            }
            catch { }
            return false;
        }

        // 更新标题栏颜色：使用系统默认（null），不强制设置任何颜色
        // 微软文档说明："You can't set transparent colors. The color's alpha channel is ignored."
        // 任何自定义颜色都会导致按钮背景变成纯色方块。设为 null 让系统使用 Fluent 默认透明样式。
        private void UpdateTitleBarColors()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow != null)
                {
                    var root = this.Content as FrameworkElement;
                    var titleBar = appWindow.TitleBar;

                    // 全部设为 null：让系统使用 Fluent 默认样式（透明背景 + 系统主题色 glyph）
                    try { titleBar.ButtonForegroundColor = null; } catch { }
                    try { titleBar.ButtonBackgroundColor = null; } catch { }
                    try { titleBar.ButtonHoverForegroundColor = null; } catch { }
                    try { titleBar.ButtonHoverBackgroundColor = null; } catch { }
                    try { titleBar.ButtonPressedForegroundColor = null; } catch { }
                    try { titleBar.ButtonPressedBackgroundColor = null; } catch { }
                    try { titleBar.ButtonInactiveForegroundColor = null; } catch { }
                    try { titleBar.ButtonInactiveBackgroundColor = null; } catch { }
                    try { titleBar.ForegroundColor = null; } catch { }
                    try { titleBar.BackgroundColor = null; } catch { }
                    try { titleBar.InactiveForegroundColor = null; } catch { }
                    try { titleBar.InactiveBackgroundColor = null; } catch { }

                    // 设置标题栏左右内边距列，为系统标题按钮（最小化/最大化/关闭）预留空间
                    try
                    {
                        var leftInset = titleBar.LeftInset;
                        var rightInset = titleBar.RightInset;
                        System.Diagnostics.Debug.WriteLine($"TitleBar insets: Left={leftInset} Right={rightInset}");
                        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "error.log"),
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Debug] TitleBar insets: Left={leftInset} Right={rightInset}{Environment.NewLine}"); } catch { }
                        var leftCol = root?.FindName("LeftPaddingColumn") as ColumnDefinition;
                        var rightCol = root?.FindName("RightPaddingColumn") as ColumnDefinition;
                        if (leftCol != null && leftInset > 0)
                            leftCol.Width = new GridLength(leftInset);
                        // RightInset 可能在窗口首次渲染时为 0，使用 138 作为回退值（3 个按钮 × 46px）
                        if (rightCol != null)
                        {
                            int rightWidth = rightInset > 0 ? rightInset : 138;
                            rightCol.Width = new GridLength(rightWidth);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>主题模式选择变更：立即应用到 UI（递归遍历所有 FrameworkElement）。</summary>
        private void ThemeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root == null) return;
                var combo = root.FindName("ThemeModeCombo") as ComboBox;
                if (combo == null || combo.SelectedItem == null) return;
                var tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
                ApplyThemeMode(tag);
            }
            catch { }
        }

        /// <summary>应用主题模式（Light/Dark/System），递归遍历可视化树强制每个元素生效。</summary>
        private void ApplyThemeMode(string mode)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root == null) return;
                var theme = mode switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default, // System
                };
                root.RequestedTheme = theme;
                UpdateThemeRecursive(root, theme);
                try { UpdateTitleBarColors(); } catch { }
            }
            catch { }
        }

        /// <summary>递归遍历可视化树，强制每个 FrameworkElement 的 RequestedTheme。</summary>
        private void UpdateThemeRecursive(DependencyObject parent, ElementTheme theme)
        {
            try
            {
                if (parent == null) return;
                if (parent is FrameworkElement fe)
                    fe.RequestedTheme = theme;
                int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++)
                {
                    var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                    UpdateThemeRecursive(child, theme);
                }
            }
            catch { }
        }

        /// <summary>应用颜色主题：覆盖 SystemAccentColor 及其明暗变体。</summary>
        private void ApplyColorTheme(string colorTheme)
        {
            try
            {
                var res = Application.Current.Resources;
                (Color main, Color light1, Color light2, Color light3, Color dark1, Color dark2, Color dark3) preset = colorTheme switch
                {
                    "Blue" => (Color.FromArgb(255, 0, 120, 212), Color.FromArgb(255, 58, 160, 255), Color.FromArgb(255, 0, 120, 212), Color.FromArgb(255, 0, 90, 158), Color.FromArgb(255, 0, 90, 158), Color.FromArgb(255, 0, 70, 122), Color.FromArgb(255, 0, 50, 87)),
                    "Green" => (Color.FromArgb(255, 16, 137, 62), Color.FromArgb(255, 76, 197, 122), Color.FromArgb(255, 16, 137, 62), Color.FromArgb(255, 12, 102, 46), Color.FromArgb(255, 12, 102, 46), Color.FromArgb(255, 9, 77, 35), Color.FromArgb(255, 6, 51, 23)),
                    "Orange" => (Color.FromArgb(255, 202, 80, 16), Color.FromArgb(255, 255, 140, 76), Color.FromArgb(255, 202, 80, 16), Color.FromArgb(255, 151, 60, 12), Color.FromArgb(255, 151, 60, 12), Color.FromArgb(255, 113, 45, 9), Color.FromArgb(255, 76, 30, 6)),
                    "Purple" => (Color.FromArgb(255, 120, 75, 160), Color.FromArgb(255, 180, 135, 220), Color.FromArgb(255, 120, 75, 160), Color.FromArgb(255, 90, 56, 120), Color.FromArgb(255, 90, 56, 120), Color.FromArgb(255, 68, 42, 90), Color.FromArgb(255, 45, 28, 60)),
                    "Pink" => (Color.FromArgb(255, 233, 30, 99), Color.FromArgb(255, 255, 94, 158), Color.FromArgb(255, 233, 30, 99), Color.FromArgb(255, 175, 23, 74), Color.FromArgb(255, 175, 23, 74), Color.FromArgb(255, 131, 17, 56), Color.FromArgb(255, 88, 12, 37)),
                    _ => (Color.FromArgb(255, 0, 120, 212), Color.FromArgb(255, 58, 160, 255), Color.FromArgb(255, 0, 120, 212), Color.FromArgb(255, 0, 90, 158), Color.FromArgb(255, 0, 90, 158), Color.FromArgb(255, 0, 70, 122), Color.FromArgb(255, 0, 50, 87)), // System 回退蓝
                };
                res["SystemAccentColor"] = preset.main;
                res["SystemAccentColorLight1"] = preset.light1;
                res["SystemAccentColorLight2"] = preset.light2;
                res["SystemAccentColorLight3"] = preset.light3;
                res["SystemAccentColorDark1"] = preset.dark1;
                res["SystemAccentColorDark2"] = preset.dark2;
                res["SystemAccentColorDark3"] = preset.dark3;
            }
            catch { }
        }

        /// <summary>
        /// 应用窗口背景材质（Fluent Design）：Mica 云母 / Acrylic 亚克力 / None 不透明。
        /// 同时根据 TransparentBackground 配置决定根 Grid 是否透明（让背景材质透出）。
        /// </summary>
        private void ApplyBackdrop(string backdropType, bool transparentBackground)
        {
            try
            {
                // 1. 设置系统背景材质
                Microsoft.UI.Xaml.Media.SystemBackdrop backdrop = null;
                if (string.Equals(backdropType, "Mica", StringComparison.OrdinalIgnoreCase))
                {
                    backdrop = new MicaBackdrop();
                }
                else if (string.Equals(backdropType, "Acrylic", StringComparison.OrdinalIgnoreCase))
                {
                    backdrop = new DesktopAcrylicBackdrop();
                }
                // None => backdrop = null（纯色不透明）
                try { this.SystemBackdrop = backdrop; } catch { }

                // 2. 设置根 Grid 透明度：透明则背景材质透出，不透明则使用主题画刷
                try
                {
                    var root = this.Content as FrameworkElement;
                    if (root is Grid rootGrid)
                    {
                        rootGrid.Background = transparentBackground && backdrop != null
                            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
                            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>背景材质下拉框选择变更：立即应用并写入 _config。</summary>
        private void BackdropType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_isLoadingSettings) return;
                var combo = sender as ComboBox;
                if (combo?.SelectedItem is ComboBoxItem item)
                {
                    var type = item.Tag as string ?? "Mica";
                    _config["backdrop_type"] = type;
                    var transparent = _config.TryGetValue("transparent_background", out var tb)
                        ? tb.Equals("true", StringComparison.OrdinalIgnoreCase) : true;
                    ApplyBackdrop(type, transparent);
                }
            }
            catch { }
        }

        /// <summary>透明背景开关切换：立即应用并写入 _config。</summary>
        private void TransparentBackground_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isLoadingSettings) return;
                var ts = sender as ToggleSwitch;
                if (ts == null) return;
                var on = ts.IsOn;
                _config["transparent_background"] = on ? "true" : "false";
                var type = _config.TryGetValue("backdrop_type", out var bt) ? bt : "Mica";
                ApplyBackdrop(type, on);
            }
            catch { }
        }

        private void MinButton_Click(object sender, RoutedEventArgs e)
        {
            try { ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), SW_MINIMIZE); } catch { }
        }

        private void MaxButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (IsWindowMaximized(hwnd))
                {
                    SendMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_RESTORE), IntPtr.Zero);
                }
                else
                {
                    SendMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_MAXIMIZE), IntPtr.Zero);
                }
            }
            catch { }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 读取关闭行为配置：Tray 则最小化到托盘，Exit 则直接退出
                var behavior = _config.TryGetValue("close_behavior", out var cb) ? cb : "Tray";
                if (string.Equals(behavior, "Exit", StringComparison.OrdinalIgnoreCase))
                {
                    _isExiting = true;
                    try { this.Close(); } catch { }
                }
                else
                {
                    // 最小化到托盘
                    try { _trayManager?.HideToTray(); } catch { }
                }
            }
            catch { try { this.Close(); } catch { } }
        }

        private void CollapseNavButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var nav = root?.FindName("NavView") as Microsoft.UI.Xaml.Controls.NavigationView;
                if (nav != null) nav.IsPaneOpen = !nav.IsPaneOpen;
            }
            catch { }
        }

        private void ToggleThemeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root == null) return;
                var effectiveIsDark = root.ActualTheme == ElementTheme.Dark;
                root.RequestedTheme = effectiveIsDark ? ElementTheme.Light : ElementTheme.Dark;
                var toggleBtn = root.FindName("ToggleThemeButton") as Button;
                if (toggleBtn != null)
                {
                    var isDark = root.RequestedTheme == ElementTheme.Dark;
                    var glyph = isDark ? "☀" : "☾";
                    toggleBtn.Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe UI Symbol") };
                }
                UpdateTitleBarColors();
            }
            catch { }
        }

        private async void ChangeDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            // (kept original implementation)
        }

        /// <summary>下载历史壁纸开关切换：根据开关状态启用/禁用日期选择框。</summary>
        private void DownloadHistoryCheck_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                var ts = sender as ToggleSwitch;
                var on = ts?.IsOn == true;
                var root = this.Content as FrameworkElement;
                var yearCb = root?.FindName("YearCombo") as ComboBox;
                var monthCb = root?.FindName("MonthCombo") as ComboBox;
                var dayCb = root?.FindName("DayCombo") as ComboBox;
                if (yearCb != null) yearCb.IsEnabled = on;
                if (monthCb != null) monthCb.IsEnabled = on;
                if (dayCb != null) dayCb.IsEnabled = on;
            }
            catch { }
        }

        private void MaxDatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            try
            {
                if (sender?.Date == null) return;
                var selected = sender.Date.Value.Date;
                if (selected.Year < 2021)
                {
                    sender.Date = new DateTimeOffset(new DateTime(2021, 1, 1));
                }
            }
            catch { }
        }

        private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var root = this.Content as FrameworkElement;
            var status = root?.FindName("DownloadStatusText") as TextBlock;
            if (btn != null) btn.IsEnabled = false;
            try
            {
                if (status != null) status.Text = "准备下载...";

                bool downloadHistory = false;
                var chk = root?.FindName("DownloadHistoryCheck") as ToggleSwitch;
                if (chk != null) downloadHistory = chk.IsOn;

                // ensure target folder is absolute and exists
                var targetFolder = WallpaperFolderPath;
                if (string.IsNullOrEmpty(targetFolder)) targetFolder = Path.Combine(AppFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wallpaper");
                if (!Path.IsPathRooted(targetFolder)) targetFolder = Path.Combine(AppFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), targetFolder);
                try { if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder); }
                catch (Exception ex) { if (status != null) status.Text = "无法创建下载目录: " + ex.Message; return; }

                using var http = new System.Net.Http.HttpClient();
                // set common headers to mimic a browser (some servers block default HttpClient UA)
                try
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
                    http.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                    http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
                    // ensure connection is closed between requests to avoid server rejecting keep-alive reuse
                    try { http.DefaultRequestHeaders.ConnectionClose = true; } catch { }
                    try { http.DefaultRequestHeaders.Referrer = new Uri("https://www.bing.com/"); } catch { }
                }
                catch { }

                int downloaded = 0, errors = 0;

                async Task<bool> DownloadToFileAsync(Uri uri, string dest)
                {
                    try
                    {
                        using var resp = await http.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                        if (!resp.IsSuccessStatusCode)
                        {
                            var msg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {uri}";
                            LogException(new Exception(msg));
                            try { if (status != null) status.Text = "下载失败: " + msg; } catch { }
                            return false;
                        }
                        using var stream = await resp.Content.ReadAsStreamAsync();
                        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                        await stream.CopyToAsync(fs);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                        try { if (status != null) status.Text = "下载时发生错误: " + ex.Message; } catch { }
                        return false;
                    }
                }

                if (downloadHistory)
                {
                    DateTimeOffset startDate = DateTimeOffset.Now.AddDays(-7);
                    try
                    {
                        var yearCb = root?.FindName("YearCombo") as ComboBox;
                        var monthCb = root?.FindName("MonthCombo") as ComboBox;
                        var dayCb = root?.FindName("DayCombo") as ComboBox;
                        if (yearCb != null && monthCb != null && dayCb != null && yearCb.SelectedItem is ComboBoxItem yit && monthCb.SelectedItem is ComboBoxItem mit && dayCb.SelectedItem is ComboBoxItem dit)
                        {
                            if (int.TryParse(yit.Content as string, out var y) && int.TryParse(mit.Content as string, out var m) && int.TryParse(dit.Content as string, out var d))
                            {
                                try { startDate = new DateTimeOffset(new DateTime(y, m, d)); } catch { }
                            }
                        }
                    }
                    catch { }

                    var minAllowed = new DateTime(2021, 1, 1);
                    if (startDate.Date < minAllowed) startDate = new DateTimeOffset(minAllowed);
                    int max = (int)(DateTimeOffset.Now.Date - startDate.Date).TotalDays; if (max <= 0) max = 1;

                    var aspectCb = root?.FindName("AspectRatioCombo") as ComboBox;
                    var resCb = root?.FindName("ResolutionCombo") as ComboBox;
                    string selAspect = (aspectCb?.SelectedItem as ComboBoxItem)?.Content as string;
                    string selRes = (resCb?.SelectedItem as ComboBoxItem)?.Content as string ?? "3840x2160";

                    var localXml = Path.Combine(AppContext.BaseDirectory, "Assets", "list.xml");
                    XDocument localDoc = null;
                    int totalWallpapersInList = 0;
                    int totalResolutionGroups = 0;
                    try { if (status != null) status.Text = $"尝试读取 Assets/list.xml，默认路径: {localXml}，输出目录: {AppContext.BaseDirectory}"; } catch { }

                    // 尝试多个可能的位置，包含打包应用和调试输出目录
                    var candidatePaths = new List<string>
                    {
                        Path.Combine(AppContext.BaseDirectory, "Assets", "list.xml"),
                        Path.Combine(Directory.GetCurrentDirectory(), "Assets", "list.xml"),
                        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "list.xml")
                    };
                    // 如果应用被打包为 MSIX，可从 Package 安装位置读取
                    try
                    {
                        var pkgPath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
                        candidatePaths.Insert(0, Path.Combine(pkgPath, "Assets", "list.xml"));
                    }
                    catch { }

                    string usedPath = null;
                    foreach (var p in candidatePaths)
                    {
                        try
                        {
                            if (File.Exists(p))
                            {
                                localXml = p;
                                localDoc = XDocument.Load(localXml);
                                usedPath = p;
                                break;
                            }
                        }
                        catch (Exception ex) { LogException(ex); }
                    }

                    if (localDoc != null)
                    {
                        try
                        {
                            totalWallpapersInList = localDoc.Descendants("wallpaper").Count();
                            totalResolutionGroups = localDoc.Descendants("resolution").Count();
                            try { if (status != null) status.Text = $"已加载 Assets/list.xml：{totalWallpapersInList} 条 wallpaper，{totalResolutionGroups} 个 resolution。路径: {usedPath ?? localXml}"; } catch { }
                        }
                        catch (Exception ex)
                        {
                            localDoc = null;
                            LogException(ex);
                            try { if (status != null) status.Text = "无法解析 Assets/list.xml：" + ex.Message; } catch { }
                        }
                    }
                    else
                    {
                        try { if (status != null) status.Text = "未找到 Assets/list.xml。已尝试位置: " + string.Join(", ", candidatePaths); } catch { }
                    }

                    // 多线程下载：用 SemaphoreSlim 控制并发
                    var maxThreads = _config.TryGetValue("download_threads", out var dtv) && int.TryParse(dtv, out var dti) ? Math.Max(1, Math.Min(32, dti)) : 4;
                    var semaphore = new System.Threading.SemaphoreSlim(maxThreads);
                    var downloadTasks = new List<Task>();
                    for (int i = 0; i < max; i++)
                    {
                        var dateToCheck = startDate.Date.AddDays(i);
                        var dateStr = dateToCheck.ToString("yyyy-MM-dd");
                        await semaphore.WaitAsync();
                        downloadTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                XElement node = null;
                            if (localDoc != null)
                            {
                                try
                                {
                                    // 首先尝试精确匹配用户选择的分辨率组（resolution name）
                                    var resNodes = localDoc.Descendants("resolution").ToList();
                                    var exactRes = resNodes.FirstOrDefault(r => string.Equals((string)r.Attribute("name"), selRes, StringComparison.OrdinalIgnoreCase));
                                    if (exactRes != null)
                                    {
                                        node = exactRes.Descendants("wallpaper").FirstOrDefault(x => ((string)x.Attribute("date")) == dateStr);
                                    }

                                    // 若未找到，尝试按比例匹配的 resolution 组
                                    if (node == null && !string.IsNullOrEmpty(selAspect))
                                    {
                                        var aspectRes = resNodes.FirstOrDefault(r => string.Equals((string)r.Attribute("aspect_ratio"), selAspect, StringComparison.OrdinalIgnoreCase));
                                        if (aspectRes != null) node = aspectRes.Descendants("wallpaper").FirstOrDefault(x => ((string)x.Attribute("date")) == dateStr);
                                    }

                                    // 若仍未找到，尝试按 URL 中已包含的分辨率后缀匹配（优先使用明确带有 _{WxH}.jpg 的条目）
                                    if (node == null)
                                    {
                                        node = localDoc.Descendants("wallpaper").FirstOrDefault(x => ((string)x.Attribute("date")) == dateStr && ((string)x.Attribute("url"))?.Contains("_" + selRes + ".jpg") == true);
                                    }

                                    // 如果还是没有，不要回退到任意第一个 wallpaper（那通常是 3840x2160），而是直接认为此日期在所选分辨率下不存在
                                }
                                catch { node = null; }
                            }
                            if (node == null) { if (status != null) status.Text = $"未找到 {dateStr} 的 {selRes} 分辨率壁纸，跳过。"; return; }
                            var u = node.Element("url")?.Value;
                            if (string.IsNullOrEmpty(u)) return;
                            if (u.StartsWith("//")) u = "https:" + u; else if (u.StartsWith("/")) u = "https://cn.bing.com" + u;
                            try
                            {
                                // 根据所选分辨率调整 URL 后缀（如果选择的是 3840x2160 则保持不变）
                                u = AdjustUrlToResolution(u, selRes);
                            }
                            catch { }
                            // 对于 cn.bing.com/th?id=... 形式的 URL，尝试直接使用原始 id 加上分辨率后缀生成可下载直链
                            try
                            {
                                if (u.Contains("cn.bing.com/th?id=") || u.Contains("bing.com/th?id="))
                                {
                                    var m = System.Text.RegularExpressions.Regex.Match(u, @"th\?id=([^_]+)_(\d{3,4}x\d{3,4})\.jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                    if (m.Success)
                                    {
                                        var idPart = m.Groups[1].Value;
                                        u = $"https://cn.bing.com/th?id={idPart}_{selRes}.jpg";
                                    }
                                }
                            }
                            catch { }
                            if (!Uri.IsWellFormedUriString(u, UriKind.Absolute)) { Interlocked.Increment(ref errors); return; }
                            var uri = new Uri(u);
                            var name = Path.GetFileName(uri.LocalPath);
                            if (string.IsNullOrEmpty(name)) name = dateStr + "_" + selRes + ".jpg";
                            var dest = Path.Combine(targetFolder, name);
                            if (File.Exists(dest)) { Interlocked.Increment(ref downloaded); return; }
                            try
                            {
                                try { if (status != null) status.Text = $"开始下载: {uri} -> 目标分辨率 {selRes}"; } catch { }

                                bool ok = false;
                                try
                                {
                                    // 使用独立 HttpClient 每次单独请求，避免并行连接复用导致被拒绝
                                    using var singleHttp = new System.Net.Http.HttpClient();
                                    singleHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
                                    singleHttp.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                                    try { singleHttp.DefaultRequestHeaders.Referrer = new Uri("https://www.bing.com/"); } catch { }

                                    using var resp = await singleHttp.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        using var stream = await resp.Content.ReadAsStreamAsync();
                                        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                                        await stream.CopyToAsync(fs);
                                        ok = true;
                                    }
                                    else
                                    {
                                        LogException(new Exception($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {uri}"));
                                    }
                                }
                                catch (Exception ex) { LogException(ex); }

                                if (ok) Interlocked.Increment(ref downloaded); else Interlocked.Increment(ref errors);
                                try { if (status != null) this.DispatcherQueue.TryEnqueue(() => status.Text = $"已下载 {downloaded} 张，失败 {errors} 张。目录: {targetFolder} (当前请求分辨率: {selRes})"); } catch { }
                            }
                            catch (Exception ex) { Interlocked.Increment(ref errors); LogException(ex); }
                        }
                        catch (Exception ex) { Interlocked.Increment(ref errors); LogException(ex); }
                        finally { semaphore.Release(); }
                        }));
                    }
                    await Task.WhenAll(downloadTasks);
                    if (status != null) status.Text = $"已下载 {downloaded} 张，失败 {errors} 张。目录: {targetFolder}";
                }
                else
                {
                    var mainImg = root?.FindName("MainImage") as Microsoft.UI.Xaml.Controls.Image;
                    if (mainImg?.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bi && bi.UriSource != null)
                    {
                        try
                        {
                            var chosenRes = (root?.FindName("ResolutionCombo") as ComboBox)?.SelectedItem is ComboBoxItem rci ? (rci.Content as string) : "3840x2160";

                            string datePart = DateTime.Now.ToString("yyyy-MM-dd");
                            try
                            {
                                var api = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-US";
                                var json = await http.GetStringAsync(api);
                                using var jd = System.Text.Json.JsonDocument.Parse(json);
                                if (jd.RootElement.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
                                {
                                    var first = images[0];
                                    if (first.TryGetProperty("enddate", out var ed))
                                    {
                                        var s = ed.GetString();
                                        if (!string.IsNullOrEmpty(s) && s.Length >= 8 && int.TryParse(s, out _))
                                        {
                                            if (DateTime.TryParseExact(s, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                                            {
                                                datePart = dt.ToString("yyyy-MM-dd");
                                            }
                                        }
                                    }
                                    else if (first.TryGetProperty("startdate", out var sd))
                                    {
                                        var s = sd.GetString();
                                        if (!string.IsNullOrEmpty(s) && s.Length >= 8 && DateTime.TryParseExact(s, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt2))
                                        {
                                            datePart = dt2.ToString("yyyy-MM-dd");
                                        }
                                    }
                                }
                            }
                            catch { }

                            // 尝试从 Assets/list.xml 查找当天的 URL（优先级高于 Image.Source）
                            var localXml = Path.Combine(AppContext.BaseDirectory, "Assets", "list.xml");
                            XDocument localDoc = null;
                            var candidatePaths = new List<string>
                            {
                                Path.Combine(AppContext.BaseDirectory, "Assets", "list.xml"),
                                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "list.xml"),
                                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "list.xml")
                            };
                            try
                            {
                                var pkgPath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
                                candidatePaths.Insert(0, Path.Combine(pkgPath, "Assets", "list.xml"));
                            }
                            catch { }

                            string usedPath = null;
                            foreach (var p in candidatePaths)
                            {
                                try
                                {
                                    if (File.Exists(p))
                                    {
                                        localXml = p;
                                        localDoc = XDocument.Load(localXml);
                                        usedPath = p;
                                        break;
                                    }
                                }
                                catch { }
                            }

                            string urlStr = null;
                            if (localDoc != null)
                            {
                                try
                                {
                                    var node = localDoc.Descendants("wallpaper").FirstOrDefault(x => ((string)x.Attribute("date")) == datePart);
                                    if (node != null)
                                    {
                                        urlStr = node.Element("url")?.Value;
                                    }
                                }
                                catch { }
                            }

                            // 如果从 list.xml 没有找到，则回退到当前显示的 Image Uri
                            if (string.IsNullOrEmpty(urlStr)) urlStr = bi.UriSource.AbsoluteUri;

                            try { urlStr = AdjustUrlToResolution(urlStr, chosenRes); } catch { }

                            if (!Uri.IsWellFormedUriString(urlStr, UriKind.Absolute)) { if (status != null) status.Text = "图片 URL 无效"; }
                            else
                            {
                                var uri = new Uri(urlStr);
                                var ext = Path.GetExtension(uri.AbsolutePath);
                                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                                var safeRes = chosenRes.Replace("/", "_");
                                var fileName = datePart + "_" + safeRes + ext;
                                var dest = Path.Combine(targetFolder, fileName);
                                if (!File.Exists(dest))
                                {
                                    bool ok = await DownloadToFileAsync(uri, dest);
                                    if (ok) downloaded = 1; else errors = 1;
                                }

                                try
                                {
                                    bool setOk = App.SetDesktopWallpaper(dest);
                                    if (status != null) status.Text = $"已下载 {downloaded} 张，失败 {errors} 张。设置壁纸: {(setOk ? "成功" : "失败")}. 文件: {dest}";
                                }
                                catch (Exception ex) { LogException(ex); if (status != null) status.Text = $"已下载 {downloaded} 张，失败 {errors} 张。尝试设置壁纸时出错: " + ex.Message; }
                            }
                        }
                        catch (Exception ex) { LogException(ex); if (status != null) status.Text = "下载失败: " + ex.Message; }
                    }
                    else { if (status != null) status.Text = "没有可下载的图片"; }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                try { var status2 = (this.Content as FrameworkElement)?.FindName("DownloadStatusText") as TextBlock; if (status2 != null) status2.Text = "发生未处理异常: " + ex.Message; } catch { }
            }
            finally { try { if (btn != null) btn.IsEnabled = true; } catch { } }
        }

        private void DatePart_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var status = root?.FindName("DownloadStatusText") as TextBlock;
                var yearCb = root?.FindName("YearCombo") as ComboBox;
                var monthCb = root?.FindName("MonthCombo") as ComboBox;
                var dayCb = root?.FindName("DayCombo") as ComboBox;
                if (yearCb?.SelectedItem is ComboBoxItem yit && monthCb?.SelectedItem is ComboBoxItem mit && dayCb?.SelectedItem is ComboBoxItem dit)
                {
                    if (int.TryParse(yit.Content as string, out var y) && int.TryParse(mit.Content as string, out var m) && int.TryParse(dit.Content as string, out var d))
                    {
                        try
                        {
                            var dateStr = new DateTime(y, m, d).ToString("yyyy-MM-dd");
                            var localXml = Path.Combine(AppContext.BaseDirectory, "Assets", "list.xml");
                            bool exists = false;
                            if (File.Exists(localXml))
                            {
                                try
                                {
                                    var xdoc = XDocument.Load(localXml);
                                    exists = xdoc.Descendants("wallpaper").Any(x => ((string)x.Attribute("date")) == dateStr);
                                }
                                catch { }
                            }

                            if (status != null) status.Text = exists ? $"已找到 {dateStr} 的壁纸，可下载。" : $"未找到 {dateStr} 的壁纸。";
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }


        // 设置开机自启（使用注册表 CurrentUser\Software\Microsoft\Windows\CurrentVersion\Run）
        private void OpenBili_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://space.bilibili.com/619002007/") { UseShellExecute = true }); } catch { }
        }

        private void OpenMail_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mailto:bili-vapegirl233_official@hotmail.com") { UseShellExecute = true }); } catch { }
        }

        private void OpenAuthorGit_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/WorldNo1Steve") { UseShellExecute = true }); } catch { }
        }

        private void OpenAuthorX_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://x.com/WorldNo1Steve") { UseShellExecute = true }); } catch { }
        }

        private void OpenProjectGit_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/PrehistoricStarDreamStudios/BingWPDownloadHelper/") { UseShellExecute = true }); } catch { }
        }

        /// <summary>打开友情链接主页：使用系统默认浏览器打开配置中的 FriendlyLinksUrl。</summary>
        private async void OpenLinksButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = _config.TryGetValue("friendly_links_url", out var u) && !string.IsNullOrWhiteSpace(u)
                    ? u : "https://www.bing.com/?mkt=zh-CN";
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch { }
        }

        /// <summary>点击"必应"按钮：使用系统默认浏览器打开必应首页。</summary>
        private async void OpenBingLink_Click(object sender, RoutedEventArgs e)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.bing.com/?mkt=zh-CN")); } catch { }
        }

        /// <summary>显示今日格言：根据当天日期固定取一句，在 MottoText 中显示，并在右下方以 InfoBar 弹出。</summary>
        private void ShowMottoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 根据日期取一句格言（每天固定）
                int dayOfYear = DateTime.Now.DayOfYear;
                int idx = (dayOfYear % 10) + 1; // 1..10
                var motto = Strings.GetString("Motto" + idx);
                if (string.IsNullOrEmpty(motto) || motto == "Motto" + idx) motto = Strings.GetString("Motto1");

                // 显示在 MottoText 中
                var root = this.Content as FrameworkElement;
                if (root == null) return;
                var tb = root.FindName("MottoText") as TextBlock;
                if (tb != null) tb.Text = motto;

                // 同时在右下方弹出 InfoBar（Fluent Design 通知样式）
                try
                {
                    var containerGrid = root as Grid;
                    if (containerGrid == null)
                    {
                        var nav = root.FindName("NavView") as Microsoft.UI.Xaml.Controls.NavigationView;
                        containerGrid = nav?.Parent as Grid ?? root as Grid;
                    }
                    if (containerGrid != null)
                    {
                        var bar = new InfoBar
                        {
                            Message = motto,
                            IsOpen = true,
                            Severity = InfoBarSeverity.Informational,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Margin = new Thickness(0, 0, 12, 12),
                        };
                        // 5 秒后自动关闭
                        var dq = this.DispatcherQueue;
                        var t = dq.CreateTimer();
                        t.Interval = TimeSpan.FromSeconds(5);
                        t.Tick += (s, a) =>
                        {
                            try { bar.IsOpen = false; t.Stop(); } catch { }
                        };
                        t.Start();

                        containerGrid.Children.Add(bar);
                        try { Canvas.SetZIndex(bar, 9999); } catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>优化内存按钮：调用 EmptyWorkingSet 将本进程工作集压缩至分页文件。</summary>
        private async void OptimizeMemoryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 触发系统空闲任务整理（rundll32 advapi32.dll,ProcessIdleTasks）
                var psi = new System.Diagnostics.ProcessStartInfo("rundll32.exe", "advapi32.dll,ProcessIdleTasks")
                {
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);

                // 同时调用 EmptyWorkingSet（psapi.dll）压缩本进程工作集
                EmptyWorkingSetForCurrent();
                await System.Threading.Tasks.Task.CompletedTask;

                var dlg = new ContentDialog
                {
                    Title = Strings.GetString("ToolOptimizeMemory"),
                    Content = Strings.GetString("ToolOptimizeMemorySuccess"),
                    CloseButtonText = Strings.GetString("MsgOK"),
                    XamlRoot = (this.Content as FrameworkElement)?.XamlRoot
                };
                _ = dlg.ShowAsync();
            }
            catch
            {
                var dlg = new ContentDialog
                {
                    Title = Strings.GetString("ToolOptimizeMemory"),
                    Content = Strings.GetString("ToolOptimizeMemoryFailed"),
                    CloseButtonText = Strings.GetString("MsgOK"),
                    XamlRoot = (this.Content as FrameworkElement)?.XamlRoot
                };
                _ = dlg.ShowAsync();
            }
        }

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        private void EmptyWorkingSetForCurrent()
        {
            try
            {
                var h = System.Diagnostics.Process.GetCurrentProcess().Handle;
                EmptyWorkingSet(h);
            }
            catch { }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (key == null) return;
                var name = "BingPaper";
                if (enable)
                {
                    // 带 --minimized 参数：开机静默启动到托盘
                    try { key.SetValue(name, '"' + exe + '"' + " --minimized"); } catch { }
                }
                else
                {
                    try { key.DeleteValue(name, false); } catch { }
                }
            }
            catch { }
        }

        private void SaveSoftwareSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var auto = root?.FindName("AutoStartCheck") as ToggleSwitch;
                if (auto != null) SetAutoStart(auto.IsOn);

                // 关闭行为
                var closeCombo = root?.FindName("CloseBehaviorCombo") as ComboBox;
                if (closeCombo?.SelectedItem is ComboBoxItem cit)
                    _config["close_behavior"] = cit.Tag as string ?? "Tray";

                // 主题模式 + 颜色主题
                var themeCombo = root?.FindName("ThemeModeCombo") as ComboBox;
                if (themeCombo?.SelectedItem is ComboBoxItem tit)
                {
                    var tm = tit.Tag as string ?? "System";
                    _config["theme_mode"] = tm;
                    ApplyThemeMode(tm);
                }
                var colorCombo = root?.FindName("ColorThemeCombo") as ComboBox;
                if (colorCombo?.SelectedItem is ComboBoxItem cit2)
                {
                    var ct = cit2.Tag as string ?? "System";
                    _config["color_theme"] = ct;
                    ApplyColorTheme(ct);
                }

                // 下载线程数
                var threadsBox = root?.FindName("DownloadThreadsBox") as NumberBox;
                if (threadsBox != null)
                    _config["download_threads"] = Math.Max(1, Math.Min(32, (int)threadsBox.Value)).ToString();

                // 显示语言
                var langCombo = root?.FindName("LanguageCombo") as ComboBox;
                if (langCombo?.SelectedItem is ComboBoxItem lit)
                {
                    var newLang = lit.Tag as string ?? "zh-CN";
                    _config["language"] = newLang;
                    // 立即应用 Culture（托盘菜单等后续操作会用新语言）
                    try
                    {
                        var cult = string.IsNullOrEmpty(newLang) || newLang == "auto"
                            ? AppConfig.DetectSystemLanguage()
                            : newLang;
                        Strings.Culture = new CultureInfo(cult);
                    }
                    catch { }
                    // 刷新当前可见 UI 文字
                    try { ApplyLanguageToUI(); } catch { }
                    // 语言切换需要重启才能完全刷新所有 XAML 静态文字
                    var hint = root?.FindName("LanguageRestartHint") as TextBlock;
                    if (hint != null) { hint.Text = Strings.GetString("MsgNeedRestartForLanguage"); hint.Visibility = Visibility.Visible; }
                }

                // 背景材质 + 透明背景（立即应用，无需重启）
                var backdropCombo = root?.FindName("BackdropTypeCombo") as ComboBox;
                if (backdropCombo?.SelectedItem is ComboBoxItem bci)
                    _config["backdrop_type"] = bci.Tag as string ?? "Mica";
                var transparentSwitch = root?.FindName("TransparentBackgroundSwitch") as ToggleSwitch;
                if (transparentSwitch != null)
                    _config["transparent_background"] = transparentSwitch.IsOn ? "true" : "false";

                SaveConfig();
                var status = root?.FindName("AppSettingsStatusText") as TextBlock;
                if (status != null) status.Text = Strings.GetString("SettingsSavedRestartHint");
            }
            catch { }
        }

        /// <summary>
        /// 将当前 Strings.Culture 对应的本地化文字应用到运行中的 UI 控件。
        /// x:Uid 绑定的 XAML 文字需重启生效；这里重建托盘菜单（代码动态创建）。
        /// </summary>
        private void ApplyLanguageToUI()
        {
            try
            {
                // 托盘菜单（代码动态创建，重建以应用新语言）
                try
                {
                    _trayManager?.Dispose();
                    _trayManager = new TrayManager(this);
                    _trayManager.ShowRequested += (s, e) => { try { _trayManager?.ShowFromTray(); } catch { } };
                    _trayManager.ExitRequested += (s, e) => { try { _isExiting = true; this.Close(); } catch { } };
                }
                catch { }
            }
            catch { }
        }

        #region 壁纸列表加载与标签筛选预览

        // 候选 list.xml 路径（打包目录、当前目录、调试输出目录、用户自动更新目录）
        private List<string> CandidateListXmlPaths()
        {
            var paths = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "list.xml"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "list.xml"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "list.xml")
            };
            try
            {
                var pkgPath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
                paths.Insert(0, Path.Combine(pkgPath, "Assets", "list.xml"));
            }
            catch { }
            // 用户自动更新产生的列表（可写）
            var userList = Path.Combine(AppFolderPath, "list.xml");
            paths.Add(userList);
            return paths;
        }

        // 从 list.xml 加载所有壁纸（url + 标签），合并 Assets 与用户目录，按 url 去重
        private void LoadWallpapersFromXml()
        {
            _allWallpapers.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in CandidateListXmlPaths().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var doc = XDocument.Load(p);
                    foreach (var wp in doc.Descendants("wallpaper"))
                    {
                        var url = wp.Element("url")?.Value?.Trim();
                        if (string.IsNullOrEmpty(url)) continue;
                        if (seen.Contains(url)) continue;
                        seen.Add(url);
                        var tags = new List<string>();
                        var labelEl = wp.Element("label");
                        if (labelEl != null && !string.IsNullOrWhiteSpace(labelEl.Value))
                        {
                            tags = labelEl.Value.Split(',')
                                .Select(t => t.Trim())
                                .Where(t => !string.IsNullOrEmpty(t))
                                .Distinct()
                                .ToList();
                        }
                        _allWallpapers.Add((url, tags));
                    }
                }
                catch { }
            }
        }

        // 填充标签筛选下拉：全部 / 官方十类 / 未分类
        private void PopulateTagFilter()
        {
            var root = this.Content as FrameworkElement;
            var combo = root?.FindName("TagFilterCombo") as ComboBox;
            if (combo == null) return;
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = "全部", Tag = "All" });
            foreach (var t in OfficialTags)
            {
                combo.Items.Add(new ComboBoxItem { Content = t, Tag = t });
            }
            combo.Items.Add(new ComboBoxItem { Content = UnclassifiedTag, Tag = UnclassifiedTag });
            combo.SelectedIndex = 0;
        }

        private void TagFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var combo = root?.FindName("TagFilterCombo") as ComboBox;
                if (combo == null) return;
                // 初始化阶段尚未填充时跳过
                if (combo.Items.Count == 0) return;
                var tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
                _filteredWallpapers = (tag == "All")
                    ? _allWallpapers.ToList()
                    : _allWallpapers.Where(w => w.tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
                FillPreviewGrid();
            }
            catch { }
        }

        // 把 UHD URL 转为 1366x768 预览 URL（必应图片链接 _UHD.jpg → _1366x768.jpg）
        private static string UhdToPreview(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            // 补全协议
            if (url.StartsWith("//")) url = "https:" + url;
            else if (url.StartsWith("/")) url = "https://cn.bing.com" + url;
            // _UHD.jpg → _1366x768.jpg
            return url.Replace("_UHD.jpg", "_1366x768.jpg", StringComparison.OrdinalIgnoreCase);
        }

        // 将过滤后的壁纸列表填充到预览 GridView（多张平铺，每张取中间 768x768 缩小为 240x240）
        private void FillPreviewGrid()
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var grid = root?.FindName("PreviewGridView") as GridView;
                var countText = root?.FindName("PreviewCountText") as TextBlock;
                if (grid == null) return;

                var items = new List<WallpaperPreviewItem>();
                foreach (var wp in _filteredWallpapers)
                {
                    items.Add(new WallpaperPreviewItem
                    {
                        PreviewUrl = UhdToPreview(wp.url),
                        Tags = wp.tags.Count > 0 ? string.Join(",", wp.tags) : UnclassifiedTag
                    });
                }
                grid.ItemsSource = items;
                if (countText != null) countText.Text = $"共 {items.Count} 张";
            }
            catch { }
        }

        /// <summary>
        /// GridView 虚拟化回调：容器 realize 时才下载图片（DecodePixelWidth=256 限内存），
        /// 容器回收时清空 Source 取消下载。彻底避免 1982 张图同时下载。
        /// </summary>
        private void PreviewGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            try
            {
                // 在模板根 Grid 里找 Image
                var img = (args.ItemContainer.ContentTemplateRoot as FrameworkElement)?.FindName("ItemImg") as Microsoft.UI.Xaml.Controls.Image;
                if (img == null) return;

                if (args.InRecycleQueue)
                {
                    // 容器被回收：取消下载、释放内存
                    img.Source = null;
                    return;
                }

                var item = args.Item as WallpaperPreviewItem;
                if (item == null || string.IsNullOrEmpty(item.PreviewUrl)) return;

                // 仅对可见项创建 BitmapImage。
                // 只设 DecodePixelWidth 不设 Height，保持原图宽高比（1366x768→800x449），
                // 再由 Image 的 Stretch=UniformToFill 在 240x240 容器内取中间正方形（对应原图中间 768x768）。
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.DecodePixelWidth = 800;
                try { bmp.UriSource = new Uri(item.PreviewUrl); }
                catch { return; }
                img.Source = bmp;
            }
            catch { }
        }

        /// <summary>预览项悬停：放大到 1.1 倍。</summary>
        private void PreviewItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe)
                {
                    var img = fe.FindName("ItemImg") as Microsoft.UI.Xaml.Controls.Image;
                    if (img?.RenderTransform is ScaleTransform st)
                    {
                        st.ScaleX = 1.1;
                        st.ScaleY = 1.1;
                    }
                }
            }
            catch { }
        }

        /// <summary>预览项离开：恢复原始大小。</summary>
        private void PreviewItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe)
                {
                    var img = fe.FindName("ItemImg") as Microsoft.UI.Xaml.Controls.Image;
                    if (img?.RenderTransform is ScaleTransform st)
                    {
                        st.ScaleX = 1;
                        st.ScaleY = 1;
                    }
                }
            }
            catch { }
        }

        #endregion

        #region 自动更新列表

        // 把 URL 截断到第一个 .jpg（含），删除其后所有参数
        private static string StripUrlToJpg(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var idx = url.IndexOf(".jpg", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return url;
            return url.Substring(0, idx + 4);
        }

        // 自动更新：下载 GitHub zip → 解压到 AutoUpdate → 读取 picture/2026-07+ → 提取 Download4K 链接 → 截断到 .jpg → 以"未分类"追加到用户 list.xml
        private async Task AutoUpdateListAsync()
        {
            try
            {
                var autoUpdateDir = Path.Combine(AppFolderPath, "AutoUpdate");
                var userListPath = Path.Combine(AppFolderPath, "list.xml");
                var zipUrl = "https://github.com/niumoo/bing-wallpaper/archive/refs/heads/main.zip";

                // 下载 zip（404 时等待重试，最多 5 次）
                byte[] zipBytes = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        using var http = new System.Net.Http.HttpClient();
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                        // GitHub 会 302 跳转，HttpClient 默认跟随
                        using var resp = await http.GetAsync(zipUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            // 404：等待后重试
                            await Task.Delay(TimeSpan.FromSeconds(30));
                            continue;
                        }
                        resp.EnsureSuccessStatusCode();
                        zipBytes = await resp.Content.ReadAsByteArrayAsync();
                        break;
                    }
                    catch
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                    }
                }
                if (zipBytes == null || zipBytes.Length == 0) { return; }

                // 解压到 AutoUpdate 目录（先清空旧内容）
                try { if (Directory.Exists(autoUpdateDir)) Directory.Delete(autoUpdateDir, true); } catch { }
                Directory.CreateDirectory(autoUpdateDir);
                using (var ms = new MemoryStream(zipBytes))
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    // 只解压 picture 目录，跳过 .github 等无关文件夹（避免只读文件导致 UnauthorizedAccessException）
                    foreach (var entry in archive.Entries)
                    {
                        var name = entry.FullName;
                        if (!name.Contains("picture/", StringComparison.OrdinalIgnoreCase)) continue;
                        var rel = name.Substring(name.IndexOf("picture/", StringComparison.OrdinalIgnoreCase));
                        var dest = Path.Combine(autoUpdateDir, rel.Replace('/', '\\'));
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(dest);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? autoUpdateDir);
                        try
                        {
                            entry.ExtractToFile(dest, true);
                            // 清除只读属性，避免后续 Delete 失败
                            File.SetAttributes(dest, FileAttributes.Normal);
                        }
                        catch { }
                    }
                }

                // 定位 bing-wallpaper-main/picture 目录
                var bingRoot = Directory.GetDirectories(autoUpdateDir, "bing-wallpaper-main", SearchOption.AllDirectories).FirstOrDefault();
                if (bingRoot == null) bingRoot = autoUpdateDir;
                var pictureDir = Path.Combine(bingRoot, "picture");
                if (!Directory.Exists(pictureDir)) { return; }

                // 读取 2026-07 及以后的月份目录，提取从 2026-07-06 起的 Download4K 链接
                var cutoff = new DateTime(2026, 7, 6);
                var newEntries = new List<(string date, string url)>();
                var monthDirs = Directory.GetDirectories(pictureDir, "2026-*")
                    .OrderBy(d => Path.GetFileName(d))
                    .ToList();
                foreach (var monthDir in monthDirs)
                {
                    var monthName = Path.GetFileName(monthDir); // 2026-07
                    if (!DateTime.TryParseExact(monthName + "-01", "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var monthDate)) continue;
                    if (monthDate < new DateTime(2026, 7, 1)) continue;

                    var readme = Path.Combine(monthDir, "README.md");
                    if (!File.Exists(readme)) continue;
                    string[] lines;
                    try { lines = File.ReadAllLines(readme, Encoding.UTF8); }
                    catch { continue; }

                    foreach (var line in lines)
                    {
                        // 格式：![](smallUrl)DATE [download 4k](fullUrl)
                        var m = System.Text.RegularExpressions.Regex.Match(line,
                            @"\](\d{4}-\d{2}-\d{2})\s*\[download 4k\]\(([^)]+)\)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!m.Success) continue;
                        var dateStr = m.Groups[1].Value;
                        var fullUrl = m.Groups[2].Value;
                        if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var entryDate)) continue;
                        if (entryDate < cutoff) continue;
                        var cleanUrl = StripUrlToJpg(fullUrl);
                        if (!string.IsNullOrEmpty(cleanUrl))
                        {
                            newEntries.Add((dateStr, cleanUrl));
                        }
                    }
                }

                if (newEntries.Count == 0) { return; }

                // 追加到用户 list.xml（按 url 去重），标签为"未分类"
                var existingUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                XDocument userDoc;
                XElement userRoot;
                if (File.Exists(userListPath))
                {
                    try
                    {
                        userDoc = XDocument.Load(userListPath);
                        userRoot = userDoc.Root ?? new XElement("wallpapers");
                        foreach (var wp in userDoc.Descendants("wallpaper"))
                        {
                            var u = wp.Element("url")?.Value?.Trim();
                            if (!string.IsNullOrEmpty(u)) existingUrls.Add(u);
                        }
                    }
                    catch
                    {
                        userDoc = new XDocument(new XElement("wallpapers"));
                        userRoot = userDoc.Root;
                    }
                }
                else
                {
                    userDoc = new XDocument(new XElement("wallpapers"));
                    userRoot = userDoc.Root;
                }

                int added = 0;
                foreach (var entry in newEntries)
                {
                    if (existingUrls.Contains(entry.url)) continue;
                    existingUrls.Add(entry.url);
                    userRoot.Add(new XElement("wallpaper",
                        new XElement("url", entry.url),
                        new XElement("label", UnclassifiedTag)));
                    added++;
                }

                if (added > 0)
                {
                    userDoc.Save(userListPath);
                }

                // 记录今日已运行，并刷新内存中的壁纸列表
                _config["auto_update_last_run"] = DateTime.Now.ToString("yyyy-MM-dd");
                try { SaveConfig(); } catch { }

                // 重新加载列表以反映新增壁纸
                try
                {
                    LoadWallpapersFromXml();
                    var root = this.Content as FrameworkElement;
                    var combo = root?.FindName("TagFilterCombo") as ComboBox;
                    if (combo != null && combo.SelectedItem is ComboBoxItem item)
                    {
                        var tag = item.Tag as string ?? "All";
                        _filteredWallpapers = (tag == "All")
                            ? _allWallpapers.ToList()
                            : _allWallpapers.Where(w => w.tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
                    }
                }
                catch { }
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>
        /// 「立即检查更新」按钮：手动触发一次自动更新流程，完成后刷新预览到最新一张。
        /// </summary>
        private async void CheckUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            try
            {
                if (btn != null) { btn.IsEnabled = false; btn.Content = "正在更新…"; }
                try { await AutoUpdateListAsync(); }
                catch (Exception exi) { LogException(exi); }

                // 重新加载列表并刷新预览网格
                try
                {
                    LoadWallpapersFromXml();
                    var root = this.Content as FrameworkElement;
                    var combo = root?.FindName("TagFilterCombo") as ComboBox;
                    if (combo != null && combo.SelectedItem is ComboBoxItem item)
                    {
                        var tag = item.Tag as string ?? "All";
                        _filteredWallpapers = (tag == "All")
                            ? _allWallpapers.ToList()
                            : _allWallpapers.Where(w => w.tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
                    }
                    else
                    {
                        _filteredWallpapers = _allWallpapers.ToList();
                    }
                    FillPreviewGrid();
                }
                catch { }
            }
            catch { }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "立即检查更新"; }
            }
        }

        #endregion

    }

    /// <summary>
    /// 壁纸预览网格的数据项（供 GridView 绑定）。
    /// PreviewUrl 为把 _UHD.jpg 替换为 _1366x768.jpg 后的预览图地址。
    /// </summary>
    public sealed class WallpaperPreviewItem
    {
        public string PreviewUrl { get; set; } = string.Empty;
        public string Tags { get; set; } = string.Empty;
    }
}
