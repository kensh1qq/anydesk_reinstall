using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ÄÄ ????? ????? ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
static class Program
{
    const string GOOGLE_DRIVE_FILE_ID = "YOUR_GOOGLE_DRIVE_FILE_ID_HERE";

    // ÄÄ ?????? ?????????? - ??????? ?? <Version> ? .csproj, ????? ?? ?????????????????????.
    //    ??? ?????? ?????????? ??????? ?? ? .csproj ? ? version.json. ÄÄÄÄÄÄÄÄÄÄÄÄ
    internal static readonly string CURRENT_VERSION =
        typeof(Program).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.3";
    // URL ????? ?????? ?? GitHub (raw). ??????: {"version":"1.0.1","url":"https://.../AnydeskReinstaller.exe"}
    internal const string UPDATE_CHECK_URL = "https://raw.githubusercontent.com/kensh1qq/anydesk_reinstall/main/version.json";

    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]   static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ?????? ??????? ????? ?? ???? ??????? (???? static ?? ???? ???????? ??? ???????).
    static Mutex _singleInstance;

    [STAThread]
    static void Main()
    {
        if (!IsAdmin())
        {
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? "")
                { UseShellExecute = true, Verb = "runas" }); }
            catch { }
            return;
        }

        // ?????? ???? ?????. Global\ - ??????? ????? ???? ??????? (????? ??? ???????????).
        _singleInstance = new Mutex(true, @"Global\AnydeskReinstaller_SingleInstance", out bool isNew);
        if (!isNew) return;

        IntPtr hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 0);

        RegisterAutostart();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(GOOGLE_DRIVE_FILE_ID));
    }

    static void RegisterAutostart()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue("AnydeskReinstaller", $"\"{exePath}\"");
        }
        catch { }
    }

    static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }
}

// ÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍ
class MainForm : Form
{
    private readonly string _driveFileId;

    // ?????????
    private volatile bool   isReinstalling = false;
    private volatile int    progress       = 0;
    private volatile string statusText     = "????????...";
    private volatile string lastResult     = "-";
    private readonly object _dateLock      = new();
    private DateTime? _lastRun = null;
    private DateTime? lastRun  { get { lock(_dateLock) return _lastRun;  } set { lock(_dateLock) _lastRun = value; } }

    // UI (???????? ??? Windows: ??????????? ????????)
    private ProgressBar progressBar = null!;
    private Label       lblStatusVal = null!, lblLastRunVal = null!, lblLastResVal = null!;
    private TextBox     txtLogs = null!;
    private Button      btnReinstall = null!;
    private NotifyIcon  trayIcon = null!;
    private System.Windows.Forms.Timer uiTimer = null!;

    private readonly List<string> logsQueue = new();
    private readonly object       logsLock  = new();
    private int lastProgress = -1;

