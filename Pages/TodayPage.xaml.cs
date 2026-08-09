using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BingPaper.Pages
{
    public sealed partial class TodayPage : Page
    {
        public TodayPage()
        {
            this.InitializeComponent();
            Loaded += TodayPage_Loaded;
        }

        private void TodayPage_Loaded(object sender, RoutedEventArgs e)
        {
            _ = InitializeAsync();
        }

        public async Task InitializeAsync()
        {
            try
            {
                var http = new HttpClient();
                var url = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-US";
                var s = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(s);
                var images = doc.RootElement.GetProperty("images");
                if (images.GetArrayLength() > 0)
                {
                    var first = images[0];
                    var urlBase = first.GetProperty("urlbase").GetString();
                    string? title = null;
                    if (first.TryGetProperty("copyright", out var cp)) title = cp.GetString();
                    else if (first.TryGetProperty("title", out var t)) title = t.GetString();

                    if (!string.IsNullOrEmpty(urlBase))
                    {
                        var res = AppData.Config.TryGetValue("default_resolution", out var dr) ? dr : "UHD";
                        var suffix = res == "720" || res.Equals("720p", StringComparison.OrdinalIgnoreCase) ? "_1280x720.jpg"
                            : res == "1080" ? "_1920x1080.jpg" : "_UHD.jpg";
                        var full = "https://www.bing.com" + urlBase + suffix;

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            try
                            {
                                MainImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(full));
                                ImageTitle.Text = title ?? string.Empty;
                            }
                            catch { }
                        });
                    }
                }

                // 初始化分段选择器位置
                DispatcherQueue.TryEnqueue(() => RecalcSelIndicator());
            }
            catch { }
        }

        private async void Seg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var btn = sender as Button;
                if (btn != null) MoveSelToButton(btn);

                string suffix = "_UHD.jpg";
                if (btn?.Name == "Btn720") suffix = "_1280x720.jpg";
                else if (btn?.Name == "Btn1080") suffix = "_1920x1080.jpg";

                var http = new HttpClient();
                var url = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-US";
                var s = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(s);
                var images = doc.RootElement.GetProperty("images");
                if (images.GetArrayLength() > 0)
                {
                    var first = images[0];
                    var urlBase = first.GetProperty("urlbase").GetString();
                    if (!string.IsNullOrEmpty(urlBase))
                    {
                        var full = "https://www.bing.com" + urlBase + suffix;
                        MainImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(full));
                        if (first.TryGetProperty("copyright", out var cp))
                            ImageTitle.Text = cp.GetString();
                    }
                }
            }
            catch { }
        }

        private void MoveSelToButton(Button btn)
        {
            try
            {
                double segGridWidth = Math.Max(0, SegGrid.ActualWidth);
                double columnWidth = segGridWidth / 3.0;
                double targetWidth = Math.Max(0, columnWidth - 12);

                int colIndex = 2;
                if (btn == Btn720) colIndex = 0;
                else if (btn == Btn1080) colIndex = 1;

                double targetX = columnWidth * colIndex + (columnWidth - targetWidth) / 2.0;

                if (AppData.AnimationEnabled && AppData.AnimationMs > 0)
                {
                    AnimateSelIndicator(SelIndicator, SelTransform, SelIndicator.Width, targetWidth, SelTransform.X, targetX, (int)AppData.AnimationMs);
                }
                else
                {
                    SelIndicator.Width = targetWidth;
                    SelTransform.X = targetX;
                    SelIndicator.Opacity = 1.0;
                }

                var fgHigh = (Brush)Application.Current.Resources["SystemControlForegroundBaseHighBrush"];
                var whiteBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
                foreach (var b in new[] { Btn720, Btn1080, BtnUHD })
                {
                    if (b != null) b.Foreground = fgHigh;
                }
                if (colIndex == 0 && Btn720 != null) Btn720.Foreground = whiteBrush;
                if (colIndex == 1 && Btn1080 != null) Btn1080.Foreground = whiteBrush;
                if (colIndex == 2 && BtnUHD != null) BtnUHD.Foreground = whiteBrush;
            }
            catch { }
        }

        public void RecalcSelIndicator()
        {
            try
            {
                FrameworkElement? target = BtnUHD ?? Btn1080 ?? (FrameworkElement?)Btn720;
                if (target == null) return;
                var t = target.TransformToVisual(SegGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
                double innerWidth = target.ActualWidth;
                SelIndicator.Width = Math.Max(0, innerWidth - 4);
                SelTransform.X = t.X + (target.ActualWidth - SelIndicator.Width) / 2.0 - 4;
                SelIndicator.Opacity = 1.0;
            }
            catch { }
        }

        private void AnimateSelIndicator(Border sel, TranslateTransform selTrans, double fromWidth, double toWidth, double fromX, double toX, int durationMs)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var dq = DispatcherQueue;
                System.Threading.Timer? timer = null;
                timer = new System.Threading.Timer(_ =>
                {
                    var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / Math.Max(1, durationMs));
                    var curW = fromWidth + (toWidth - fromWidth) * t;
                    var curX = fromX + (toX - fromX) * t;
                    try
                    {
                        dq.TryEnqueue(() =>
                        {
                            sel.Width = curW;
                            selTrans.X = curX;
                            sel.Opacity = 1.0;
                        });
                    }
                    catch { }
                    if (t >= 1.0) try { timer?.Dispose(); } catch { }
                }, null, 0, 16);
            }
            catch { }
        }
    }
}