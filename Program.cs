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
