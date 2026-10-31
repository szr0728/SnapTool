using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SnapTool
{
    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _tray;
        readonly HotkeyWindow _hk;
        readonly ClipboardWatcher _clip;
        readonly Config _cfg;
        OverlayForm _overlay;
        ToolStripMenuItem _miNotify;
        ToolStripMenuItem _miAuto;
        ToolStripMenuItem _miDir;
        ToolStripMenuItem _miFormula;
        bool _clipBusy;

        public TrayApp()
        {
            _cfg = Config.Load();

            _tray = new NotifyIcon();
            _tray.Icon = IconFactory.AppIcon();
            // 悬停托盘就能看到版本，不用点开菜单
            _tray.Text = "SnapTool " + AppInfo.Version + (AppInfo.IsBeta ? " 测试版" : "")
                        + "  (" + _cfg.HotkeyText + " 截图)";
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => StartCapture();

            BuildMenu();

            _hk = new HotkeyWindow();
            _hk.Pressed += StartCapture;
            if (!_hk.Register(_cfg.hotkey))
                Notify("热键 " + _cfg.HotkeyText + " 注册失败，可能已被别的程序占用", true);

            _clip = new ClipboardWatcher();
            _clip.Changed += OnClipboardChanged;
        }

        // ---------------- 复制时自动去掉公式定界符 ----------------

        void OnClipboardChanged()
        {
            if (!_cfg.formula_fix || _clipBusy) return;

            string text = ReadClipText();
            if (text == null) return;

            string fixedText = FormulaFix.Unwrap(text);
            if (fixedText == null || fixedText == text) return;

            if (WriteClipText(fixedText)) Notify("已去掉公式定界符");
        }

        static string ReadClipText()
        {
            // 剪贴板可能正被别的程序占用，短暂重试几次
            for (int i = 0; i < 6; i++)
            {
                try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
                catch { System.Threading.Thread.Sleep(15); }
            }
            return null;
        }

        bool WriteClipText(string s)
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    _clipBusy = true;
                    Clipboard.SetText(s);
                    return true;
                }
                catch { System.Threading.Thread.Sleep(15); }
                finally { _clipBusy = false; }
            }
            return false;
        }

        void BuildMenu()
        {
            var cap = new ToolStripMenuItem("截图  (" + _cfg.HotkeyText + ")");
            cap.Font = new Font(cap.Font, FontStyle.Bold);
            cap.Click += (s, e) => StartCapture();

            _miDir = new ToolStripMenuItem("保存目录…");
            _miDir.Click += (s, e) => ChooseDir();
            UpdateDirLabel();

            _miFormula = new ToolStripMenuItem("复制时去掉公式定界符 \\( \\)");
            _miFormula.Checked = _cfg.formula_fix;
            _miFormula.Click += (s, e) =>
            {
                _cfg.formula_fix = !_cfg.formula_fix;
                _miFormula.Checked = _cfg.formula_fix;
                _cfg.Save();
                Notify(_cfg.formula_fix ? "已开启：复制后自动去掉公式定界符" : "已关闭公式定界符清理", true);
            };

            _miNotify = new ToolStripMenuItem("桌面通知提示");
            _miNotify.Checked = _cfg.notify;
            _miNotify.Click += (s, e) =>
            {
                _cfg.notify = !_cfg.notify;
                _miNotify.Checked = _cfg.notify;
                _cfg.Save();
                if (_cfg.notify) Notify("桌面通知已开启", true);
            };

            _miAuto = new ToolStripMenuItem("开机自启动");
            _miAuto.Checked = Autostart.IsEnabled();
            _miAuto.Click += (s, e) =>
            {
                bool want = !_miAuto.Checked;
                if (Autostart.Set(want))
                {
                    _miAuto.Checked = want;
                    Notify(want ? "已开启开机自启动" : "已关闭开机自启动", true);
                }
                else Notify("设置失败，请以普通用户身份运行", true);
            };

            var exit = new ToolStripMenuItem("退出");
            exit.Click += (s, e) => ExitApp();

            var menu = new ContextMenuStrip();

            // 版本行：灰字、不可点，只用来分辨手上这份 exe 是哪次编译的
            var ver = new ToolStripMenuItem("SnapTool  " + AppInfo.Display);
            ver.Enabled = false;
            menu.Items.Add(ver);

            menu.Items.Add(cap);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miDir);
            menu.Items.Add(_miFormula);
            menu.Items.Add(_miNotify);
            menu.Items.Add(_miAuto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            _tray.ContextMenuStrip = menu;
        }

        void UpdateDirLabel()
        {
            string d = _cfg.save_dir ?? "";
            if (d.Length > 42) d = "…" + d.Substring(d.Length - 41);
            _miDir.Text = "保存目录：" + d;
        }

        void ChooseDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择截图默认保存目录";
                dlg.SelectedPath = _cfg.save_dir;
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _cfg.save_dir = dlg.SelectedPath;
                    _cfg.Save();
                    UpdateDirLabel();
                    Notify("保存目录已修改", true);
                }
            }
        }

        void StartCapture()
        {
            if (_overlay != null) return;
            try
            {
                var virt = Native.VirtualScreen();
                var shot = Native.Capture(virt);
                var ov = new OverlayForm(shot, virt, _cfg);
                _overlay = ov;
                ov.Completed += msg => Notify(msg);
                ov.FormClosed += (s, e) =>
                {
                    _overlay = null;
                    try { shot.Dispose(); } catch { }
                };
                ov.Show();
            }
            catch (Exception ex)
            {
                Notify("截图失败：" + ex.Message, true);
            }
        }

        void Notify(string msg)
        {
            Notify(msg, false);
        }

        void Notify(string msg, bool force)
        {
            if (!force && !_cfg.notify) return;
            try
            {
                _tray.BalloonTipTitle = "SnapTool " + AppInfo.Version;
                _tray.BalloonTipText = msg;
                _tray.ShowBalloonTip(1800);
            }
            catch { }
        }

        void ExitApp()
        {
            try { _hk.Dispose(); } catch { }
            try { _clip.Dispose(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            ExitThread();
        }
    }
}
