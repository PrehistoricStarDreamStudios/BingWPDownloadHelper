using System;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace BingPaper
{
    /// <summary>
    /// 提供运行时多语言字符串访问。
    /// 资源来自嵌入的 Strings\Strings.{culture}.resx。
    /// </summary>
    public static class Strings
    {
        private static readonly ResourceManager _rm =
            new ResourceManager("BingPaper.Strings.Strings", typeof(Strings).GetTypeInfo().Assembly);

        private static CultureInfo _culture = CultureInfo.CurrentUICulture;

        /// <summary>
        /// 当前应用使用的语言文化。设置后所有后续 GetString 调用使用新文化。
        /// </summary>
        public static CultureInfo Culture
        {
            get => _culture;
            set
            {
                if (value == null) value = CultureInfo.CurrentUICulture;
                _culture = value;
                CultureInfo.DefaultThreadCurrentUICulture = value;
                CultureInfo.DefaultThreadCurrentCulture = value;
            }
        }

        /// <summary>
        /// 获取指定 key 的本地化字符串。找不到则返回 key 本身。
        /// </summary>
        public static string GetString(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                var v = _rm.GetString(key, _culture);
                return string.IsNullOrEmpty(v) ? key : v;
            }
            catch
            {
                return key;
            }
        }

        /// <summary>
        /// 获取指定 key 的本地化字符串，使用当前文化。
        /// </summary>
        public static string S(string key) => GetString(key);
    }
}
