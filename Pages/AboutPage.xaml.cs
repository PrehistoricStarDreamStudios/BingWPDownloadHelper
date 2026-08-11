using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BingPaper.Pages
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            this.InitializeComponent();
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

        // ===== 右侧详情面板逻辑（对应设计稿 about.html 的 openPanel / closePanel） =====

        private void OpenOpenSourcePanel_Click(object sender, RoutedEventArgs e)
        {
            OpenPanel("开源引用", new List<AboutLinkItem>
            {
                new AboutLinkItem { Name = "Microsoft.WindowsAppSDK", Url = "https://github.com/microsoft/WindowsAppSDK" },
                new AboutLinkItem { Name = "Microsoft.Windows.SDK.BuildTools", Url = "https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools" }
            });
        }

        private void OpenToolsPanel_Click(object sender, RoutedEventArgs e)
        {
            OpenPanel("工具引用", new List<AboutLinkItem>
            {
                new AboutLinkItem { Name = "Visual Studio", Url = "https://visualstudio.microsoft.com/zh-hans/vs/" },
                new AboutLinkItem { Name = "Trae CN", Url = "https://www.trae.cn" },
                new AboutLinkItem { Name = "SteamCommunity 302", Url = "https://www.dogfight360.com/blog/18682/" }
            });
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DetailPane != null) DetailPane.IsPaneOpen = false;
            }
            catch { }
        }

        private void OpenPanel(string title, List<AboutLinkItem> items)
        {
            try
            {
                if (PanelTitle != null) PanelTitle.Text = title;
                if (PanelList != null) PanelList.ItemsSource = items;
                if (DetailPane != null) DetailPane.IsPaneOpen = true;
            }
            catch { }
        }
    }

    /// <summary>关于页右侧详情面板列表项（对应设计稿 panelData.items）。</summary>
    public sealed class AboutLinkItem
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }
}