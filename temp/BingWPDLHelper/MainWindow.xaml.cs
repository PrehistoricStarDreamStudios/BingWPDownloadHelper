using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage.Streams;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Windows.UI;
using Microsoft.Win32;
using System.Xml.Linq;

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
        // 新版配置（BingPaper）
        private AppConfig _appCfg = new AppConfig();
        // 托盘管理器
        private TrayManager? _tray;
        // 退出标志：避免 Close() 再次触发 Closing 事件造成循环
        private bool _isExiting = false;

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
                        var tmp = Path.Combine(Path.GetTempPath(), "BingPaper_error.log");
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

            // Title bar drag region
            try
            {
                var rootInit = this.Content as FrameworkElement;
                var dragRegionInit = rootInit?.FindName("DragRegion") as UIElement;
                // 延迟调用 SetTitleBar 到 Activated/Loaded，避免在窗口未就绪时触发 WinRT/WinUI 内部错误

                if (rootInit != null)
                {
                    bool inited = false;
                    rootInit.LayoutUpdated += (_, __) =>
                    {
                        if (inited) return;
                        inited = true;
                        try
                        {
                            UpdateTitleBarColors();
                        }
                        catch { }
                    };
                }
            }
            catch { }

            // Defer setting window size until Activated to avoid calling window APIs too early
            this.ExtendsContentIntoTitleBar = true;
            this.Activated += (_, __) =>
            {
                try
                {
                    var root = this.Content as FrameworkElement;
                    var dragRegion = root?.FindName("DragRegion") as UIElement;
                    if (dragRegion != null) this.SetTitleBar(dragRegion);
                    try { UpdateTitleBarColors(); } catch { }

                    if (!_initialSizeApplied)
                    {
                        try
                        {
                            int screenWidth = GetSystemMetrics(0);
                            int screenHeight = GetSystemMetrics(1);
                            int longSide = Math.Max(screenWidth, screenHeight);
                            int shortSide = Math.Min(screenWidth, screenHeight);
                            int targetWidth = (int)Math.Round(longSide * 0.5);
                            int targetHeight = (int)Math.Round(shortSide * 0.6);
                            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                            const uint SWP_NOZORDER = 0x0004;
                            const uint SWP_NOMOVE = 0x0002;
                            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, targetWidth, targetHeight, SWP_NOZORDER | SWP_NOMOVE);
                        }
                        catch { }
                        _initialSizeApplied = true;
                    }
                }
                catch { }
            };

            // 挂接 AppWindow.Closing 事件：根据 CloseBehavior 决定是否隐藏到托盘
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow != null)
                {
                    appWindow.Closing += async (s, e) =>
                    {
                        try
                        {
                            // 如果是程序主动退出（RealExitApp），直接放行
                            if (_isExiting)
                            {
                                e.Cancel = false;
                                return;
                            }

                            if (_appCfg?.CloseBehavior == "Tray")
                            {
                                // 阻止关闭，隐藏到托盘
                                e.Cancel = true;
                                _tray?.HideToTray();
                            }
                            else if (_appCfg?.AskExitConfirm == true)
                            {
                                // 必须先取消关闭，否则窗口在 await 期间被销毁，对话框无法显示
                                e.Cancel = true;

                                // 异步显示确认对话框
                                var dlg = new ContentDialog
                                {
                                    Title = Strings.GetString("MsgConfirmTitle"),
                                    Content = Strings.GetString("MsgConfirmExit"),
                                    PrimaryButtonText = Strings.GetString("MsgYes"),
                                    CloseButtonText = Strings.GetString("MsgNo"),
                                    XamlRoot = (this.Content as FrameworkElement)?.XamlRoot
                                };
                                var result = await dlg.ShowAsync();
                                if (result == ContentDialogResult.Primary)
                                {
                                    // 用户确认退出：设置退出标志后真正退出，避免再次触发 Closing 的 Tray 分支
                                    _isExiting = true;
                                    try { RealExitApp(); } catch { }
                                }
                                // 否则保持窗口打开，用户可继续操作
                            }
                            else
                            {
                                // 直接退出：保存配置，托盘释放，不 Cancel（让窗口自然关闭）
                                _isExiting = true;
                                try { _appCfg?.Save(); _tray?.Dispose(); } catch { }
                            }
                        }
                        catch
                        {
                            // 出错时不 Cancel，避免无法退出
                            e.Cancel = false;
                        }
                    };
                }
            }
            catch { }

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

            InitializeTodayUIAsync().ContinueWith(t =>
            {
                if (t.Exception != null) { LogException(t.Exception.Flatten()); }
            }, System.Threading.Tasks.TaskScheduler.Default);

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

            // Ensure AppData folders and config - 使用新的 AppConfig（%USERPROFILE%\BingPaper\）

            try
            {
                AppConfig.EnsureDirectories();
                AppFolderPath = AppConfig.AppFolder;
                WallpaperFolderPath = AppConfig.WallpaperFolder;
                ConfigFilePath = AppConfig.ConfigFilePath;

                // 加载新配置
                var appCfg = AppConfig.Load();
                _appCfg = appCfg;

                // 首次启动：若语言为 auto，检测系统语言
                if (string.IsNullOrEmpty(appCfg.Language) || appCfg.Language == "auto")
                {
                    var det = AppConfig.DetectSystemLanguage();
                    _appCfg.Language = det;
                    _appCfg.Save();
                }
                ApplyLanguageCulture(_appCfg.Language);

                // 旧版本 BingWPDLHelper 壁纸迁移（一次性）
                try
                {
                    var oldFolder = AppConfig.DetectOldWallpaperFolder();
                    if (oldFolder != null && !File.Exists(Path.Combine(AppFolderPath, ".migrated")))
                    {
                        AppConfig.MigrateOldWallpaper(oldFolder);
                        File.WriteAllText(Path.Combine(AppFolderPath, ".migrated"), DateTime.Now.ToString("o"));
                    }
                }
                catch { }

                // 兼容旧 _config 字典（从 AppConfig 派生）
                _config["version"] = AppVersion;
                _config["animation_enabled"] = AnimationEnabled ? "true" : "false";
                _config["animation_ms"] = AnimationMs.ToString();
                _config["default_resolution"] = appCfg.LastSelectedResolution;
                _config["download_folder"] = appCfg.WallpaperPath;
                if (string.IsNullOrEmpty(appCfg.WallpaperPath) || !Directory.Exists(appCfg.WallpaperPath))
                {
                    WallpaperFolderPath = AppConfig.WallpaperFolder;
                }
                else
                {
                    WallpaperFolderPath = appCfg.WallpaperPath;
                }
                if (!Directory.Exists(WallpaperFolderPath)) Directory.CreateDirectory(WallpaperFolderPath);

                // initialize AppSettings UI (autostart/default list/api/新增的设置项)
                try
                {
                    var root = this.Content as FrameworkElement;
                    var autoChk = root?.FindName("AutoStartCheck") as CheckBox;
                    if (autoChk != null)
                    {
                        autoChk.IsChecked = appCfg.AutoStart;
                        try { SetAutoStart(appCfg.AutoStart); } catch { }
                    }

                    // 自启隐藏
                    var autoStartHideChk = root?.FindName("AutoStartHideCheck") as CheckBox;
                    if (autoStartHideChk != null) autoStartHideChk.IsChecked = appCfg.AutoStartHide;

                    // 关闭行为
                    var closeBehaviorCombo = root?.FindName("CloseBehaviorCombo") as ComboBox;
                    if (closeBehaviorCombo != null)
                    {
                        closeBehaviorCombo.SelectedIndex = appCfg.CloseBehavior == "Exit" ? 1 : 0;
                    }

                    // 退出前询问
                    var askExitChk = root?.FindName("AskExitConfirmCheck") as CheckBox;
                    if (askExitChk != null) askExitChk.IsChecked = appCfg.AskExitConfirm;

                    // 下载线程数（下载页与设置页同步）
                    var threadsCombo = root?.FindName("DownloadThreadsCombo") as ComboBox;
                    var settingsThreadsCombo = root?.FindName("SettingsDownloadThreadsCombo") as ComboBox;
                    var threadsValue = Math.Max(1, Math.Min(32, appCfg.DownloadThreads));
                    foreach (var combo in new[] { threadsCombo, settingsThreadsCombo })
                    {
                        if (combo == null) continue;
                        for (int i = 0; i < combo.Items.Count; i++)
                        {
                            if (combo.Items[i] is ComboBoxItem ci && ci.Content is string s && int.TryParse(s, out var v) && v == threadsValue)
                            { combo.SelectedIndex = i; break; }
                        }
                    }

                    // 语言下拉
                    var langCombo = root?.FindName("LanguageCombo") as ComboBox;
                    if (langCombo != null)
                    {
                        langCombo.Items.Clear();
                        foreach (var lang in AppConfig.SupportedLanguages)
                        {
                            langCombo.Items.Add(new ComboBoxItem { Content = AppConfig.GetLanguageDisplayName(lang), Tag = lang });
                        }
                        for (int i = 0; i < langCombo.Items.Count; i++)
                        {
                            if ((langCombo.Items[i] as ComboBoxItem)?.Tag as string == appCfg.Language)
                            { langCombo.SelectedIndex = i; break; }
                        }
                    }

                    // 颜色主题下拉
                    var colorCombo = root?.FindName("ColorThemeCombo") as ComboBox;
                    if (colorCombo != null)
                    {
                        for (int i = 0; i < colorCombo.Items.Count; i++)
                        {
                            if (colorCombo.Items[i] is ComboBoxItem ci && ci.Tag as string == appCfg.ColorTheme)
                            { colorCombo.SelectedIndex = i; break; }
                        }
                    }

                    // 显示模式下拉
                    var modeCombo = root?.FindName("ThemeModeCombo") as ComboBox;
                    if (modeCombo != null)
                    {
                        for (int i = 0; i < modeCombo.Items.Count; i++)
                        {
                            if (modeCombo.Items[i] is ComboBoxItem ci && ci.Tag as string == appCfg.ThemeMode)
                            { modeCombo.SelectedIndex = i; break; }
                        }
                    }

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

                    // 壁纸目录显示
                    var slideshowFolder = root?.FindName("SlideshowFolderText") as TextBox;
                    if (slideshowFolder != null && string.IsNullOrEmpty(slideshowFolder.Text))
                        slideshowFolder.Text = WallpaperFolderPath;
                    var downloadFolder = root?.FindName("DownloadFolderText") as TextBox;
                    if (downloadFolder != null && string.IsNullOrEmpty(downloadFolder.Text))
                        downloadFolder.Text = WallpaperFolderPath;
                }
                catch { }

                // 应用初始主题
                try { ApplyThemeFromConfig(); } catch { }
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
                var wallpaperPreviewHost = root?.FindName("WallpaperPreviewHost") as UIElement;
                todayHost.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
                downloadHost.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
                if (settingsHost != null) settingsHost.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
                if (appSettings != null) appSettings.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
                if (aboutHost != null) aboutHost.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
                if (wallpaperPreviewHost != null) wallpaperPreviewHost.Visibility = idx == 5 ? Visibility.Visible : Visibility.Collapsed;

                // 切换到壁纸预览页时自动加载预览
                if (idx == 5)
                {
                    try { LoadWallpaperPreviews(); } catch { }
                }

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
                else if (idx == 2)
                {
                    try
                    {
                        // 切换到设置壁纸页时填充默认值（使用当前下载目录）
                        var sText = root.FindName("SlideshowFolderText") as TextBox;
                        if (sText != null) sText.Text = WallpaperFolderPath;
                        var interval = root.FindName("SlideshowInterval") as Microsoft.UI.Xaml.Controls.NumberBox;
                        if (interval != null && _config.TryGetValue("slideshow_interval", out var iv) && int.TryParse(iv, out var ivv)) interval.Value = ivv;
                        var shuffle = root.FindName("ShuffleCheck") as CheckBox; if (shuffle != null && _config.TryGetValue("slideshow_shuffle", out var sh)) shuffle.IsChecked = sh.Equals("true", StringComparison.OrdinalIgnoreCase);
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
                                            var wpUrl = (string)firstWp.Attribute("url") ?? "";
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
                        }

                        // 如果扫描 XML 后比例下拉仍为空（无网络或无 list.xml），使用本地静态默认列表
                        if (aspect.Items.Count == 0)
                        {
                            var staticResMap = new Dictionary<string, List<string>>
                            {
                                ["16:9"] = new List<string> { "1920x1080", "2560x1440", "3840x2160" },
                                ["16:10"] = new List<string> { "1920x1200", "2560x1600" },
                                ["4:3"] = new List<string> { "1024x768", "1600x1200" },
                                ["21:9"] = new List<string> { "2560x1080", "3440x1440", "5120x2160" },
                                ["9:16"] = new List<string> { "1080x1920", "1440x2560" },
                            };
                            foreach (var a in staticResMap.Keys.OrderBy(x => x))
                            {
                                aspect.Items.Add(new ComboBoxItem { Content = a });
                            }
                            aspect.Tag = staticResMap;

                            // 同时填充 _assetFileMap（使用标准 Bing 文件后缀）
                            foreach (var kv in staticResMap)
                            {
                                if (!_assetFileMap.ContainsKey(kv.Key))
                                    _assetFileMap[kv.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var r in kv.Value)
                                {
                                    if (!_assetFileMap[kv.Key].ContainsKey(r))
                                        _assetFileMap[kv.Key][r] = "_" + r + ".jpg";
                                }
                            }
                        }

                        // 默认选中 16:9，触发分辨率填充
                        if (aspect.SelectedIndex < 0)
                        {
                            for (int i = 0; i < aspect.Items.Count; i++)
                            {
                                if ((aspect.Items[i] as ComboBoxItem)?.Content as string == "16:9") { aspect.SelectedIndex = i; break; }
                            }
                            if (aspect.SelectedIndex < 0 && aspect.Items.Count > 0) aspect.SelectedIndex = 0;
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
                var intervalBox = root?.FindName("SlideshowInterval") as NumberBox;
                uint intervalMs = 1800 * 1000; // 默认 30 分钟
                if (intervalBox != null && intervalBox.Value >= 1) intervalMs = (uint)(intervalBox.Value * 1000);

                var shuffleCheck = root?.FindName("ShuffleCheck") as CheckBox;
                bool shuffle = shuffleCheck?.IsChecked == true;

                var fillCombo = root?.FindName("FillModeCombo") as ComboBox;
                string fillMode = "Fill";
                if (fillCombo?.SelectedItem is ComboBoxItem fci) fillMode = fci.Tag?.ToString() ?? "Fill";

                // 调用 App.SetDesktopSlideshow（使用正确的 COM 接口 IShellItemArray）
                bool ok = App.SetDesktopSlideshow(folder, intervalMs, shuffle, fillMode);
                if (status != null) status.Text = ok
                    ? "幻灯片已设置成功。"
                    : "设置幻灯片失败，请查看日志（%TEMP%\\BingPaper_slideshow.log）。";

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
                var autoChk = root?.FindName("AutoStartCheck") as CheckBox;
                var autoVal = (autoChk != null && autoChk.IsChecked == true) ? "true" : "false";
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

        // 更新标题栏颜色以匹配主题（WinUI3 样式：保持原生标题按钮可见，颜色随主题切换）
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
                    bool isDark = root?.ActualTheme == ElementTheme.Dark;
                    var fg = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
                    var hoverBg = isDark ? Windows.UI.Color.FromArgb(30, 255, 255, 255) : Windows.UI.Color.FromArgb(30, 0, 0, 0);
                    var pressedBg = isDark ? Windows.UI.Color.FromArgb(60, 255, 255, 255) : Windows.UI.Color.FromArgb(60, 0, 0, 0);
                    var inactiveFg = isDark ? Windows.UI.Color.FromArgb(100, 255, 255, 255) : Windows.UI.Color.FromArgb(100, 0, 0, 0);

                    var titleBar = appWindow.TitleBar;
                    try { titleBar.ButtonForegroundColor = fg; } catch { }
                    try { titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent; } catch { }
                    try { titleBar.ButtonHoverForegroundColor = fg; } catch { }
                    try { titleBar.ButtonHoverBackgroundColor = hoverBg; } catch { }
                    try { titleBar.ButtonPressedForegroundColor = fg; } catch { }
                    try { titleBar.ButtonPressedBackgroundColor = pressedBg; } catch { }
                    try { titleBar.InactiveForegroundColor = inactiveFg; } catch { }
                    try { titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent; } catch { }
                }
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
            try { this.Close(); } catch { }
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
                UpdateTitleBarColors();
            }
            catch { }
        }

        /// <summary>设置页主题下拉变更时立即应用。</summary>
        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root == null) return;
                var modeCombo = root.FindName("ThemeModeCombo") as ComboBox;
                var colorCombo = root.FindName("ColorThemeCombo") as ComboBox;
                if (modeCombo == null || colorCombo == null) return;
                // 避免初始化期间触发：仅当两者都有 SelectedItem 时才应用
                if (modeCombo.SelectedItem == null || colorCombo.SelectedItem == null) return;

                var modeTag = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
                var colorTag = (colorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Blue";

                _appCfg.ThemeMode = modeTag;
                _appCfg.ColorTheme = colorTag;
                try { _appCfg.Save(); } catch { }

                ApplyThemeFromConfig();
            }
            catch { }
        }

        private async void ChangeDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            // (kept original implementation)
        }

        private void DownloadHistoryCheck_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var yearCb = root?.FindName("YearCombo") as ComboBox;
                var monthCb = root?.FindName("MonthCombo") as ComboBox;
                var dayCb = root?.FindName("DayCombo") as ComboBox;
                if (yearCb != null) yearCb.IsEnabled = true;
                if (monthCb != null) monthCb.IsEnabled = true;
                if (dayCb != null) dayCb.IsEnabled = true;
            }
            catch { }
        }

        private void DownloadHistoryCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var yearCb = root?.FindName("YearCombo") as ComboBox;
                var monthCb = root?.FindName("MonthCombo") as ComboBox;
                var dayCb = root?.FindName("DayCombo") as ComboBox;
                if (yearCb != null) yearCb.IsEnabled = false;
                if (monthCb != null) monthCb.IsEnabled = false;
                if (dayCb != null) dayCb.IsEnabled = false;
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
                var chk = root?.FindName("DownloadHistoryCheck") as CheckBox;
                if (chk != null) downloadHistory = chk.IsChecked == true;

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

                    // 读取多线程下载线程数（默认 4，最高 32）
                    int threadCount = 4;
                    try
                    {
                        var tcCombo = root?.FindName("DownloadThreadsCombo") as ComboBox;
                        if (tcCombo?.SelectedItem is ComboBoxItem tci && int.TryParse(tci.Content as string, out var tv))
                            threadCount = Math.Max(1, Math.Min(32, tv));
                    }
                    catch { }

                    // 第一阶段：按日期生成待下载任务列表（顺序执行，因为涉及 XML 查询与 URL 构建）
                    var pendingTasks = new List<(string dateStr, Uri uri, string dest)>();
                    for (int i = 0; i < max; i++)
                    {
                        var dateToCheck = startDate.Date.AddDays(i);
                        var dateStr = dateToCheck.ToString("yyyy-MM-dd");
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
                            if (node == null) { continue; }
                            var u = (string)node.Attribute("url");
                            if (string.IsNullOrEmpty(u)) continue;
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
                            if (!Uri.IsWellFormedUriString(u, UriKind.Absolute)) { Interlocked.Increment(ref errors); continue; }
                            var uri = new Uri(u);
                            var name = Path.GetFileName(uri.LocalPath);
                            if (string.IsNullOrEmpty(name)) name = dateStr + "_" + selRes + ".jpg";
                            var dest = Path.Combine(targetFolder, name);
                            if (File.Exists(dest)) { Interlocked.Increment(ref downloaded); continue; }
                            pendingTasks.Add((dateStr, uri, dest));
                        }
                        catch (Exception ex) { Interlocked.Increment(ref errors); LogException(ex); }
                    }

                    // 第二阶段：使用 SemaphoreSlim 并发下载
                    if (pendingTasks.Count > 0)
                    {
                        try { if (status != null) status.Text = $"开始多线程下载：{pendingTasks.Count} 张，线程数 {threadCount}。目录: {targetFolder} (分辨率: {selRes})"; } catch { }
                        using var sem = new System.Threading.SemaphoreSlim(threadCount, threadCount);
                        var allTasks = pendingTasks.Select(t => System.Threading.Tasks.Task.Run(async () =>
                        {
                            await sem.WaitAsync();
                            try
                            {
                                bool ok = false;
                                try
                                {
                                    // 使用独立 HttpClient 每次单独请求，避免并行连接复用导致被拒绝
                                    using var singleHttp = new System.Net.Http.HttpClient();
                                    singleHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
                                    singleHttp.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                                    try { singleHttp.DefaultRequestHeaders.Referrer = new Uri("https://www.bing.com/"); } catch { }

                                    using var resp = await singleHttp.GetAsync(t.uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        using var stream = await resp.Content.ReadAsStreamAsync();
                                        using var fs = new FileStream(t.dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                                        await stream.CopyToAsync(fs);
                                        ok = true;
                                    }
                                    else
                                    {
                                        LogException(new Exception($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {t.uri}"));
                                    }
                                }
                                catch (Exception ex) { LogException(ex); }

                                if (ok) Interlocked.Increment(ref downloaded);
                                else Interlocked.Increment(ref errors);

                                // 在 UI 线程更新状态文本
                                var localDl = downloaded;
                                var localErr = errors;
                                try
                                {
                                    this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                                    {
                                        try { if (status != null) status.Text = $"已下载 {localDl} 张，失败 {localErr} 张。目录: {targetFolder} (分辨率: {selRes})"; } catch { }
                                    });
                                }
                                catch { }
                            }
                            finally
                            {
                                sem.Release();
                            }
                        })).ToArray();
                        await System.Threading.Tasks.Task.WhenAll(allTasks);
                    }
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
                                        urlStr = (string)node.Attribute("url");
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

        /// <summary>打开友情链接页面：直接使用系统默认浏览器打开。</summary>
        private async void OpenLinksButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = _appCfg?.FriendlyLinksUrl;
                if (string.IsNullOrWhiteSpace(url)) url = "https://www.bing.com/?mkt=zh-CN";
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch { }
        }

        /// <summary>点击"必应"按钮：使用系统默认浏览器打开必应首页。</summary>
        private async void OpenBingLink_Click(object sender, RoutedEventArgs e)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.bing.com/?mkt=zh-CN")); } catch { }
        }

        /// <summary>显示今日格言：根据当天日期固定取一句，在右下方以 InfoBar 弹出。</summary>
        private void ShowMottoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 根据日期取一句格言（每天固定）
                int dayOfYear = DateTime.Now.DayOfYear;
                int idx = (dayOfYear % 10) + 1; // 1..10
                var motto = Strings.GetString("Motto" + idx);
                if (string.IsNullOrEmpty(motto)) motto = Strings.GetString("Motto1");

                var root = this.Content as FrameworkElement;
                if (root == null) return;
                // 显示在 MottoText 中
                var tb = root.FindName("MottoText") as TextBlock;
                if (tb != null) tb.Text = motto;

                // 同时在右下方弹出 InfoBar
                try
                {
                    var nav = root.FindName("NavView") as Microsoft.UI.Xaml.Controls.NavigationView;
                    var containerGrid = nav?.Parent as Grid;
                    if (containerGrid == null) containerGrid = root as Grid;
                    if (containerGrid != null)
                    {
                        var bar = new Microsoft.UI.Xaml.Controls.InfoBar
                        {
                            Message = motto,
                            IsOpen = true,
                            Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Margin = new Thickness(0, 0, 12, 12),
                        };
                        // 设置 5 秒后自动关闭
                        var dq = this.DispatcherQueue;
                        var t = dq.CreateTimer();
                        t.Interval = TimeSpan.FromSeconds(5);
                        t.Tick += (s, a) =>
                        {
                            try { bar.IsOpen = false; t.Stop(); } catch { }
                        };
                        t.Start();

                        containerGrid.Children.Add(bar);
                        // 让 InfoBar 显示在最顶层（WinUI3 中用 Canvas.SetZIndex）
                        try { Canvas.SetZIndex(bar, 9999); } catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (key == null) return;
                var name = "BingPaper";
                if (enable)
                {
                    // 若开启"启动时最小化到托盘"，则添加 --minimized 参数
                    var hideFlag = _appCfg?.AutoStartHide == true ? " --minimized" : "";
                    try { key.SetValue(name, '"' + exe + '"' + hideFlag); } catch { }
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
                var auto = root?.FindName("AutoStartCheck") as CheckBox;
                var autoHide = root?.FindName("AutoStartHideCheck") as CheckBox;
                var closeBehavior = root?.FindName("CloseBehaviorCombo") as ComboBox;
                var askExit = root?.FindName("AskExitConfirmCheck") as CheckBox;
                var threads = root?.FindName("SettingsDownloadThreadsCombo") as ComboBox;
                var lang = root?.FindName("LanguageCombo") as ComboBox;
                var colorTheme = root?.FindName("ColorThemeCombo") as ComboBox;
                var modeTheme = root?.FindName("ThemeModeCombo") as ComboBox;

                if (auto != null) _appCfg.AutoStart = auto.IsChecked == true;
                if (autoHide != null) _appCfg.AutoStartHide = autoHide.IsChecked == true;
                if (closeBehavior != null)
                {
                    var sel = closeBehavior.SelectedItem as ComboBoxItem;
                    _appCfg.CloseBehavior = (sel?.Tag as string) == "Exit" ? "Exit" : "Tray";
                }
                if (askExit != null) _appCfg.AskExitConfirm = askExit.IsChecked == true;
                if (threads != null && threads.SelectedItem is ComboBoxItem ti && int.TryParse(ti.Content as string, out var tv))
                    _appCfg.DownloadThreads = Math.Max(1, Math.Min(32, tv));
                if (lang != null && lang.SelectedItem is ComboBoxItem li)
                    _appCfg.Language = li.Tag as string ?? "auto";
                if (colorTheme != null && colorTheme.SelectedItem is ComboBoxItem ci)
                    _appCfg.ColorTheme = ci.Tag as string ?? "System";
                if (modeTheme != null && modeTheme.SelectedItem is ComboBoxItem mi)
                    _appCfg.ThemeMode = mi.Tag as string ?? "System";

                _appCfg.Save();
                // 同步注册表自启动（参数会随 AutoStartHide 变化）
                if (auto != null) SetAutoStart(auto.IsChecked == true);

                // 应用主题
                try { ApplyThemeFromConfig(); } catch { }

                // 语言处理：WinUI3 中 XAML 文本在加载时绑定，运行时切换语言需重启
                var status = root?.FindName("AppSettingsStatusText") as TextBlock;
                bool languageChanged = false;
                if (lang != null && lang.SelectedItem is ComboBoxItem langItem)
                {
                    var newLang = langItem.Tag as string ?? "auto";
                    var oldLang = _appCfg.Language ?? "auto";
                    ApplyLanguageCulture(newLang);
                    // 如果语言确实变化，提示重启
                    if (!string.Equals(newLang, oldLang, StringComparison.OrdinalIgnoreCase))
                    {
                        languageChanged = true;
                    }
                }

                if (status != null)
                {
                    if (languageChanged)
                    {
                        status.Text = Strings.GetString("SettingsSavedRestartHint");
                        // 弹出确认重启对话框
                        _ = ShowRestartDialog();
                    }
                    else
                    {
                        status.Text = Strings.GetString("MsgOK");
                    }
                }

                // persist legacy config too
                try { SaveConfig(); } catch { }
            }
            catch { }
        }

        /// <summary>提示用户重启以应用语言变更。</summary>
        private async System.Threading.Tasks.Task ShowRestartDialog()
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = Strings.GetString("MsgConfirmTitle"),
                    Content = Strings.GetString("MsgNeedRestartForLanguage"),
                    PrimaryButtonText = Strings.GetString("MsgRestartNow"),
                    CloseButtonText = Strings.GetString("MsgRestartLater"),
                    XamlRoot = (this.Content as FrameworkElement)?.XamlRoot
                };
                var r = await dlg.ShowAsync();
                if (r == ContentDialogResult.Primary)
                {
                    // 启动新实例并退出
                    try
                    {
                        var exePath = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exePath))
                            System.Diagnostics.Process.Start(exePath);
                        RealExitApp();
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>关闭软件按钮：根据 AskExitConfirm 决定是否询问。</summary>
        private async void ExitAppButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_appCfg.AskExitConfirm)
                {
                    var dlg = new ContentDialog
                    {
                        Title = Strings.GetString("MsgConfirmTitle"),
                        Content = Strings.GetString("MsgConfirmExit"),
                        PrimaryButtonText = Strings.GetString("MsgYes"),
                        CloseButtonText = Strings.GetString("MsgNo"),
                        XamlRoot = (this.Content as FrameworkElement)?.XamlRoot
                    };
                    var result = await dlg.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        RealExitApp();
                    }
                }
                else
                {
                    RealExitApp();
                }
            }
            catch { RealExitApp(); }
        }

        private void RealExitApp()
        {
            try
            {
                _isExiting = true;
                _appCfg?.Save();
                _tray?.Dispose();
                try { this.Close(); } catch { }
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch { }
        }

        /// <summary>应用配置中的语言文化。若为 auto 则使用系统当前。</summary>
        private void ApplyLanguageCulture(string language)
        {
            try
            {
                if (string.IsNullOrEmpty(language) || language == "auto")
                {
                    language = AppConfig.DetectSystemLanguage();
                }
                var cult = new System.Globalization.CultureInfo(language);
                Strings.Culture = cult;
            }
            catch { }
        }

        /// <summary>根据配置应用主题（深浅 + 颜色）。</summary>
        private void ApplyThemeFromConfig()
        {
            try
            {
                var root = this.Content as FrameworkElement;
                if (root == null) return;
                var mode = _appCfg.ThemeMode;
                if (string.IsNullOrEmpty(mode)) mode = "System";
                ElementTheme theme = mode switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default,
                };

                // 应用颜色主题：先覆盖系统强调色资源
                ApplyColorTheme(_appCfg.ColorTheme);

                // 设置根元素 RequestedTheme
                root.RequestedTheme = theme;

                // 遍历所有 FrameworkElement，强制主题刷新
                try { UpdateThemeRecursive(root, theme); } catch { }

                UpdateTitleBarColors();
            }
            catch { }
        }

        /// <summary>递归遍历 visual tree，更新所有 FrameworkElement 的 RequestedTheme。</summary>
        private void UpdateThemeRecursive(DependencyObject parent, ElementTheme theme)
        {
            try
            {
                if (parent == null) return;
                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is FrameworkElement fe)
                    {
                        try { fe.RequestedTheme = theme; } catch { }
                    }
                    UpdateThemeRecursive(child, theme);
                }
            }
            catch { }
        }

        /// <summary>应用颜色主题：覆盖 Application 资源中的 SystemAccentColor 及其明暗变体。</summary>
        private void ApplyColorTheme(string? colorTheme)
        {
            try
            {
                if (string.IsNullOrEmpty(colorTheme)) colorTheme = "System";

                // "System" 表示不覆盖，使用 Windows 系统强调色
                if (string.Equals(colorTheme, "System", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // 基础强调色及三个明暗档（Light 1/2/3，Dark 1/2/3）
                (Color base_, Color l1, Color l2, Color l3, Color d1, Color d2, Color d3) palette = colorTheme switch
                {
                    "Green" => (
                        Color.FromArgb(0xFF, 0x10, 0x7C, 0x10),
                        Color.FromArgb(0xFF, 0x4F, 0xAE, 0x4F),
                        Color.FromArgb(0xFF, 0x6F, 0xC2, 0x6F),
                        Color.FromArgb(0xFF, 0x9C, 0xD9, 0x9C),
                        Color.FromArgb(0xFF, 0x0B, 0x6A, 0x0B),
                        Color.FromArgb(0xFF, 0x08, 0x55, 0x08),
                        Color.FromArgb(0xFF, 0x05, 0x3D, 0x05)),
                    "Orange" => (
                        Color.FromArgb(0xFF, 0xCA, 0x50, 0x10),
                        Color.FromArgb(0xFF, 0xDA, 0x7B, 0x47),
                        Color.FromArgb(0xFF, 0xE2, 0x95, 0x6B),
                        Color.FromArgb(0xFF, 0xEC, 0xB1, 0x8F),
                        Color.FromArgb(0xFF, 0xA8, 0x3F, 0x0C),
                        Color.FromArgb(0xFF, 0x82, 0x31, 0x09),
                        Color.FromArgb(0xFF, 0x5E, 0x24, 0x07)),
                    "Purple" => (
                        Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6),
                        Color.FromArgb(0xFF, 0xA7, 0x84, 0xF8),
                        Color.FromArgb(0xFF, 0xBC, 0xA4, 0xFA),
                        Color.FromArgb(0xFF, 0xD2, 0xC4, 0xFB),
                        Color.FromArgb(0xFF, 0x6D, 0x42, 0xE4),
                        Color.FromArgb(0xFF, 0x55, 0x2E, 0xC2),
                        Color.FromArgb(0xFF, 0x3F, 0x21, 0x9E)),
                    "Pink" => (
                        Color.FromArgb(0xFF, 0xE3, 0x37, 0x80),
                        Color.FromArgb(0xFF, 0xEC, 0x6A, 0xA4),
                        Color.FromArgb(0xFF, 0xF1, 0x8B, 0xBA),
                        Color.FromArgb(0xFF, 0xF6, 0xAD, 0xD0),
                        Color.FromArgb(0xFF, 0xB8, 0x2A, 0x66),
                        Color.FromArgb(0xFF, 0x8E, 0x21, 0x50),
                        Color.FromArgb(0xFF, 0x68, 0x18, 0x3A)),
                    _ => ( // Blue（默认）
                        Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),
                        Color.FromArgb(0xFF, 0x4C, 0xA0, 0xE8),
                        Color.FromArgb(0xFF, 0x7A, 0xB8, 0xEC),
                        Color.FromArgb(0xFF, 0xA8, 0xD0, 0xF0),
                        Color.FromArgb(0xFF, 0x00, 0x5A, 0x9E),
                        Color.FromArgb(0xFF, 0x00, 0x42, 0x77),
                        Color.FromArgb(0xFF, 0x00, 0x2A, 0x50))
                };

                var res = Application.Current.Resources;
                res["SystemAccentColor"] = palette.base_;
                res["SystemAccentColorLight1"] = palette.l1;
                res["SystemAccentColorLight2"] = palette.l2;
                res["SystemAccentColorLight3"] = palette.l3;
                res["SystemAccentColorDark1"] = palette.d1;
                res["SystemAccentColorDark2"] = palette.d2;
                res["SystemAccentColorDark3"] = palette.d3;
            }
            catch { }
        }

        /// <summary>窗口关闭事件处理：根据 CloseBehavior 决定隐藏到托盘或退出。</summary>
        public void OnWindowClosing()
        {
            try
            {
                if (_appCfg.CloseBehavior == "Tray")
                {
                    // 隐藏到托盘，不退出
                    _tray?.HideToTray();
                    // 阻止默认关闭（已通过 e.Cancel = true 在 Closed 处理）
                }
                else
                {
                    RealExitApp();
                }
            }
            catch { }
        }

        /// <summary>从托盘恢复显示。</summary>
        public void RestoreFromTray()
        {
            try { _tray?.ShowFromTray(); } catch { }
        }

        /// <summary>开机自启场景下仅隐藏到托盘，不显示窗口。</summary>
        public void RestoreFromTrayHideOnly()
        {
            try { _tray?.HideToTray(); } catch { }
        }

        /// <summary>初始化托盘。在窗口 Loaded 或构造完成后调用。</summary>
        public void InitTray()
        {
            try
            {
                if (_tray == null)
                {
                    _tray = new TrayManager(this);
                    _tray.ShowRequested += (s, e) =>
                    {
                        this.DispatcherQueue.TryEnqueue(() => RestoreFromTray());
                    };
                    _tray.ExitRequested += (s, e) =>
                    {
                        this.DispatcherQueue.TryEnqueue(() => RealExitApp());
                    };
                }
            }
            catch { }
        }

        /// <summary>检查启动参数 --minimized，若是则隐藏窗口到托盘。</summary>
        public bool ShouldStartMinimized()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                foreach (var a in args)
                    if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        /// <summary>优化内存按钮：调用 EmptyWorkingSet 将本进程工作集压缩至分页文件。</summary>
        private async void OptimizeMemoryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("rundll32.exe", "advapi32.dll,ProcessIdleTasks")
                {
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);

                // 同时调用 EmptyWorkingSet（psapi.dll）压缩本进程
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

        // ============= 壁纸预览页面相关 =============
        // 数据模型
        public sealed class WallpaperPreviewItem
        {
            public BitmapSource ThumbSource { get; set; } = new BitmapImage();
            public string FileName { get; set; } = "";
            public string FullPath { get; set; } = "";
        }

        // 悬停放大动画
        private void WallpaperThumb_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is Border b)
                {
                    var img = FindFirstVisualChild<Image>(b);
                    if (img?.RenderTransform is ScaleTransform st)
                    {
                        st.ScaleX = 1.15;
                        st.ScaleY = 1.15;
                    }
                }
            }
            catch { }
        }

        private void WallpaperThumb_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is Border b)
                {
                    var img = FindFirstVisualChild<Image>(b);
                    if (img?.RenderTransform is ScaleTransform st)
                    {
                        st.ScaleX = 1.0;
                        st.ScaleY = 1.0;
                    }
                }
            }
            catch { }
        }

        private static T? FindFirstVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            try
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is T t) return t;
                    var found = FindFirstVisualChild<T>(child);
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        /// <summary>刷新预览按钮</summary>
        private void RefreshPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            try { LoadWallpaperPreviews(); } catch { }
        }

        /// <summary>
        /// 从 Wallpaper 目录加载所有壁纸，裁剪中间 768x768 区域并缩小至 200x200，平铺展示。
        /// </summary>
        private async void LoadWallpaperPreviews()
        {
            try
            {
                var root = this.Content as FrameworkElement;
                var itemsControl = root?.FindName("WallpaperPreviewItems") as ItemsControl;
                if (itemsControl == null) return;

                var list = new ObservableCollection<WallpaperPreviewItem>();
                itemsControl.ItemsSource = list;

                if (string.IsNullOrEmpty(WallpaperFolderPath) || !Directory.Exists(WallpaperFolderPath))
                    return;

                var files = Directory.EnumerateFiles(WallpaperFolderPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";
                    })
                    .OrderByDescending(f => f)
                    .ToList();

                // 限制数量避免一次加载太多（最多 60 张）
                if (files.Count > 60) files = files.Take(60).ToList();

                foreach (var file in files)
                {
                    try
                    {
                        var thumb = await CropCenterSquareAsync(file, 768, 200);
                        if (thumb != null)
                        {
                            list.Add(new WallpaperPreviewItem
                            {
                                ThumbSource = thumb,
                                FileName = Path.GetFileName(file),
                                FullPath = file
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 加载图片并裁剪中心正方形区域，缩放到目标尺寸。
        /// </summary>
        private async Task<BitmapSource?> CropCenterSquareAsync(string imagePath, int cropSize, int targetSize)
        {
            try
            {
                var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(imagePath);
                using var stream = await storageFile.OpenAsync(Windows.Storage.FileAccessMode.Read);

                // 先解码原图
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                uint origW = decoder.OrientedPixelWidth;
                uint origH = decoder.OrientedPixelHeight;
                if (origW == 0 || origH == 0) return null;

                // 计算中心正方形裁剪区域（以较短边为基准，最多取 cropSize）
                uint squareSize = Math.Min(Math.Min(origW, origH), (uint)cropSize);
                uint startX = (origW - squareSize) / 2;
                uint startY = (origH - squareSize) / 2;

                var transform = new Windows.Graphics.Imaging.BitmapTransform
                {
                    Bounds = new Windows.Graphics.Imaging.BitmapBounds
                    {
                        X = startX,
                        Y = startY,
                        Width = squareSize,
                        Height = squareSize
                    },
                    ScaledWidth = (uint)targetSize,
                    ScaledHeight = (uint)targetSize,
                    InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
                };

                var pixelData = await decoder.GetPixelDataAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    transform,
                    Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);

                var bytes = pixelData.DetachPixelData();
                var bmp = new WriteableBitmap(targetSize, targetSize);
                using (var pixelBufferStream = bmp.PixelBuffer.AsStream())
                {
                    pixelBufferStream.Write(bytes, 0, bytes.Length);
                }
                return bmp;
            }
            catch
            {
                // 回退：使用 BitmapImage 加载原图
                try
                {
                    var uri = new Uri(imagePath);
                    var bi = new BitmapImage(uri);
                    return bi;
                }
                catch { return null; }
            }
        }

    }
}
