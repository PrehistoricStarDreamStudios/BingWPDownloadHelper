using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using WinRT;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BingPaper
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string exePath = Environment.ProcessPath ?? "";
            string exeDir = Path.GetDirectoryName(exePath) ?? "";
            string logPath = Path.Combine(exeDir, "error.log");

            try
            {
                WriteLog(logPath, "Starting BingPaper...");

                Bootstrap.TryInitialize(0x00020000, out int hresult);
                WriteLog(logPath, $"Bootstrap.TryInitialize returned: hresult=0x{hresult:X8}");

                ComWrappersSupport.InitializeComWrappers();
                WriteLog(logPath, "WinRT.ComWrappersSupport.InitializeComWrappers() called");

                Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    WriteLog(logPath, "Application.Start callback invoked");
                    var syncContext = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(syncContext);
                    WriteLog(logPath, "SynchronizationContext set");
                    
                    try
                    {
                        var app = new App();
                        WriteLog(logPath, "App instance created");
                    }
                    catch (Exception ex)
                    {
                        WriteLog(logPath, $"App creation error: {ex}");
                        throw;
                    }
                    
                    try
                    {
                        var window = new MainWindow();
                        WriteLog(logPath, "MainWindow created");
                        // 检测 --minimized 参数：开机静默启动时不显示窗口，直接最小化到托盘
                        bool startMinimized = args != null && args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
                        if (!startMinimized)
                        {
                            window.Activate();
                            WriteLog(logPath, "Window activated");
                        }
                        else
                        {
                            WriteLog(logPath, "Starting minimized to tray (--minimized)");
                            // 不激活窗口，构造函数内已初始化托盘，窗口保持隐藏
                            try
                            {
                                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                                Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId).Hide();
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteLog(logPath, $"MainWindow creation error: {ex}");
                        WriteLog(logPath, $"Exception type: {ex.GetType().FullName}");
                        WriteLog(logPath, $"Exception message: {ex.Message}");
                        WriteLog(logPath, $"Stack trace: {ex.StackTrace}");
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                WriteLog(logPath, $"Fatal error: {ex}");
                WriteLog(logPath, $"Stack trace: {ex.StackTrace}");
                Console.WriteLine($"Fatal error: {ex}");
                Environment.Exit(1);
            }
        }

        private static void WriteLog(string path, string message)
        {
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Program] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
