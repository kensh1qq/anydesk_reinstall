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

// ── Точка входа ─────────────────────────────────────────────────────────────
static class Program
{
    const string GOOGLE_DRIVE_FILE_ID = "YOUR_GOOGLE_DRIVE_FILE_ID_HERE";

    // ── Версия приложения — меняйте при каждом обновлении в version.json ────────────
    internal const string CURRENT_VERSION  = "1.0.0";
    // URL файла версии на GitHub (raw). Формат: {"version":"1.0.1","url":"https://.../AnydeskReinstaller.exe"}
    internal const string UPDATE_CHECK_URL = "https://raw.githubusercontent.com/kensh1qq/anydesk_reinstall/main/version.json";

    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]   static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

// ════════════════════════════════════════════════════════════════════════════
class MainForm : Form
{
    const int INTERVAL_HOURS = 168; // 1 неделя

    private readonly string _driveFileId;

    // Состояние
    private volatile bool   isReinstalling = false;
    private volatile int    progress       = 0;
    private volatile string statusText     = "Ожидание...";
    private volatile string lastResult     = "—";
    private readonly object _dateLock      = new();
    private DateTime? _lastRun = null;
    private DateTime  _nextRun = DateTime.Now.AddHours(168);
    private DateTime? lastRun  { get { lock(_dateLock) return _lastRun;  } set { lock(_dateLock) _lastRun = value; } }
    private DateTime  nextRun  { get { lock(_dateLock) return _nextRun;  } set { lock(_dateLock) _nextRun = value; } }

    // UI
    private ThermoPanel thermoPanel = null!;
    private Label       lblStatusVal = null!, lblLastRunVal = null!, lblLastResVal = null!, lblNextRunVal = null!;
    private RichTextBox txtLogs = null!;
    private Button      btnReinstall = null!;
    private NotifyIcon  trayIcon = null!;
    private System.Windows.Forms.Timer uiTimer = null!;

    private readonly List<string> logsQueue = new();
    private readonly object       logsLock  = new();
    private int lastProgress = -1;

    public MainForm(string driveFileId)
    {
        _driveFileId = driveFileId;
        InitializeTray();
        InitializeWindow();
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        Log($"❆ AnyDesk Reinstaller v{Program.CURRENT_VERSION} запущен. Иконка в трее.");
        Log("❆ Автозапуск при старте Windows зарегистрирован.");
        Log("⏱ Следующий цикл: " + nextRun.ToString("HH:mm  dd.MM.yyyy"));
        StartMainScheduler();
        CheckForUpdateInBackground(); // Проверяем обновление в фоне
    }

    // ── ТРЕЙ ─────────────────────────────────────────────────────────────────
    private void InitializeTray()
    {
        trayIcon = new NotifyIcon
        {
            Icon    = SystemIcons.Application,
            Text    = "AnyDesk Reinstaller",
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        // .NET 6+ WinForms: ContextMenuStrip вместо устаревшего ContextMenu
        var cms = new ContextMenuStrip();
        cms.Items.Add("Открыть панель",          null, (_, _) => ShowMainWindow());
        cms.Items.Add("Переустановить сейчас",   null, (_, _) => { if (!isReinstalling) TriggerReinstall(); });
        cms.Items.Add(new ToolStripSeparator());
        cms.Items.Add("Выход",                   null, (_, _) => ExitApp());
        trayIcon.ContextMenuStrip = cms;

        trayIcon.ShowBalloonTip(3000, "AnyDesk Reinstaller",
            "Программа запущена. Переустановка раз в неделю.", ToolTipIcon.Info);
    }

    // ── ОКНО (скрыто по умолчанию) ───────────────────────────────────────────
    private void InitializeWindow()
    {
        Text = "AnyDesk Reinstaller";
        Size = MinimumSize = MaximumSize = new Size(740, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 12, 28);
        ForeColor = Color.FromArgb(194, 202, 255);
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        Icon = SystemIcons.Application;
        ShowInTaskbar = false;

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideMainWindow(); }
        };

        // Термометр с двойной буферизацией
        thermoPanel = new ThermoPanel { Bounds = new Rectangle(20, 20, 110, 440) };
        Controls.Add(thermoPanel);

