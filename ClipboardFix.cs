using System;
using System.Windows.Forms;

namespace SnapTool
{
    /// <summary>
    /// 剥掉整段包在最外层的公式定界符，得到裸 LaTeX。
    ///
    /// 规则（刻意保守，宁可不改也不改坏）：
    ///   1. 整段（去掉首尾空白后）必须以某对定界符开头并结尾；
    ///   2. 中间不能再出现同类定界符——否则说明是多条公式混排，动它会撕坏正文；
    ///   3. 本身不带定界符的（如 \boldsymbol{\begin{cases}...\end{cases}}）一律返回 null，不碰。
    ///
    /// 支持 \( \)  \[ \]  $$ $$ 三种；\(\ 与 \\[6pt] 这类转义按反斜杠奇偶判定，不会误判。
    /// </summary>
    static class FormulaFix
    {
        static readonly string[][] Pairs =
        {
            new[] { "\\(", "\\)" },
            new[] { "\\[", "\\]" },
            new[] { "$$", "$$" },
        };

        /// <summary>需要改写时返回新文本；不需要改写返回 null。</summary>
        public static string Unwrap(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string t = text.Trim();
            if (t.Length < 5) return null;

            foreach (var p in Pairs)
            {
                string open = p[0], close = p[1];
                if (t.Length <= open.Length + close.Length) continue;
                if (!t.StartsWith(open, StringComparison.Ordinal)) continue;
                if (!EndsWithDelim(t, close)) continue;

                string inner = t.Substring(open.Length, t.Length - open.Length - close.Length);
                if (inner.Trim().Length == 0) continue;

                // 中间还有定界符 → 多条公式混排，或者本来就有嵌套，保守不动
                if (CountDelim(inner, open) > 0) continue;
                if (CountDelim(inner, close) > 0) continue;

                return inner.Trim();
            }
            return null;
        }

        /// <summary>结尾是否为"真正的"定界符：前导反斜杠个数为奇数才算（\\) 是换行，不算）。</summary>
        static bool EndsWithDelim(string t, string close)
        {
            if (!t.EndsWith(close, StringComparison.Ordinal)) return false;
            if (close[0] != '\\') return true;

            int k = t.Length - close.Length;
            int bs = 0, j = k;
            while (j >= 0 && t[j] == '\\') { bs++; j--; }
            return bs % 2 == 1;
        }

        /// <summary>统计 s 中未被转义的定界符个数。</summary>
        static int CountDelim(string s, string token)
        {
            int n = 0, i = 0;
            while (i <= s.Length - token.Length)
            {
                int k = s.IndexOf(token, i, StringComparison.Ordinal);
                if (k < 0) break;
                if (token[0] == '\\')
                {
                    int bs = 0, j = k;
                    while (j >= 0 && s[j] == '\\') { bs++; j--; }
                    if (bs % 2 == 1) n++;
                }
                else n++;
                i = k + 1;
            }
            return n;
        }
    }

    /// <summary>只用来接收 WM_CLIPBOARDUPDATE 的隐藏消息窗口。</summary>
    class ClipboardWatcher : NativeWindow, IDisposable
    {
        bool _listening;

        public event Action Changed;

        public ClipboardWatcher()
        {
            try
            {
                CreateHandle(new CreateParams());
                _listening = Native.AddClipboardFormatListener(Handle);
            }
            catch { _listening = false; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_CLIPBOARDUPDATE)
            {
                try { if (Changed != null) Changed(); } catch { }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try
            {
                if (_listening) Native.RemoveClipboardFormatListener(Handle);
            }
            catch { }
            _listening = false;
            try { DestroyHandle(); } catch { }
        }
    }
}
