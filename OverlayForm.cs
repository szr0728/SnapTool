using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace SnapTool
{
    enum ToolKind { Select, Rect, Ellipse, Arrow, Pen, Text, Mosaic }

    class Ann
    {
        public ToolKind Kind;
        public Point A;
        public Point B;
        public List<Point> Pts;
        public Color Color;
        public float Width = 3f;
        public string Text;
    }

    class OverlayForm : Form
    {
        static readonly Color Accent = Color.FromArgb(30, 159, 255);
        static readonly Color DimColor = Color.FromArgb(118, 0, 0, 0);
        static readonly Color[] Palette =
        {
            Color.FromArgb(255, 74, 74),
            Color.FromArgb(255, 196, 0),
            Color.FromArgb(0, 190, 100),
            Color.FromArgb(30, 159, 255)
        };

        const string UiFont = "Microsoft YaHei UI";

        readonly Bitmap _screen;
        readonly Rectangle _virt;
        readonly Config _cfg;

        Bitmap _work;
        Bitmap _bg;
        Rectangle _sel;
        bool _hasSel;
        ToolKind _tool = ToolKind.Select;
        Color _color = Palette[0];

        readonly List<Ann> _ops = new List<Ann>();
        Ann _drag;

        // 0 空闲  1 拉选区  2 移动  3 缩放  4 画标注
        int _state;
        int _handle = -1;
        Point _start;
        Point _cur;
        Rectangle _selStart;
        bool _confirming;

        readonly List<Rectangle> _btns = new List<Rectangle>();
        readonly List<int> _ids = new List<int>();
        Rectangle _bar = Rectangle.Empty;
        int _hover = -1;

        // 增量重绘：记录上一次画面里各元素的位置，鼠标移动时只重画真正变化的区域
        Rectangle _prevSel = Rectangle.Empty;
        Rectangle _prevBar = Rectangle.Empty;
        Rectangle _prevMag = Rectangle.Empty;

        // 字体只建一次（每帧 new Font 是实打实的开销）
        readonly Font _fHint;
        readonly Font _fSize;
        readonly Font _fMag;
        readonly Font _fAnn;
        readonly Font _fIconA;

        TextBox _tb;
        Point _textAt;

        public bool Copied;
        public bool Saved;
        public event Action<string> Completed;

        public OverlayForm(Bitmap screen, Rectangle virt, Config cfg)
        {
            _screen = screen;
            _virt = virt;
            _cfg = cfg;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = virt;
            KeyPreview = true;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            _work = new Bitmap(virt.Width, virt.Height, PixelFormat.Format32bppArgb);
            _bg = new Bitmap(virt.Width, virt.Height, PixelFormat.Format32bppArgb);

            _fHint = new Font(UiFont, 14f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fSize = new Font(UiFont, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fMag = new Font(UiFont, 11f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fAnn = new Font(UiFont, 20f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fIconA = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);

            RebuildWork();
            RebuildBg();
        }

        int CW { get { return _virt.Width; } }
        int CH { get { return _virt.Height; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _virt.X, _virt.Y, _virt.Width, _virt.Height, Native.SWP_SHOWWINDOW);
            Native.SetForegroundWindow(Handle);
            Activate();
            Focus();
            _cur = PointToClient(Cursor.Position);
        }

        // ================= 图层 =================

        void RebuildWork()
        {
            using (var g = Graphics.FromImage(_work))
            {
                g.DrawImageUnscaled(_screen, 0, 0);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var op in _ops) DrawAnn(g, op);
            }
        }

        /// <summary>
        /// 生成"整屏 + 全变暗"的底层图。只在打开遮罩时做一次。
        /// 选区为什么不用重建它：选区内部由 OnPaint 单独搬 _work 覆盖，外部本来就是这张全暗图。
        /// 旧实现每次鼠标移动都重建它（整屏拷贝 + 四次整屏填充），这就是拖动卡顿的根源。
        /// </summary>
        void RebuildBg()
        {
            using (var g = Graphics.FromImage(_bg))
            using (var br = new SolidBrush(DimColor))
            {
                g.DrawImageUnscaled(_screen, 0, 0);
                g.FillRectangle(br, 0, 0, CW, CH);
            }
            LayoutToolbar();
        }

        /// <summary>选区变化后的增量重绘：只刷 旧选区 ∪ 新选区 ∪ 两个工具条 ∪ 两个放大镜。</summary>
        void InvalidateSel()
        {
            LayoutToolbar();

            var r = Union(_prevSel, _sel);
            r = Union(r, _prevBar);
            r = Union(r, _bar);
            r = Union(r, _prevMag);
            r = Union(r, MagVisible ? MagRect(_cur) : Rectangle.Empty);

            _prevSel = _sel;
            _prevBar = _bar;
            _prevMag = MagVisible ? MagRect(_cur) : Rectangle.Empty;

            if (r.Width <= 0 || r.Height <= 0) { Invalidate(); return; }

            // 边框、8 个控制点、上方尺寸标签都在选区外一点点
            r.Inflate(26, 30);
            Invalidate(r);
        }

        void DrawAnn(Graphics g, Ann op)
        {
            using (var pen = new Pen(op.Color, op.Width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                switch (op.Kind)
                {
                    case ToolKind.Rect:
                        {
                            var r = RectOf(op.A, op.B);
                            if (r.Width > 0 && r.Height > 0) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                            break;
                        }
                    case ToolKind.Ellipse:
                        {
                            var r = RectOf(op.A, op.B);
                            if (r.Width > 0 && r.Height > 0) g.DrawEllipse(pen, r);
                            break;
                        }
                    case ToolKind.Arrow:
                        DrawArrow(g, pen, op.A, op.B);
                        break;
                    case ToolKind.Pen:
                        {
                            if (op.Pts == null || op.Pts.Count == 0) break;
                            if (op.Pts.Count == 1)
                            {
                                using (var br = new SolidBrush(op.Color))
                                {
                                    float s = Math.Max(2f, op.Width);
                                    g.FillEllipse(br, op.Pts[0].X - s / 2, op.Pts[0].Y - s / 2, s, s);
                                }
                            }
                            else g.DrawLines(pen, op.Pts.ToArray());
                            break;
                        }
                    case ToolKind.Mosaic:
                        DrawMosaic(g, RectOf(op.A, op.B));
                        break;
                    case ToolKind.Text:
                        DrawAnnText(g, op);
                        break;
                }
            }
        }

        void DrawArrow(Graphics g, Pen pen, Point a, Point b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 2) return;

            double ang = Math.Atan2(dy, dx);
            float head = Math.Max(11f, pen.Width * 3.8f);
            if (head > len) head = (float)len;

            float tx = (float)(b.X - Math.Cos(ang) * head);
            float ty = (float)(b.Y - Math.Sin(ang) * head);
            double spread = 0.44;
            var p1 = new PointF((float)(b.X - Math.Cos(ang - spread) * head), (float)(b.Y - Math.Sin(ang - spread) * head));
            var p2 = new PointF((float)(b.X - Math.Cos(ang + spread) * head), (float)(b.Y - Math.Sin(ang + spread) * head));

            g.DrawLine(pen, new PointF(a.X, a.Y), new PointF(tx, ty));
            using (var br = new SolidBrush(pen.Color))
                g.FillPolygon(br, new[] { new PointF(b.X, b.Y), p1, p2 });
        }

        void DrawMosaic(Graphics g, Rectangle r)
        {
            r = Rectangle.Intersect(r, new Rectangle(0, 0, CW, CH));
            if (r.Width < 2 || r.Height < 2) return;

            const int block = 10;
            int w = Math.Max(1, r.Width / block);
            int h = Math.Max(1, r.Height / block);

            // 先完整读出来，避免边读边写同一张位图
            using (var crop = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
            {
                using (var cg = Graphics.FromImage(crop))
                {
                    cg.InterpolationMode = InterpolationMode.NearestNeighbor;
                    cg.PixelOffsetMode = PixelOffsetMode.Half;
                    cg.DrawImage(_work, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
                }

                using (var small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var sg = Graphics.FromImage(small))
                    {
                        sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        sg.DrawImage(crop, new Rectangle(0, 0, w, h));
                    }

                    var oldMode = g.InterpolationMode;
                    var oldOffset = g.PixelOffsetMode;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(small, r);
                    g.InterpolationMode = oldMode;
                    g.PixelOffsetMode = oldOffset;
                }
            }
        }

        void DrawAnnText(Graphics g, Ann op)
        {
            if (string.IsNullOrEmpty(op.Text)) return;
            using (var br = new SolidBrush(op.Color))
            {
                var old = g.TextRenderingHint;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(op.Text, _fAnn, br, op.A.X - 1, op.A.Y - 1);
                g.TextRenderingHint = old;
            }
        }

        // ================= 绘制 =================

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // 只有脏区参与绘制。整屏 1:1 拷贝一次，而不是每次鼠标移动都重画两层整屏。
            var clip = e.ClipRectangle;

            g.DrawImage(_bg, clip, clip, GraphicsUnit.Pixel);

            if (_hasSel && _sel.Width > 0 && _sel.Height > 0)
            {
                // 选区内部用原亮度图层，同样只搬脏区与选区的交集
                var inner = Rectangle.Intersect(_sel, clip);
                if (inner.Width > 0 && inner.Height > 0)
                    g.DrawImage(_work, inner, inner, GraphicsUnit.Pixel);

                var saved = g.Save();
                g.SetClip(_sel, CombineMode.Intersect);
                if (_drag != null)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    if (_drag.Kind == ToolKind.Mosaic)
                    {
                        var r = RectOf(_drag.A, _drag.B);
                        using (var br = new SolidBrush(Color.FromArgb(56, 255, 255, 255)))
                            g.FillRectangle(br, r);
                        using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f))
                        {
                            pen.DashStyle = DashStyle.Dash;
                            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                        }
                    }
                    else DrawAnn(g, _drag);
                }
                g.Restore(saved);
                DrawSelectionChrome(g);
            }
            else if (!_hasSel)
            {
                DrawCrosshair(g);
                DrawHint(g);
            }

            if (MagVisible) DrawMagnifier(g, _cur);
        }

        bool MagVisible
        {
            get
            {
                if (!_cfg.magnifier) return false;
                if (!((!_hasSel && _state == 0) || _state == 1 || _state == 3)) return false;
                return !_bar.Contains(_cur);
            }
        }

        /// <summary>放大镜的目标矩形（纯函数，用于增量重绘时算脏区）。</summary>
        static Rectangle MagDst(Point p, int cw, int ch)
        {
            const int srcSize = 17, zoom = 8;
            int size = srcSize * zoom;

            var dst = new Rectangle(p.X + 20, p.Y + 20, size, size);
            if (dst.Right > cw - 4) dst.X = p.X - 20 - size;
            if (dst.Bottom > ch - 4) dst.Y = p.Y - 20 - size;
            if (dst.X < 4) dst.X = 4;
            if (dst.Y < 4) dst.Y = 4;
            return dst;
        }

        Rectangle MagRect(Point p)
        {
            var r = MagDst(p, CW, CH);
            r.Inflate(4, 4);
            r.Height += 28;     // 底部还有一行坐标标签
            return r;
        }

        static Rectangle Union(Rectangle a, Rectangle b)
        {
            if (a.Width <= 0 || a.Height <= 0) return b;
            if (b.Width <= 0 || b.Height <= 0) return a;
            return Rectangle.Union(a, b);
        }

        void DrawCrosshair(Graphics g)
        {
            using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1f))
            {
                pen.DashStyle = DashStyle.Dot;
                g.DrawLine(pen, 0, _cur.Y, CW, _cur.Y);
                g.DrawLine(pen, _cur.X, 0, _cur.X, CH);
            }
        }

        void DrawHint(Graphics g)
        {
            string s = "按住鼠标左键框选区域      右键 / Esc 取消      Ctrl+S 保存";
            {
                var f = _fHint;
                var size = g.MeasureString(s, f);
                int w = (int)Math.Ceiling(size.Width) + 34;
                int h = 38;
                int x = Math.Max(8, (CW - w) / 2);
                int y = 40;
                using (var path = IconFactory.Rounded(new Rectangle(x, y, w, h), 19))
                using (var br = new SolidBrush(Color.FromArgb(216, 22, 24, 28)))
                    g.FillPath(br, path);
                using (var br = new SolidBrush(Color.FromArgb(238, 255, 255, 255)))
                    g.DrawString(s, f, br, x + 17, y + (h - size.Height) / 2);
            }
        }

        void DrawSelectionChrome(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.None;
            using (var pen = new Pen(Accent, 1f))
                g.DrawRectangle(pen, _sel.X, _sel.Y, _sel.Width - 1, _sel.Height - 1);

            foreach (var h in HandleRects())
            {
                using (var br = new SolidBrush(Color.White)) g.FillRectangle(br, h);
                using (var pen = new Pen(Accent, 1f)) g.DrawRectangle(pen, h.X, h.Y, h.Width - 1, h.Height - 1);
            }

            DrawSizeLabel(g);
            DrawToolbar(g);
        }

        void DrawSizeLabel(Graphics g)
        {
            string s = string.Format("{0} × {1}", _sel.Width, _sel.Height);
            {
                var f = _fSize;
                var size = g.MeasureString(s, f);
                int w = (int)Math.Ceiling(size.Width) + 14;
                int h = (int)Math.Ceiling(size.Height) + 4;
                int x = _sel.X;
                int y = _sel.Y - h - 4;
                if (y < 0) y = _sel.Y + 4;
                if (x + w > CW) x = CW - w;
                if (x < 0) x = 0;
                using (var path = IconFactory.Rounded(new Rectangle(x, y, w, h), 4))
                using (var br = new SolidBrush(Color.FromArgb(214, 20, 22, 26)))
                    g.FillPath(br, path);
                using (var br = new SolidBrush(Color.White))
                    g.DrawString(s, f, br, x + 7, y + 2);
            }
        }

        Rectangle[] HandleRects()
        {
            const int s = 7;
            const int h = s / 2;
            int l = _sel.Left, t = _sel.Top, r = _sel.Right - 1, b = _sel.Bottom - 1;
            int cx = (l + r) / 2, cy = (t + b) / 2;
            var pts = new[]
            {
                new Point(l, t), new Point(cx, t), new Point(r, t), new Point(r, cy),
                new Point(r, b), new Point(cx, b), new Point(l, b), new Point(l, cy)
            };
            var res = new Rectangle[8];
            for (int i = 0; i < 8; i++) res[i] = new Rectangle(pts[i].X - h, pts[i].Y - h, s, s);
            return res;
        }

        // ================= 工具条 =================

        void LayoutToolbar()
        {
            _btns.Clear();
            _ids.Clear();
            _bar = Rectangle.Empty;
            // 拉框过程中不显示工具条（和微信一致），松手后才出现。
            // 顺带省掉拖动时每帧重画 14 个按钮的开销。
            if (_state == 1) return;
            if (!_hasSel || _sel.Width < 6 || _sel.Height < 6) return;

            const int barH = 40, pad = 6, gap = 8, colorW = 26, btnW = 34;

            var widths = new List<int>();
            var ids = new List<int>();
            for (int i = 0; i < 4; i++) { widths.Add(colorW); ids.Add(i); }
            widths.Add(gap); ids.Add(-1);
            for (int i = 0; i < 6; i++) { widths.Add(btnW); ids.Add(4 + i); }
            widths.Add(gap); ids.Add(-1);
            for (int i = 0; i < 4; i++) { widths.Add(btnW); ids.Add(10 + i); }

            int total = pad * 2;
            foreach (var w in widths) total += w;

            int x = _sel.Right - total;
            int y = _sel.Bottom + 8;
            if (y + barH > CH) y = _sel.Top - 8 - barH;
            if (y < 0) y = Math.Max(0, _sel.Bottom - 8 - barH);
            if (x < 0) x = 0;
            if (x + total > CW) x = CW - total;
            if (x < 0) x = 0;

            _bar = new Rectangle(x, y, total, barH);

            int cx = x + pad;
            for (int i = 0; i < widths.Count; i++)
            {
                if (ids[i] < 0) { cx += widths[i]; continue; }
                _btns.Add(new Rectangle(cx, y + (barH - 30) / 2, widths[i], 30));
                _ids.Add(ids[i]);
                cx += widths[i];
            }
        }

        void DrawToolbar(Graphics g)
        {
            if (_btns.Count == 0) return;

            using (var path = IconFactory.Rounded(_bar, 8))
            using (var br = new SolidBrush(Color.FromArgb(240, 28, 30, 34)))
                g.FillPath(br, path);
            using (var path = IconFactory.Rounded(_bar, 8))
            using (var pen = new Pen(Color.FromArgb(58, 255, 255, 255), 1f))
                g.DrawPath(pen, path);

            for (int i = 0; i < _btns.Count; i++)
            {
                int id = _ids[i];
                var r = _btns[i];

                if (id != _hover && id >= 4 && id <= 9 && _tool == (ToolKind)(id - 3))
                {
                    using (var path = IconFactory.Rounded(r, 6))
                    using (var br = new SolidBrush(Color.FromArgb(92, Accent)))
                        g.FillPath(br, path);
                }
                if (id == _hover)
                {
                    using (var path = IconFactory.Rounded(r, 6))
                    using (var br = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                        g.FillPath(br, path);
                }

                if (id < 4)
                {
                    var c = Palette[id];
                    var dot = new Rectangle(r.X + (r.Width - 14) / 2, r.Y + (r.Height - 14) / 2, 14, 14);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var br = new SolidBrush(c)) g.FillEllipse(br, dot);
                    g.SmoothingMode = SmoothingMode.None;
                    if (_color.ToArgb() == c.ToArgb())
                    {
                        using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.6f))
                            g.DrawEllipse(pen, dot.X - 3, dot.Y - 3, dot.Width + 6, dot.Height + 6);
                    }
                }
                else DrawIcon(g, id, r);
            }
        }

        void DrawIcon(Graphics g, int id, Rectangle r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = Color.FromArgb(238, 238, 242);
            var rc = new Rectangle(r.X + 9, r.Y + 9, r.Width - 18, r.Height - 18);

            using (var pen = new Pen(c, 1.8f))
            using (var br = new SolidBrush(c))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                switch (id)
                {
                    case 4:
                        g.DrawRectangle(pen, rc.X, rc.Y, rc.Width, rc.Height);
                        break;
                    case 5:
                        g.DrawEllipse(pen, rc);
                        break;
                    case 6:
                        DrawArrow(g, pen, new Point(rc.Left, rc.Bottom), new Point(rc.Right - 1, rc.Top));
                        break;
                    case 7:
                        g.DrawLines(pen, new[]
                        {
                            new PointF(rc.Left, rc.Bottom - 2), new PointF(rc.Left + 3, rc.Top + 3),
                            new PointF(rc.Left + 6, rc.Bottom - 2), new PointF(rc.Left + 9, rc.Top + 3),
                            new PointF(rc.Right, rc.Bottom - 3)
                        });
                        break;
                    case 8:
                        {
                            var f = _fIconA;
                            var size = g.MeasureString("A", f);
                            g.DrawString("A", f, br,
                                r.X + (r.Width - size.Width) / 2,
                                r.Y + (r.Height - size.Height) / 2 + 1);
                        }
                        break;
                    case 9:
                        {
                            int cw = Math.Max(2, rc.Width / 3);
                            for (int i = 0; i < 3; i++)
                                for (int j = 0; j < 3; j++)
                                {
                                    if (((i + j) & 1) != 0) continue;
                                    g.FillRectangle(br, rc.X + i * cw, rc.Y + j * cw, cw - 1, cw - 1);
                                }
                            break;
                        }
                    case 10:
                        g.DrawArc(pen, rc.X + 1, rc.Y + 3, rc.Width, rc.Height - 2, 150, 210);
                        g.FillPolygon(br, new[]
                        {
                            new PointF(rc.Left + 4, rc.Top + 1), new PointF(rc.Left - 3, rc.Top + 7),
                            new PointF(rc.Left + 5, rc.Top + 11)
                        });
                        break;
                    case 11:
                        g.DrawLine(pen, rc.X + rc.Width / 2f, rc.Top, rc.X + rc.Width / 2f, rc.Bottom - 5);
                        g.FillPolygon(br, new[]
                        {
                            new PointF(rc.X + rc.Width / 2f, rc.Bottom),
                            new PointF(rc.X + rc.Width / 2f - 4, rc.Bottom - 6),
                            new PointF(rc.X + rc.Width / 2f + 4, rc.Bottom - 6)
                        });
                        g.DrawLine(pen, rc.Left, rc.Bottom + 1, rc.Right, rc.Bottom + 1);
                        break;
                    case 12:
                        g.DrawLine(pen, rc.Left, rc.Top, rc.Right, rc.Bottom);
                        g.DrawLine(pen, rc.Right, rc.Top, rc.Left, rc.Bottom);
                        break;
                    case 13:
                        g.DrawLines(pen, new[]
                        {
                            new PointF(rc.Left, rc.Top + rc.Height * 0.55f),
                            new PointF(rc.Left + rc.Width * 0.38f, rc.Bottom),
                            new PointF(rc.Right + 1, rc.Top - 1)
                        });
                        break;
                }
            }
        }

        void DrawMagnifier(Graphics g, Point p)
        {
            const int srcSize = 17;
            int size = srcSize * 8;

            var dst = MagDst(p, CW, CH);

            int sx = p.X - srcSize / 2;
            int sy = p.Y - srcSize / 2;
            sx = Math.Max(0, Math.Min(sx, CW - srcSize));
            sy = Math.Max(0, Math.Min(sy, CH - srcSize));

            var oldMode = g.InterpolationMode;
            var oldOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            try { g.DrawImage(_screen, dst, new Rectangle(sx, sy, srcSize, srcSize), GraphicsUnit.Pixel); }
            catch { }
            g.InterpolationMode = oldMode;
            g.PixelOffsetMode = oldOffset;

            g.SmoothingMode = SmoothingMode.None;
            using (var pen = new Pen(Color.FromArgb(150, 255, 60, 60), 1f))
            {
                int mx = dst.X + size / 2;
                int my = dst.Y + size / 2;
                g.DrawLine(pen, dst.X, my, dst.Right - 1, my);
                g.DrawLine(pen, mx, dst.Y, mx, dst.Bottom - 1);
            }
            using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.2f))
                g.DrawRectangle(pen, dst.X, dst.Y, dst.Width - 1, dst.Height - 1);
            using (var pen = new Pen(Color.FromArgb(110, 0, 0, 0), 1f))
                g.DrawRectangle(pen, dst.X + 1, dst.Y + 1, dst.Width - 3, dst.Height - 3);

            string coord = string.Format("{0}, {1}", p.X + _virt.X, p.Y + _virt.Y);
            {
                var f = _fMag;
                var size2 = g.MeasureString(coord, f);
                int w = (int)Math.Ceiling(size2.Width) + 10;
                int h = (int)Math.Ceiling(size2.Height) + 2;
                var lab = new Rectangle(dst.X, dst.Bottom + 3, w, h);
                if (lab.Right > CW) lab.X = CW - w;
                using (var br = new SolidBrush(Color.FromArgb(200, 20, 22, 26)))
                    g.FillRectangle(br, lab);
                using (var br = new SolidBrush(Color.White))
                    g.DrawString(coord, f, br, lab.X + 5, lab.Y + 1);
            }
        }

        // ================= 交互 =================

        static Rectangle RectOf(Point a, Point b)
        {
            return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        int HitHandle(Point p)
        {
            var hs = HandleRects();
            for (int i = 0; i < hs.Length; i++)
            {
                var r = hs[i];
                r.Inflate(2, 2);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        int HitButton(Point p)
        {
            if (!_bar.Contains(p)) return -1;
            for (int i = 0; i < _btns.Count; i++)
                if (_btns[i].Contains(p)) return _ids[i];
            return -1;
        }

        void ClickButton(int id)
        {
            if (id < 4) { _color = Palette[id]; }
            else if (id <= 9) { _tool = (ToolKind)(id - 3); }
            else if (id == 10) { Undo(); return; }
            else if (id == 11) { Confirm(true); return; }
            else if (id == 12) { CancelAll(); return; }
            else if (id == 13) { Confirm(false); return; }
            UpdateCursor(_cur);
            Invalidate();
        }

        void Undo()
        {
            if (_ops.Count == 0) return;
            _ops.RemoveAt(_ops.Count - 1);
            RebuildWork();
            Invalidate();
        }

        Ann NewAnn(ToolKind kind, Point p)
        {
            var a = new Ann { Kind = kind, A = p, B = p, Color = _color, Width = 3f };
            if (kind == ToolKind.Pen) a.Pts = new List<Point> { p };
            return a;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _cur = e.Location;

            if (e.Button == MouseButtons.Right) { CancelAll(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (_tb != null) CommitText();

            if (_hasSel)
            {
                int id = HitButton(e.Location);
                if (id >= 0) { ClickButton(id); return; }

                int h = HitHandle(e.Location);
                if (h >= 0) { _state = 3; _handle = h; _start = e.Location; _selStart = _sel; return; }

                if (_sel.Contains(e.Location))
                {
                    if (_tool == ToolKind.Select) { _state = 2; _start = e.Location; _selStart = _sel; return; }
                    if (_tool == ToolKind.Text) { OpenText(e.Location); return; }
                    _state = 4;
                    _drag = NewAnn(_tool, e.Location);
                    Invalidate();
                    return;
                }
            }

            _state = 1;
            _start = e.Location;
            _sel = new Rectangle(e.Location.X, e.Location.Y, 0, 0);
            _hasSel = true;
            _prevSel = Rectangle.Empty;
            _prevBar = Rectangle.Empty;
            _prevMag = Rectangle.Empty;
            LayoutToolbar();
            Invalidate();   // 提示条与十字线要消失，这一次整屏刷
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _cur = e.Location;

            switch (_state)
            {
                case 1:
                    _sel = RectOf(_start, e.Location);
                    InvalidateSel();
                    return;
                case 2:
                    {
                        var r = new Rectangle(_selStart.X + (e.X - _start.X), _selStart.Y + (e.Y - _start.Y), _selStart.Width, _selStart.Height);
                        if (r.X < 0) r.X = 0;
                        if (r.Y < 0) r.Y = 0;
                        if (r.Right > CW) r.X = CW - r.Width;
                        if (r.Bottom > CH) r.Y = CH - r.Height;
                        _sel = r;
                        InvalidateSel();
                        return;
                    }
                case 3:
                    ResizeSelection(e.Location);
                    InvalidateSel();
                    return;
                case 4:
                    if (_drag != null)
                    {
                        var before = DragBounds();
                        if (_drag.Kind == ToolKind.Pen) _drag.Pts.Add(e.Location);
                        else _drag.B = e.Location;
                        var after = DragBounds();

                        var r = Rectangle.Intersect(_sel, Union(before, after));
                        r = Union(r, _prevMag);
                        r = Union(r, MagVisible ? MagRect(_cur) : Rectangle.Empty);
                        _prevMag = MagVisible ? MagRect(_cur) : Rectangle.Empty;
                        if (r.Width > 0 && r.Height > 0)
                        {
                            r.Inflate(8, 8);
                            Invalidate(r);
                        }
                        else Invalidate();
                    }
                    return;
            }

            int old = _hover;
            _hover = HitButton(e.Location);
            UpdateCursor(e.Location);

            if (!_hasSel)
            {
                // 无选区时十字线横竖贯穿整屏，只能整屏刷
                Invalidate();
                return;
            }
            if (old != _hover)
            {
                Invalidate();
                return;
            }
            var inv = Union(_prevMag, MagVisible ? MagRect(_cur) : Rectangle.Empty);
            _prevMag = MagVisible ? MagRect(_cur) : Rectangle.Empty;
            if (inv.Width > 0 && inv.Height > 0) Invalidate(inv);
        }

        /// <summary>正在绘制的标注覆盖的范围（用于增量重绘）。</summary>
        Rectangle DragBounds()
        {
            if (_drag == null) return Rectangle.Empty;
            if (_drag.Kind == ToolKind.Pen && _drag.Pts != null && _drag.Pts.Count > 0)
            {
                int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
                foreach (var p in _drag.Pts)
                {
                    if (p.X < l) l = p.X;
                    if (p.X > r) r = p.X;
                    if (p.Y < t) t = p.Y;
                    if (p.Y > b) b = p.Y;
                }
                return Rectangle.FromLTRB(l, t, r, b);
            }
            return RectOf(_drag.A, _drag.B);
        }

        void ResizeSelection(Point p)
        {
            int dx = p.X - _start.X;
            int dy = p.Y - _start.Y;
            var s = _selStart;
            int l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;

            switch (_handle)
            {
                case 0: l += dx; t += dy; break;
                case 1: t += dy; break;
                case 2: r += dx; t += dy; break;
                case 3: r += dx; break;
                case 4: r += dx; b += dy; break;
                case 5: b += dy; break;
                case 6: l += dx; b += dy; break;
                case 7: l += dx; break;
            }

            l = Math.Max(0, Math.Min(l, CW));
            r = Math.Max(0, Math.Min(r, CW));
            t = Math.Max(0, Math.Min(t, CH));
            b = Math.Max(0, Math.Min(b, CH));

            _sel = Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (_state == 1)
            {
                _sel = RectOf(_start, e.Location);
                if (_sel.Width < 6 || _sel.Height < 6)
                {
                    _hasSel = false;
                    _sel = Rectangle.Empty;
                    _tool = ToolKind.Select;
                }
                _state = 0;
                if (_hasSel) InvalidateSel();       // 工具条这时才出现
                else { LayoutToolbar(); Invalidate(); }
            }
            else if (_state == 2 || _state == 3)
            {
                _state = 0;
                InvalidateSel();
            }
            else if (_state == 4)
            {
                _state = 0;
                var d = _drag;
                _drag = null;
                if (d != null)
                {
                    bool ok = d.Kind == ToolKind.Pen
                        ? (d.Pts != null && d.Pts.Count > 1)
                        : (Math.Abs(d.B.X - d.A.X) > 2 || Math.Abs(d.B.Y - d.A.Y) > 2);
                    if (ok)
                    {
                        _ops.Add(d);
                        RebuildWork();
                    }
                }
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && _hasSel && _sel.Contains(e.Location) && HitButton(e.Location) < 0)
                Confirm(false);
        }

        void UpdateCursor(Point p)
        {
            if (_hasSel)
            {
                int h = HitHandle(p);
                if (h >= 0 && _state == 0)
                {
                    switch (h)
                    {
                        case 0:
                        case 4: Cursor = Cursors.SizeNWSE; return;
                        case 2:
                        case 6: Cursor = Cursors.SizeNESW; return;
                        case 1:
                        case 5: Cursor = Cursors.SizeNS; return;
                        case 3:
                        case 7: Cursor = Cursors.SizeWE; return;
                    }
                }
                if (_state == 0 && _sel.Contains(p) && _tool == ToolKind.Select)
                {
                    Cursor = Cursors.SizeAll;
                    return;
                }
            }
            Cursor = Cursors.Cross;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_tb != null) return;

            if (e.KeyCode == Keys.Escape) { CancelAll(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { Confirm(false); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.S) { Confirm(true); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; e.SuppressKeyPress = true; }
        }

        void SelectAll()
        {
            _hasSel = true;
            _sel = new Rectangle(0, 0, CW, CH);
            _state = 0;
            _tool = ToolKind.Select;
            LayoutToolbar();
            _prevSel = _sel;        // 同步记录，避免后续增量重绘漏刷
            _prevBar = _bar;
            Invalidate();
        }

        // ================= 文字输入 =================

        void OpenText(Point p)
        {
            if (_tb != null) CommitText();

            Color back;
            try { back = _work.GetPixel(Math.Max(0, Math.Min(p.X, CW - 1)), Math.Max(0, Math.Min(p.Y, CH - 1))); }
            catch { back = Color.FromArgb(30, 30, 30); }

            var tb = new TextBox();
            tb.BorderStyle = BorderStyle.None;
            tb.Font = new Font(UiFont, 20f, FontStyle.Regular, GraphicsUnit.Pixel);
            tb.ForeColor = _color;
            tb.BackColor = back;
            tb.Width = 180;
            tb.Location = p;
            tb.KeyDown += (s, ev) =>
            {
                if (ev.KeyCode == Keys.Enter)
                {
                    ev.SuppressKeyPress = true;
                    ev.Handled = true;
                    CommitText();
                }
                else if (ev.KeyCode == Keys.Escape)
                {
                    ev.SuppressKeyPress = true;
                    ev.Handled = true;
                    CancelText();
                }
            };

            _tb = tb;
            _textAt = p;
            Controls.Add(tb);
            tb.Show();
            tb.Focus();
            Invalidate();
        }

        void CommitText()
        {
            if (_tb == null) return;
            var tb = _tb;
            _tb = null;
            string text = tb.Text;
            var at = _textAt;

            Controls.Remove(tb);
            tb.Dispose();

            if (!string.IsNullOrEmpty(text))
            {
                _ops.Add(new Ann { Kind = ToolKind.Text, A = at, B = at, Text = text, Color = _color, Width = 3f });
                RebuildWork();
            }
            Focus();
            Invalidate();
        }

        void CancelText()
        {
            if (_tb == null) return;
            var tb = _tb;
            _tb = null;
            Controls.Remove(tb);
            tb.Dispose();
            Focus();
            Invalidate();
        }

        // ================= 输出 =================

        void CancelAll()
        {
            if (_tb != null) { CancelText(); return; }
            Close();
        }

        void Confirm(bool saveAs)
        {
            if (_confirming) return;
            if (!_hasSel || _sel.Width < 1 || _sel.Height < 1) { CancelAll(); return; }
            if (_tb != null) CommitText();

            string path = null;
            if (saveAs)
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = "保存截图";
                    try { dlg.InitialDirectory = _cfg.save_dir; } catch { }
                    dlg.FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    dlg.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp";
                    dlg.AddExtension = true;
                    dlg.OverwritePrompt = true;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    path = dlg.FileName;
                }
            }

            _confirming = true;
            var rect = _sel;

            using (var result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(result))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(_work, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
                }

                if (path != null)
                {
                    try
                    {
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        ImageFormat fmt = ext == ".jpg" || ext == ".jpeg" ? ImageFormat.Jpeg
                                        : ext == ".bmp" ? ImageFormat.Bmp : ImageFormat.Png;
                        result.Save(path, fmt);
                        Saved = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "保存失败：" + ex.Message, "SnapTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        Clipboard.SetImage(result);
                        Copied = true;
                        break;
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(60);
                    }
                }
            }

            string msg;
            if (Saved && Copied) msg = "已保存并复制到剪贴板";
            else if (Saved) msg = "已保存到 " + path;
            else if (Copied) msg = "已复制到剪贴板";
            else msg = "复制失败，剪贴板可能被其他程序占用";

            try { if (Completed != null) Completed(msg); } catch { }
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { if (_tb != null) { _tb.Dispose(); _tb = null; } } catch { }
            try { _work.Dispose(); } catch { }
            try { _bg.Dispose(); } catch { }
        }
    }
}
