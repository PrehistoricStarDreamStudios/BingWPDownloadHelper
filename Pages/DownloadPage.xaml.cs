using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BingPaper.Pages
{
    public sealed partial class DownloadPage : Page
    {
        public DownloadPage()
        {
            this.InitializeComponent();
            Loaded += DownloadPage_Loaded;
        }

        private void DownloadPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                DownloadFolderText.Text = AppData.WallpaperFolderPath;
                InitDateCombos();
                InitAspectAndResolution();
            }
            catch { }
        }

        private void InitDateCombos()
        {
            YearCombo.Items.Clear(); MonthCombo.Items.Clear(); DayCombo.Items.Clear();
            int startYear = 2021;
            int currentYear = DateTime.Now.Year;
            for (int y = startYear; y <= currentYear; y++) YearCombo.Items.Add(new ComboBoxItem { Content = y.ToString() });
            for (int m = 1; m <= 12; m++) MonthCombo.Items.Add(new ComboBoxItem { Content = m.ToString("D2") });
            for (int d = 1; d <= 31; d++) DayCombo.Items.Add(new ComboBoxItem { Content = d.ToString("D2") });
            var defDate = DateTime.Now.Date.AddDays(-7);
            SelectComboByContent(YearCombo, defDate.Year.ToString());
            SelectComboByContent(MonthCombo, defDate.Month.ToString("D2"));
            SelectComboByContent(DayCombo, defDate.Day.ToString("D2"));
        }

        private void InitAspectAndResolution()
        {
            try
            {
                var assetDir = Path.Combine(AppContext.BaseDirectory, "Assets");
                if (!Directory.Exists(assetDir)) return;
                var files = Directory.GetFiles(assetDir, "list*.xml");
                var aspectSet = new HashSet<string>();
                var resMap = new Dictionary<string, List<string>>();

                foreach (var f in files)
                {
                    try
                    {
                        var xdoc = System.Xml.Linq.XDocument.Load(f);
                        var resNodes = xdoc.Descendants("resolution").ToList();
                        foreach (var resNode in resNodes)
                        {
                            var r = (string?)resNode.Attribute("name") ?? "";
                            var ar = (string?)resNode.Attribute("aspect_ratio") ?? "";
                            if (string.IsNullOrEmpty(ar) && !string.IsNullOrEmpty(r) && r.Contains('x'))
                            {
                                var parts = r.Split('x');
                                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                                    ar = $"{w / Gcd(w, h)}:{h / Gcd(w, h)}";
                            }
                            if (!string.IsNullOrEmpty(ar))
                            {
                                aspectSet.Add(ar);
                                if (!resMap.ContainsKey(ar)) resMap[ar] = new List<string>();
                                if (!string.IsNullOrEmpty(r) && !resMap[ar].Contains(r)) resMap[ar].Add(r);
                            }
                        }
                    }
                    catch { }
                }

                foreach (var a in aspectSet.OrderBy(x => x)) AspectRatioCombo.Items.Add(new ComboBoxItem { Content = a });
                // Select 16:9 or first
                for (int i = 0; i < AspectRatioCombo.Items.Count; i++)
                {
                    if ((AspectRatioCombo.Items[i] as ComboBoxItem)?.Content as string == "16:9")
                    { AspectRatioCombo.SelectedIndex = i; break; }
                }
                if (AspectRatioCombo.SelectedIndex < 0 && AspectRatioCombo.Items.Count > 0) AspectRatioCombo.SelectedIndex = 0;

                AspectRatioCombo.Tag = resMap;
            }
            catch { }
        }

        private void AspectRatioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                ResolutionCombo.Items.Clear();
                var map = AspectRatioCombo.Tag as Dictionary<string, List<string>>;
                var key = (AspectRatioCombo.SelectedItem as ComboBoxItem)?.Content as string;
                if (!string.IsNullOrEmpty(key) && map != null && map.ContainsKey(key))
                {
                    foreach (var rr in map[key].Distinct().OrderBy(x => x))
                        ResolutionCombo.Items.Add(new ComboBoxItem { Content = rr, Tag = "_" + rr + ".jpg" });
                }
                if (ResolutionCombo.Items.Count == 0)
                    ResolutionCombo.Items.Add(new ComboBoxItem { Content = "3840x2160", Tag = "_3840x2160.jpg" });

                // Select highest resolution
                long bestArea = -1; int bestIndex = -1;
                for (int j = 0; j < ResolutionCombo.Items.Count; j++)
                {
                    var it = ResolutionCombo.Items[j] as ComboBoxItem;
                    if (it == null) continue;
                    var content = it.Content as string ?? "";
                    var m = System.Text.RegularExpressions.Regex.Match(content, @"(\d{3,4})x(\d{3,4})");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var w) && int.TryParse(m.Groups[2].Value, out var h))
                    {
                        long area = (long)w * h;
                        if (area > bestArea) { bestArea = area; bestIndex = j; }
                    }
                }
                if (bestIndex >= 0) ResolutionCombo.SelectedIndex = bestIndex;
            }
            catch { }
        }

        private void DownloadHistoryCheck_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = DownloadHistoryCheck.IsOn;
            YearCombo.IsEnabled = enabled;
            MonthCombo.IsEnabled = enabled;
            DayCombo.IsEnabled = enabled;
        }

        private void DatePart_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = AppData.WallpaperFolderPath;
                var folderText = DownloadFolderText.Text?.Trim();
                if (!string.IsNullOrEmpty(folderText)) folder = folderText;

                if (!Directory.Exists(folder))
                {
                    DownloadStatusText.Text = "下载目录不存在: " + folder;
                    return;
                }

                var resItem = ResolutionCombo.SelectedItem as ComboBoxItem;
                var suffix = resItem?.Tag as string ?? "_UHD.jpg";

                DownloadStatusText.Text = "正在获取壁纸列表...";
                var http = new System.Net.Http.HttpClient();
                string apiUrl;
                if (DownloadHistoryCheck.IsOn)
                {
                    var y = (YearCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "2021";
                    var m = (MonthCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "01";
                    var d = (DayCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "01";
                    apiUrl = $"https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=en-US&date={y}{m}{d}";
                }
                else
                {
                    apiUrl = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=en-US";
                }

                var json = await http.GetStringAsync(apiUrl);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var images = doc.RootElement.GetProperty("images");

                int count = 0;
                DownloadStatusText.Text = "";
                foreach (var img in images.EnumerateArray())
                {
                    var urlBase = img.GetProperty("urlbase").GetString();
                    if (string.IsNullOrEmpty(urlBase)) continue;
                    var fullUrl = "https://www.bing.com" + urlBase + suffix;
                    var fileName = urlBase.Split('/').Last().TrimStart('_') + suffix;
                    if (!fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) fileName += ".jpg";
                    var outPath = Path.Combine(folder, fileName);

                    if (!File.Exists(outPath))
                    {
                        try
                        {
                            var data = await http.GetByteArrayAsync(fullUrl);
                            await File.WriteAllBytesAsync(outPath, data);
                            count++;
                            DownloadStatusText.AppendText($"已下载: {fileName}\n");
                        }
                        catch (Exception ex)
                        {
                            DownloadStatusText.AppendText($"下载失败: {fileName} - {ex.Message}\n");
                        }
                    }
                    else
                    {
                        DownloadStatusText.AppendText($"已存在: {fileName}\n");
                    }
                }
                DownloadStatusText.AppendText($"\n完成，共下载 {count} 张壁纸。");
            }
            catch (Exception ex)
            {
                DownloadStatusText.Text = "下载出错: " + ex.Message;
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
                    DownloadFolderText.Text = folder.Path;
                    AppData.WallpaperFolderPath = folder.Path;
                    AppData.Config["download_folder"] = folder.Path;
                }
            }
            catch { }
        }

        private void DownloadFolderText_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var newPath = DownloadFolderText.Text?.Trim();
                if (string.IsNullOrEmpty(newPath)) return;
                if (!Path.IsPathRooted(newPath)) newPath = Path.GetFullPath(newPath);
                try
                {
                    if (!Directory.Exists(newPath)) Directory.CreateDirectory(newPath);
                    AppData.WallpaperFolderPath = newPath;
                    AppData.Config["download_folder"] = newPath;
                    DownloadStatusText.Text = "下载目录已更新：" + newPath;
                }
                catch (Exception ex)
                {
                    DownloadStatusText.Text = "目录不可用：" + ex.Message;
                }
            }
        }

        private static void SelectComboByContent(ComboBox cb, string content)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                if ((cb.Items[i] as ComboBoxItem)?.Content as string == content) { cb.SelectedIndex = i; return; }
            }
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a); b = Math.Abs(b);
            while (b != 0) { var t = a % b; a = b; b = t; }
            return a == 0 ? 1 : a;
        }
    }
}