using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using System.Reflection;
using Microsoft.Win32;


// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BingPaper
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        private const int SPI_SETDESKWALLPAPER = 20;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDWININICHANGE = 0x02;

        // 使用 IDesktopWallpaper 接口设置壁纸（首选），支持 URL 自动下载并带日志；失败时保留原有回退策略。
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
            void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);
            void GetMonitorDevicePathCount(out uint count);
            void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT displayRect);
            void SetBackgroundColor(uint color);
            void GetBackgroundColor(out uint color);
            void SetPosition(DESKTOP_WALLPAPER_POSITION position);
            void GetPosition(out DESKTOP_WALLPAPER_POSITION position);
            void SetSlideshow([MarshalAs(UnmanagedType.IUnknown)] object items);
            void GetSlideshow(out object items);
            void SetSlideshowOptions(DESKTOP_SLIDESHOW_OPTIONS options, uint slideshowTick);
            void GetSlideshowOptions(out DESKTOP_SLIDESHOW_OPTIONS options, out uint slideshowTick);
            void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, DESKTOP_SLIDESHOW_DIRECTION direction);
            DESKTOP_SLIDESHOW_STATE GetStatus();
            void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem { }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray { }

        private enum DESKTOP_WALLPAPER_POSITION
        {
            DWPOS_CENTER = 0,
            DWPOS_TILE = 1,
            DWPOS_STRETCH = 2,
            DWPOS_FIT = 3,
            DWPOS_FILL = 4,
            DWPOS_SPAN = 5
        }

        private enum DESKTOP_SLIDESHOW_DIRECTION { DSD_FORWARD = 0, DSD_BACKWARD = 1 }
        private enum DESKTOP_SLIDESHOW_STATE { DSS_ENABLED = 0, DSS_SLIDESHOW = 1, DSS_DISABLED = 2 }

        [Flags]
        private enum DESKTOP_SLIDESHOW_OPTIONS : uint { DSSO_SHUFFLE = 0x1 }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SHCreateShellItemArrayFromShellItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem psi,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemArray ppsiItemArray);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        public static bool SetDesktopWallpaper(string imagePath)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "BingPaper_wallpaper.log");
            void Log(string m)
            {
                try { File.AppendAllText(logPath, DateTime.Now.ToString("o") + " " + m + Environment.NewLine); } catch { }
            }

            if (string.IsNullOrEmpty(imagePath))
            {
                Log("invalid path: null or empty");
                return false;
            }

            string localPath = imagePath;
            bool downloadedTemp = false;
            try
            {
                if (imagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Log("Downloading URL: " + imagePath);
                        using (var http = new System.Net.Http.HttpClient())
                        {
                            var bytes = http.GetByteArrayAsync(imagePath).GetAwaiter().GetResult();
                            var ext = Path.GetExtension(new Uri(imagePath).AbsolutePath);
                            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                            var tmp = Path.Combine(Path.GetTempPath(), "BingPaper_" + Guid.NewGuid().ToString() + ext);
                            File.WriteAllBytes(tmp, bytes);
                            localPath = tmp;
                            downloadedTemp = true;
                            Log("Downloaded to: " + localPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Download failed: " + ex.Message);
                        return false;
                    }
                }

                // 使用 IDesktopWallpaper 首选设置壁纸（改用 GetTypedObjectForIUnknown 以确保接口可用）
                try
                {
                    var clsid = new Guid("C2CF3110-460E-4FC1-B9D0-8A0D4F2E2F7A"); // CLSID_DesktopWallpaper
                    var type = Type.GetTypeFromCLSID(clsid);
                    var obj = Activator.CreateInstance(type);
                    if (obj != null)
                    {
                        IntPtr pUnk = IntPtr.Zero;
                        try
                        {
                            pUnk = Marshal.GetIUnknownForObject(obj);
                            var dw = (IDesktopWallpaper)Marshal.GetTypedObjectForIUnknown(pUnk, typeof(IDesktopWallpaper));
                            try
                            {
                                dw.SetWallpaper(null, localPath);
                                dw.SetPosition(DESKTOP_WALLPAPER_POSITION.DWPOS_FILL);
                                Log("IDesktopWallpaper applied: " + localPath);
                                try { Marshal.ReleaseComObject(dw); } catch { }
                                return true;
                            }
                            catch (Exception ex)
                            {
                                Log("IDesktopWallpaper invoke exception: " + ex.Message);
                                try { Marshal.ReleaseComObject(dw); } catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log("IDesktopWallpaper exception: " + ex.Message);
                        }
                        finally
                        {
                            if (pUnk != IntPtr.Zero) try { Marshal.Release(pUnk); } catch { }
                            try { Marshal.ReleaseComObject(obj); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("IDesktopWallpaper outer exception: " + ex.Message);
                }

                // 回退：SystemParametersInfo (保留)
                try
                {
                    int res = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, localPath, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
                    Log($"SPI_SETDESKWALLPAPER result={res}, lastError={Marshal.GetLastWin32Error()}");
                    if (res != 0) return true;
                }
                catch (Exception ex)
                {
                    Log("SPI exception: " + ex.Message);
                }

                // 继续回退：写注册表并再试一次
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop", true))
                    {
                        if (key != null)
                        {
                            key.SetValue("Wallpaper", localPath);
                            key.SetValue("WallpaperStyle", "2");
                            key.SetValue("TileWallpaper", "0");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Registry write failed: " + ex.Message);
                }

                try
                {
                    int res2 = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, localPath, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
                    Log($"Registry+SPI result={res2}, lastError={Marshal.GetLastWin32Error()}");
                    if (res2 != 0) return true;
                }
                catch (Exception ex)
                {
                    Log("Registry+SPI exception: " + ex.Message);
                }

                // 最后回退 ActiveDesktop
                try
                {
                    var clsidActive = new Guid("75048700-EF1F-11D0-9888-006097DEACF9");
                    var typeA = Type.GetTypeFromCLSID(clsidActive);
                    var objA = Activator.CreateInstance(typeA);
                    if (objA != null)
                    {
                        try
                        {
                            typeA.InvokeMember("SetWallpaper", BindingFlags.InvokeMethod, null, objA, new object[] { localPath, 0 });
                            typeA.InvokeMember("ApplyChanges", BindingFlags.InvokeMethod, null, objA, new object[] { 1 });
                            Log("ActiveDesktop.SetWallpaper applied");
                            Marshal.ReleaseComObject(objA);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Log("ActiveDesktop invoke exception: " + ex.Message);
                            try { Marshal.ReleaseComObject(objA); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("ActiveDesktop exception: " + ex.Message);
                }

                Log("All methods failed");
                return false;
            }
            finally
            {
                // 不删除下载的临时文件，保证系统可以读取；若希望删除可在此处实现
            }
        }

        public static bool SetDesktopSlideshow(string folderPath, uint intervalMs, bool shuffle, string position)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "BingPaper_slideshow.log");
            void Log(string m)
            {
                try { File.AppendAllText(logPath, DateTime.Now.ToString("o") + " " + m + Environment.NewLine); } catch { }
            }

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                Log("invalid folder: " + folderPath);
                return false;
            }

            // 验证目录中是否有图片文件
            try
            {
                var hasImages = Directory.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Any(f => { var ext = Path.GetExtension(f).ToLowerInvariant(); return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp"; });
                if (!hasImages)
                {
                    Log("no image files found in: " + folderPath);
                    return false;
                }
            }
            catch (Exception ex) { Log("folder check exception: " + ex.Message); }

            try
            {
                // 从文件夹路径创建 IShellItem
                SHCreateItemFromParsingName(folderPath, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem shellItem);
                Log("IShellItem created from: " + folderPath);

                // 从 IShellItem 创建 IShellItemArray
                SHCreateShellItemArrayFromShellItem(shellItem, typeof(IShellItemArray).GUID, out IShellItemArray itemArray);
                Log("IShellItemArray created");

                // 创建 IDesktopWallpaper COM 对象
                var clsid = new Guid("C2CF3110-460E-4FC1-B9D0-8A0D4F2E2F7A");
                var type = Type.GetTypeFromCLSID(clsid);
                var obj = Activator.CreateInstance(type);
                if (obj == null) { Log("Failed to create DesktopWallpaper COM object"); return false; }

                IntPtr pUnk = IntPtr.Zero;
                try
                {
                    pUnk = Marshal.GetIUnknownForObject(obj);
                    var dw = (IDesktopWallpaper)Marshal.GetTypedObjectForIUnknown(pUnk, typeof(IDesktopWallpaper));
                    try
                    {
                        // 设置幻灯片源
                        dw.SetSlideshow(itemArray);
                        Log("SetSlideshow called");

                        // 设置幻灯片选项（乱序 + 间隔）
                        var options = shuffle ? DESKTOP_SLIDESHOW_OPTIONS.DSSO_SHUFFLE : 0;
                        // 最小间隔为 1 秒（1000ms）
                        uint tick = Math.Max(1000, intervalMs);
                        dw.SetSlideshowOptions(options, tick);
                        Log("SetSlideshowOptions called: shuffle=" + shuffle + ", interval=" + tick + "ms");

                        // 设置填充方式
                        DESKTOP_WALLPAPER_POSITION pos;
                        switch (position?.ToLowerInvariant())
                        {
                            case "center": pos = DESKTOP_WALLPAPER_POSITION.DWPOS_CENTER; break;
                            case "tile": pos = DESKTOP_WALLPAPER_POSITION.DWPOS_TILE; break;
                            case "stretch": pos = DESKTOP_WALLPAPER_POSITION.DWPOS_STRETCH; break;
                            case "fit": pos = DESKTOP_WALLPAPER_POSITION.DWPOS_FIT; break;
                            case "span": pos = DESKTOP_WALLPAPER_POSITION.DWPOS_SPAN; break;
                            default: pos = DESKTOP_WALLPAPER_POSITION.DWPOS_FILL; break;
                        }
                        dw.SetPosition(pos);
                        Log("SetPosition called: " + pos);

                        // 同时通过注册表设置间隔（确保某些系统版本生效）
                        try
                        {
                            using var key = Registry.CurrentUser.CreateSubKey("Control Panel\\Personalization\\Desktop Slideshow", true);
                            if (key != null)
                            {
                                key.SetValue("Interval", tick, RegistryValueKind.DWord);
                                key.SetValue("Shuffle", shuffle ? 1 : 0, RegistryValueKind.DWord);
                            }
                        }
                        catch (Exception ex) { Log("Registry interval write: " + ex.Message); }

                        return true;
                    }
                    catch (Exception ex) { Log("IDesktopWallpaper invoke exception: " + ex.Message); }
                    finally { try { Marshal.ReleaseComObject(dw); } catch { } }
                }
                catch (Exception ex) { Log("IDesktopWallpaper exception: " + ex.Message); }
                finally
                {
                    if (pUnk != IntPtr.Zero) try { Marshal.Release(pUnk); } catch { }
                    try { Marshal.ReleaseComObject(obj); } catch { }
                }
            }
            catch (Exception ex) { Log("Outer exception: " + ex.Message); }

            return false;
        }
    }
}