        // Заголовок
        Controls.Add(new Label
        {
            Text      = "AnyDesk Reinstaller",
            Bounds    = new Rectangle(150, 20, 560, 35),
            Font      = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
            ForeColor = Color.FromArgb(221, 228, 255)
        });
        Controls.Add(new Label
        {
            Text      = "Переустановка раз в неделю  •  Работает в трее  •  Автозапуск",
            Bounds    = new Rectangle(152, 55, 560, 20),
            Font      = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(96, 105, 154)
        });

        // Карточки
        Controls.AddRange(new Control[]
        {
            CreateCard("ПОСЛЕДНИЙ ЗАПУСК",  out lblLastRunVal, new Rectangle(150, 90, 165, 65)),
            CreateCard("РЕЗУЛЬТАТ",         out lblLastResVal, new Rectangle(325, 90, 165, 65)),
            CreateCard("СЛЕДУЮЩИЙ ЦИКЛ",    out lblNextRunVal, new Rectangle(500, 90, 210, 65))
        });

        // Лог
        Controls.Add(new Label
        {
            Text      = "ЖУРНАЛ ОПЕРАЦИЙ",
            Bounds    = new Rectangle(152, 175, 220, 20),
            Font      = new Font("Segoe UI Bold", 8.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(70, 75, 115)
        });
        txtLogs = new RichTextBox
        {
            Bounds      = new Rectangle(150, 198, 560, 205),
            BackColor   = Color.FromArgb(6, 6, 15),
            ForeColor   = Color.FromArgb(194, 202, 255),
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly    = true,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Font        = new Font("Consolas", 9.5F)
        };
        Controls.Add(txtLogs);

        // Статус
        Controls.Add(new Label
        {
            Text      = "Статус:",
            Bounds    = new Rectangle(150, 422, 55, 22),
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(60, 65, 100)
        });
        lblStatusVal = new Label
        {
            Bounds    = new Rectangle(207, 422, 290, 22),
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(123, 133, 200)
        };
        Controls.Add(lblStatusVal);

        // Кнопка
        btnReinstall = new Button
        {
            Text      = "⚡ Переустановить сейчас",
            Bounds    = new Rectangle(505, 412, 205, 42),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(108, 99, 255),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            Cursor    = Cursors.Hand
        };
        btnReinstall.FlatAppearance.BorderSize = 0;
        btnReinstall.Click += (_, _) => { if (!isReinstalling) TriggerReinstall(); };
        Controls.Add(btnReinstall);

        // Незаметная подпись « by kensh1qq »
        Controls.Add(new Label
        {
            Text      = "by kensh1qq",
            Bounds    = new Rectangle(620, 468, 100, 16),
            Font      = new Font("Segoe UI", 7F, FontStyle.Italic),
            ForeColor = Color.FromArgb(28, 30, 55),  // почти невидимая на тёмном фоне
            TextAlign = ContentAlignment.MiddleRight
        });

        // UI таймер (запускается только при открытом окне)
        uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        uiTimer.Tick += UiTimer_Tick;
    }

    // ── ПОКАЗАТЬ / СКРЫТЬ ────────────────────────────────────────────────────
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

