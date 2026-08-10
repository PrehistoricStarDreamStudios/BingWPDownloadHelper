using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;

namespace BingPaper.Pages
{
    public sealed partial class SetWallpaperPage : Page
    {
        public SetWallpaperPage()
        {
            this.InitializeComponent();
            Loaded += SetWallpaperPage_Loaded;
        }

        private void SetWallpaperPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SlideshowFolderText.Text = AppData.WallpaperFolderPath;
                UpdateIntervalEnabledState();
                // Restore saved settings
                if (AppData.Config.TryGetValue("slideshow_interval", out var iv) && int.TryParse(iv, out var x))
                {
                    for (int i = 0; i < SlideshowIntervalCombo.Items.Count; i++)
                    {
                        if (SlideshowIntervalCombo.Items[i] is ComboBoxItem ci && (ci.Tag as string) == x.ToString())
                        { SlideshowIntervalCombo.SelectedIndex = i; break; }
                    }
                }
                if (AppData.Config.TryGetValue("slideshow_shuffle", out var sh))
                    ShuffleCheck.IsOn = sh.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (AppData.Config.TryGetValue("slideshow_fill", out var f))
                {
                    foreach (ComboBoxItem it in FillModeCombo.Items)
                    { if (it.Tag?.ToString() == f) { FillModeCombo.SelectedItem = it; break; } }
                }
            }
            catch { }
        }

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
            catch { }
        }

        private void PlaybackMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateIntervalEnabledState();
        }

        private void UpdateIntervalEnabledState()
        {
            try
            {
                // 切换时间仅在幻灯片放映模式下可用
                bool isSlideshow = PlaybackModeGroup != null && PlaybackModeGroup.SelectedIndex == 1;
                if (SlideshowIntervalCombo != null) SlideshowIntervalCombo.IsEnabled = isSlideshow;
            }
            catch { }
        }

        private void ApplySlideshowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = AppData.WallpaperFolderPath;
                var sText = SlideshowFolderText.Text?.Trim();
                if (!string.IsNullOrEmpty(sText)) folder = sText;

                if (!Directory.Exists(folder))
                {
                    SettingsStatusText.Text = "幻灯片目录不存在: " + folder;
                    return;
                }

                uint intervalMs = 1800 * 1000;
                if (SlideshowIntervalCombo.SelectedItem is ComboBoxItem ivci && uint.TryParse(ivci.Tag as string, out var secs))
                    intervalMs = secs * 1000;

                bool shuffle = ShuffleCheck.IsOn;
                string fillMode = "Fill";
                if (FillModeCombo.SelectedItem is ComboBoxItem fci) fillMode = fci.Tag?.ToString() ?? "Fill";

                bool ok = App.SetDesktopSlideshow(folder, intervalMs, shuffle, fillMode);
                SettingsStatusText.Text = ok
                    ? "幻灯片已设置成功。"
                    : "设置幻灯片失败，请查看日志。";

                AppData.Config["slideshow_interval"] = intervalMs.ToString();
                AppData.Config["slideshow_shuffle"] = shuffle ? "true" : "false";
                AppData.Config["slideshow_fill"] = fillMode;
            }
            catch (Exception ex)
            {
                SettingsStatusText.Text = "发生错误: " + ex.Message;
            }
        }

        private async void OpenFolderPicker_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    SlideshowFolderText.Text = folder.Path;
                }
            }
            catch { }
        }
    }
}