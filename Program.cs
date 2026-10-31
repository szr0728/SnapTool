using System;
using System.Threading;
using System.Windows.Forms;

namespace SnapTool
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\SnapTool_SingleInstance_v1", out created))
            {
                if (!created) return;

                Native.EnableDpiAwareness();
#if NET8_0_OR_GREATER
                // net48 没有这个 API；DPI 感知已由 app.manifest + EnableDpiAwareness() 设置
                try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
#endif
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.ThreadException += (s, e) =>
                {
                    try { MessageBox.Show(e.Exception.Message, "SnapTool", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
                };

                Application.Run(new TrayApp());
            }
        }
    }
}
