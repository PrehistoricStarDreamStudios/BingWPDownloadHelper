using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace BingPaper
{
    /// <summary>
    /// 管理系统托盘图标、右键菜单与窗口显示/隐藏行为。
    /// 直接使用 Win32 Shell_NotifyIcon API，避免 H.NotifyIcon.Core 的 API 兼容问题。
    /// </summary>
    public sealed class TrayManager : IDisposable
    {
        private readonly Window _window;
        private IntPtr _windowHandle = IntPtr.Zero;
        private bool _iconAdded;
        private IntPtr _hMenu = IntPtr.Zero;
        private bool _disposed;
        private uint _callbackMessage;
        private IntPtr _hIcon = IntPtr.Zero;

        // 窗口子类化回调
        private SUBCLASSPROC? _subclassProc;
        private IntPtr _subclassProcPtr = IntPtr.Zero;
        private const uint SUBCLASS_ID = 1001;

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
                _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            }
            catch { }

            if (_windowHandle == IntPtr.Zero)
                return;

            // 注册唯一回调消息
            _callbackMessage = RegisterWindowMessage("BingPaper_TrayCallback_" + Guid.NewGuid().ToString("N"));

            // 加载 .ico 图标
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            if (!System.IO.File.Exists(iconPath))
                iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appicon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }

            // 子类化窗口以接收托盘回调消息
            _subclassProc = WndProc;
            _subclassProcPtr = Marshal.GetFunctionPointerForDelegate(_subclassProc);
            SetWindowSubclass(_windowHandle, _subclassProc, SUBCLASS_ID, 0);

            // 创建托盘图标
            AddIcon();
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, uint dwRefData)
        {
            if (msg == _callbackMessage)
            {
                switch ((int)lParam)
                {
                    case WM_LBUTTONUP:
                        ShowRequested?.Invoke(this, EventArgs.Empty);
                        return IntPtr.Zero;
                    case WM_RBUTTONUP:
                        ShowContextMenu();
                        return IntPtr.Zero;
                }
            }
            else if (msg == WM_DESTROY)
            {
                RemoveIcon();
            }
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        private void AddIcon()
        {
            if (_iconAdded || _windowHandle == IntPtr.Zero)
                return;

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = 0,
                uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE,
                hIcon = _hIcon,
                uCallbackMessage = _callbackMessage,
                szTip = (GetString("TrayTooltip") ?? "Bing Wallpaper"),
            };
            if (nid.szTip.Length > 127)
                nid.szTip = nid.szTip[..127];

            _iconAdded = Shell_NotifyIcon(NIM_ADD, ref nid);

            // 如果失败，尝试 V2 结构体大小
            if (!_iconAdded)
            {
                nid.cbSize = NOTIFYICONDATA_V2_SIZE;
                _iconAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
            }
        }

        private void RemoveIcon()
        {
            if (!_iconAdded || _windowHandle == IntPtr.Zero)
                return;

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = 0,
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _iconAdded = false;
        }

        public void ShowContextMenu()
        {
            try
            {
                if (_hMenu != IntPtr.Zero)
                {
                    DestroyMenu(_hMenu);
                    _hMenu = IntPtr.Zero;
                }

                _hMenu = CreatePopupMenu();

                AppendMenu(_hMenu, MF_STRING, CMD_SHOW, GetString("TrayShow") ?? "Show");
                AppendMenu(_hMenu, MF_SEPARATOR, 0, null);
                AppendMenu(_hMenu, MF_STRING, CMD_EXIT, GetString("TrayExit") ?? "Exit");

                GetCursorPos(out POINT pt);

                if (_windowHandle != IntPtr.Zero)
                    SetForegroundWindow(_windowHandle);

                var cmd = TrackPopupMenu(_hMenu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_BOTTOMALIGN,
                    pt.X, pt.Y, 0, _windowHandle, IntPtr.Zero);

                if (cmd == CMD_SHOW)
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                else if (cmd == CMD_EXIT)
                    ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            catch { }
        }

        public void Show()
        {
            AddIcon();
        }

        public void HideToTray()
        {
            if (_windowHandle != IntPtr.Zero)
                ShowWindow(_windowHandle, SW_HIDE);
        }

        public void ShowFromTray()
        {
            if (_windowHandle != IntPtr.Zero)
            {
                ShowWindow(_windowHandle, SW_SHOW);
                ShowWindow(_windowHandle, SW_RESTORE);
                SetForegroundWindow(_windowHandle);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RemoveIcon();

            if (_hMenu != IntPtr.Zero)
            {
                DestroyMenu(_hMenu);
                _hMenu = IntPtr.Zero;
            }

            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }

            if (_subclassProcPtr != IntPtr.Zero && _windowHandle != IntPtr.Zero)
            {
                try
                {
                    RemoveWindowSubclass(_windowHandle, _subclassProc, SUBCLASS_ID);
                }
                catch { }
            }
        }

        private static string? GetString(string key)
        {
            try { return Strings.GetString(key); }
            catch { return null; }
        }

        #region Win32 P/Invoke

        private const int IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_MESSAGE = 0x00000001;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_DELETE = 0x00000002;

        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_RETURNCMD = 0x00000100;
        private const uint TPM_LEFTALIGN = 0x00000000;
        private const uint TPM_BOTTOMALIGN = 0x00002000;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_DESTROY = 0x0002;

        private const uint NOTIFYICONDATA_V2_SIZE = 904;

        // Win10 下 Shell_NotifyIcon 使用的 NOTIFYICONDATAW 结构体
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        // 子类化回调委托
        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, uint dwRefData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        // 窗口子类化（Comctl32.dll v6，WinUI3 已初始化）
        [DllImport("comctl32.dll")]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, uint dwRefData);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("comctl32.dll")]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        #endregion
    }
}