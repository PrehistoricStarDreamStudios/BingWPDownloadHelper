using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BingPaper.Pages
{
    public sealed partial class PreviewPage : Page
    {
        private static readonly string[] OfficialTags = { "精选", "人文", "风景", "节日", "动物", "植物", "海洋", "建筑", "景点", "其他" };
        private const string UnclassifiedTag = "未分类";

        public PreviewPage()
        {
            this.InitializeComponent();
            Loaded += PreviewPage_Loaded;
        }

        private void PreviewPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AppData.AllWallpapers.Count == 0)
                {
                    LoadWallpapersFromXml();
                    PopulateTagFilter();
                }
                if (AppData.AllWallpapers.Count > 0 && AppData.FilteredWallpapers.Count == 0)
                {
                    AppData.FilteredWallpapers = AppData.AllWallpapers.ToList();
                }
                FillPreviewGrid();
            }
            catch { }
        }

        private void LoadWallpapersFromXml()
        {
            try
            {
                var assetDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets");
                if (!System.IO.Directory.Exists(assetDir)) return;
                var files = System.IO.Directory.GetFiles(assetDir, "list*.xml");
                foreach (var f in files)
                {
                    try
                    {
                        var xdoc = System.Xml.Linq.XDocument.Load(f);
                        var wps = xdoc.Descendants("wallpaper");
                        foreach (var wp in wps)
                        {
                            var urlEl = wp.Element("url");
                            var tagsEl = wp.Element("tags");
                            if (urlEl == null) continue;
                            var url = urlEl.Value.Trim();
                            if (string.IsNullOrEmpty(url)) continue;
                            var tags = new List<string>();
                            if (tagsEl != null)
                            {
                                var tagText = tagsEl.Value.Trim();
                                if (!string.IsNullOrEmpty(tagText))
                                    tags.AddRange(tagText.Split(',', '，', ';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)));
                            }
                            if (tags.Count == 0) tags.Add(UnclassifiedTag);
                            // Deduplicate by URL
                            if (!AppData.AllWallpapers.Any(w => w.url == url))
                                AppData.AllWallpapers.Add((url, tags));
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void PopulateTagFilter()
        {
            TagFilterCombo.Items.Clear();
            TagFilterCombo.Items.Add(new ComboBoxItem { Content = "全部", Tag = "__all__" });
            var allTags = new HashSet<string>();
            foreach (var wp in AppData.AllWallpapers)
                foreach (var t in wp.tags)
                    allTags.Add(t);
            foreach (var t in OfficialTags)
            {
                if (allTags.Contains(t))
                {
                    TagFilterCombo.Items.Add(new ComboBoxItem { Content = t, Tag = t });
                    allTags.Remove(t);
                }
            }
            foreach (var t in allTags.OrderBy(x => x))
                TagFilterCombo.Items.Add(new ComboBoxItem { Content = t, Tag = t });
            TagFilterCombo.SelectedIndex = 0;
        }

        private void TagFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var tag = (TagFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "__all__";
                if (tag == "__all__")
                    AppData.FilteredWallpapers = AppData.AllWallpapers.ToList();
                else
                    AppData.FilteredWallpapers = AppData.AllWallpapers.Where(w => w.tags.Contains(tag)).ToList();
                FillPreviewGrid();
            }
            catch { }
        }

        private void FillPreviewGrid()
        {
            try
            {
                PreviewGridView.ItemsSource = null;
                PreviewGridView.ItemsSource = AppData.FilteredWallpapers;
                PreviewCountText.Text = $"共 {AppData.FilteredWallpapers.Count} 张";
            }
            catch { }
        }

        private void PreviewGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            try
            {
                if (args.Item is not (string url, List<string> tags)) return;
                var img = args.ItemContainer.ContentTemplateRoot is Grid grid
                    ? grid.FindName("ItemImg") as Image
                    : null;
                if (img != null)
                {
                    img.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url));
                }
            }
            catch { }
        }

        private void PreviewItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is Grid grid)
                {
                    var scale = grid.FindName("ItemScale") as ScaleTransform;
                    if (scale != null)
                    {
                        scale.ScaleX = 1.08;
                        scale.ScaleY = 1.08;
                    }
                }
            }
            catch { }
        }

        private void PreviewItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is Grid grid)
                {
                    var scale = grid.FindName("ItemScale") as ScaleTransform;
                    if (scale != null)
                    {
                        scale.ScaleX = 1;
                        scale.ScaleY = 1;
                    }
                }
            }
            catch { }
        }
    }
}