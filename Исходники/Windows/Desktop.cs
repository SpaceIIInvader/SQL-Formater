using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public class Row {
    public string relative { get; set; } public string status { get; set; }
    public string message { get; set; } public string encoding { get; set; }
    public string originalHash { get; set; } public string data { get; set; }
    public string source { get; set; } public string preview { get; set; }
}
public class Plan {
    public string root { get; set; } public List<Row> rows { get; set; }
    public string backup { get; set; } public int saved { get; set; }
    public List<string> unsupported { get; set; } public List<string> warnings { get; set; }
}
public class Reply { public bool ok { get; set; } public string error { get; set; } public Plan result { get; set; } }
public class AppSettings {
    public int Version { get; set; }
    public string FolderPath { get; set; }
    public string PresetFile { get; set; }
    public string PresetText { get; set; }
    public bool IncludeSubfolders { get; set; }
    public int CaseSelection { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string LastBackupFolder { get; set; }
    public AppSettings() {
        Version = 1; FolderPath = ""; PresetFile = ""; IncludeSubfolders = true;
        CaseSelection = 0; WindowWidth = 1200; WindowHeight = 850; LastBackupFolder = "";
    }
}
public static class SettingsStore {
    public static readonly string DefaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SQLAtelier.settings.json");
    public static AppSettings Load(string file, out string notice) {
        notice = "";
        if (!File.Exists(file)) return new AppSettings();
        try {
            if (new FileInfo(file).Length > 131072) throw new Exception("Файл настроек больше 128 КБ.");
            var result = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(file, Encoding.UTF8));
            if (result == null) throw new Exception("Пустой объект настроек.");
            if (result.Version != 1) throw new Exception("Неизвестная версия настроек.");
            result.CaseSelection = Math.Max(0, Math.Min(3, result.CaseSelection));
            result.WindowWidth = Math.Max(880, Math.Min(7680, result.WindowWidth));
            result.WindowHeight = Math.Max(650, Math.Min(4320, result.WindowHeight));
            return result;
        } catch (Exception e) {
            // Preserve the original instead of silently replacing damaged user settings.
            string damaged = file + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            File.Move(file, damaged);
            notice = "Настройки повреждены; использованы стандартные. Оригинал: " + damaged + ". " + e.Message;
            return new AppSettings();
        }
    }
    public static void Save(string file, AppSettings value) {
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            // Stable, readable JSON: one setting per line, suitable for manual editing.
            var json = new JavaScriptSerializer();
            var values = new Dictionary<string, object> {
                { "Version", value.Version }, { "FolderPath", value.FolderPath },
                { "PresetFile", value.PresetFile }, { "PresetText", value.PresetText },
                { "IncludeSubfolders", value.IncludeSubfolders }, { "CaseSelection", value.CaseSelection },
                { "WindowWidth", value.WindowWidth }, { "WindowHeight", value.WindowHeight },
                { "WindowMaximized", value.WindowMaximized }, { "LastBackupFolder", value.LastBackupFolder }
            };
            var lines = new List<string>();
            foreach (var pair in values) lines.Add("  " + json.Serialize(pair.Key) + ": " + json.Serialize(pair.Value));
            File.WriteAllText(temporary, "{\r\n" + String.Join(",\r\n", lines) + "\r\n}\r\n", new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temporary, file, file + ".bak", true);
            else File.Move(temporary, file);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public static class Engine {
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly string Cache = Path.Combine(Path.GetTempPath(), "SQLAtelier", BuildInfo.Version);
    public static string Preset { get { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("preset.ini")) using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd(); } }
    private static void Extract(string resource, string file) {
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        using (var output = new FileStream(Path.Combine(Cache, file), FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
    }
    public static void Prepare() {
        Directory.CreateDirectory(Cache);
        string node = Path.Combine(Cache, "node.exe"); bool valid = false;
        if (File.Exists(node)) using (var sha = SHA256.Create()) using (var stream = File.OpenRead(node)) valid = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Equals(BuildInfo.NodeHash, StringComparison.OrdinalIgnoreCase);
        if (!valid) {
            string temporary = node + "." + Guid.NewGuid().ToString("N");
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("node.gz"))
            using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
            using (var output = File.Create(temporary)) gzip.CopyTo(output);
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(temporary))
                if (!BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Equals(BuildInfo.NodeHash, StringComparison.OrdinalIgnoreCase)) throw new Exception("Повреждён встроенный движок Node.js.");
            if (File.Exists(node)) File.Delete(node); File.Move(temporary, node);
        }
        Extract("formatter.js", "formatter.js"); Extract("worker.cjs", "desktop-worker.cjs"); Extract("node-license.txt", "node-license.txt");
    }
    public static Plan Call(object request) {
        var info = new ProcessStartInfo(Path.Combine(Cache, "node.exe"), "--no-warnings desktop-worker.cjs") {
            WorkingDirectory = Cache, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Never inherit Node injection options from the launching shell.
        info.EnvironmentVariables.Remove("NODE_OPTIONS"); info.EnvironmentVariables.Remove("NODE_PATH");
        using (var process = Process.Start(info)) {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            byte[] input = Encoding.UTF8.GetBytes(Json.Serialize(request));
            process.StandardInput.BaseStream.Write(input, 0, input.Length); process.StandardInput.Close();
            string output = stdout.GetAwaiter().GetResult(); string error = stderr.GetAwaiter().GetResult(); process.WaitForExit();
            if (String.IsNullOrWhiteSpace(output)) throw new Exception("Ошибка движка: " + error);
            Reply reply = Json.Deserialize<Reply>(output);
            if (!reply.ok) throw new Exception(reply.error);
            if (process.ExitCode != 0) throw new Exception("Движок завершился с кодом " + process.ExitCode + ": " + error);
            return reply.result;
        }
    }
}
public class MainWindow : Form {
    TextBox folder = new TextBox(), presetPath = new TextBox();
    Button chooseFolder = new Button(), choosePreset = new Button(), scan = new Button(), save = new Button(), backup = new Button();
    CheckBox recursive = new CheckBox(); ComboBox charCase = new ComboBox();
    ListView files = new ListView(); TextBox original = new TextBox(), formatted = new TextBox();
    Label summary = new Label(), compatibility = new Label(); ProgressBar progress = new ProgressBar();
    string preset = Engine.Preset, backupPath = "", selectedPresetFile = ""; Plan plan; bool busy = false;
    readonly string settingsFile;
    readonly Timer settingsTimer = new Timer { Interval = 650 };
    bool restoringSettings = true;
    public MainWindow() : this(SettingsStore.DefaultPath) { }
    public MainWindow(string settingsPath) {
        settingsFile = settingsPath;
        Text = "SQL Atelier — форматирование папки SQL"; Width = 1200; Height = 850;
        MinimumSize = new Size(880, 650); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(242, 245, 247);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        Controls.Add(layout);
        var row1 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        row1.Controls.Add(new Label { Text = "Папка с SQL:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        folder.Name = "folderPath"; folder.Dock = DockStyle.Fill; folder.Margin = new Padding(4, 7, 8, 4); row1.Controls.Add(folder, 1, 0);
        chooseFolder.Text = "Выбрать папку…"; chooseFolder.Dock = DockStyle.Fill; row1.Controls.Add(chooseFolder, 2, 0); layout.Controls.Add(row1, 0, 0);
        var row2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        row2.Controls.Add(new Label { Text = "Пресет INI:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        presetPath.Text = "Встроенный preset_ibeexpert.ini"; presetPath.ReadOnly = true; presetPath.Dock = DockStyle.Fill; presetPath.Margin = new Padding(4, 5, 8, 4); row2.Controls.Add(presetPath, 1, 0);
        choosePreset.Text = "Загрузить INI…"; choosePreset.Dock = DockStyle.Fill; row2.Controls.Add(choosePreset, 2, 0); layout.Controls.Add(row2, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        recursive.Name = "includeSubfolders"; recursive.Text = "Вложенные папки"; recursive.Checked = true; recursive.AutoSize = true; recursive.Margin = new Padding(3, 8, 16, 3);
        charCase.Name = "caseSelection"; charCase.DropDownStyle = ComboBoxStyle.DropDownList; charCase.Width = 170; charCase.Items.AddRange(new object[] { "Регистр из пресета", "Сохранить регистр", "Нижний регистр", "Верхний регистр" }); charCase.SelectedIndex = 0;
        scan.Text = "Проверить и выровнять"; scan.Width = 200; scan.Height = 34;
        save.Text = "Сохранить изменения"; save.Width = 205; save.Height = 34; save.Enabled = false; save.BackColor = Color.FromArgb(200, 234, 180);
        actions.Controls.AddRange(new Control[] { recursive, charCase, scan, save }); layout.Controls.Add(actions, 0, 2);
        compatibility.Dock = DockStyle.Fill; compatibility.Text = "Частичная поддержка INI IBExpert. Точный результат может отличаться от IBExpert."; compatibility.ForeColor = Color.FromArgb(120, 85, 25); layout.Controls.Add(compatibility, 0, 3);
        var split = new SplitContainer { Size = new Size(1000, 500), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 210 };
        files.Dock = DockStyle.Fill; files.View = View.Details; files.FullRowSelect = true; files.HideSelection = false;
        files.Columns.Add("Файл", 420); files.Columns.Add("Состояние", 150); files.Columns.Add("Кодировка", 110); files.Columns.Add("Описание", 450); split.Panel1.Controls.Add(files);
        var editors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editors.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); editors.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editors.Controls.Add(new Label { Text = "Исходный файл", AutoSize = true }, 0, 0); editors.Controls.Add(new Label { Text = "После форматирования", AutoSize = true }, 1, 0);
        foreach (var box in new TextBox[] { original, formatted }) { box.Dock = DockStyle.Fill; box.Multiline = true; box.ReadOnly = true; box.WordWrap = false; box.ScrollBars = ScrollBars.Both; box.Font = new Font("Consolas", 10); box.BackColor = Color.White; }
        editors.Controls.Add(original, 0, 1); editors.Controls.Add(formatted, 1, 1); split.Panel2.Controls.Add(editors); layout.Controls.Add(split, 0, 4);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        summary.Text = "Укажите путь к папке и нажмите «Проверить и выровнять»."; summary.Dock = DockStyle.Fill; summary.Padding = new Padding(0, 8, 0, 0);
        backup.Text = "Открыть резервную копию"; backup.Dock = DockStyle.Fill; backup.Enabled = false; footer.Controls.Add(summary, 0, 0); footer.Controls.Add(backup, 1, 0); layout.Controls.Add(footer, 0, 5);
        progress.Dock = DockStyle.Fill; layout.Controls.Add(progress, 0, 6);
        chooseFolder.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "Папка с отдельными SQL-файлами процедур или пакетов", ShowNewFolderButton = false, SelectedPath = folder.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; };
        choosePreset.Click += delegate { using (var dialog = new OpenFileDialog { Filter = "INI-пресеты (*.ini)|*.ini|Все файлы (*.*)|*.*", FileName = selectedPresetFile }) if (dialog.ShowDialog(this) == DialogResult.OK) try { preset = File.ReadAllText(dialog.FileName, Encoding.UTF8); selectedPresetFile = dialog.FileName; presetPath.Text = dialog.FileName; InvalidatePlan(); } catch (Exception e) { ShowError(e); } };
        scan.Click += async delegate { await Analyze(); }; save.Click += async delegate { await SavePlan(); };
        folder.TextChanged += delegate { InvalidatePlan(); }; recursive.CheckedChanged += delegate { InvalidatePlan(); }; charCase.SelectedIndexChanged += delegate { InvalidatePlan(); };
        files.SelectedIndexChanged += delegate { if (files.SelectedItems.Count == 0) return; var r = (Row)files.SelectedItems[0].Tag; original.Text = WindowsLines(r.source ?? ""); formatted.Text = WindowsLines(r.preview ?? r.message); };
        backup.Click += delegate { if (Directory.Exists(backupPath)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + backupPath + "\"") { UseShellExecute = true }); };
        settingsTimer.Tick += delegate { settingsTimer.Stop(); PersistSettings(); };
        ResizeEnd += delegate { QueueSettings(); };
        SizeChanged += delegate { QueueSettings(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (busy) { e.Cancel = true; summary.Text = "Дождитесь завершения текущей операции."; }
            else { settingsTimer.Stop(); PersistSettings(); }
        };
        FormClosed += delegate { settingsTimer.Dispose(); };
        RestoreSettings();
    }
    void RestoreSettings() {
        try {
            string notice; var preferences = SettingsStore.Load(settingsFile, out notice);
            folder.Text = preferences.FolderPath ?? ""; recursive.Checked = preferences.IncludeSubfolders;
            charCase.SelectedIndex = preferences.CaseSelection;
            selectedPresetFile = preferences.PresetFile ?? "";
            if (selectedPresetFile.Length > 0) {
                if (File.Exists(selectedPresetFile)) { preset = File.ReadAllText(selectedPresetFile, Encoding.UTF8); presetPath.Text = selectedPresetFile; }
                else if (!String.IsNullOrEmpty(preferences.PresetText)) { preset = preferences.PresetText; presetPath.Text = selectedPresetFile + " (сохранённая копия)"; }
                else { selectedPresetFile = ""; notice += " Пресет не найден; используется встроенный."; }
            }
            var screen = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(preferences.WindowWidth, screen.Width), Math.Min(preferences.WindowHeight, screen.Height));
            if (preferences.WindowMaximized) WindowState = FormWindowState.Maximized;
            backupPath = preferences.LastBackupFolder ?? ""; backup.Enabled = Directory.Exists(backupPath);
            if (notice.Length > 0) summary.Text = notice;
        } catch (Exception e) { summary.Text = "Не удалось загрузить настройки: " + e.Message; }
        finally { restoringSettings = false; }
        if (!File.Exists(settingsFile)) PersistSettings();
    }
    void QueueSettings() { if (restoringSettings) return; settingsTimer.Stop(); settingsTimer.Start(); }
    internal void PersistSettings() {
        if (restoringSettings) return;
        try {
            var size = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            SettingsStore.Save(settingsFile, new AppSettings {
                FolderPath = folder.Text, PresetFile = selectedPresetFile,
                PresetText = selectedPresetFile.Length > 0 ? preset : null,
                IncludeSubfolders = recursive.Checked, CaseSelection = charCase.SelectedIndex,
                WindowWidth = size.Width, WindowHeight = size.Height,
                WindowMaximized = WindowState == FormWindowState.Maximized, LastBackupFolder = backupPath
            });
        } catch (Exception e) { summary.Text = "Не удалось сохранить настройки: " + e.Message; }
    }
    static string WindowsLines(string text) { return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n"); }
    void InvalidatePlan() { if (busy || restoringSettings) return; plan = null; save.Enabled = false; files.Items.Clear(); original.Clear(); formatted.Clear(); QueueSettings(); }
    void Busy(bool value) { busy = value; chooseFolder.Enabled = choosePreset.Enabled = scan.Enabled = folder.Enabled = recursive.Enabled = charCase.Enabled = !value; save.Enabled = !value && plan != null && plan.rows.Exists(r => r.status == "changed"); progress.Style = value ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks; }
    void ShowError(Exception e) { summary.Text = "Ошибка: " + e.Message; MessageBox.Show(this, e.Message, "SQL Atelier", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    void Display() {
        files.BeginUpdate(); files.Items.Clear();
        foreach (var r in plan.rows) {
            string label = r.status == "changed" ? "Будет изменён" : r.status == "unchanged" ? "Без изменений" : r.status == "saved" ? "Сохранён" : "Ошибка";
            var item = new ListViewItem(new string[] { r.relative, label, r.encoding, r.message }); item.Tag = r;
            if (r.status == "error") item.ForeColor = Color.Firebrick; else if (r.status == "changed") item.ForeColor = Color.FromArgb(40, 100, 30);
            files.Items.Add(item);
        }
        files.EndUpdate();
        if (files.Items.Count > 0) files.Items[0].Selected = true;
    }
    async Task Analyze() {
        if (String.IsNullOrWhiteSpace(folder.Text)) { folder.Focus(); summary.Text = "Введите путь к папке."; return; }
        InvalidatePlan(); PersistSettings(); Busy(true); summary.Text = "Анализ и форматирование SQL-файлов… Исходники пока не изменяются.";
        var request = new { operation = "analyze", folder = folder.Text.Trim().Trim('"'), presetText = preset, recursive = recursive.Checked, caseMode = charCase.SelectedIndex == 0 ? (int?)null : charCase.SelectedIndex - 1 };
        try {
            plan = await Task.Run(() => Engine.Call(request)); Display();
            int changes = plan.rows.FindAll(r => r.status == "changed").Count, errors = plan.rows.FindAll(r => r.status == "error").Count;
            summary.Text = String.Format("Файлов: {0}. Изменятся: {1}. Ошибок: {2}. Перед сохранением создаётся резервная копия.", plan.rows.Count, changes, errors);
            save.Text = "Сохранить файлов: " + changes;
            compatibility.Text = "Не применяются параметры INI: " + plan.unsupported.Count + ". Нажмите здесь, чтобы увидеть список.";
            compatibility.Cursor = Cursors.Hand;
        } catch (Exception e) { plan = null; ShowError(e); }
        finally { Busy(false); }
    }
    async Task SavePlan() {
        if (plan == null) return;
        Busy(true); summary.Text = "Сохранение и создание резервной копии…";
        try {
            var request = new { operation = "save", root = plan.root, rows = plan.rows };
            Plan result = await Task.Run(() => Engine.Call(request));
            foreach (var update in result.rows) { var r = plan.rows.Find(x => x.relative == update.relative); if (r != null) { r.status = update.status; r.message = update.message; } }
            backupPath = result.backup; backup.Enabled = Directory.Exists(backupPath); PersistSettings(); Display();
            summary.Text = "Сохранено файлов: " + result.saved + ". Ошибок: " + result.rows.FindAll(r => r.status == "error").Count + ". Резервная копия: " + backupPath;
            save.Text = "Сохранить изменения";
        } catch (Exception e) { ShowError(e); plan = null; }
        finally { Busy(false); }
    }
    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);
        compatibility.Click += delegate { if (plan != null) MessageBox.Show(this, "Неподдерживаемые параметры:\r\n\r\n" + String.Join("\r\n", plan.unsupported), "Поддержка пресета", MessageBoxButtons.OK, MessageBoxIcon.Information); };
    }
}
public static class Program {
    [STAThread] public static void Main(string[] args) {
        try {
            Engine.Prepare();
            if (args.Length == 2 && args[0] == "--self-test") { SelfTest(args[1]); return; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainWindow());
        } catch (Exception e) {
            if (args.Length == 2 && args[0] == "--self-test") { File.WriteAllText(args[1], "FAIL: " + e, Encoding.UTF8); Environment.Exit(1); }
            MessageBox.Show(e.Message, "SQL Atelier — ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    static void SelfTest(string report) {
        string root = Path.Combine(Path.GetTempPath(), "SQLAtelier-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "SQLAtelier.settings.json");
        using (var window = new MainWindow(settingsPath)) {
            window.CreateControl(); window.PerformLayout(); if (window.Controls.Count == 0) throw new Exception("Window layout initialization failed");
            ((TextBox)window.Controls.Find("folderPath", true)[0]).Text = root;
            ((CheckBox)window.Controls.Find("includeSubfolders", true)[0]).Checked = false;
            ((ComboBox)window.Controls.Find("caseSelection", true)[0]).SelectedIndex = 3;
            var autosaveWait = Stopwatch.StartNew();
            while (autosaveWait.ElapsedMilliseconds < 1100) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            string autosaveNotice;
            if (SettingsStore.Load(settingsPath, out autosaveNotice).FolderPath != root) throw new Exception("Debounced settings autosave failed");
        }
        using (var restored = new MainWindow(settingsPath)) {
            if (((TextBox)restored.Controls.Find("folderPath", true)[0]).Text != root ||
                ((CheckBox)restored.Controls.Find("includeSubfolders", true)[0]).Checked ||
                ((ComboBox)restored.Controls.Find("caseSelection", true)[0]).SelectedIndex != 3) throw new Exception("Settings did not survive window restart");
        }
        string loadNotice; var snapshot = SettingsStore.Load(settingsPath, out loadNotice);
        snapshot.PresetFile = Path.Combine(root, "missing.ini"); snapshot.PresetText = Engine.Preset;
        SettingsStore.Save(settingsPath, snapshot);
        using (var restored = new MainWindow(settingsPath)) { restored.PersistSettings(); }
        if (SettingsStore.Load(settingsPath, out loadNotice).PresetText != Engine.Preset) throw new Exception("Cached preset lost");
        File.WriteAllText(settingsPath, "broken JSON", Encoding.UTF8);
        var recovered = SettingsStore.Load(settingsPath, out loadNotice);
        if (loadNotice.Length == 0 || !recovered.IncludeSubfolders || Directory.GetFiles(root, "*.invalid-*").Length != 1) throw new Exception("Corrupt settings recovery failed");
        SettingsStore.Save(settingsPath, new AppSettings());
        string file = Path.Combine(root, "test.sql"), source = "SELECT a,b FROM t WHERE x=1 AND y=2;\r\n"; File.WriteAllText(file, source, new UTF8Encoding(false));
        var plan = Engine.Call(new { operation = "analyze", folder = root, presetText = Engine.Preset, recursive = true });
        if (plan.rows.Count != 1 || plan.rows[0].status != "changed") throw new Exception("Analyze failed");
        var saved = Engine.Call(new { operation = "save", root = plan.root, rows = plan.rows });
        if (saved.saved != 1 || File.ReadAllText(Path.Combine(saved.backup, "test.sql")) != source || !File.ReadAllText(file).Contains("select a\r\n")) throw new Exception("Save/backup failed");
        var second = Engine.Call(new { operation = "analyze", folder = root, presetText = Engine.Preset, recursive = true });
        if (second.rows.Count != 1 || second.rows[0].status != "unchanged") throw new Exception("Idempotence failed");
        File.WriteAllText(report, "PASS: window initialization, automatic settings save, settings roundtrip across restart, cached preset, corrupt settings preservation/recovery, embedded runtime, INI, analyze, save, exact backup, CRLF, backup exclusion, idempotence.\r\nTest folder: " + root, Encoding.UTF8);
    }
}
