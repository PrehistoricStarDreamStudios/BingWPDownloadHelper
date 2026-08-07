// 每日获取必应当日壁纸，调用智谱 GLM 视觉模型对图片进行分类，
// 将 <wallpaper><url/><label/></wallpaper> 追加到 Assets/list.xml。
//
// 环境变量：
//   ZHIPU_API_KEY : 智谱开放平台 API Key（必填）
//   INPUT_DATE    : 可选，指定日期 YYYY-MM-DD（手动触发时传入）
//
// 标签集合（与软件端一致）：
//   精选 人文 风景 节日 动物 植物 海洋 建筑 景点 其他
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BingPaperListUpdater;

internal static class Program
{
    private static readonly string[] OfficialTags =
        { "精选", "人文", "风景", "节日", "动物", "植物", "海洋", "建筑", "景点", "其他" };
    private const string UnclassifiedTag = "未分类";
    private const string ListXmlPath = "Assets/list.xml";
    private const string BingApi =
        "https://cn.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&pid=hp&FORM=BEHPTB&uhd=1&uhdwidth=3840&uhdheight=2160&setmkt=zh-CN&setlang=en";
    private const string BingBase = "https://cn.bing.com";
    private const string ZhipuApiUrl = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    private const string ZhipuModel = "glm-4v";

    private static readonly HttpClient Http = new();

    private static async Task<int> Main(string[] args)
    {
        var apiKey = Environment.GetEnvironmentVariable("ZHIPU_API_KEY")?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.Error.WriteLine("ERROR: ZHIPU_API_KEY not set");
            return 1;
        }

        var inputDate = Environment.GetEnvironmentVariable("INPUT_DATE")?.Trim();
        var targetDate = string.IsNullOrEmpty(inputDate)
            ? DateTime.Today
            : DateTime.Parse(inputDate);

        Console.WriteLine($"Fetching Bing wallpaper for {targetDate:yyyy-MM-dd} ...");
        var (dateStr, uhdUrl) = await FetchTodayWallpaperAsync();
        Console.WriteLine($"Got URL: {uhdUrl}");

        // 去重
        var existing = LoadExistingUrls(ListXmlPath);
        if (existing.Contains(uhdUrl))
        {
            Console.WriteLine("Wallpaper already in list, skip.");
            return 0;
        }

        // 下载图片并用智谱模型分类（失败回退未分类，保证列表完整）
        var tags = new List<string> { UnclassifiedTag };
        try
        {
            Console.WriteLine("Downloading image for classification ...");
            var imgBytes = await DownloadImageBytesAsync(uhdUrl);
            Console.WriteLine($"Downloaded {imgBytes.Length} bytes, classifying with {ZhipuModel} ...");
            tags = await ClassifyWithZhipuAsync(imgBytes, apiKey);
            Console.WriteLine($"Classified tags: {string.Join(",", tags)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: classification failed, fallback to '{UnclassifiedTag}': {ex.Message}");
            tags = new List<string> { UnclassifiedTag };
        }

        AppendWallpaper(ListXmlPath, uhdUrl, tags);
        Console.WriteLine($"Appended to {ListXmlPath} with tags={string.Join(",", tags)}");
        return 0;
    }

    /// <summary>通过必应 API 获取当日壁纸信息，返回 (日期, UHD URL)。</summary>
    private static async Task<(string date, string url)> FetchTodayWallpaperAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, BingApi);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var images = doc.RootElement.GetProperty("images");
        if (images.GetArrayLength() == 0)
            throw new InvalidOperationException("Bing API returned no images");
        var img = images[0];
        var enddate = img.GetProperty("enddate").GetString() ?? "";
        var dateStr = enddate.Length == 8
            ? $"{enddate[0..4]}-{enddate[4..6]}-{enddate[6..8]}"
            : DateTime.Today.ToString("yyyy-MM-dd");
        var url = BingBase + (img.GetProperty("url").GetString() ?? "");
        return (dateStr, StripToJpg(url));
    }

    /// <summary>把 URL 截断到第一个 .jpg（含），删除其后所有参数。</summary>
    private static string StripToJpg(string url)
    {
        var idx = url.IndexOf(".jpg", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? url : url[..(idx + 4)];
    }

    private static async Task<byte[]> DownloadImageBytesAsync(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        req.Headers.Referrer = new Uri("https://www.bing.com/");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync();
    }

    /// <summary>调用智谱 GLM-4V 对图片分类，返回标签列表（仅包含官方标签）。</summary>
    private static async Task<List<string>> ClassifyWithZhipuAsync(byte[] imageBytes, string apiKey)
    {
        var b64 = Convert.ToBase64String(imageBytes);
        var prompt = "请对这张必应每日壁纸进行分类。只能从以下标签中选择一个或多个，" +
                     "用逗号分隔，不要输出其它任何内容：\n" +
                     "精选,人文,风景,节日,动物,植物,海洋,建筑,景点,其他";
        var payload = new
        {
            model = ZhipuModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{b64}" } }
                    }
                }
            },
            temperature = 0.1,
            max_tokens = 64
        };

        var req = new HttpRequestMessage(HttpMethod.Post, ZhipuApiUrl);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(payload);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim() ?? "";

        var tags = new List<string>();
        var seen = new HashSet<string>();
        foreach (var t in Regex.Split(text, @"[,，、\s]+"))
        {
            var s = t.Trim();
            if (OfficialTags.Contains(s) && seen.Add(s))
                tags.Add(s);
        }
        if (tags.Count == 0) tags.Add("其他");
        return tags;
    }

    /// <summary>读取 list.xml 中已有的 url 集合，避免重复追加。</summary>
    private static HashSet<string> LoadExistingUrls(string listPath)
    {
        var urls = new HashSet<string>();
        if (!File.Exists(listPath)) return urls;
        try
        {
            var doc = XDocument.Load(listPath);
            foreach (var wp in doc.Descendants("wallpaper"))
            {
                var u = wp.Element("url")?.Value?.Trim();
                if (!string.IsNullOrEmpty(u)) urls.Add(u);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: parse existing list failed: {ex.Message}");
        }
        return urls;
    }

    /// <summary>向 list.xml 追加一条 wallpaper 记录。</summary>
    private static void AppendWallpaper(string listPath, string url, List<string> tags)
    {
        XDocument doc;
        XElement root;
        if (File.Exists(listPath))
        {
            doc = XDocument.Load(listPath);
            root = doc.Root ?? new XElement("wallpapers");
        }
        else
        {
            root = new XElement("wallpapers");
            doc = new XDocument(root);
        }
        root.Add(new XElement("wallpaper",
            new XElement("url", url),
            new XElement("label", string.Join(",", tags))));
        doc.Save(listPath);
    }
}