    // ── КАРТОЧКА ─────────────────────────────────────────────────────────────
    private Panel CreateCard(string title, out Label val, Rectangle bounds)
    {
        var p = new Panel { Bounds = bounds, BackColor = Color.FromArgb(20, 20, 45) };
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(35, 255, 255, 255), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        p.Controls.Add(new Label { Text = title, Bounds = new Rectangle(12, 10, bounds.Width - 24, 14),
            Font = new Font("Segoe UI Bold", 7.5F, FontStyle.Bold), ForeColor = Color.FromArgb(70, 75, 115) });
        val = new Label { Text = "—", Bounds = new Rectangle(10, 26, bounds.Width - 20, 28),
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold), ForeColor = Color.FromArgb(138, 147, 212) };
        p.Controls.Add(val);
        return p;
    }

    // ── UI ТАЙМЕР ────────────────────────────────────────────────────────────
    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (progress != lastProgress) { lastProgress = progress; thermoPanel.SetProgress(progress); }

        lblStatusVal.Text  = statusText;
        lblLastRunVal.Text = lastRun.HasValue ? lastRun.Value.ToString("HH:mm  dd.MM") : "—";
        lblLastResVal.Text = lastResult;

        if      (lastResult.Contains("Успешно")) lblLastResVal.ForeColor = Color.FromArgb(52, 211, 153);
        else if (lastResult.Contains("Ошибка"))  lblLastResVal.ForeColor = Color.FromArgb(248, 113, 113);
        else                                     lblLastResVal.ForeColor = Color.FromArgb(138, 147, 212);

        if (isReinstalling)
        {
            lblNextRunVal.Text     = "Выполняется...";
            btnReinstall.Enabled   = false;
            btnReinstall.BackColor = Color.FromArgb(37, 40, 64);
        }
        else
        {
            btnReinstall.Enabled   = true;
            btnReinstall.BackColor = Color.FromArgb(108, 99, 255);
            TimeSpan left = nextRun - DateTime.Now;
            if (left.TotalSeconds > 0)
            {
                int d = (int)left.TotalDays, h = left.Hours, m = left.Minutes;
                lblNextRunVal.Text = d > 0
                    ? $"{d}д {h:00}ч {m:00}м"
                    : $"{h:00}ч {m:00}м";
            }
            else lblNextRunVal.Text = "Запуск...";
        }

        FlushLogs();
    }

    private void FlushLogs()
    {
        lock (logsLock)
        {
            if (logsQueue.Count > 0)
            {
                foreach (var line in logsQueue) AppendColorLog(line);
                logsQueue.Clear();
            }
        }
    }

    private void AppendColorLog(string text)
    {
        txtLogs.SelectionStart  = txtLogs.TextLength;
        txtLogs.SelectionLength = 0;
        if      (text.Contains("[✓]")) txtLogs.SelectionColor = Color.FromArgb(52, 211, 153);
        else if (text.Contains("[✗]")) txtLogs.SelectionColor = Color.FromArgb(248, 113, 113);
        else if (text.Contains("[►]")) txtLogs.SelectionColor = Color.FromArgb(96, 165, 250);
        else if (text.Contains("✦"))   txtLogs.SelectionColor = Color.FromArgb(139, 92, 246);
        else if (text.Contains("⏱"))   txtLogs.SelectionColor = Color.FromArgb(245, 158, 11);
        else                           txtLogs.SelectionColor = Color.FromArgb(150, 155, 190);
        txtLogs.AppendText(text + Environment.NewLine);
        txtLogs.SelectionColor = txtLogs.ForeColor;
        txtLogs.SelectionStart = txtLogs.TextLength;
        txtLogs.ScrollToCaret();
    }

    private void Log(string line)
    {
        lock (logsLock) logsQueue.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
    }

    // ── ПЛАНИРОВЩИК (1 неделя) ────────────────────────────────────────────────
    private void TriggerReinstall() =>
        new Thread(DoReinstall) { IsBackground = true, Name = "Reinstaller", Priority = ThreadPriority.BelowNormal }.Start();

    private void StartMainScheduler()
    {
        new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            TriggerReinstall();
            while (true)
            {
                nextRun = DateTime.Now.AddHours(INTERVAL_HOURS);
                Log("⏱ Следующий цикл: " + nextRun.ToString("HH:mm  dd.MM.yyyy"));
                while (DateTime.Now < nextRun) Thread.Sleep(60000);
                TriggerReinstall();
            }
        }) { IsBackground = true, Name = "Scheduler", Priority = ThreadPriority.Lowest }.Start();
    }

    private void Step(int p, string s)
    {
        progress   = p;
        statusText = s;
        try { if (trayIcon != null) trayIcon.Text = "AnyDesk: " + s[..Math.Min(s.Length, 60)]; } catch { }
    }

    // ── ЛОГИКА ПЕРЕУСТАНОВКИ ──────────────────────────────────────────────────
    private void DoReinstall()
    {
        if (isReinstalling) return;
        isReinstalling = true;
        try { trayIcon.ShowBalloonTip(4000, "AnyDesk Reinstaller", "Начинаем переустановку AnyDesk...", ToolTipIcon.Info); } catch { }

        try
        {
            lastRun = DateTime.Now;

            // ШАГ 1: Убиваем все процессы AnyDesk
            Step(5, "Останавливаем процессы AnyDesk...");
            Log("[►] Завершаем anydesk.exe");
            Run("taskkill", "/f /im anydesk.exe");
            Run("taskkill", "/f /im AnyDesk.exe");
            Thread.Sleep(1500);

            // ШАГ 2: Останавливаем и удаляем службу
            Step(12, "Останавливаем службу AnyDesk...");
            Log("[►] net stop AnyDesk");
            Run("net", "stop AnyDesk");
            Thread.Sleep(1000);
            Log("[►] sc delete AnyDesk");
            Run("sc", "delete AnyDesk");
            Thread.Sleep(1000);

            // ШАГ 3: Полное удаление без GUI (ручное + реестр)
            Step(25, "Удаляем файлы AnyDesk...");
            RemoveOld();

            // ШАГ 4: Чистим реестр от старой версии
            Step(38, "Чистим реестр...");
            Log("[►] Удаляем записи реестра AnyDesk");
            CleanRegistry();
            Thread.Sleep(1000);

            // ШАГ 5: Распаковываем НАШУ версию
            Step(50, "Распаковываем установщик v6.0.8...");
            string tmp = Path.Combine(Path.GetTempPath(), "AnyDesk_setup_temp.exe");
            if (File.Exists(tmp)) File.Delete(tmp);
            Extract(tmp);
            Log("[✓] Установщик готов: " + tmp);

            // ШАГ 6: Тихая установка НАШЕЙ версии (без --remove на tmp!)
            Step(70, "Тихая установка v6.0.8...");
            Log("[►] Запуск: --install --silent");
            Run(tmp, "--install \"C:\\Program Files (x86)\\AnyDesk\" --start-with-win --silent");
            Thread.Sleep(5000);

            // ШАГ 7: Блокируем автообновление
            Step(88, "Блокируем автообновление...");
            Log("[►] Отключаем auto-update AnyDesk");
            BlockAnyDeskUpdate();

            // ШАГ 8: Финальная очистка
            Step(95, "Финальная очистка...");
            try { File.Delete(tmp); } catch { }

            Step(100, "✓ Готово!");
            Log("[✓] AnyDesk v6.0.8 успешно переустановлен!");
            lastResult = "Успешно";
            try { trayIcon.ShowBalloonTip(4000, "AnyDesk Reinstaller", "AnyDesk v6.0.8 установлен!", ToolTipIcon.Info); } catch { }
            Thread.Sleep(3000);
            Step(0, "Ожидание (1 неделя)...");
        }
        catch (Exception ex)
        {
            Log("[✗] Ошибка: " + ex.Message);
            lastResult = "Ошибка";
            Step(0, "Ошибка. Следующий цикл через неделю.");
            try { trayIcon.ShowBalloonTip(5000, "AnyDesk — Ошибка", ex.Message, ToolTipIcon.Error); } catch { }
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

    // Удаляем файлы и папки AnyDesk вручную — без вызова --remove (он показывает GUI!)
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
            Log("   Удаляем папку: " + dir);
            try { Directory.Delete(dir, true); found = true; }
            catch (Exception ex) { Log("   [!] Не удалось удалить: " + ex.Message); }
        }
        if (!found) Log("   Существующих папок AnyDesk не найдено.");
    }

    // Чистим реестр от старой версии AnyDesk (авто-старт, деинсталляция и т.д.)
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
                Log("   Реестр очищен: HKLM\\" + key);
            }
            catch { }
        }

        // Убираем AnyDesk из автостарта (старая запись)
        try
        {
            using RegistryKey? run = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("AnyDesk", false);
            Log("   Удалён автостарт старой версии");
        }
        catch { }

        // Также из HKCU Run
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("AnyDesk", false);
        }
        catch { }
    }

    // Блокируем автообновление AnyDesk через реестр
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

        // 1. Из встроенных ресурсов
        foreach (string rn in names)
        {
            using var stream = asm.GetManifestResourceStream(rn);
            if (stream == null) continue;
            Log("[✓] Распаковываем из ресурсов EXE...");
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            stream.CopyTo(fs);
            return;
        }

        // 2. Ищем рядом с EXE
        string exeDir = AppContext.BaseDirectory;
        foreach (string name in names)
        {
            string lp = Path.Combine(exeDir, name);
            if (!File.Exists(lp)) continue;
            File.Copy(lp, outPath, true);
            Log("[✓] Использован локальный файл: " + name);
            return;
        }

        // 3. Скачиваем — HttpClient вместо устаревшего WebRequest
        bool useGDrive = !string.IsNullOrEmpty(_driveFileId) && !_driveFileId.Contains("YOUR_GOOGLE_DRIVE");
        string url = useGDrive
            ? $"https://drive.google.com/uc?export=download&id={_driveFileId}"
            : "https://download.anydesk.com/AnyDesk.exe";

        Log("[►] Скачиваем установщик: " + url);

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
                Step(43 + (int)(got * 14 / total), $"Загрузка: {got * 100 / total}% ({got / 1048576.0:F1} МБ)");
            else
                Step(43, $"Загрузка: {got / 1048576.0:F1} МБ...");
        }
        Log("[✓] Скачивание завершено");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  АВТО-ОБНОВЛЕНИЕ ЛАУНЧЕРА
    //  version.json на GitHub: {"version":"1.0.1","url":"https://.../AnydeskReinstaller.exe"}
    // ════════════════════════════════════════════════════════════════════════
    private void CheckForUpdateInBackground()
    {
        new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            Thread.Sleep(5000); // Небольшая задержка — пусть UI сначала поднимется

            try
            {
                Log("[►] Проверяем наличие обновлений лаунчера...");

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AnydeskReinstaller/" + Program.CURRENT_VERSION);

                string json = client.GetStringAsync(Program.UPDATE_CHECK_URL).Result;

                // Парсим JSON вручную (без зависимостей): {"version":"X.Y.Z","url":"..."}
                string remoteVersion = ExtractJsonValue(json, "version");
                string downloadUrl   = ExtractJsonValue(json, "url");

                if (string.IsNullOrEmpty(remoteVersion) || string.IsNullOrEmpty(downloadUrl))
                {
                    Log("[!] Не удалось разобрать version.json");
                    return;
                }

                if (CompareVersions(remoteVersion, Program.CURRENT_VERSION) <= 0)
                {
                    Log($"[✓] Версия актуальна (v{Program.CURRENT_VERSION})");
                    return;
                }

                // Есть обновление!
                Log($"[✓] Доступно обновление: v{Program.CURRENT_VERSION} → v{remoteVersion}");
                try { trayIcon.ShowBalloonTip(5000, "AnyDesk Reinstaller — Обновление",
                    $"Скачиваем v{remoteVersion}...", ToolTipIcon.Info); } catch { }

                // Скачиваем новый EXE во временную папку
                string tmpExe = Path.Combine(Path.GetTempPath(), $"AnydeskReinstaller_v{remoteVersion}.exe");
                DownloadUpdate(downloadUrl, tmpExe, remoteVersion);

                // Применяем обновление
                ApplyUpdate(tmpExe, remoteVersion);
            }
            catch (Exception ex)
            {
                // Не показываем ошибку пользователю — обновление необязательно
                Log("[!] Проверка обновлений недоступна: " + ex.Message);
            }
        }) { IsBackground = true, Name = "UpdateChecker" }.Start();
    }

    private void DownloadUpdate(string url, string outPath, string version)
    {
        Log($"[►] Скачиваем обновление v{version}...");

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
            string pct = total > 0 ? $"{got * 100 / total}%" : $"{got / 1048576.0:F1} МБ";
            Log($"[►] Обновление: {pct}");
        }

        Log($"[✓] Обновление v{version} скачано");
    }

    private void ApplyUpdate(string newExePath, string version)
    {
        try
        {
            string currentExe = Environment.ProcessPath!;

            // PowerShell скрипт: ждёт закрытия текущего процесса → заменяет файл → перезапускает
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

            // Уведомляем и закрываем — PowerShell перезапустит нас
            trayIcon.ShowBalloonTip(3000, "AnyDesk Reinstaller",
                $"Применяем обновление v{version}. Перезапуск...", ToolTipIcon.Info);
            Thread.Sleep(3500);

            // Выходим — PowerShell нас перезапустит
            Application.Exit();
        }
        catch (Exception ex)
        {
            Log("[✗] Не удалось применить обновление: " + ex.Message);
        }
    }

    // Простой парсер JSON-значения по ключу (без внешних зависимостей)
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

    // Сравнение версий вида "1.2.3". Возвращает > 0 если a > b
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


