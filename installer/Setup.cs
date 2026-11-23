// Marmoset Toolbag 5 简体中文语言包 — 安装/卸载程序
//
// 编译（无需联网、无需 NuGet，使用系统内置 .NET Framework 编译器）：
//   csc.exe /target:winexe /platform:anycpu /optimize+ /langversion:7.3 ^
//           /win32manifest:installer\app.manifest /win32icon:installer\app.ico ^
//           /resource:dist\payload.bin.gz,payload ^
//           /out:"dist\Toolbag5_汉化补丁.exe" installer\Setup.cs
//
// 设计要点
//   * 补丁以「差分字节」形式内嵌，安装包只有几十 KB，不携带 28 MB 的 exe。
//   * 每个差分都带原始字节，写入前逐字节核对；版本不符时拒绝写入，绝不会写坏程序。
//   * 安装前把原文件备份到 ProgramData，卸载时原样还原英文。
//   * 写入 Program Files 需要管理员权限，清单已声明 requireAdministrator。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ToolbagZh
{
    internal static class Program
    {
        internal const string AppTitle = "Marmoset Toolbag 5 简体中文语言包";
        internal const string ShortName = "Toolbag 5 汉化补丁";
        internal const string Version = "1.0.0";
        internal const string SupportedBuild = "5.0.3.2（build 5032）";
        internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Toolbag5_SimplifiedChinese";
        internal const string AppId = "Toolbag5_SimplifiedChinese";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = false, uninstall = false;
            foreach (string a in args)
            {
                if (a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) silent = true;
                if (a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
            }

            try
            {
                Trace.Reset();
                Trace.Write($"=== start v{Version} args=[{string.Join(" ", args)}] " +
                          $"silent={silent} uninstall={uninstall} ===");
                Trace.Write("install dir detected: " + (Util.FindInstallDir() ?? "<none>"));
                if (uninstall) Application.Run(new UninstallForm(silent));
                else Application.Run(new InstallForm(silent));
                Trace.Write("=== exit ===");
            }
            catch (Exception ex)
            {
                Trace.Write("FATAL: " + ex);
                if (!silent)
                    MessageBox.Show("程序发生错误：\r\n\r\n" + ex.Message, AppTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal static class Trace
    {
        private static readonly object Gate = new object();
        public static string Path_
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ToolbagZh", "setup.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string dir = System.IO.Path.GetDirectoryName(Path_);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(Path_,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static void Reset()
        {
            try
            {
                if (File.Exists(Path_)) File.Delete(Path_);
            }
            catch { }
        }
    }

    // ============================================================ payload

    internal sealed class Patch
    {
        public int Offset;
        public byte[] Old;
        public byte[] New;
    }

    /// <summary>
    /// A compile-time length immediate that must be widened together with a translated string.
    /// Some labels are built as `std::string(ptr, N)` with N baked into the code, so writing a
    /// longer UTF-8 string without updating N truncates it mid-character.
    /// </summary>
    internal sealed class LenEdit
    {
        public int Offset;
        public int OldValue;
        public int NewValue;
    }

    internal sealed class PayloadData
    {
        public List<Patch> Patches = new List<Patch>();
        public List<LenEdit> LenEdits = new List<LenEdit>();
        public byte[] Text;
        public string Version;
        public int OrigExeSize;
        public byte[] OrigExeSha;
        public byte[] OrigTextSha;

        public static PayloadData Load()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("payload"))
            {
                if (s == null) throw new InvalidOperationException("安装包数据缺失，文件可能不完整。");
                using (var gz = new GZipStream(s, CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    gz.CopyTo(ms);
                    return Parse(ms.ToArray());
                }
            }
        }

        private static PayloadData Parse(byte[] b)
        {
            var d = new PayloadData();
            if (b.Length < 12 || b[0] != (byte)'T' || b[1] != (byte)'B' ||
                b[2] != (byte)'Z' || b[3] != (byte)'2')
                throw new InvalidOperationException("安装包数据格式无效。");

            int pos = 4;
            int n = ReadInt(b, ref pos);
            if (n < 0 || n > 100000) throw new InvalidOperationException("安装包数据损坏。");
            for (int i = 0; i < n; i++)
            {
                var p = new Patch();
                p.Offset = ReadInt(b, ref pos);
                p.Old = ReadBlock(b, ref pos);
                p.New = ReadBlock(b, ref pos);
                d.Patches.Add(p);
            }
            d.Text = ReadBlock(b, ref pos);
            d.Version = Encoding.UTF8.GetString(ReadBlock(b, ref pos));
            d.OrigExeSize = ReadInt(b, ref pos);
            d.OrigExeSha = ReadBlock(b, ref pos);
            d.OrigTextSha = ReadBlock(b, ref pos);
            // Optional trailer (payload v2): compile-time length immediates to widen.
            if (pos + 4 <= b.Length)
            {
                int nImm = ReadInt(b, ref pos);
                if (nImm < 0 || nImm > 100000)
                    throw new InvalidOperationException("安装包数据损坏。");
                for (int i = 0; i < nImm; i++)
                {
                    var e = new LenEdit();
                    e.Offset = ReadInt(b, ref pos);
                    e.OldValue = ReadInt(b, ref pos);
                    e.NewValue = ReadInt(b, ref pos);
                    d.LenEdits.Add(e);
                }
            }
            return d;
        }

        private static int ReadInt(byte[] b, ref int pos)
        {
            if (pos + 4 > b.Length) throw new InvalidOperationException("安装包数据被截断。");
            int v = b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24);
            pos += 4;
            return v;
        }

        private static byte[] ReadBlock(byte[] b, ref int pos)
        {
            int len = ReadInt(b, ref pos);
            if (len < 0 || pos + len > b.Length) throw new InvalidOperationException("安装包数据被截断。");
            var outB = new byte[len];
            Buffer.BlockCopy(b, pos, outB, 0, len);
            pos += len;
            return outB;
        }
    }

    // ============================================================ util

    internal static class Util
    {
        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        public static string Sha256File(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Find the Toolbag 5 install directory from the uninstall registry key.</summary>
        public static string FindInstallDir()
        {
            string[] roots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string root in roots)
                {
                    try
                    {
                        using (RegistryKey k = hive.OpenSubKey(root + @"\MSET_Toolbag5"))
                        {
                            if (k == null) continue;
                            string fromUninstall = k.GetValue("UninstallString") as string;
                            if (!string.IsNullOrEmpty(fromUninstall))
                            {
                                string dir = Path.GetDirectoryName(fromUninstall.Trim('"'));
                                if (IsInstallDir(dir)) return dir;
                            }
                            string loc = k.GetValue("InstallLocation") as string;
                            if (IsInstallDir(loc)) return loc;
                        }
                    }
                    catch { }
                }
            }
            foreach (string drive in new[] { "C:", "D:", "E:", "F:", "G:" })
            {
                string guess = drive + @"\Program Files\Marmoset\Toolbag 5";
                if (IsInstallDir(guess)) return guess;
            }
            return null;
        }

        public static bool IsInstallDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                return File.Exists(Path.Combine(dir, "toolbag.exe"));
            }
            catch { return false; }
        }

        public static bool ToolbagRunning()
        {
            try { return Process.GetProcessesByName("toolbag").Length > 0; }
            catch { return false; }
        }

        public static void KillToolbag()
        {
            foreach (Process p in Process.GetProcessesByName("toolbag"))
            {
                try { p.Kill(); p.WaitForExit(10000); } catch { }
            }
        }

        public static int CountLines(byte[] utf8)
        {
            string s = Encoding.UTF8.GetString(utf8);
            int n = 1;
            for (int i = 0; i < s.Length - 1; i++)
                if (s[i] == '\r' && s[i + 1] == '\n') n++;
            return n;
        }

        public static string ExePath { get { return Application.ExecutablePath; } }
    }

    // ============================================================ theme

    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(24, 26, 30);
        public static readonly Color Card = Color.FromArgb(32, 35, 41);
        public static readonly Color CardHi = Color.FromArgb(40, 44, 51);
        public static readonly Color Line = Color.FromArgb(58, 63, 72);
        public static readonly Color Text = Color.FromArgb(232, 235, 240);
        public static readonly Color Dim = Color.FromArgb(150, 157, 168);
        public static readonly Color Accent = Color.FromArgb(88, 166, 255);
        public static readonly Color AccentHi = Color.FromArgb(116, 184, 255);
        public static readonly Color Ok = Color.FromArgb(74, 201, 138);
        public static readonly Color Warn = Color.FromArgb(240, 178, 74);
        public static readonly Color Err = Color.FromArgb(238, 110, 110);

        public static Font Ui(float size, FontStyle style = FontStyle.Regular)
        {
            string[] prefer = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
            foreach (string name in prefer)
            {
                try
                {
                    var f = new Font(name, size, style);
                    if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }
    }

    internal static class Draw
    {
        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return path;
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class FlatButton : Button
    {
        public bool Primary;
        private bool _hover;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Height = 38;
            Font = Theme.Ui(9.5f);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Primary ? (_hover ? Theme.AccentHi : Theme.Accent)
                                 : (_hover ? Theme.CardHi : Theme.Card);
            Color text = Primary ? Color.FromArgb(16, 18, 22) : Theme.Text;
            if (!Enabled) { fill = Theme.Card; text = Theme.Dim; }
            using (var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 6))
            using (var b = new SolidBrush(fill))
            {
                g.FillPath(b, path);
                if (!Primary) using (var p = new Pen(Theme.Line)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class ProgressBar2 : Control
    {
        private int _value;
        public int Value
        {
            get { return _value; }
            set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        public ProgressBar2()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = 8;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
            using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
            if (_value <= 0) return;
            int w = Math.Max(Height, Width * _value / 100);
            using (var path = Draw.Rounded(new Rectangle(0, 0, w - 1, Height - 1), Height / 2))
            using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
        }
    }

    // ============================================================ base form

    internal abstract class BaseForm : Form
    {
        protected readonly Label Head = new Label();
        protected readonly Label Sub = new Label();

        protected BaseForm()
        {
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9.5f);
            ClientSize = new Size(620, 452);
            Text = Program.AppTitle;
            Icon = LoadOwnIcon();

            Head.AutoSize = false;
            Head.SetBounds(28, 22, 564, 30);
            Head.Font = Theme.Ui(15f, FontStyle.Bold);
            Head.ForeColor = Theme.Text;
            Head.BackColor = Color.Transparent;
            Head.Text = Program.AppTitle;

            Sub.AutoSize = false;
            Sub.SetBounds(30, 58, 564, 22);
            Sub.Font = Theme.Ui(9f);
            Sub.ForeColor = Theme.Dim;
            Sub.BackColor = Color.Transparent;
            Sub.Text = "为 Marmoset Toolbag 5 安装简体中文界面与提示文本";

            Controls.Add(Head);
            Controls.Add(Sub);
        }

        private static Icon LoadOwnIcon()
        {
            try
            {
                Icon i = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (i != null) return i;
            }
            catch { }
            return null;
        }

        protected static Label MakeLabel(string text, int x, int y, int w, int h, float size,
                                         Color color, FontStyle style = FontStyle.Regular)
        {
            var l = new Label();
            l.AutoSize = false;
            l.SetBounds(x, y, w, h);
            l.Text = text;
            l.Font = Theme.Ui(size, style);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            return l;
        }

        protected void SetStatus(string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, color))); return; }
            Sub.Text = text;
            Sub.ForeColor = color;
        }

        /// <summary>Whether this instance runs unattended (/S).</summary>
        protected abstract bool Silent { get; }

        /// <summary>
        /// Ask a yes/no question.  Unattended runs have nobody to answer, so the decision is
        /// logged and the safe default returned.  A silent install must never block on a
        /// modal dialog, which is invisible and looks like a hang.
        /// </summary>
        protected bool Confirm(string message, bool safeDefault)
        {
            Trace.Write("CONFIRM: " + message.Replace("\r\n", " | "));
            if (Silent) return safeDefault;
            return MessageBox.Show(this, message, Program.AppTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        /// <summary>Report an error; in unattended mode it only goes to the log.</summary>
        protected void Fail(string message)
        {
            Trace.Write("FAIL: " + message.Replace("\r\n", " | "));
            if (!Silent)
                MessageBox.Show(this, message, Program.AppTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ============================================================ install

    internal sealed class InstallForm : BaseForm
    {
        private readonly bool _silent;
        private readonly TextBox _path = new TextBox();
        private readonly FlatButton _browse = new FlatButton();
        private readonly FlatButton _install = new FlatButton();
        private readonly FlatButton _close = new FlatButton();
        private readonly CheckBox _launch = new CheckBox();
        private readonly ProgressBar2 _bar = new ProgressBar2();
        private readonly Label _log = new Label();
        private readonly Panel _card = new Panel();
        private bool _busy;

        protected override bool Silent { get { return _silent; } }

        public InstallForm(bool silent)
        {
            _silent = silent;
            Text = Program.AppTitle + "  v" + Program.Version;

            _card.SetBounds(28, 92, 564, 152);
            _card.BackColor = Theme.Card;
            _card.Paint += (s, e) =>
            {
                using (var p = new Pen(Theme.Line))
                    e.Graphics.DrawRectangle(p, 0, 0, _card.Width - 1, _card.Height - 1);
            };
            Controls.Add(_card);

            _card.Controls.Add(MakeLabel("Toolbag 5 安装目录", 18, 14, 300, 20, 9f, Theme.Dim));

            _path.SetBounds(18, 38, 448, 26);
            _path.BackColor = Theme.CardHi;
            _path.ForeColor = Theme.Text;
            _path.BorderStyle = BorderStyle.FixedSingle;
            _path.Font = Theme.Ui(9.5f);
            _card.Controls.Add(_path);

            _browse.Text = "浏览…";
            _browse.SetBounds(474, 37, 72, 28);
            _browse.Height = 28;
            _browse.Font = Theme.Ui(9f);
            _browse.Click += (s, e) => Browse();
            _card.Controls.Add(_browse);

            _card.Controls.Add(MakeLabel("本语言包包含", 18, 74, 200, 20, 9f, Theme.Dim));
            _card.Controls.Add(MakeLabel(
                "• 菜单、面板标题、按钮与选项标签汉化\r\n" +
                "• 工具提示与说明文字汉化\r\n" +
                "• 安装前自动备份原文件，可随时卸载还原英文",
                18, 96, 528, 48, 8.8f, Theme.Text));

            _launch.SetBounds(28, 258, 340, 24);
            _launch.Text = "安装完成后启动 Toolbag";
            _launch.ForeColor = Theme.Text;
            _launch.BackColor = Color.Transparent;
            _launch.FlatStyle = FlatStyle.Flat;
            _launch.Checked = true;
            Controls.Add(_launch);

            _bar.SetBounds(28, 296, 564, 8);
            Controls.Add(_bar);

            _log.AutoSize = false;
            _log.SetBounds(28, 310, 564, 34);
            _log.Font = Theme.Ui(8.8f);
            _log.ForeColor = Theme.Dim;
            _log.BackColor = Color.Transparent;
            Controls.Add(_log);

            _install.Text = "开始安装";
            _install.Primary = true;
            _install.SetBounds(376, 370, 100, 38);
            _install.Click += (s, e) => DoInstall();
            Controls.Add(_install);

            _close.Text = "退出";
            _close.SetBounds(492, 370, 100, 38);
            _close.Click += (s, e) => Close();
            Controls.Add(_close);

            string found = Util.FindInstallDir();
            if (found != null) _path.Text = found;
            else SetStatus("未检测到 Toolbag 5，请手动选择安装目录", Theme.Warn);

            if (_silent)
            {
                Opacity = 0;
                ShowInTaskbar = false;
                Shown += (s, e) => { _launch.Checked = false; DoInstall(); };
            }
        }

        private void Browse()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "请选择 Marmoset Toolbag 5 安装目录（其中包含 toolbag.exe）";
                if (Directory.Exists(_path.Text)) d.SelectedPath = _path.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _path.Text = d.SelectedPath;
            }
        }

        private void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(s))); return; }
            _log.Text = s;
        }

        private void DoInstall()
        {
            if (_busy) return;
            string dir = _path.Text.Trim().Trim('"');
            if (!Util.IsInstallDir(dir))
            {
                Fail("所选目录中没有找到 toolbag.exe。\r\n请选择正确的 Marmoset Toolbag 5 安装目录。");
                return;
            }
            if (Util.ToolbagRunning())
            {
                if (!Confirm("Toolbag 正在运行，需要先关闭才能安装。\r\n\r\n是否立即关闭？", true))
                    return;
                Util.KillToolbag();
                Thread.Sleep(1500);
            }

            _busy = true;
            _install.Enabled = false;
            _browse.Enabled = false;
            _path.Enabled = false;

            var t = new Thread(() => InstallWorker(dir));
            t.IsBackground = true;
            t.Start();
        }

        private void InstallWorker(string dir)
        {
            try
            {
                Trace.Write("install start, dir=" + dir);
                SetProgress(5, "正在读取语言包数据…");
                PayloadData pl = PayloadData.Load();
                Trace.Write($"payload loaded: patches={pl.Patches.Count} text={pl.Text.Length} " +
                          $"version={pl.Version} origSize={pl.OrigExeSize}");
                SetProgress(10, "已载入 " + pl.Patches.Count + " 处界面文本");

                string exePath = Path.Combine(dir, "toolbag.exe");
                string txtPath = Path.Combine(dir, "data", "text", "english.txt");
                if (!File.Exists(txtPath))
                {
                    Fail("未找到 data\\text\\english.txt，安装目录可能不完整。");
                    FinishAbort();
                    return;
                }

                byte[] exe = File.ReadAllBytes(exePath);
                Trace.Write($"read exe: {exe.Length} bytes sha={Util.Sha256(exe).Substring(0, 16)}");
                SetProgress(18, "已读取 toolbag.exe");

                bool pristine = exe.Length == pl.OrigExeSize &&
                    Util.SameBytes(HexUtil.FromHex(Util.Sha256(exe)), pl.OrigExeSha);
                Trace.Write("pristine=" + pristine);

                if (!pristine)
                {
                    if (PatchEngine.LooksPatched(exe, pl))
                    {
                        Trace.Write("already patched");
                        if (!Confirm("该 Toolbag 已经安装过汉化。\r\n\r\n是否重新安装（覆盖现有汉化）？",
                                     true))
                        {
                            Trace.Write("user declined reinstall");
                            FinishAbort();
                            return;
                        }
                    }
                    else
                    {
                        Trace.Write("version mismatch, asking user");
                        if (!Confirm(
                            "当前 toolbag.exe 与语言包支持的目标版本不一致。\r\n\r\n" +
                            "语言包针对 " + Program.SupportedBuild + " 制作。\r\n" +
                            "继续安装可能失败，或造成界面文本错位。\r\n\r\n是否仍要继续？",
                            false))
                        {
                            Trace.Write("user declined mismatched version");
                            FinishAbort();
                            return;
                        }
                    }
                }

                SetProgress(28, "正在备份原文件…");
                BackupStore.Backup(dir, exePath, txtPath, pl, pristine);
                Trace.Write("backup done");

                SetProgress(42, "正在汉化 toolbag.exe…");
                int applied = PatchEngine.Apply(exe, pl);
                Trace.Write("patched " + applied + " strings, writing exe");
                SetProgress(72, "已应用 " + applied + " 处界面文本");

                // Write to a temp file then swap, so an interrupted write cannot leave the
                // user with a half-patched executable.
                string tmpExe = exePath + ".zhnew";
                File.WriteAllBytes(tmpExe, exe);
                Trace.Write("temp exe written, replacing original");
                File.Copy(tmpExe, exePath, true);
                File.Delete(tmpExe);
                Trace.Write("exe replaced");

                SetProgress(82, "正在写入语言文本…");
                File.WriteAllBytes(txtPath, pl.Text);
                Trace.Write("text written");

                SetProgress(88, "正在汉化工作区标签…");
                int ws = 0;
                try { ws = WorkspaceLabels.Apply(); }
                catch (Exception ex) { Trace.Write("workspaces: apply failed " + ex.Message); }

                SetProgress(92, "正在注册卸载信息…");
                RegistryWriter.Register(dir);

                int lines = Util.CountLines(pl.Text);
                SetProgress(100, "安装完成");
                SetStatus("汉化安装完成", Theme.Ok);
                Log("完成：" + applied + " 处界面文本，" + lines + " 行语言文本" +
                    (ws > 0 ? "，" + ws + " 个工作区标签" : ""));
                Trace.Write($"install success: applied={applied} lines={lines} workspaces={ws}");

                if (InvokeRequired) BeginInvoke(new Action(AfterInstall));
                else AfterInstall();
            }
            catch (Exception ex)
            {
                Trace.Write("EXCEPTION: " + ex);
                SetProgress(0, "");
                SetStatus("安装失败", Theme.Err);
                Fail("安装过程中出现错误：\r\n\r\n" + ex.Message +
                     "\r\n\r\n如果程序已被修改，可重新安装 Toolbag 或使用本程序卸载。");
                FinishAbort();
            }
        }

        private void AfterInstall()
        {
            _busy = false;
            _install.Enabled = true;
            _browse.Enabled = true;
            _path.Enabled = true;
            _install.Text = "重新安装";
            _close.Text = "完成";

            bool launch = _launch.Checked;
            if (_silent) { Close(); return; }

            MessageBox.Show(this,
                "汉化安装完成。\r\n\r\n" +
                "• 已自动备份原文件，可随时卸载还原英文\r\n" +
                "• 卸载方式：在「设置 → 应用」中找到本语言包并卸载，\r\n" +
                "  或再次运行本程序并选择「卸载汉化」",
                Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (launch)
            {
                try { Process.Start(Path.Combine(_path.Text.Trim(), "toolbag.exe")); } catch { }
            }
            Close();
        }

        private void FinishAbort()
        {
            if (InvokeRequired) { BeginInvoke(new Action(FinishAbort)); return; }
            _busy = false;
            // An unattended run has no user to dismiss the window, so it must terminate or it
            // would linger invisibly forever.
            if (_silent) { Close(); return; }
            _install.Enabled = true;
            _browse.Enabled = true;
            _path.Enabled = true;
        }

        private void SetProgress(int value, string message)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(value, message))); return; }
            _bar.Value = value;
            if (!string.IsNullOrEmpty(message)) Log(message);
        }
    }

    // ============================================================ engine

    internal static class PatchEngine
    {
        /// <summary>True when every original byte matches, i.e. the file is the expected build.</summary>
        public static bool AllPatchesMatch(byte[] exe, PayloadData pl)
        {
            foreach (Patch p in pl.Patches)
            {
                if (p.Offset < 0 || p.Offset + p.Old.Length > exe.Length) return false;
                for (int i = 0; i < p.Old.Length; i++)
                    if (exe[p.Offset + i] != p.Old[i]) return false;
            }
            return pl.Patches.Count > 0;
        }

        /// <summary>True when most patches already hold the translated bytes.</summary>
        public static bool LooksPatched(byte[] exe, PayloadData pl)
        {
            int hits = 0, checkedN = 0;
            foreach (Patch p in pl.Patches)
            {
                if (p.Offset < 0 || p.Offset + p.New.Length > exe.Length) continue;
                checkedN++;
                bool same = true;
                for (int i = 0; i < p.New.Length; i++)
                    if (exe[p.Offset + i] != p.New[i]) { same = false; break; }
                if (same) hits++;
            }
            return checkedN > 0 && hits * 2 > checkedN;
        }

        /// <summary>
        /// Write every translation into its string slot.  Each slot is the original text plus
        /// the NUL padding that follows it, so a Chinese string may be longer than the English
        /// one as long as it fits.  The original bytes are re-verified immediately before
        /// writing, so a stale offset can never corrupt the binary.
        /// </summary>
        public static int Apply(byte[] exe, PayloadData pl)
        {
            int applied = 0;
            foreach (Patch p in pl.Patches)
            {
                if (p.Offset < 0 || p.Offset + p.Old.Length > exe.Length)
                    throw new InvalidOperationException("补丁位置超出文件范围，版本可能不匹配。");

                for (int i = 0; i < p.Old.Length; i++)
                {
                    if (exe[p.Offset + i] != p.Old[i])
                        throw new InvalidOperationException(
                            "原文件内容与语言包预期不一致，无法安全写入。\r\n" +
                            "请确认 Toolbag 版本为 " + Program.SupportedBuild + "。");
                }

                int slotEnd = p.Offset + p.Old.Length;
                while (slotEnd < exe.Length && exe[slotEnd] == 0) slotEnd++;
                int slot = slotEnd - p.Offset;

                if (p.New.Length > slot - 1)
                    throw new InvalidOperationException("内部错误：译文超出可用空间。");

                for (int i = 0; i < p.New.Length; i++) exe[p.Offset + i] = p.New[i];
                for (int i = p.New.Length; i < slot; i++) exe[p.Offset + i] = 0;
                applied++;
            }

            // Widen the compile-time length immediates belonging to the strings that grew.
            foreach (LenEdit e in pl.LenEdits)
            {
                if (e.Offset < 0 || e.Offset + 4 > exe.Length)
                    throw new InvalidOperationException("补丁位置超出文件范围，版本可能不匹配。");
                int cur = exe[e.Offset] | (exe[e.Offset + 1] << 8) |
                          (exe[e.Offset + 2] << 16) | (exe[e.Offset + 3] << 24);
                if (cur != e.OldValue)
                    throw new InvalidOperationException(
                        "原文件内容与语言包预期不一致，无法安全写入。\r\n" +
                        "请确认 Toolbag 版本为 " + Program.SupportedBuild + "。");
                exe[e.Offset] = (byte)(e.NewValue & 0xFF);
                exe[e.Offset + 1] = (byte)((e.NewValue >> 8) & 0xFF);
                exe[e.Offset + 2] = (byte)((e.NewValue >> 16) & 0xFF);
                exe[e.Offset + 3] = (byte)((e.NewValue >> 24) & 0xFF);
            }
            return applied;
        }
    }

    internal static class HexUtil
    {
        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return new byte[0];
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }

    // ============================================================ backup

    internal static class BackupStore
    {
        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ToolbagZh");
            }
        }

        public static string ExeBackup { get { return Path.Combine(Dir, "toolbag.exe.orig"); } }
        public static string TextBackup { get { return Path.Combine(Dir, "english.txt.orig"); } }
        public static string InfoFile { get { return Path.Combine(Dir, "install.txt"); } }

        public static void Backup(string dir, string exePath, string txtPath,
                                  PayloadData pl, bool pristine)
        {
            Directory.CreateDirectory(Dir);

            // Only ever snapshot a pristine original; never overwrite a good backup with a
            // patched file, otherwise uninstall would restore Chinese.
            if (!File.Exists(ExeBackup) && pristine)
                File.Copy(exePath, ExeBackup, true);

            if (!File.Exists(TextBackup))
            {
                byte[] t = File.ReadAllBytes(txtPath);
                if (Util.SameBytes(HexUtil.FromHex(Util.Sha256(t)), pl.OrigTextSha))
                    File.Copy(txtPath, TextBackup, true);
            }

            var ini = new StringBuilder();
            ini.AppendLine("dir=" + dir);
            ini.AppendLine("version=" + Program.Version);
            ini.AppendLine("app_version=" + pl.Version);
            File.WriteAllText(InfoFile, ini.ToString(), Encoding.UTF8);
        }

        public static string ReadDir()
        {
            if (File.Exists(InfoFile))
            {
                foreach (string line in File.ReadAllLines(InfoFile))
                    if (line.StartsWith("dir=", StringComparison.Ordinal))
                    {
                        string d = line.Substring(4).Trim();
                        if (Util.IsInstallDir(d)) return d;
                    }
            }
            return Util.FindInstallDir();
        }
    }

    // ============================================================ registry

    internal static class RegistryWriter
    {
        public static void Register(string installDir)
        {
            try
            {
                // Keep a copy of the installer so the uninstall entry always works.
                string self = Util.ExePath;
                string kept = Path.Combine(BackupStore.Dir, "Toolbag5_汉化补丁.exe");
                if (!string.Equals(self, kept, StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, kept, true);

                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(Program.UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", Program.AppTitle);
                    k.SetValue("DisplayVersion", Program.Version);
                    k.SetValue("Publisher", "汉化补丁（非官方）");
                    k.SetValue("InstallLocation", installDir);
                    k.SetValue("DisplayIcon", kept);
                    k.SetValue("UninstallString", "\"" + kept + "\" /uninstall");
                    k.SetValue("QuietUninstallString", "\"" + kept + "\" /uninstall /S");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", EstimateKb(), RegistryValueKind.DWord);
                }
            }
            catch
            {
                // Registry access is a convenience only; never fail the install over it.
            }
        }

        public static void Unregister()
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(Program.UninstallKey, false);
            }
            catch { }
        }

        private static int EstimateKb()
        {
            try
            {
                int kb = 0;
                foreach (string f in new[] { BackupStore.ExeBackup, BackupStore.TextBackup })
                    if (File.Exists(f)) kb += (int)(new FileInfo(f).Length / 1024);
                return kb;
            }
            catch { return 0; }
        }
    }

    // ============================================================ workspace labels

    /// <summary>
    /// Workspace tab captions are the *file names* of the workspace json files in the user's
    /// profile, so the only way to translate them is to rename the files.  The app resolves
    /// `workspaceOrder` / `activeWorkspace` in prefs.txt by name, so a rename that does not also
    /// update prefs.txt makes the app fail to start.  Both sides are therefore changed together,
    /// and the original names are recorded so uninstall can undo it exactly.
    /// </summary>
    internal static class WorkspaceLabels
    {
        private static readonly string[][] Map =
        {
            new[] { "Animate", "动画" },
            new[] { "Render", "渲染" },
            new[] { "Setup", "设置" },
            new[] { "Texture", "贴图" },
            new[] { "Classic", "经典" },
            new[] { "Workspace 1", "工作区 1" },
        };

        public static string BackupDir { get { return Path.Combine(BackupStore.Dir, "workspaces"); } }
        public static string ManifestFile { get { return Path.Combine(BackupDir, "renames.txt"); } }

        /// <summary>Locate the interactive user's Toolbag profile directory.</summary>
        public static string FindProfileDir()
        {
            var candidates = new List<string>();
            try
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Marmoset Toolbag 5"));
            }
            catch { }

            // Elevation can switch the effective profile; fall back to scanning other profiles.
            try
            {
                string users = Path.GetDirectoryName(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (!string.IsNullOrEmpty(users))
                    foreach (string d in Directory.GetDirectories(users))
                        candidates.Add(Path.Combine(d, "AppData", "Local", "Marmoset Toolbag 5"));
            }
            catch { }

            // Prefer a profile that actually holds one of the workspaces we know how to rename,
            // so an elevated run never edits the administrator's unused profile.
            foreach (string c in candidates)
            {
                string wsDir = Path.Combine(c, "workspaces");
                if (!Directory.Exists(wsDir)) continue;
                foreach (string[] pair in Map)
                    if (File.Exists(Path.Combine(wsDir, pair[0] + ".json")) ||
                        File.Exists(Path.Combine(wsDir, pair[1] + ".json")))
                        return c;
            }

            foreach (string c in candidates)
                if (Directory.Exists(Path.Combine(c, "workspaces"))) return c;
            return null;
        }

        private static string PrefsPath(string profile)
        {
            return Path.Combine(profile, "prefs.txt");
        }

        private static string WorkspacesPath(string profile)
        {
            return Path.Combine(profile, "workspaces");
        }

        public static int Apply()
        {
            string profile = FindProfileDir();
            if (profile == null) { Trace.Write("workspaces: profile not found"); return 0; }
            string wsDir = WorkspacesPath(profile);
            string prefs = PrefsPath(profile);

            Directory.CreateDirectory(BackupDir);

            // Snapshot prefs.txt once, before the first rename, as a last-resort fallback.
            string prefsBak = Path.Combine(BackupDir, "prefs.txt.orig");
            if (File.Exists(prefs) && !File.Exists(prefsBak))
                File.Copy(prefs, prefsBak, true);

            var done = new List<string>();
            foreach (string[] pair in Map)
            {
                string oldName = pair[0], newName = pair[1];
                string src = Path.Combine(wsDir, oldName + ".json");
                string dst = Path.Combine(wsDir, newName + ".json");
                if (!File.Exists(src) || File.Exists(dst)) continue;
                try
                {
                    File.Move(src, dst);
                    done.Add(oldName + "|" + newName);
                }
                catch (Exception ex)
                {
                    Trace.Write("workspaces: move failed " + oldName + " " + ex.Message);
                }
            }

            if (done.Count == 0) { Trace.Write("workspaces: nothing to rename"); return 0; }

            // Record the manifest before touching prefs, so an interrupted run is still undoable.
            File.WriteAllLines(ManifestFile, done.ToArray(), new UTF8Encoding(false));

            var map = new Dictionary<string, string>();
            foreach (string line in done)
            {
                int bar = line.IndexOf('|');
                if (bar > 0) map[line.Substring(0, bar)] = line.Substring(bar + 1);
            }
            RemapPrefs(prefs, map);

            Trace.Write("workspaces: renamed " + done.Count);
            return done.Count;
        }

        /// <summary>Rewrite workspaceOrder / activeWorkspace using the given name map.</summary>
        private static void RemapPrefs(string prefsPath, Dictionary<string, string> map)
        {
            if (!File.Exists(prefsPath) || map.Count == 0) return;

            string raw = Encoding.UTF8.GetString(File.ReadAllBytes(prefsPath));
            string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.StartsWith("workspaceOrder ", StringComparison.Ordinal))
                {
                    string val = ln.Substring("workspaceOrder ".Length);
                    var parts = new List<string>();
                    foreach (string p in val.Split(';'))
                        if (p.Length > 0) parts.Add(map.ContainsKey(p) ? map[p] : p);
                    lines[i] = "workspaceOrder " + string.Join(";", parts.ToArray()) + ";";
                }
                else if (ln.StartsWith("activeWorkspace ", StringComparison.Ordinal))
                {
                    string val = ln.Substring("activeWorkspace ".Length).Trim();
                    if (map.ContainsKey(val)) lines[i] = "activeWorkspace " + map[val];
                }
            }

            File.WriteAllBytes(prefsPath, Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));
        }

        public static void Restore()
        {
            string profile = FindProfileDir();
            if (profile == null) { Trace.Write("workspaces: profile not found for restore"); return; }
            string wsDir = WorkspacesPath(profile);

            var back = new Dictionary<string, string>();   // new -> old
            if (File.Exists(ManifestFile))
            {
                foreach (string line in File.ReadAllLines(ManifestFile))
                {
                    int bar = line.IndexOf('|');
                    if (bar > 0) back[line.Substring(bar + 1)] = line.Substring(0, bar);
                }
            }

            int n = 0;
            foreach (var kv in back)
            {
                string src = Path.Combine(wsDir, kv.Key + ".json");
                string dst = Path.Combine(wsDir, kv.Value + ".json");
                if (!File.Exists(src) || File.Exists(dst)) continue;
                try { File.Move(src, dst); n++; } catch { }
            }

            // Reverse-map prefs so other settings the app changed meanwhile are preserved.
            RemapPrefs(PrefsPath(profile), back);
            Trace.Write("workspaces: restored " + n);
        }
    }

    // ============================================================ uninstall

    internal sealed class UninstallForm : BaseForm
    {
        private readonly bool _silent;
        private readonly FlatButton _run = new FlatButton();
        private readonly FlatButton _close = new FlatButton();
        private readonly ProgressBar2 _bar = new ProgressBar2();
        private readonly Label _info = new Label();
        private bool _busy;

        protected override bool Silent { get { return _silent; } }

        public UninstallForm(bool silent)
        {
            _silent = silent;
            Text = Program.AppTitle + " — 卸载";
            Head.Text = "卸载汉化，还原英文";
            Sub.Text = "从备份恢复原始文件，使 Toolbag 回到英文界面";

            _info.AutoSize = false;
            _info.SetBounds(28, 96, 564, 158);
            _info.BackColor = Theme.Card;
            _info.ForeColor = Theme.Text;
            _info.Font = Theme.Ui(9.2f);
            _info.Padding = new Padding(16, 14, 16, 14);
            _info.Text = "正在检查备份…";
            Controls.Add(_info);

            _bar.SetBounds(28, 272, 564, 8);
            Controls.Add(_bar);

            _run.Text = "卸载汉化";
            _run.Primary = true;
            _run.SetBounds(376, 300, 100, 38);
            _run.Click += (s, e) => DoUninstall();
            Controls.Add(_run);

            _close.Text = "退出";
            _close.SetBounds(492, 300, 100, 38);
            _close.Click += (s, e) => Close();
            Controls.Add(_close);

            Load += (s, e) =>
            {
                bool ok = File.Exists(BackupStore.ExeBackup) && File.Exists(BackupStore.TextBackup);
                if (ok)
                {
                    _info.Text =
                        "检测到汉化备份：\r\n\r\n  " + BackupStore.Dir + "\r\n\r\n" +
                        "点击「卸载汉化」将把 toolbag.exe 与 english.txt 恢复为原始英文版本。";
                }
                else
                {
                    _info.Text = "未找到汉化备份，无法卸载。\r\n\r\n备份目录：" + BackupStore.Dir;
                    _info.ForeColor = Theme.Warn;
                    _run.Enabled = false;
                }
                if (_silent) { Opacity = 0; ShowInTaskbar = false; DoUninstall(); }
            };
        }

        private void DoUninstall()
        {
            if (_busy) return;
            Trace.Write("uninstall start");
            if (Util.ToolbagRunning())
            {
                if (!Confirm("Toolbag 正在运行，需要先关闭才能卸载。\r\n\r\n是否立即关闭？", true))
                    return;
                Util.KillToolbag();
                Thread.Sleep(1500);
            }

            _busy = true;
            _run.Enabled = false;
            try
            {
                string dir = BackupStore.ReadDir();
                Trace.Write("uninstall dir=" + (dir ?? "<null>"));
                if (dir == null)
                {
                    Fail("找不到 Toolbag 安装目录，无法卸载。\r\n" +
                         "请手动从备份目录恢复文件：" + BackupStore.Dir);
                    _run.Enabled = true;
                    _busy = false;
                    return;
                }

                _bar.Value = 20;
                File.Copy(BackupStore.ExeBackup, Path.Combine(dir, "toolbag.exe"), true);
                Trace.Write("exe restored");
                _bar.Value = 60;
                File.Copy(BackupStore.TextBackup,
                          Path.Combine(dir, "data", "text", "english.txt"), true);
                Trace.Write("text restored");
                _bar.Value = 80;

                try { WorkspaceLabels.Restore(); }
                catch (Exception ex) { Trace.Write("workspaces: restore failed " + ex.Message); }
                Trace.Write("workspace labels restored");
                _bar.Value = 85;

                RegistryWriter.Unregister();
                Trace.Write("registry entry removed");
                _bar.Value = 100;

                SetStatus("已还原为英文", Theme.Ok);
                _info.Text = "卸载完成，Toolbag 已恢复英文界面。";
                Trace.Write("uninstall success");

                if (!_silent)
                {
                    MessageBox.Show(this, "汉化已卸载，Toolbag 已恢复英文界面。",
                        Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                Close();
            }
            catch (Exception ex)
            {
                Trace.Write("UNINSTALL EXCEPTION: " + ex);
                Fail("卸载失败：\r\n\r\n" + ex.Message);
                _run.Enabled = true;
                _busy = false;
            }
        }
    }
}
