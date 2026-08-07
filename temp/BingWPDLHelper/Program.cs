using System;
using System.IO;
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
            string exeDir = Path.GetDirectoryName(exePath);
            string logPath = Path.Combine(exeDir ?? "", "error.log");

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

                        // 初始化托盘系统（必须在窗口 Activate 之前完成，以便启用托盘右键菜单）
                        try
                        {
                            window.InitTray();
                            WriteLog(logPath, "Tray initialized");
                        }
                        catch (Exception exTray)
                        {
                            WriteLog(logPath, $"Tray init error: {exTray.Message}");
                        }

                        // 检查启动参数 --minimized：若开启则隐藏窗口到托盘（开机自启默认行为）
                        bool minimized = false;
                        try
                        {
                            minimized = window.ShouldStartMinimized();
                            WriteLog(logPath, $"ShouldStartMinimized={minimized}");
                        }
                        catch { }

                        if (minimized)
                        {
                            // 仅激活窗口（WinUI 要求激活后才能调用 Hide/Show），然后立即隐藏到托盘
                            window.Activate();
                            try { window.RestoreFromTrayHideOnly(); } catch { }
                            WriteLog(logPath, "Window activated then hidden to tray");
                        }
                        else
                        {
                            window.Activate();
                            WriteLog(logPath, "Window activated");
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