// ════════════════════════════════════════════════════════════════════════════
//  ТЕРМОМЕТР (Panel с DoubleBuffered = перерисовка только при изменении)
// ════════════════════════════════════════════════════════════════════════════
class ThermoPanel : Panel
{
    private int _progress = 0;

    public ThermoPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
    }

    public void SetProgress(int value)
    {
        if (_progress == value) return;
        _progress = value;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int w = Width, h = Height;
        int bulbRadius = 24, bulbX = w / 2, bulbY = h - bulbRadius - 15;
        int tubeW = 16, tubeH = h - bulbRadius * 2 - 50;
        int tubeX = (w - tubeW) / 2, tubeY = 20;

        // Стеклянная трубка
        using (var path = new GraphicsPath())
        {
            path.AddArc(tubeX, tubeY, tubeW, tubeW, 180, 180);
            path.AddLine(tubeX + tubeW, tubeY + tubeW / 2, tubeX + tubeW, bulbY - 5);
            path.AddArc(bulbX - bulbRadius, bulbY - bulbRadius, bulbRadius * 2, bulbRadius * 2, -100, 380);
            path.AddLine(tubeX, bulbY - 5, tubeX, tubeY + tubeW / 2);
            path.CloseAllFigures();
            using (var bg = new SolidBrush(Color.FromArgb(15, 255, 255, 255))) g.FillPath(bg, path);
            using (var bp = new Pen(Color.FromArgb(30, 255, 255, 255), 1.5F))  g.DrawPath(bp, path);
        }

        // Деления шкалы
        int stepH = tubeH / 4;
        using var scalePen   = new Pen(Color.FromArgb(45, 255, 255, 255), 1);
        using var scaleFont  = new Font("Segoe UI Semibold", 7.5F);
        using var scaleBrush = new SolidBrush(Color.FromArgb(40, 45, 75));
        for (int i = 0; i <= 4; i++)
        {
            int y = tubeY + tubeW / 2 + stepH * i;
            g.DrawLine(scalePen, tubeX - 6, y, tubeX - 1, y);
            g.DrawString((100 - i * 25).ToString(), scaleFont, scaleBrush, tubeX - 28, y - 6);
        }

        // Цвет жидкости
        Color cs, ce;
        if      (_progress == 0)   { cs = Color.FromArgb(108, 99, 255); ce = Color.FromArgb(162, 89, 247); }
        else if (_progress <= 30)  { cs = Color.FromArgb(59, 130, 246); ce = Color.FromArgb(6, 182, 212);  }
        else if (_progress <= 65)  { cs = Color.FromArgb(245, 158, 11); ce = Color.FromArgb(239, 68, 68);  }
        else                       { cs = Color.FromArgb(52, 211, 153); ce = Color.FromArgb(16, 185, 129); }

        // Колба
        using (var bb = new SolidBrush(cs))
            g.FillEllipse(bb, bulbX - (bulbRadius - 5), bulbY - (bulbRadius - 5),
                (bulbRadius - 5) * 2, (bulbRadius - 5) * 2);

        // Заполнение трубки
        int fillH = (int)(tubeH * (_progress / 100.0));
        if (fillH > 1)
        {
            int fillY = tubeY + tubeW / 2 + tubeH - fillH;
            var fr    = new Rectangle(tubeX + 3, fillY, tubeW - 6, fillH + 10);
            if (fr.Width > 0 && fr.Height > 0)
            {
                // .NET 8: LinearGradientBrush через два Point, не Rectangle+Mode
                using var fb = new LinearGradientBrush(
                    new Point(fr.Left, fr.Bottom), new Point(fr.Left, fr.Top), ce, cs);
                g.FillRectangle(fb, fr);
                using var eb = new SolidBrush(ce);
                g.FillEllipse(eb, tubeX + 3, fillY - 3, tubeW - 6, tubeW - 6);
            }
        }

        // Процент
        string pct = $"{_progress}%";
        using var pf = new Font("Segoe UI Bold", 13F, FontStyle.Bold);
        using var pb = new SolidBrush(Color.FromArgb(176, 186, 255));
        SizeF sz = g.MeasureString(pct, pf);
        g.DrawString(pct, pf, pb, (w - sz.Width) / 2, bulbY - 7);
    }
}
