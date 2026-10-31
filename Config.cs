using System;
using System.IO;
using System.Text;

namespace SnapTool
{
    class Config
    {
        public string hotkey { get; set; }
        public string save_dir { get; set; }
        public bool magnifier { get; set; }
        public bool notify { get; set; }
        /// <summary>复制内容整段被 \(...\) 包住时自动去掉定界符。默认开启。</summary>
        public bool formula_fix { get; set; }

        static string DirPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapTool"); }
        }

        static string FilePath
        {
            get { return Path.Combine(DirPath, "config.json"); }
        }

        public static Config Load()
        {
            Config c = new Config();
            c.formula_fix = true;   // 默认开启；文件里显式写了 false 才关
            try
            {
                if (File.Exists(FilePath)) Parse(c, File.ReadAllText(FilePath));
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(c.hotkey)) c.hotkey = "alt+a";
            if (string.IsNullOrWhiteSpace(c.save_dir))
                c.save_dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

            try
            {
                if (!Directory.Exists(c.save_dir)) Directory.CreateDirectory(c.save_dir);
            }
            catch
            {
            }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                var sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"hotkey\": \"").Append(Esc(hotkey)).Append("\",\r\n");
                sb.Append("  \"save_dir\": \"").Append(Esc(save_dir)).Append("\",\r\n");
                sb.Append("  \"magnifier\": ").Append(magnifier ? "true" : "false").Append(",\r\n");
                sb.Append("  \"notify\": ").Append(notify ? "true" : "false").Append(",\r\n");
                sb.Append("  \"formula_fix\": ").Append(formula_fix ? "true" : "false").Append("\r\n");
                sb.Append("}\r\n");
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch
            {
            }
        }

        // ---- 极简 JSON 读写：只有 4 个键，不值得为此拖进一整个 JSON 序列化器 ----

        static string Esc(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            var sb = new StringBuilder(v.Length + 8);
            foreach (char ch in v)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        static int StrEnd(string s, int from)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        static string Unesc(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int cp;
                            if (int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out cp))
                            {
                                sb.Append((char)cp);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>按 key 顺序扫描，遇到 "键": 值 就赋值；注释、尾逗号、未知键一律跳过。</summary>
        static void Parse(Config c, string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                int q1 = s.IndexOf('"', i);
                if (q1 < 0) return;
                int q2 = StrEnd(s, q1 + 1);
                if (q2 < 0) return;
                string key = s.Substring(q1 + 1, q2 - q1 - 1);

                int colon = s.IndexOf(':', q2 + 1);
                if (colon < 0) return;
                int p = colon + 1;
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (p >= s.Length) return;

                string sv = null;
                int bv = -1; // -1 未取到，0 false，1 true

                if (s[p] == '"')
                {
                    int v2 = StrEnd(s, p + 1);
                    if (v2 < 0) return;
                    sv = Unesc(s.Substring(p + 1, v2 - p - 1));
                    i = v2 + 1;
                }
                else
                {
                    int e = p;
                    while (e < s.Length && s[e] != ',' && s[e] != '}' && s[e] != '\r' && s[e] != '\n') e++;
                    string raw = s.Substring(p, e - p).Trim();
                    if (raw == "true") bv = 1;
                    else if (raw == "false") bv = 0;
                    i = e;
                }

                if (string.Equals(key, "hotkey", StringComparison.OrdinalIgnoreCase) && sv != null)
                    c.hotkey = sv;
                else if (string.Equals(key, "save_dir", StringComparison.OrdinalIgnoreCase) && sv != null)
                    c.save_dir = sv;
                else if (string.Equals(key, "magnifier", StringComparison.OrdinalIgnoreCase) && bv >= 0)
                    c.magnifier = bv == 1;
                else if (string.Equals(key, "notify", StringComparison.OrdinalIgnoreCase) && bv >= 0)
                    c.notify = bv == 1;
                else if (string.Equals(key, "formula_fix", StringComparison.OrdinalIgnoreCase) && bv >= 0)
                    c.formula_fix = bv == 1;
            }
        }

        public string HotkeyText
        {
            get { return string.IsNullOrWhiteSpace(hotkey) ? "ALT+A" : hotkey.Trim().ToUpperInvariant(); }
        }

        /// <summary>解析 "alt+a" / "ctrl+shift+x" / "f1" 这类写法。</summary>
        public static bool TryParse(string spec, out int mods, out int vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            foreach (var raw in spec.ToLowerInvariant().Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;

                if (p == "alt") { mods |= Native.MOD_ALT; continue; }
                if (p == "ctrl" || p == "control") { mods |= Native.MOD_CONTROL; continue; }
                if (p == "shift") { mods |= Native.MOD_SHIFT; continue; }
                if (p == "win" || p == "cmd" || p == "super") { mods |= Native.MOD_WIN; continue; }
                if (p == "space") { vk = 0x20; continue; }
                if (p == "printscreen" || p == "prtsc") { vk = 0x2C; continue; }

                if (p.Length == 1)
                {
                    char ch = p[0];
                    if (ch >= 'a' && ch <= 'z') { vk = 'A' + (ch - 'a'); continue; }
                    if (ch >= '0' && ch <= '9') { vk = '0' + (ch - '0'); continue; }
                    return false;
                }

                if (p[0] == 'f')
                {
                    int n;
                    if (int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24) { vk = 0x6F + n; continue; }
                }
                return false;
            }
            return vk != 0;
        }
    }
}
