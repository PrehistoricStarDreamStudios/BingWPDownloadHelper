using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;

namespace BingPaper
{
    public sealed partial class MainWindow : Window
    {
        public static MainWindow? Instance { get; private set; }

        private string _appFolderPath = null!;
        private string _wallpaperFolderPath = null!;
        private string _configFilePath = null!;
        private const string AppVersion = "b0.1";
        private bool _initialSizeApplied = false;

        private TrayManager? _trayManager;
        private bool _isExiting = false;

        public MainWindow()
        {
            Instance = this;
            this.InitializeComponent();

            // WinUI3 标准标题栏：将内容延伸到标题栏区域
            this.ExtendsContentIntoTitleBar = true;

            // 配置 OverlappedPresenter
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsMinimizable = true;
                    presenter.IsMaximizable = true;
                    presenter.IsResizable = true;
                    presenter.IsAlwaysOnTop = false;
                }
                try
                {
                    var cornerPreference = NativeMethods.DWMWCP_ROUND;
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
                }
                catch { }
            }
            catch { }

            // 设置标题栏拖拽区域
            try
            {
                this.SetTitleBar(this.DragRegion);
                UpdateTitleBarColors();
            }
            catch { }

            // 初始化主题切换按钮
            try { UpdateToggleButtonIcon(); } catch { }

            // 窗口激活事件
            this.Activated += (_, __) =>
            {
                try
                {
                    UpdateTitleBarColors();
                    if (!_initialSizeApplied)
                    {
                        ApplyInitialWindowSize();
                        _initialSizeApplied = true;
                    }
                    SetWindowIcon();
                }
                catch { }
            };

