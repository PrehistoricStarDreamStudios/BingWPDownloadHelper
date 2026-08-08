using System;
using System.Runtime.InteropServices;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;

namespace BingPaper
{
    /// <summary>
    /// 管理系统托盘图标、右键菜单与窗口显示/隐藏行为。
    /// 基于 H.NotifyIcon.Core.TrayIcon 直接操作 Win32 托盘图标，
    /// 避免 BitmapImage 异步加载导致图标空白的问题。
    /// </summary>
    public sealed class TrayManager : IDisposable
    {
        private readonly Window _window;
        private IntPtr _windowHandle = IntPtr.Zero;
        private TrayIcon? _trayIcon;
        private IntPtr _hMenu = IntPtr.Zero;
        private bool _disposed;

        // 菜单项命令 ID
        private const int CMD_SHOW = 1;
        private const int CMD_EXIT = 2;

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
                // 获取主窗口句柄
                try
                {
                    _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                }
                catch { }

                // 1. 加载 .ico 文件为 HICON
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
                if (!System.IO.File.Exists(iconPath))
                    iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appicon.ico");
                var hIcon = IntPtr.Zero;
                if (System.IO.File.Exists(iconPath))
                {
                    hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                }

                // 2. 创建 TrayIcon
                _trayIcon = new TrayIcon(new IconData
                {
                    hIcon = hIcon,
                    hCallback = IntPtr.Zero,
                    uCallbackMessage = 0,
                    uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE,
                    szTip = Strings.GetString("TrayTooltip") ?? "Bing Wallpaper",
                    dwState = 0,
                    dwStateMask = 0,
                    uVersion = 0,
                });

                _trayIcon.LeftClick += OnLeftClick;
                _trayIcon.RightClick += OnRightClick;

                _trayIcon.Create();
            }
            catch { }
        }

        private void OnLeftClick(object? sender, EventArgs e)
        {
            try
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
            catch { }
        }

        private void OnRightClick(object? sender, EventArgs e)
        {
            try
            {
                ShowContextMenu();
            }
            catch { }
        }

        private void ShowContextMenu()
        {
            try
            {
                if (_hMenu != IntPtr.Zero)
                {
                    DestroyMenu(_hMenu);
                    _hMenu = IntPtr.Zero;
                }

                _hMenu = CreatePopupMenu();

                var showText = Strings.GetString("TrayShow");
                AppendMenu(_hMenu, MF_STRING, CMD_SHOW, showText);
                AppendMenu(_hMenu, MF_SEPARATOR, 0, null);
                var exitText = Strings.GetString("TrayExit");
                AppendMenu(_hMenu, MF_STRING, CMD_EXIT, exitText);

                // 获取当前鼠标位置
                GetCursorPos(out POINT pt);

                // 设置菜单为前景（支持键盘导航）
                if (_windowHandle != IntPtr.Zero)
                    SetForegroundWindow(_windowHandle);

                // 显示弹出菜单
                var cmd = TrackPopupMenu(_hMenu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_BOTTOMALIGN, pt.X, pt.Y, 0, _windowHandle != IntPtr.Zero ? _windowHandle : GetShellWindow(), IntPtr.Zero);

                if (cmd == CMD_SHOW)
                {
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (cmd == CMD_EXIT)
                {
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch { }
        }

        public void Show()
        {
            try { _trayIcon?.Create(); } catch { }
        }

        public void HideToTray()
        {
            try { _window.Hide(); } catch { }
        }

        public void ShowFromTray()
        {
            try
            {
                _window.Show();
                _window.Activate();
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
            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.LeftClick -= OnLeftClick;
                    _trayIcon.RightClick -= OnRightClick;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
            }
            catch { }
            try
            {
                if (_hMenu != IntPtr.Zero)
                {
                    DestroyMenu(_hMenu);
                    _hMenu = IntPtr.Zero;
                }
            }
            catch { }
        }

        #region Win32 P/Invoke

        private const int IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_RETURNCMD = 0x00000100;
        private const uint TPM_LEFTALIGN = 0x00000000;
        private const uint TPM_BOTTOMALIGN = 0x00002000;
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenu(IntPtr hMenu, uint flags, int id, string? text);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        #endregion
    }
}