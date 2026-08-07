using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using WinRT;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BingPaper
{
    class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

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

                if (hresult != 0)
                {
                    var msg = $"Windows App SDK 初始化失败 (hresult=0x{hresult:X8})。\n请确保已安装 Windows App SDK 运行时。";
                    WriteLog(logPath, msg);
                    MessageBox(IntPtr.Zero, msg, "BingPaper 启动错误", 0x10);
                    return;
                }

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
                        MessageBox(IntPtr.Zero, $"应用初始化失败:\n{ex.Message}", "BingPaper 错误", 0x10);
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                WriteLog(logPath, $"Fatal error: {ex}");
                WriteLog(logPath, $"Stack trace: {ex.StackTrace}");
                MessageBox(IntPtr.Zero, $"致命错误:\n{ex}", "BingPaper 错误", 0x10);
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