            // 注册异常处理
            Application.Current.UnhandledException += (s, e) => { try { LogException(e.Exception); e.Handled = true; } catch { } };
            TaskScheduler.UnobservedTaskException += (s, e) => { try { LogException(e.Exception); e.SetObserved(); } catch { } };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { LogException(e.ExceptionObject as Exception); } catch { } };

            // NavigationView 初始化
            try
            {
                var root = this.Content as FrameworkElement;
                var nav = root?.FindName("NavView") as NavigationView;
                if (nav != null && root != null)
                {
                    root.Loaded += (_, __) =>
                    {
                        try { nav.IsPaneOpen = true; UpdateNavLayout(nav); } catch { }
                    };
                    nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (dep, args) =>
                    {
                        this.DispatcherQueue.TryEnqueue(() => { try { UpdateNavLayout(nav); } catch { } });
                    });
                    this.SizeChanged += (_, __) => { try { UpdateNavLayout(nav); } catch { } };
                }
            }
            catch { }

            // 初始化配置和目录
            InitializeConfig();

            // 初始化托盘管理器
            InitializeTrayManager();

            // 注册 Loaded 事件
            try { var rootEl = this.Content as FrameworkElement; if (rootEl != null) rootEl.Loaded += MainWindow_Loaded; } catch { }
        }

        private void InitializeConfig()
        {
            try
            {
                // 使用 AppConfig 路径
                AppConfig.EnsureDirectories();
                _appFolderPath = AppConfig.AppFolder;
                _wallpaperFolderPath = AppConfig.WallpaperFolder;
                _configFilePath = AppConfig.ConfigFilePath;

                // 加载配置文件
                var cfg = AppConfig.Load();
                _wallpaperFolderPath = string.IsNullOrEmpty(cfg.WallpaperPath) ? _wallpaperFolderPath : cfg.WallpaperPath;

                // 同步到 AppData（供各页面使用）
                AppData.AppFolderPath = _appFolderPath;
                AppData.WallpaperFolderPath = _wallpaperFolderPath;
                AppData.ConfigFilePath = _configFilePath;

                // 加载配置到 AppData.Config
                if (File.Exists(_configFilePath))
                {
                    var lines = File.ReadAllLines(_configFilePath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#") || trimmed.StartsWith("["))
                            continue;
                        var idx = trimmed.IndexOf('=');
                        if (idx > 0)
                        {
                            var k = trimmed.Substring(0, idx).Trim();
                            var v = trimmed.Substring(idx + 1).Trim();
                            AppData.Config[k] = v;
                        }
                    }
                }

                // 动画设置
                if (AppData.Config.TryGetValue("animation_ms", out var ams) && int.TryParse(ams, out var ms))
                    AppData.AnimationMs = ms;
                if (AppData.Config.TryGetValue("animation_enabled", out var aen))
                    AppData.AnimationEnabled = aen.Equals("1", StringComparison.OrdinalIgnoreCase) || aen.Equals("true", StringComparison.OrdinalIgnoreCase);

                // 应用语言
                try
                {
                    var savedLang = AppData.Config.TryGetValue("language", out var sl) ? sl : "zh-CN";
                    if (string.IsNullOrEmpty(savedLang) || savedLang == "auto")
                        savedLang = AppConfig.DetectSystemLanguage();
                    try { Strings.Culture = new CultureInfo(savedLang); } catch { }
                }
                catch { }

                // 应用主题
                try
                {
                    var tm = AppData.Config.TryGetValue("theme_mode", out var tmv) ? tmv : "System";
                    ApplyThemeMode(tm);
                }
                catch { }

                // 应用背景材质
                try
                {
                    var backdropType = AppData.Config.TryGetValue("backdrop_type", out var btv) ? btv : "Mica";
                    var transparent = AppData.Config.TryGetValue("transparent_background", out var tgv)
                        ? (tgv == "1" || tgv.Equals("true", StringComparison.OrdinalIgnoreCase)) : true;
                    ApplyBackdrop(backdropType, transparent);
                }
                catch { }
            }
            catch { }
        }

        private void InitializeTrayManager()
        {
            try
            {
                _trayManager = new TrayManager(this);
                _trayManager.ShowRequested += (s, e) =>
                {
                    try { _trayManager?.ShowFromTray(); } catch { }
                };
                _trayManager.ExitRequested += (s, e) =>
                {
                    try
                    {
                        _isExiting = true;
                        _trayManager?.Dispose();
                        this.Close();
                        Application.Current.Exit();
                    }
                    catch { }
                };

                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(windowId);
                    appWindow.Closing += (s, e) =>
                    {
                        if (!_isExiting)
                        {
                            e.Cancel = true;
                            try { _trayManager?.HideToTray(); } catch { }
                        }
                        else
                        {
                            try { SaveConfig(); } catch { }
                        }
                    };
                    this.Closed += (s, e) =>
                    {
                        try { _trayManager?.Dispose(); } catch { }
                    };
                }
                catch { }
            }
            catch { }
        }

        private void ApplyInitialWindowSize()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                int wx = 0, wy = 0, ww = 0, wh = 0;
                bool hasSavedRect = false;
                if (AppData.Config.TryGetValue("win_x", out var sx) && AppData.Config.TryGetValue("win_y", out var sy)
                    && AppData.Config.TryGetValue("win_w", out var sw) && AppData.Config.TryGetValue("win_h", out var sh))
                {
                    hasSavedRect = int.TryParse(sx, out wx) && int.TryParse(sy, out wy)
                        && int.TryParse(sw, out ww) && int.TryParse(sh, out wh);
                }
                if (hasSavedRect)
                {
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, wx, wy, ww, wh, NativeMethods.SWP_NOZORDER);
                }
                else
                {
                    int screenWidth = NativeMethods.GetSystemMetrics(0);
                    int screenHeight = NativeMethods.GetSystemMetrics(1);
                    int longSide = Math.Max(screenWidth, screenHeight);
                    int shortSide = Math.Min(screenWidth, screenHeight);
                    int targetWidth = (int)Math.Round(longSide * 0.5);
                    int targetHeight = (int)Math.Round(shortSide * 0.6);
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, targetWidth, targetHeight,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOMOVE);
                }
            }
            catch { }
        }

        private void SetWindowIcon()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var exeDir = AppContext.BaseDirectory;
                string[] icoPaths = [
                    Path.Combine(exeDir, "Assets", "appicon.ico"),
                    Path.Combine(exeDir, "appicon.ico"),
                    Path.Combine(Environment.CurrentDirectory, "Assets", "appicon.ico"),
                    Path.Combine(Environment.CurrentDirectory, "appicon.ico"),
                ];

                string? foundIco = null;
                foreach (var p in icoPaths) { if (File.Exists(p)) { foundIco = p; break; } }

                if (foundIco != null)
                {
                    var hIcon = NativeMethods.LoadImage(IntPtr.Zero, foundIco, NativeMethods.IMAGE_ICON, 0, 0,
                        NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
                    if (hIcon != IntPtr.Zero)
                    {
                        NativeMethods.SendMessage(hwnd, 0x0080, (IntPtr)1, hIcon); // ICON_BIG
                        NativeMethods.SendMessage(hwnd, 0x0080, (IntPtr)0, hIcon); // ICON_SMALL
                    }
                }
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 确保托盘图标已初始化（窗口句柄就绪后重试）
                try { _trayManager?.EnsureInitialized(); } catch { }

                // 导航到首页（今日壁纸）
                NavView.SelectedItem = NavView.MenuItems[0] as NavigationViewItem;
                // 自动更新列表
                TryAutoUpdateList();
            }
            catch { }
        }

        #region Navigation

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            try
            {
                int idx = 0;
                try { if (args?.SelectedItem is NavigationViewItem it && it.Tag != null) idx = int.Parse(it.Tag.ToString() ?? "0"); }
                catch { idx = 0; }

                Type? pageType = idx switch
                {
                    0 => typeof(Pages.TodayPage),
                    1 => typeof(Pages.DownloadPage),
                    2 => typeof(Pages.SetWallpaperPage),
                    3 => typeof(Pages.AppSettingsPage),
                    4 => typeof(Pages.AboutPage),
                    5 => typeof(Pages.PreviewPage),
                    _ => typeof(Pages.TodayPage),
                };

                contentFrame.Navigate(pageType);
            }
            catch { }
        }

        public void ShowOrActivate()
        {
            try { this.Activate(); } catch { }
        }

        public void ShowSettings()
        {
            try
            {
                foreach (NavigationViewItem it in NavView.FooterMenuItems)
                {
                    if (it.Tag != null && it.Tag.ToString() == "3") { NavView.SelectedItem = it; break; }
                }
            }
            catch { }
        }

        public void TriggerCheckDownloads()
        {
            try
            {
                foreach (var item in NavView.MenuItems)
                {
                    if (item is NavigationViewItem nvi && nvi.Tag != null && nvi.Tag.ToString() == "1")
                    { NavView.SelectedItem = nvi; break; }
                }
            }
            catch { }
        }

        private void CollapseNavButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var nav = root?.FindName("NavView") as NavigationView;
                if (nav != null) nav.IsPaneOpen = !nav.IsPaneOpen;
            }
            catch { }
        }

        private void UpdateNavLayout(NavigationView nav)
        {
            try
            {
                if (nav == null) return;
                double openW = 240;
                try { double winW = this.Bounds.Width; if (winW > 0) openW = Math.Max(200, Math.Min(400, winW * 0.4)); } catch { }
                try
                {
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
                    if (maxText > 0) openW = Math.Max(180, Math.Min(600, Math.Ceiling(24 + maxText + 32)));
                    nav.OpenPaneLength = openW;
                }
                catch { }
            }
            catch { }
        }

        #endregion

        #region Theme & Backdrop

        private void ToggleThemeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root == null) return;
                var effectiveIsDark = root.ActualTheme == ElementTheme.Dark;
                root.RequestedTheme = effectiveIsDark ? ElementTheme.Light : ElementTheme.Dark;
                UpdateToggleButtonIcon();
                UpdateTitleBarColors();
                ApplyBackdropFromConfig();
            }
            catch { }
        }

        private void UpdateToggleButtonIcon()
        {
            try
            {
                if (ToggleIcon != null)
                {
                    var isDark = this.AppTitleBar?.ActualTheme == ElementTheme.Dark;
                    // 深色模式显示太阳（点击切到浅色），浅色模式显示月亮（点击切到深色）
                    ToggleIcon.Glyph = isDark ? "\uE793" : "\uE706";
                }
            }
            catch { }
        }

        public void ApplyBackdropFromConfig()
        {
            var backdropType = AppData.Config.TryGetValue("backdrop_type", out var btv) ? btv : "Mica";
            var transparent = AppData.Config.TryGetValue("transparent_background", out var tgv)
                ? (tgv == "1" || tgv.Equals("true", StringComparison.OrdinalIgnoreCase)) : true;
            ApplyBackdrop(backdropType, transparent);
        }

        private void ApplyBackdrop(string backdropType, bool transparentBackground)
        {
            try
            {
                var rootGrid = this.Content as Grid;
                if (rootGrid == null) return;

                // 1. 设置 SystemBackdrop
                if (string.Equals(backdropType, "Mica", StringComparison.OrdinalIgnoreCase))
                {
                    this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                }
                else if (string.Equals(backdropType, "Acrylic", StringComparison.OrdinalIgnoreCase))
                {
                    this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                }
                else
                {
                    this.SystemBackdrop = null;
                }

                // 2. Aero / LiquidGlass 自定义效果
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (string.Equals(backdropType, "Aero", StringComparison.OrdinalIgnoreCase))
                {
                    var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                    NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

                    var accent = new NativeMethods.AccentPolicy
                    {
                        AccentState = NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND,
                        AccentFlags = 0,
                        GradientColor = 0,
                        AnimationId = 0
                    };
                    var accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
                    try
                    {
                        Marshal.StructureToPtr(accent, accentPtr, false);
                        var data = new NativeMethods.WindowCompositionAttributeData
                        {
                            Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                            Data = accentPtr,
                            SizeOfData = Marshal.SizeOf(accent)
                        };
                        NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
                    }
                    finally { Marshal.FreeHGlobal(accentPtr); }
                }
                else if (string.Equals(backdropType, "LiquidGlass", StringComparison.OrdinalIgnoreCase))
                {
                    var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                    NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

                    var accent = new NativeMethods.AccentPolicy
                    {
                        AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                        AccentFlags = 0x2,
                        GradientColor = 0x80FFFFFF,
                        AnimationId = 0
                    };
                    var accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
                    try
                    {
                        Marshal.StructureToPtr(accent, accentPtr, false);
                        var data = new NativeMethods.WindowCompositionAttributeData
                        {
                            Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                            Data = accentPtr,
                            SizeOfData = Marshal.SizeOf(accent)
                        };
                        NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
                    }
                    finally { Marshal.FreeHGlobal(accentPtr); }
                }

                // 3. 背景透明
                bool isAeroOrLiquid = string.Equals(backdropType, "Aero", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(backdropType, "LiquidGlass", StringComparison.OrdinalIgnoreCase);
                if (isAeroOrLiquid && transparentBackground)
                    rootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                else if (transparentBackground && this.SystemBackdrop != null)
                    rootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                else
                    rootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
            }
            catch { }
        }

        private void ApplyThemeMode(string mode)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root == null) return;
                switch (mode)
                {
                    case "Light": root.RequestedTheme = ElementTheme.Light; break;
                    case "Dark": root.RequestedTheme = ElementTheme.Dark; break;
                    default: root.RequestedTheme = ElementTheme.Default; break;
                }
            }
            catch { }
        }

        private void UpdateTitleBarColors()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow != null)
                {
                    var titleBar = appWindow.TitleBar;
                    // 设置 caption 按钮背景为透明，与 Mica 融合
                    try { titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0); } catch { }
                    try { titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 128, 128, 128); } catch { }
                    try { titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 128, 128, 128); } catch { }
                    try { titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0); } catch { }
                    // caption 按钮前景色跟随主题
                    var isDark = this.AppTitleBar?.ActualTheme == ElementTheme.Dark;
                    var fgColor = isDark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 0, 0, 0);
                    try { titleBar.ButtonForegroundColor = fgColor; } catch { }
                    try { titleBar.ButtonHoverForegroundColor = fgColor; } catch { }
                    try { titleBar.ButtonPressedForegroundColor = fgColor; } catch { }
                    try { titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(120, 128, 128, 128); } catch { }
                    try { titleBar.ForegroundColor = fgColor; } catch { }
                    try { titleBar.BackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0); } catch { }
                    try { titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(120, 128, 128, 128); } catch { }
                    try { titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0); } catch { }
                }
            }
            catch { }
        }

        #endregion

        #region Config & AutoUpdate

        public async Task AutoUpdateListAsync()
        {
            try
            {
                var http = new System.Net.Http.HttpClient();
                var url = "https://raw.githubusercontent.com/SDNet123456/BingPaper/main/Assets/list.xml";
                var xml = await http.GetStringAsync(url);
                var outPath = Path.Combine(AppContext.BaseDirectory, "Assets", "list.xml");
                var dir = Path.GetDirectoryName(outPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, xml);
                AppData.Config["auto_update_last_run"] = DateTime.Now.ToString("yyyy-MM-dd");
                SaveConfig();
            }
            catch { }
        }

        private async void TryAutoUpdateList()
        {
            try
            {
                var autoUpdateOn = AppData.Config.TryGetValue("auto_update_list", out var auv)
                    ? auv.Equals("true", StringComparison.OrdinalIgnoreCase) || auv == "1"
                    : true;
                var lastRun = AppData.Config.TryGetValue("auto_update_last_run", out var lr) ? lr : "";
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                if (autoUpdateOn && lastRun != today)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await AutoUpdateListAsync(); }
                        catch { }
                    });
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var lines = new List<string> { "version=" + AppVersion };
                foreach (var kv in AppData.Config)
                {
                    if (kv.Key == "animation_ms" || kv.Key == "animation_enabled")
                        continue;
                    lines.Add(kv.Key + "=" + kv.Value);
                }
                lines.Add("animation_enabled=" + (AppData.AnimationEnabled ? "1" : "0"));
                lines.Add("animation_ms=" + AppData.AnimationMs.ToString());

                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    if (NativeMethods.GetWindowRect(hwnd, out var rc))
                    {
                        lines.Add("win_x=" + rc.left);
                        lines.Add("win_y=" + rc.top);
                        lines.Add("win_w=" + (rc.right - rc.left));
                        lines.Add("win_h=" + (rc.bottom - rc.top));
                    }
                }
                catch { }

                File.WriteAllLines(_configFilePath, lines);
            }
            catch { }
        }

        #endregion

        #region Helpers

        private void LogException(Exception? ex)
        {
            try
            {
                if (ex == null) return;
                var text = $"----- EXCEPTION {DateTime.Now:o} -----\nType: {ex.GetType().FullName}\nMessage: {ex.Message}\nStackTrace:\n{ex.StackTrace}\n";
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
                        var tmp = Path.Combine(Path.GetTempPath(), "BingPaper_error.log");
                        File.AppendAllText(tmp, text + Environment.NewLine);
                    }
                    catch { }
                }
            }
            catch { }
        }

        #endregion
    }
}