using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using Microsoft.Win32;

namespace BingPaper.Pages
{
    public sealed partial class AppSettingsPage : Page
    {
        private bool _isLoading = false;

        public AppSettingsPage()
        {
            _isLoading = true;
            this.InitializeComponent();
            Loaded += AppSettingsPage_Loaded;
        }

        private void AppSettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoading = true;
            try
            {
                // AutoStart
                if (AppData.Config.TryGetValue("autostart", out var av))
                    AutoStartCheck.IsOn = av.Equals("true", StringComparison.OrdinalIgnoreCase);

                // CloseBehavior
                var cb = AppData.Config.TryGetValue("close_behavior", out var cbv) ? cbv : "Tray";
                for (int i = 0; i < CloseBehaviorCombo.Items.Count; i++)
                {
                    if (CloseBehaviorCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == cb)
                    { CloseBehaviorCombo.SelectedIndex = i; break; }
                }

                // ThemeMode
                var tm = AppData.Config.TryGetValue("theme_mode", out var tmv) ? tmv : "System";
                for (int i = 0; i < ThemeModeCombo.Items.Count; i++)
                {
                    if (ThemeModeCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == tm)
                    { ThemeModeCombo.SelectedIndex = i; break; }
                }

                // ColorTheme
                var ct = AppData.Config.TryGetValue("color_theme", out var ctv) ? ctv : "System";
                for (int i = 0; i < ColorThemeCombo.Items.Count; i++)
                {
                    if (ColorThemeCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == ct)
                    { ColorThemeCombo.SelectedIndex = i; break; }
                }

                // DownloadThreads
                if (AppData.Config.TryGetValue("download_threads", out var dtv) && int.TryParse(dtv, out var dti))
                    DownloadThreadsBox.Value = Math.Max(1, Math.Min(32, dti));

                // Language
                var lang = AppData.Config.TryGetValue("language", out var lv) ? lv : "zh-CN";
                if (lang == "auto") lang = AppConfig.DetectSystemLanguage();
                for (int i = 0; i < LanguageCombo.Items.Count; i++)
                {
                    if (LanguageCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == lang)
                    { LanguageCombo.SelectedIndex = i; break; }
                }

                // BackdropType
                var backdropType = AppData.Config.TryGetValue("backdrop_type", out var btv) ? btv : "Mica";
                for (int i = 0; i < BackdropTypeCombo.Items.Count; i++)
                {
                    if (BackdropTypeCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == backdropType)
                    { BackdropTypeCombo.SelectedIndex = i; break; }
                }
                if (BackdropTypeCombo.SelectedIndex < 0) BackdropTypeCombo.SelectedIndex = 0;

                // TransparentBackground
                var tg = AppData.Config.TryGetValue("transparent_background", out var tgv)
                    ? tgv.Equals("true", StringComparison.OrdinalIgnoreCase) : true;
                TransparentBackgroundSwitch.IsOn = tg;

                // AutoUpdateList
                var au = AppData.Config.TryGetValue("auto_update_list", out var auv)
                    ? auv.Equals("true", StringComparison.OrdinalIgnoreCase) : true;
                AutoUpdateListCheck.IsOn = au;

                // DefaultList
                var defList = AppData.Config.TryGetValue("default_list", out var dl) ? dl : "local";
                foreach (ComboBoxItem it in DefaultListCombo.Items)
                {
                    if ((it.Tag as string ?? it.Content as string) == defList) { DefaultListCombo.SelectedItem = it; break; }
                }

                // ApiSource
                var api = AppData.Config.TryGetValue("api_source", out var ap) ? ap : "bing";
                if (api == "bing") ApiSourceCombo.SelectedIndex = 0;
            }
            catch { }
            _isLoading = false;
        }

        private void AutoStartCheck_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            try
            {
                SetAutoStart(AutoStartCheck.IsOn);
                AppData.Config["autostart"] = AutoStartCheck.IsOn ? "true" : "false";
            }
            catch { }
        }

        private void CloseBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (CloseBehaviorCombo.SelectedItem is ComboBoxItem ci)
                AppData.Config["close_behavior"] = ci.Tag as string ?? "Tray";
        }

        private void ThemeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ThemeModeCombo.SelectedItem is ComboBoxItem ci)
            {
                var tag = ci.Tag as string ?? "System";
                AppData.Config["theme_mode"] = tag;
                ApplyThemeMode(tag);
            }
        }

        private void ColorTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ColorThemeCombo.SelectedItem is ComboBoxItem ci)
            {
                var tag = ci.Tag as string ?? "System";
                AppData.Config["color_theme"] = tag;
            }
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (LanguageCombo.SelectedItem is ComboBoxItem ci)
            {
                var tag = ci.Tag as string ?? "zh-CN";
                AppData.Config["language"] = tag;
                try { Strings.Culture = new CultureInfo(tag); } catch { }
                LanguageRestartHint.Visibility = Visibility.Visible;
            }
        }

        private void BackdropType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (BackdropTypeCombo.SelectedItem is ComboBoxItem ci)
            {
                var type = ci.Tag as string ?? "Mica";
                AppData.Config["backdrop_type"] = type;
                // Trigger backdrop update via MainWindow
                MainWindow.Instance?.ApplyBackdropFromConfig();
            }
        }

        private void TransparentBackground_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            AppData.Config["transparent_background"] = TransparentBackgroundSwitch.IsOn ? "true" : "false";
            MainWindow.Instance?.ApplyBackdropFromConfig();
        }

        private void SaveSoftwareSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppData.Config["download_threads"] = ((int)DownloadThreadsBox.Value).ToString();
                AppData.Config["auto_update_list"] = AutoUpdateListCheck.IsOn ? "true" : "false";
                if (DefaultListCombo.SelectedItem is ComboBoxItem dlItem)
                    AppData.Config["default_list"] = dlItem.Tag as string ?? dlItem.Content as string ?? "local";
                AppSettingsStatusText.Text = "设置已保存。";
            }
            catch (Exception ex)
            {
                AppSettingsStatusText.Text = "保存失败: " + ex.Message;
            }
        }

        private void CheckUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStatusText.Text = "正在检查更新...";
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { if (MainWindow.Instance != null) await MainWindow.Instance.AutoUpdateListAsync(); }
                catch { }
                DispatcherQueue.TryEnqueue(() => AppSettingsStatusText.Text = "列表更新检查完成。");
            });
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (key == null) return;
                var exePath = Environment.ProcessPath ?? "";
                if (enable) key.SetValue("BingPaper", exePath);
                else key.DeleteValue("BingPaper", false);
            }
            catch { }
        }

        private static void ApplyThemeMode(string mode)
        {
            try
            {
                var root = App.MainWindowInstance?.Content as FrameworkElement;
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
    }
}