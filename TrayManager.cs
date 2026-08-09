using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace BingPaper
{
    /// <summary>
    /// 管理系统托盘图标、右键菜单与窗口显示/隐藏行为。
    /// 使用 Win32 Shell_NotifyIcon API，采用默认对齐（x64: 8字节）与 Win32 原生布局一致。
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

        private SUBCLASSPROC? _subclassProc;
        private const uint SUBCLASS_ID = 1001;

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

            _callbackMessage = RegisterWindowMessage("BingPaper_TrayCallback_" + Guid.NewGuid().ToString("N"));

            LoadAppIcon();

            _subclassProc = WndProc;
            SetWindowSubclass(_windowHandle, _subclassProc, SUBCLASS_ID, 0);

            AddIcon();
        }

        private void LoadAppIcon()
        {
            string[] searchPaths = [
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "appicon.ico"),
                System.IO.Path.Combine(Environment.CurrentDirectory, "Assets", "appicon.ico"),
                System.IO.Path.Combine(Environment.CurrentDirectory, "appicon.ico"),
            ];

            foreach (var p in searchPaths)
            {
                if (System.IO.File.Exists(p))
                {
                    _hIcon = LoadImage(IntPtr.Zero, p, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                    if (_hIcon != IntPtr.Zero) break;
                }
            }

            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
            }
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

            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512);
            }

            if (_hIcon == IntPtr.Zero)
                return;

            var tip = (GetString("TrayTooltip") ?? "Bing Wallpaper");
            if (tip.Length > 127) tip = tip[..127];

            var nid = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _windowHandle,
                uID = 0,
                uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE,
                uCallbackMessage = (int)_callbackMessage,
                hIcon = _hIcon,
                szTip = tip,
                dwState = 0,
                dwStateMask = 0,
                szInfo = "",
                uVersion = 0,
                szInfoTitle = "",
                dwInfoFlags = 0,
                guidItem = Guid.Empty,
                hBalloonIcon = IntPtr.Zero
            };

            _iconAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
        }

        private void RemoveIcon()
        {
            if (!_iconAdded || _windowHandle == IntPtr.Zero)
                return;

            var nid = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _windowHandle,
                uID = 0
            };

            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _iconAdded = false;
        }

        /// <summary>
        /// 确保托盘图标已初始化。在窗口 Loaded 后调用，以防构造时窗口句柄尚未就绪。
        /// </summary>
        public void EnsureInitialized()
        {
            if (_windowHandle != IntPtr.Zero && _iconAdded)
                return;

            try
            {
                if (_windowHandle == IntPtr.Zero)
                    _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            }
            catch { }

            if (_windowHandle == IntPtr.Zero)
                return;

            if (_callbackMessage == 0)
                _callbackMessage = RegisterWindowMessage("BingPaper_TrayCallback_" + Guid.NewGuid().ToString("N"));

            if (_hIcon == IntPtr.Zero)
                LoadAppIcon();

            if (_subclassProc == null)
            {
                _subclassProc = WndProc;
                SetWindowSubclass(_windowHandle, _subclassProc, SUBCLASS_ID, 0);
            }

            AddIcon();
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

            if (_windowHandle != IntPtr.Zero)
            {
                try { RemoveWindowSubclass(_windowHandle, _subclassProc, SUBCLASS_ID); }
                catch { }
            }
        }

        private static string? GetString(string key)
        {
            try { return Strings.GetString(key); }
            catch { return null; }
        }

        #region Win32 Structs & P/Invoke

        // NOTIFYICONDATAW 布局使用默认对齐（x64: 8字节），与 Win32 API 一致
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

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

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, uint dwRefData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

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

        [DllImport("comctl32.dll")]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, uint dwRefData);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("comctl32.dll")]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

        #endregion
    }
}