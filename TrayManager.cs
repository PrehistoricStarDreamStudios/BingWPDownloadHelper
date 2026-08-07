using System;
using System.IO;
using System.Runtime.InteropServices;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BingPaper
{
    /// <summary>
    /// 管理系统托盘图标、右键菜单与窗口显示/隐藏行为。
    /// 基于 H.NotifyIcon.WinUI。
    /// </summary>
    public sealed class TrayManager : IDisposable
    {
        private readonly Window _window;
        private TaskbarIcon? _trayIcon;
        private MenuFlyout? _menu;
        private bool _disposed;

        public event EventHandler? ShowRequested;
        public event EventHandler? ExitRequested;

        public TrayManager(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                _trayIcon = new TaskbarIcon
                {
                    ToolTipText = Strings.GetString("TrayTooltip") ?? "Bing Wallpaper",
                    NoLeftClickDelay = true,
                    // 左键单击时执行 ShowRequested；右键单击时由 ContextFlyout 自动弹出菜单
                    MenuActivation = PopupActivationMode.RightClick,
                    // 使用第二窗口渲染 WinUI3 风格菜单（Fluent Design）
                    ContextMenuMode = H.NotifyIcon.ContextMenuMode.SecondWindow,
                };

                // 设置图标：H.NotifyIcon.WinUI 在 unpackaged 模式下用 BitmapImage 加载 .ico
                // 会触发 ToIconAsync → System.Drawing.Icon(stream) 异步抛 "picture must be a picture
                // that can be used as a Icon"，且该异常无法被 try-catch 捕获（在 OnIconSourceChanged
                // 的异步回调中抛出）。因此这里不设置 IconSource，托盘将显示默认图标，避免启动崩溃。
                // 如需自定义图标，后续可改用 GeneratedIconSource 或 Win32 直接注入 hIcon。

                // 构建右键菜单（使用 ContextFlyout，WinUI3 标准属性）
                _menu = new MenuFlyout();
                var showItem = new MenuFlyoutItem { Text = Strings.GetString("TrayShow") };
                showItem.Click += (s, e) => ShowRequested?.Invoke(this, EventArgs.Empty);
                _menu.Items.Add(showItem);

                var sep = new MenuFlyoutSeparator();
                _menu.Items.Add(sep);

                var exitItem = new MenuFlyoutItem { Text = Strings.GetString("TrayExit") };
                exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
                _menu.Items.Add(exitItem);

                _trayIcon.ContextFlyout = _menu;

                // 左键单击显示窗口
                _trayIcon.LeftClickCommand = new RelayCommand(() =>
                {
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                });

                _trayIcon.ForceCreate();
            }
            catch { }
        }

        /// <summary>
        /// 显示托盘图标（如果之前隐藏）。
        /// </summary>
        public void Show()
        {
            try { _trayIcon?.ForceCreate(); } catch { }
        }

        /// <summary>
        /// 隐藏窗口到托盘（不退出）。
        /// </summary>
        public void HideToTray()
        {
            try { _window.Hide(); } catch { }
        }

        /// <summary>
        /// 显示窗口（从托盘恢复）。
        /// </summary>
        public void ShowFromTray()
        {
            try
            {
                _window.Show();
                _window.Activate();
                // 把窗口带到前台
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                catch { }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _trayIcon?.Dispose(); } catch { }
        }

        #region Win32 helpers
        private const int SW_RESTORE = 9;
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        #endregion
    }

    /// <summary>
    /// 简单的 ICommand 实现，供托盘左键命令使用。
    /// </summary>
    public sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public RelayCommand(Action action) { _action = action; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
