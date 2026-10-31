using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTool
{
    static class IconFactory
    {
        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);

        /// <summary>
        /// 托盘图标直接从 exe 自己的图标资源里取（就是编译时 ApplicationIcon 写进去的那份），
        /// 和文件图标、任务栏图标完全同源，不用再内嵌一份 ico（那样 exe 会白白多 18KB）。
        /// </summary>
        public static Icon AppIcon()
        {
            try
            {
                string exe = Application.ExecutablePath;
                if (ExtractIconEx(exe, -1, null, null, 0) > 0)
                {
                    var large = new IntPtr[1];
                    var small = new IntPtr[1];
                    if (ExtractIconEx(exe, 0, large, small, 1) > 0)
                    {
                        IntPtr h = small[0] != IntPtr.Zero ? small[0] : large[0];
                        IntPtr extra = small[0] != IntPtr.Zero ? large[0] : IntPtr.Zero;
                        try
                        {
                            if (h != IntPtr.Zero) return (Icon)Icon.FromHandle(h).Clone();
                        }
                        finally
                        {
                            if (h != IntPtr.Zero) Native.DestroyIcon(h);
                            if (extra != IntPtr.Zero) Native.DestroyIcon(extra);
                        }
                    }
                }
            }
            catch { }
            return DrawAppIcon(SystemInformation.SmallIconSize.Width);
        }

        /// <summary>兜底：万一资源读不到，按同一套设计现画一个。</summary>
        public static Icon DrawAppIcon(int px)
        {
            if (px < 8) px = 8;
            using (var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    float k = px / 32f;
                    var box = new Rectangle(0, 0, px, px);
                    if (px > 16) box = new Rectangle(1, 1, px - 2, px - 2);
                    using (var path = Rounded(box, (int)Math.Round(8 * k)))
                    using (var br = new LinearGradientBrush(box, Color.FromArgb(72, 176, 255), Color.FromArgb(12, 108, 226), 55f))
                        g.FillPath(br, path);

                    using (var pen = new Pen(Color.White, Math.Max(1.4f, 2.3f * k)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        float m = 8 * k, len = 6 * k, e = px - m;
                        // 四个取景角
                        g.DrawLine(pen, m, m + len, m, m);
                        g.DrawLine(pen, m, m, m + len, m);
                        g.DrawLine(pen, e - len, m, e, m);
                        g.DrawLine(pen, e, m, e, m + len);
                        g.DrawLine(pen, m, e - len, m, e);
                        g.DrawLine(pen, m, e, m + len, e);
                        g.DrawLine(pen, e - len, e, e, e);
                        g.DrawLine(pen, e, e, e, e - len);
                    }
                    // 中心圆点（和 app.ico 一致）
                    if (px >= 16)
                    {
                        float r = 3.6f * k;
                        using (var br = new SolidBrush(Color.White))
                            g.FillEllipse(br, px / 2f - r, px / 2f - r, r * 2, r * 2);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "SnapTool";

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static bool Set(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>一个只用来接收 WM_HOTKEY 的隐藏消息窗口。</summary>
    class HotkeyWindow : NativeWindow, IDisposable
    {
        const int HotkeyId = 0xB001;
        public event Action Pressed;
        bool _registered;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register(string spec)
        {
            Unregister();
            int mods, vk;
            if (!Config.TryParse(spec, out mods, out vk)) return false;
            try
            {
                _registered = Native.RegisterHotKey(Handle, HotkeyId, mods | Native.MOD_NOREPEAT, vk);
            }
            catch { _registered = false; }
            return _registered;
        }

        public void Unregister()
        {
            if (!_registered) return;
            try { Native.UnregisterHotKey(Handle, HotkeyId); } catch { }
            _registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt64() == HotkeyId)
            {
                try { if (Pressed != null) Pressed(); } catch { }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            try { DestroyHandle(); } catch { }
        }
    }
}
