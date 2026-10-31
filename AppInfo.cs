using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTool
{
    /// <summary>
    /// 版本号的唯一来源是 csproj 里的 &lt;Version&gt;，这里只负责读出来显示。
    ///
    /// 测试阶段版本号带预发布后缀（如 1.1.0-beta），程序里会自动标注「测试版」；
    /// 等不再改了，把 csproj 的 Version 改成 1.1.0，后缀消失，就是一个正式版。
    ///
    /// 光有版本号还不够——同一版本号可能编译了好几次（调 bug 时），
    /// 所以再带上 exe 的编译时间，两次构建才分得清。
    /// </summary>
    static class AppInfo
    {
        /// <summary>形如 1.1.0-beta</summary>
        public static readonly string Version;

        /// <summary>形如 10-31 15:21，取 exe 的最后写入时间</summary>
        public static readonly string Build;

        /// <summary>形如 v1.1.0-beta · 10-31 15:21</summary>
        public static readonly string Display;

        /// <summary>版本号带 -beta / -rc 之类后缀时为 true</summary>
        public static readonly bool IsBeta;

        static AppInfo()
        {
            string ver = ReadVersion();
            Version = ver;
            IsBeta = ver.IndexOf('-') > 0;

            string build = "";
            try
            {
                DateTime t = File.GetLastWriteTime(Application.ExecutablePath);
                if (t.Year > 2000) build = t.ToString("MM-dd HH:mm");
            }
            catch { }
            Build = build;

            Display = "v" + Version + (Build.Length > 0 ? "  ·  " + Build : "");
        }

        static string ReadVersion()
        {
            // InformationalVersion 才带 -beta 这类后缀，AssemblyVersion 只有数字
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(AssemblyInformationalVersionAttribute));
                if (attr != null && !string.IsNullOrEmpty(attr.InformationalVersion))
                {
                    string s = attr.InformationalVersion;
                    int plus = s.IndexOf('+');   // SDK 可能附加 +<git hash>
                    if (plus > 0) s = s.Substring(0, plus);
                    s = s.Trim();
                    if (s.Length > 0) return s;
                }

                System.Version v = asm.GetName().Version;
                if (v != null) return v.Major + "." + v.Minor + "." + (v.Build < 0 ? 0 : v.Build);
            }
            catch { }

            return "0.0.0";
        }
    }
}
