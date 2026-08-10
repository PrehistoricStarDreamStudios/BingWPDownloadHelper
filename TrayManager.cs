using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BingPaper
{
    /// <summary>
    /// 管理系统托盘图标、右键菜单（WinUI3 MenuFlyout）与窗口显示/隐藏行为。
    /// 托盘图标使用 Win32 Shell_NotifyIcon API，右键菜单使用 WinUI3 原生 MenuFlyout。
    /// </summary>
    public sealed class TrayManager : IDisposable
    {
        private readonly Window _window;
        private IntPtr _windowHandle = IntPtr.Zero;
        private bool _iconAdded;
        private bool _disposed;
        private uint _callbackMessage;
        private IntPtr _hIcon = IntPtr.Zero;

        private SUBCLASSPROC? _subclassProc;
        private const uint SUBCLASS_ID = 1001;

        private MenuFlyout? _menuFlyout;
        private bool _hideWindowAfterMenuClose = false;

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
                uFlags = (int)(NIF_ICON | NIF_TIP | NIF_MESSAGE),
                uCallbackMessage = (int)_callbackMessage,
                hIcon = _hIcon,
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

        /// <summary>
        /// 显示 WinUI3 MenuFlyout 托盘右键菜单。
        /// </summary>
        public void ShowContextMenu()
        {
            try
            {
                _window.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        GetCursorPos(out POINT cursorPt);

                        // 用 ClientToScreen 获取客户区原点的屏幕坐标，再反推相对坐标
                        // 比 ScreenToClient 更可靠（窗口隐藏/最小化时也能正确计算）
                        var clientOrigin = new POINT { X = 0, Y = 0 };
                        ClientToScreen(_windowHandle, ref clientOrigin);

                        var dpi = GetDpiForWindow(_windowHandle);
                        var scale = dpi / 96.0;
                        var x = (cursorPt.X - clientOrigin.X) / scale;
                        var y = (cursorPt.Y - clientOrigin.Y) / scale;

                        // 确保菜单已创建
                        EnsureMenuCreated();

                        // 如果窗口隐藏，临时显示以承载 MenuFlyout
                        var wasHidden = IsWindowVisible(_windowHandle) == 0;
                        if (wasHidden)
                        {
                            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
                        }
                        _hideWindowAfterMenuClose = wasHidden;

                        var target = _window.Content as Microsoft.UI.Xaml.FrameworkElement;
                        if (target != null)
                        {
                            if (target.ActualWidth > 0)
                                _menuFlyout.ShowAt(target, new Windows.Foundation.Point(x, y));
                            else
                                _menuFlyout.ShowAt(target, new Windows.Foundation.Point(0, 0));
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void EnsureMenuCreated()
        {
            if (_menuFlyout != null) return;

            _menuFlyout = new MenuFlyout();

            var showItem = new MenuFlyoutItem
            {
                Text = GetString("TrayShow") ?? "显示软件"
            };
            showItem.Click += (s, e) => ShowRequested?.Invoke(this, EventArgs.Empty);
            _menuFlyout.Items.Add(showItem);

            _menuFlyout.Items.Add(new MenuFlyoutSeparator());

            var exitItem = new MenuFlyoutItem
            {
                Text = GetString("TrayExit") ?? "关闭软件"
            };
            exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
            _menuFlyout.Items.Add(exitItem);

            // 只注册一次 Closed 事件：菜单关闭后按需隐藏窗口
            _menuFlyout.Closed += (s, e) =>
            {
                if (_hideWindowAfterMenuClose)
                {
                    _hideWindowAfterMenuClose = false;
                    try { ShowWindow(_windowHandle, SW_HIDE); } catch { }
                }
            };
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

            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }

            if (_windowHandle != IntPtr.Zero && _subclassProc != null)
            {
                try { RemoveWindowSubclass(_windowHandle, _subclassProc, SUBCLASS_ID); }
                catch { }
            }
        }

        private static string GetString(string key)
        {
            try { return Strings.GetString(key); }
            catch { return key; }
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

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const int SW_SHOWNOACTIVATE = 4;

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
        private static extern bool GetCursorPos(out POINT pt);

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

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern int IsWindowVisible(IntPtr hWnd);

        #endregion
    }
}