    // ?????? ?????????? (???? + ????); ???????? ???? ??? ?? ??????????? ??????? app.ico
    private static readonly Icon AppIcon = LoadAppIcon();
    private static Icon LoadAppIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            if (s != null) return new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }

    public MainForm(string driveFileId)
    {
        _driveFileId = driveFileId;
        InitializeTray();
        InitializeWindow();
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        Log($"? AnyDesk Reinstaller v{Program.CURRENT_VERSION} ???????. ?????? ? ????.");
        Log("? ?????????? ??? ?????? Windows ???????????????.");
        Log("? ????????????? - ???????, ??????? ®?????????????? ??????¯.");
        CheckForUpdateInBackground(); // ????????? ?????????? ? ????
    }

    // ÄÄ ???? ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
    private void InitializeTray()
    {
        trayIcon = new NotifyIcon
        {
            Icon    = AppIcon,
            Text    = "AnyDesk Reinstaller",
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        // .NET 6+ WinForms: ContextMenuStrip ?????? ??????????? ContextMenu
        var cms = new ContextMenuStrip();
        cms.Items.Add("??????? ??????",          null, (_, _) => ShowMainWindow());
        cms.Items.Add("?????????????? ??????",   null, (_, _) => { if (!isReinstalling) TriggerReinstall(); });
        cms.Items.Add(new ToolStripSeparator());
        cms.Items.Add("?????",                   null, (_, _) => ExitApp());
        trayIcon.ContextMenuStrip = cms;

        trayIcon.ShowBalloonTip(3000, "AnyDesk Reinstaller",
            "????????? ????????. ????????????? - ???????, ???????.", ToolTipIcon.Info);
    }

    // ÄÄ ???? (?????? ?? ?????????) ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
    private void InitializeWindow()
    {
        Text            = "AnyDesk Reinstaller";
        Font            = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        ClientSize      = new Size(564, 432);
        StartPosition   = FormStartPosition.CenterScreen;
        Icon            = AppIcon;
        ShowInTaskbar   = false;

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideMainWindow(); }
        };

        // ÄÄ ?????? ®?????????¯ ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
        var grpState = new GroupBox { Text = "?????????", Bounds = new Rectangle(12, 8, 366, 150) };
        grpState.Controls.Add(new Label
        {
            Text = "??????? ??????:", Bounds = new Rectangle(14, 28, 100, 20),
            ForeColor = SystemColors.GrayText
        });
        lblStatusVal = new Label
        {
            Text = "????????...", Bounds = new Rectangle(118, 28, 236, 20),
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold)
        };
        grpState.Controls.Add(lblStatusVal);
        progressBar = new ProgressBar
        {
            Bounds = new Rectangle(14, 56, 340, 24), Minimum = 0, Maximum = 100, Value = 0
        };
        grpState.Controls.Add(progressBar);
        grpState.Controls.Add(new Label
        {
            Text = "????????????? AnyDesk ?? ??????, ?????? ? ????, ?????????? Windows.",
            Bounds = new Rectangle(14, 92, 340, 44), ForeColor = SystemColors.GrayText
        });
        Controls.Add(grpState);

        // ÄÄ ?????? ®????????? ????????¯ ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
        var grpInfo = new GroupBox { Text = "????????? ????????", Bounds = new Rectangle(386, 8, 166, 150) };
        AddInfoRow(grpInfo, "????????? ??????:", out lblLastRunVal, 34);
        AddInfoRow(grpInfo, "?????????:",         out lblLastResVal, 84);
        Controls.Add(grpInfo);

        // ÄÄ ?????? ®?????? ????????¯ ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
        var grpLog = new GroupBox { Text = "?????? ????????", Bounds = new Rectangle(12, 165, 540, 210) };
        txtLogs = new TextBox
        {
            Bounds     = new Rectangle(12, 22, 516, 176),
            Multiline  = true,
            ReadOnly   = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor  = Color.White,
            Font       = new Font("Consolas", 9F)
        };
        grpLog.Controls.Add(txtLogs);
        Controls.Add(grpLog);

        // ÄÄ ?????? ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
        btnReinstall = new Button { Text = "?????????????? ??????", Bounds = new Rectangle(12, 388, 180, 32) };
        btnReinstall.Click += (_, _) => { if (!isReinstalling) TriggerReinstall(); };
        Controls.Add(btnReinstall);

        var btnHide = new Button { Text = "???????? ? ????", Bounds = new Rectangle(202, 388, 130, 32) };
        btnHide.Click += (_, _) => HideMainWindow();
        Controls.Add(btnHide);

        var btnAbout = new Button { Text = "? ?????????", Bounds = new Rectangle(342, 388, 120, 32) };
        btnAbout.Click += (_, _) => MessageBox.Show(
            $"AnyDesk Reinstaller v{Program.CURRENT_VERSION}\nby kensh1qq\n\n" +
            "????????????????? AnyDesk ?? ??????,\n???????? ? ????, ?????????? Windows.",
            "? ?????????", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Controls.Add(btnAbout);

        Controls.Add(new Label
        {
            Text = "by kensh1qq", Bounds = new Rectangle(466, 396, 86, 18),
            ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleRight
        });

        // UI ?????? (??????????? ?????? ??? ???????? ????)
        uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        uiTimer.Tick += UiTimer_Tick;
    }

    // ÄÄ ???????? / ?????? ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        uiTimer.Start();
        FlushLogs();
    }

    private void HideMainWindow()
    {
        uiTimer.Stop();
        Hide();
        ShowInTaskbar = false;
    }

    private void ExitApp() { trayIcon.Visible = false; Application.Exit(); }

    // ÄÄ ?????? ???? (??????? ??????, ???????? ?????) ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
    private static void AddInfoRow(Control parent, string title, out Label val, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = title, Bounds = new Rectangle(12, y, 144, 16), ForeColor = SystemColors.GrayText
        });
        val = new Label
        {
            Text = "-", Bounds = new Rectangle(12, y + 16, 144, 20),
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold)
        };
        parent.Controls.Add(val);
    }

    // ÄÄ UI ?????? ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (progress != lastProgress)
        {
            lastProgress = progress;
            progressBar.Value = Math.Max(0, Math.Min(100, progress));
        }

        lblStatusVal.Text  = statusText;
        lblLastRunVal.Text = lastRun.HasValue ? lastRun.Value.ToString("HH:mm  dd.MM") : "-";
        lblLastResVal.Text = lastResult;

        // ?????? ????????? ??????????; ????????? - ??????????? ????????? ???
        if      (lastResult.Contains("???????")) lblLastResVal.ForeColor = Color.Green;
        else if (lastResult.Contains("??????"))  lblLastResVal.ForeColor = Color.Firebrick;
        else                                     lblLastResVal.ForeColor = SystemColors.ControlText;

        btnReinstall.Enabled = !isReinstalling;

        FlushLogs();
    }

    private void FlushLogs()
    {
        lock (logsLock)
        {
            if (logsQueue.Count > 0)
            {
                foreach (var line in logsQueue) txtLogs.AppendText(line + Environment.NewLine);
                logsQueue.Clear();
            }
        }
    }

    private void Log(string line)
    {
        lock (logsLock) logsQueue.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
    }

    // ÄÄ ?????? ????????????? (?????? ???????, ???????) ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
    private void TriggerReinstall() =>
        new Thread(DoReinstall) { IsBackground = true, Name = "Reinstaller", Priority = ThreadPriority.BelowNormal }.Start();

    private void Step(int p, string s)
    {
        progress   = p;
        statusText = s;
        try { if (trayIcon != null) trayIcon.Text = "AnyDesk: " + s[..Math.Min(s.Length, 60)]; } catch { }
    }

    // ÄÄ ?????? ????????????? ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
    private void DoReinstall()
    {
        if (isReinstalling) return;
        isReinstalling = true;
        try { trayIcon.ShowBalloonTip(4000, "AnyDesk Reinstaller", "???????? ????????????? AnyDesk...", ToolTipIcon.Info); } catch { }

        try
        {
            lastRun = DateTime.Now;

            // ??? 1: ??????? ??? ???????? AnyDesk
            Step(5, "????????????? ???????? AnyDesk...");
            Log("[] ????????? anydesk.exe");
            Run("taskkill", "/f /im anydesk.exe");
            Run("taskkill", "/f /im AnyDesk.exe");
            Thread.Sleep(1500);

            // ??? 2: ????????????? ? ??????? ??????
            Step(12, "????????????? ?????? AnyDesk...");
            Log("[] net stop AnyDesk");
            Run("net", "stop AnyDesk");
            Thread.Sleep(1000);
            Log("[] sc delete AnyDesk");
            Run("sc", "delete AnyDesk");
            Thread.Sleep(1000);

            // ??? 3: ?????? ???????? ??? GUI (?????? + ??????)
            Step(25, "??????? ????? AnyDesk...");
            RemoveOld();

            // ??? 4: ?????? ?????? ?? ?????? ??????
            Step(38, "?????? ??????...");
            Log("[] ??????? ?????? ??????? AnyDesk");
            CleanRegistry();
            Thread.Sleep(1000);

            // ??? 5: ????????????? ???? ??????
            Step(50, "????????????? ?????????? v6.0.8...");
            string tmp = Path.Combine(Path.GetTempPath(), "AnyDesk_setup_temp.exe");
            if (File.Exists(tmp)) File.Delete(tmp);
            Extract(tmp);
            Log("[û] ?????????? ?????: " + tmp);

            // ??? 6: ????? ????????? ????? ?????? (??? --remove ?? tmp!)
            Step(70, "????? ????????? v6.0.8...");
            Log("[] ??????: --install --silent");
            Run(tmp, "--install \"C:\\Program Files (x86)\\AnyDesk\" --start-with-win --silent");
            Thread.Sleep(5000);

            // ??? 7: ????????? ??????????????
            Step(88, "????????? ??????????????...");
            Log("[] ????????? auto-update AnyDesk");
            BlockAnyDeskUpdate();

            // ??? 8: ????????? ???????
            Step(95, "????????? ???????...");
            try { File.Delete(tmp); } catch { }

            Step(100, "û ??????!");
            Log("[û] AnyDesk v6.0.8 ??????? ??????????????!");
            lastResult = "???????";
            try { trayIcon.ShowBalloonTip(4000, "AnyDesk Reinstaller", "AnyDesk v6.0.8 ??????????!", ToolTipIcon.Info); } catch { }
            Thread.Sleep(3000);
            Step(0, "??????. ????????.");
        }
        catch (Exception ex)
        {
            Log("[?] ??????: " + ex.Message);
            lastResult = "??????";
            Step(0, "?????? ??? ?????????????.");
            try { trayIcon.ShowBalloonTip(5000, "AnyDesk - ??????", ex.Message, ToolTipIcon.Error); } catch { }
        }
        finally { isReinstalling = false; }
    }

    private static void Run(string cmd, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(cmd, args)
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit();
        }
        catch { }
    }

    // ??????? ????? ? ????? AnyDesk ??????? - ??? ?????? --remove (?? ?????????? GUI!)
    private void RemoveOld()
    {
        string[] dirs =
        {
            @"C:\Program Files (x86)\AnyDesk",
            @"C:\Program Files\AnyDesk",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),        "AnyDesk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),   "AnyDesk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),  "AnyDesk")
        };

        bool found = false;
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            Log("   ??????? ?????: " + dir);
            try { Directory.Delete(dir, true); found = true; }
            catch (Exception ex) { Log("   [!] ?? ??????? ???????: " + ex.Message); }
        }
        if (!found) Log("   ???????????? ????? AnyDesk ?? ???????.");
    }

    // ?????? ?????? ?? ?????? ?????? AnyDesk (????-?????, ????????????? ? ?.?.)
    private void CleanRegistry()
    {
        string[] regKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AnyDesk",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AnyDesk",
            @"SOFTWARE\AnyDesk",
            @"SOFTWARE\WOW6432Node\AnyDesk"
        };

        foreach (string key in regKeys)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(key, false);
                Log("   ?????? ??????: HKLM\\" + key);
            }
            catch { }
        }

        // ??????? AnyDesk ?? ?????????? (?????? ??????)
        try
        {
            using RegistryKey? run = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("AnyDesk", false);
            Log("   ?????? ????????? ?????? ??????");
        }
        catch { }

        // ????? ?? HKCU Run
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("AnyDesk", false);
        }
        catch { }
    }

    // ????????? ?????????????? AnyDesk ????? ??????
    private static void BlockAnyDeskUpdate()
    {
        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\AnyDesk");
            key.SetValue("DisableUpdateNotifications", 1, RegistryValueKind.DWord);
            key.SetValue("UpdateChannel",              "",  RegistryValueKind.String);
            key.SetValue("AutoUpdateEnabled",          0,  RegistryValueKind.DWord);
        }
        catch { }
        // WOW6432Node
        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WOW6432Node\AnyDesk");
            key.SetValue("DisableUpdateNotifications", 1, RegistryValueKind.DWord);
            key.SetValue("UpdateChannel",              "",  RegistryValueKind.String);
            key.SetValue("AutoUpdateEnabled",          0,  RegistryValueKind.DWord);
        }
        catch { }
    }

    private void Extract(string outPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        string[] names = { "AnyDesk-6-0-8-without_advertising.exe", "AnyDesk.exe", "anydesk.exe" };

        // 1. ?? ?????????? ????????
        foreach (string rn in names)
        {
            using var stream = asm.GetManifestResourceStream(rn);
            if (stream == null) continue;
            Log("[û] ????????????? ?? ???????? EXE...");
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            stream.CopyTo(fs);
            return;
        }

        // 2. ???? ????? ? EXE
        string exeDir = AppContext.BaseDirectory;
        foreach (string name in names)
        {
            string lp = Path.Combine(exeDir, name);
            if (!File.Exists(lp)) continue;
            File.Copy(lp, outPath, true);
            Log("[û] ??????????? ????????? ????: " + name);
            return;
        }

        // 3. ????????? - HttpClient ?????? ??????????? WebRequest
        bool useGDrive = !string.IsNullOrEmpty(_driveFileId) && !_driveFileId.Contains("YOUR_GOOGLE_DRIVE");
        string url = useGDrive
            ? $"https://drive.google.com/uc?export=download&id={_driveFileId}"
            : "https://download.anydesk.com/AnyDesk.exe";

        Log("[] ????????? ??????????: " + url);

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).Result;
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        using var respStream = response.Content.ReadAsStreamAsync().Result;
        using var fileStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buf = new byte[65536]; long got = 0; int read;
        while ((read = respStream.Read(buf, 0, buf.Length)) > 0)
        {
            fileStream.Write(buf, 0, read);
            got += read;
            if (total > 0)
                Step(43 + (int)(got * 14 / total), $"????????: {got * 100 / total}% ({got / 1048576.0:F1} ??)");
            else
                Step(43, $"????????: {got / 1048576.0:F1} ??...");
        }
        Log("[û] ?????????? ?????????");
    }

    // ÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍ
    //  ????-?????????? ????????
    //  version.json ?? GitHub: {"version":"1.0.1","url":"https://.../AnydeskReinstaller.exe"}
    // ÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍÍ
    private void CheckForUpdateInBackground()
    {
        new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            Thread.Sleep(5000); // ????????? ???????? - ????? UI ??????? ??????????

            try
            {
                Log("[] ????????? ??????? ?????????? ????????...");

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AnydeskReinstaller/" + Program.CURRENT_VERSION);

                string json = client.GetStringAsync(Program.UPDATE_CHECK_URL).Result;

                // ?????? JSON ??????? (??? ????????????): {"version":"X.Y.Z","url":"..."}
                string remoteVersion = ExtractJsonValue(json, "version");
                string downloadUrl   = ExtractJsonValue(json, "url");

                if (string.IsNullOrEmpty(remoteVersion) || string.IsNullOrEmpty(downloadUrl))
                {
                    Log("[!] ?? ??????? ????????? version.json");
                    return;
                }

                if (CompareVersions(remoteVersion, Program.CURRENT_VERSION) <= 0)
                {
                    Log($"[û] ?????? ????????? (v{Program.CURRENT_VERSION})");
                    return;
                }

                // ???? ??????????!
                Log($"[û] ???????? ??????????: v{Program.CURRENT_VERSION}  v{remoteVersion}");
                try { trayIcon.ShowBalloonTip(5000, "AnyDesk Reinstaller - ??????????",
                    $"????????? v{remoteVersion}...", ToolTipIcon.Info); } catch { }

                // ????????? ????? EXE ?? ????????? ?????
                string tmpExe = Path.Combine(Path.GetTempPath(), $"AnydeskReinstaller_v{remoteVersion}.exe");
                DownloadUpdate(downloadUrl, tmpExe, remoteVersion);

                // ????????? ??????????
                ApplyUpdate(tmpExe, remoteVersion);
            }
            catch (Exception ex)
            {
                // ?? ?????????? ?????? ???????????? - ?????????? ?????????????
                Log("[!] ???????? ?????????? ??????????: " + ex.Message);
            }
        }) { IsBackground = true, Name = "UpdateChecker" }.Start();
    }

    private void DownloadUpdate(string url, string outPath, string version)
    {
        Log($"[] ????????? ?????????? v{version}...");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnydeskReinstaller/" + Program.CURRENT_VERSION);

        using var response   = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).Result;
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        using var rs = response.Content.ReadAsStreamAsync().Result;
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buf = new byte[65536]; long got = 0; int read;
        while ((read = rs.Read(buf, 0, buf.Length)) > 0)
        {
            fs.Write(buf, 0, read);
            got += read;
            string pct = total > 0 ? $"{got * 100 / total}%" : $"{got / 1048576.0:F1} ??";
            Log($"[] ??????????: {pct}");
        }

        Log($"[û] ?????????? v{version} ???????");
    }

    private void ApplyUpdate(string newExePath, string version)
    {
        try
        {
            string currentExe = Environment.ProcessPath!;

            // PowerShell ??????: ???? ???????? ???????? ????????  ???????? ????  ?????????????
            string ps = $@"
$pid_to_wait = {Environment.ProcessId}
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Process -Id $pid_to_wait -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {{
    Start-Sleep -Milliseconds 500
}}
Start-Sleep -Seconds 1
try {{
    Copy-Item -Path '{newExePath}' -Destination '{currentExe}' -Force
    Remove-Item '{newExePath}' -Force -ErrorAction SilentlyContinue
    Start-Process '{currentExe}'
}} catch {{
    # fallback
}}
";
            string scriptPath = Path.Combine(Path.GetTempPath(), "anydesk_reinstaller_update.ps1");
            File.WriteAllText(scriptPath, ps);

            Process.Start(new ProcessStartInfo("powershell",
                $"-ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"{scriptPath}\"")
            {
                UseShellExecute     = true,
                WindowStyle         = ProcessWindowStyle.Hidden,
                CreateNoWindow      = true
            });

            // ?????????? ? ????????? - PowerShell ???????????? ???
            trayIcon.ShowBalloonTip(3000, "AnyDesk Reinstaller",
                $"????????? ?????????? v{version}. ??????????...", ToolTipIcon.Info);
            Thread.Sleep(3500);

            // ??????? - PowerShell ??? ????????????.
            // Application.Exit() ?????? ?????????? ?? UI-??????, ??????? ???????? ????? ?????.
            try
            {
                if (IsHandleCreated)
                    BeginInvoke((Action)(() => { trayIcon.Visible = false; Application.Exit(); }));
                else
                    Application.Exit();
            }
            catch { Application.Exit(); }
        }
        catch (Exception ex)
        {
            Log("[?] ?? ??????? ????????? ??????????: " + ex.Message);
        }
    }

    // ??????? ?????? JSON-???????? ?? ????? (??? ??????? ????????????)
    private static string ExtractJsonValue(string json, string key)
    {
        string search = $"\"{key}\"";
        int idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        idx = json.IndexOf(':', idx + search.Length);
        if (idx < 0) return "";
        idx = json.IndexOf('"', idx + 1);
        if (idx < 0) return "";
        int end = json.IndexOf('"', idx + 1);
        return end < 0 ? "" : json.Substring(idx + 1, end - idx - 1);
    }

    // ????????? ?????? ???? "1.2.3". ?????????? > 0 ???? a > b
    private static int CompareVersions(string a, string b)
    {
        try
        {
            var va = new Version(a);
            var vb = new Version(b);
            return va.CompareTo(vb);
        }
        catch { return 0; }
    }
}


// (ThermoPanel ?????? - ??????? ?? ??????????? ProgressBar ??? ????????? ????)

