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
            LoadIcon();

            // 子类化窗口以接收托盘回调消息
            _subclassProc = WndProc;
            _subclassProcPtr = Marshal.GetFunctionPointerForDelegate(_subclassProc);
            SetWindowSubclass(_windowHandle, _subclassProc, SUBCLASS_ID, 0);

            // 创建托盘图标
            AddIcon();
        }

        private void LoadIcon()
        {
            // 尝试多个路径查找 .ico 文件
            string[] searchPaths = [
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "appicon.ico"),
                System.IO.Path.Combine(Environment.CurrentDirectory, "Assets", "appicon.ico"),
                System.IO.Path.Combine(Environment.CurrentDirectory, "appicon.ico"),
            ];

            string? foundPath = null;
            foreach (var p in searchPaths)
            {
                if (System.IO.File.Exists(p))
                {
                    foundPath = p;
                    break;
                }
            }

            if (foundPath != null)
            {
                _hIcon = LoadImage(IntPtr.Zero, foundPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }

            // 如果 .ico 加载失败，尝试从 appicon.png 加载
            if (_hIcon == IntPtr.Zero)
            {
                string[] pngPaths = [
                    System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.png"),
                    System.IO.Path.Combine(AppContext.BaseDirectory, "appicon.png"),
                ];
                foreach (var p in pngPaths)
                {
                    if (System.IO.File.Exists(p))
                    {
                        // 使用 LoadImage 加载 .png 需要 LR_LOADFROMFILE，但 LoadImage 对 .png 支持有限
                        // 这里仅作为 fallback，实际 .ico 是首选
                        _hIcon = LoadImage(IntPtr.Zero, p, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                        if (_hIcon != IntPtr.Zero) break;
                    }
                }
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

        /// <summary>
        /// 使用手动内存分配构造 NOTIFYICONDATAW 并调用 Shell_NotifyIcon。
        /// 避免 .NET 结构体布局与 Win32 API 不一致导致的 cbSize 不匹配问题。
        /// </summary>
        private void AddIcon()
        {
            if (_iconAdded || _windowHandle == IntPtr.Zero)
                return;

            const int NOTIFYICONDATAW_V3_SIZE = 928; // 0x3A0 - Win10 版本

            // 分配+清零内存
            IntPtr pNid = Marshal.AllocHGlobal(NOTIFYICONDATAW_V3_SIZE);
            try
            {
                var zero = new byte[NOTIFYICONDATAW_V3_SIZE];
                Marshal.Copy(zero, 0, pNid, NOTIFYICONDATAW_V3_SIZE);

                // 手动设置字段（使用 x64 4-byte pack 布局）
                int offset = 0;

                WriteInt32(pNid, offset, NOTIFYICONDATAW_V3_SIZE); offset += 4; // cbSize
                WriteIntPtr(pNid, offset, _windowHandle); offset += 8;          // hWnd
                WriteInt32(pNid, offset, 0); offset += 4;                      // uID
                WriteInt32(pNid, offset, (int)(NIF_ICON | NIF_TIP | NIF_MESSAGE)); offset += 4; // uFlags
                WriteInt32(pNid, offset, (int)_callbackMessage); offset += 4;  // uCallbackMessage
                WriteIntPtr(pNid, offset, _hIcon); offset += 8;                // hIcon

                // szTip[128] - 固定长度 Unicode 字符串
                var tip = (GetString("TrayTooltip") ?? "Bing Wallpaper");
                if (tip.Length > 127) tip = tip[..127];
                offset += WriteFixedString(pNid, offset, tip, 128);

                // dwState / dwStateMask - 跳过（不需要）
                offset += 8;

                // szInfo[256] - 跳过（不需要）
                offset += 512;

                // uVersion - 跳过
                offset += 4;

                // szInfoTitle[64] - 跳过（不需要）
                offset += 128;

                // dwInfoFlags - 跳过
                offset += 4;

                // guidItem - 跳过（不需要，但 V3 需要此字段占位）
                offset += 16;

                // hBalloonIcon - 跳过（不需要）
                offset += 8;

                // 调用 Shell_NotifyIcon
                _iconAdded = Shell_NotifyIcon(NIM_ADD, pNid);

                // 如果 V3 失败，尝试 V2 大小
                if (!_iconAdded)
                {
                    const int NOTIFYICONDATAW_V2_SIZE = 904; // 0x388
                    WriteInt32(pNid, 0, NOTIFYICONDATAW_V2_SIZE); // 只改 cbSize
                    _iconAdded = Shell_NotifyIcon(NIM_ADD, pNid);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pNid);
            }
        }

        private static void WriteInt32(IntPtr ptr, int offset, int value)
        {
            Marshal.WriteInt32(ptr, offset, value);
        }

        private static void WriteIntPtr(IntPtr ptr, int offset, IntPtr value)
        {
            Marshal.WriteIntPtr(ptr, offset, value);
        }

        private static int WriteFixedString(IntPtr ptr, int offset, string value, int maxChars)
        {
            // 将字符串转为字节数组（Unicode/UTF-16LE）
            var chars = value.ToCharArray();
            if (chars.Length > maxChars - 1)
                Array.Resize(ref chars, maxChars - 1);

            var bytes = System.Text.Encoding.Unicode.GetBytes(chars);
            Marshal.Copy(bytes, 0, ptr + offset, bytes.Length);

            // 写入空终止符
            Marshal.WriteInt16(ptr, offset + bytes.Length, 0);

            return maxChars * 2; // 返回写入的字节数
        }

        private void RemoveIcon()
        {
            if (!_iconAdded || _windowHandle == IntPtr.Zero)
                return;

            const int NOTIFYICONDATAW_V3_SIZE = 928;
            IntPtr pNid = Marshal.AllocHGlobal(NOTIFYICONDATAW_V3_SIZE);
            try
            {
                var zero = new byte[NOTIFYICONDATAW_V3_SIZE];
                Marshal.Copy(zero, 0, pNid, NOTIFYICONDATAW_V3_SIZE);
                WriteInt32(pNid, 0, NOTIFYICONDATAW_V3_SIZE);
                WriteIntPtr(pNid, 4, _windowHandle);

                Shell_NotifyIcon(NIM_DELETE, pNid);
            }
            finally
            {
                Marshal.FreeHGlobal(pNid);
            }
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

        // 子类化回调委托
        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, uint dwRefData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, IntPtr lpData);

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