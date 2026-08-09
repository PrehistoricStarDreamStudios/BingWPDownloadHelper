using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;

namespace BingPaper.Pages
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            this.InitializeComponent();
            Loaded += AboutPage_Loaded;
        }

        private void AboutPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                AboutVersionText.Text = $"BingPaper v0.1";
            }
            catch { }
        }

        private void OpenLinksButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://www.bing.com/?mkt=zh-CN");
        }

        private void OpenBingLink_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://www.bing.com/");
        }

        private void OpenProjectGit_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/SDNet123456/BingPaper");
        }

        private void OpenBili_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://space.bilibili.com/");
        }

        private void OpenAuthorGit_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/SDNet123456");
        }

        private void OpenAuthorX_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://x.com/");
        }

        private void OpenMail_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("mailto:sdnet@sdnet.page.gd");
        }

        private void ShowMottoButton_Click(object sender, RoutedEventArgs e)
        {
            var mottos = new[]
            {
                "代码改变世界，壁纸改变桌面。",
                "每天一张新壁纸，每天一个好心情。",
                "Bing 每日壁纸，让桌面不再单调。",
                "生活不止眼前的代码，还有 Bing 的壁纸。",
                "世界那么大，Bing 带你去看看。",
                "壁纸，是桌面的灵魂。",
                "好的壁纸，是灵感的源泉。",
                "每一张 Bing 壁纸，都是一扇窗。",
            };
            var rng = new Random();
            MottoText.Text = mottos[rng.Next(mottos.Length)];
        }

        private void OptimizeMemoryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                NativeMethods.EmptyWorkingSet(proc.Handle);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch { }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}