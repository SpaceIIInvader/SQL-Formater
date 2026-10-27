using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
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
    public string sql { get; set; }
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
public class ScrollTextBox : TextBox {
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);
    [StructLayout(LayoutKind.Sequential)] struct ScrollInfo {
        public int size; public uint mask; public int minimum, maximum; public uint page; public int position, track;
    }
    public event EventHandler ScrollPositionChanged;
    public int HorizontalPosition { get { return HorizontalInfo().position; } }
    public int MaximumHorizontalPosition {
        get { var info = HorizontalInfo(); return Math.Max(0, info.maximum - info.minimum - (int)info.page + 1); }
    }
    ScrollInfo HorizontalInfo() {
        var info = new ScrollInfo { size = Marshal.SizeOf(typeof(ScrollInfo)), mask = 7 };
        if (IsHandleCreated) GetScrollInfo(Handle, 0, ref info);
        return info;
    }
    public void ScrollHorizontallyTo(int position) {
        if (!IsHandleCreated) return;
        int target = Math.Max(0, Math.Min(MaximumHorizontalPosition, position));
        int characterWidth = Math.Max(1, TextRenderer.MeasureText("0000000000", Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width / 10);
        int characters = (int)Math.Round((double)(target - HorizontalPosition) / characterWidth);
        if (characters != 0) SendMessage(Handle, 0xB6, new IntPtr(characters), IntPtr.Zero);
    }
    public int FirstVisibleLine { get { return IsHandleCreated ? SendMessage(Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() : 0; } }
    public int MaximumFirstLine {
        get {
            if (!IsHandleCreated) return 0;
            var info = new ScrollInfo { size = Marshal.SizeOf(typeof(ScrollInfo)), mask = 3 };
            return GetScrollInfo(Handle, 1, ref info) ? Math.Max(0, info.maximum - info.minimum - (int)info.page + 1) : 0;
        }
    }
    public void ScrollToLine(int line) {
        if (!IsHandleCreated) return;
        int target = Math.Max(0, Math.Min(MaximumFirstLine, line));
        SendMessage(Handle, 0xB6, IntPtr.Zero, new IntPtr(target - FirstVisibleLine));
    }
    protected override void WndProc(ref Message message) {
        // Wheel, scrollbar, Page Up/Down, caret movement, selection and native scrolling.
        bool observe = message.Msg == 0x114 || message.Msg == 0x115 || message.Msg == 0x20A || message.Msg == 0x20E || message.Msg == 0x100 ||
            message.Msg == 0x102 || message.Msg == 0xB5 || message.Msg == 0xB6 ||
            message.Msg == 0xB7 || message.Msg == 0x200 || message.Msg == 0x113;
        int before = observe ? FirstVisibleLine : 0;
        int horizontalBefore = observe ? HorizontalPosition : 0;
        base.WndProc(ref message);
        if (observe && (before != FirstVisibleLine || horizontalBefore != HorizontalPosition) && ScrollPositionChanged != null) ScrollPositionChanged(this, EventArgs.Empty);
    }
}
public class SqlDifference {
    public int Left, Right;
    public string Before, After;
    public int HiddenCount, SourceIndex;
}
public static class SqlComparison {
    public static List<SqlDifference> Fold(List<SqlDifference> rows, bool collapse, HashSet<int> expanded) {
        if (!collapse) return new List<SqlDifference>(rows);
        var visible = new List<SqlDifference>();
        int index = 0;
        while (index < rows.Count) {
            if (rows[index].Left == 0 || rows[index].Right == 0) { visible.Add(rows[index++]); continue; }
            int start = index;
            while (index < rows.Count && rows[index].Left > 0 && rows[index].Right > 0) index++;
            int end = index;
            int firstHidden = start + (start == 0 ? 0 : 3);
            int afterHidden = end - (end == rows.Count ? 0 : 3);
            if (afterHidden - firstHidden < 2 || expanded.Contains(firstHidden)) {
                for (int i = start; i < end; i++) visible.Add(rows[i]);
            } else {
                for (int i = start; i < firstHidden; i++) visible.Add(rows[i]);
                visible.Add(new SqlDifference { SourceIndex = firstHidden, HiddenCount = afterHidden - firstHidden });
                for (int i = afterHidden; i < end; i++) visible.Add(rows[i]);
            }
        }
        return visible;
    }
    static string[] Lines(string text) { return text.Length == 0 ? new string[0] : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); }
    public static List<SqlDifference> Compare(string before, string after) {
        var a = Lines(before); var b = Lines(after);
        if (a.Length > 10000 || b.Length > 10000) throw new Exception("Для сравнения допускается до 10 000 строк с каждой стороны. Выберите нужный фрагмент.");
        int prefix = 0, suffix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        int n = a.Length - prefix - suffix, m = b.Length - prefix - suffix;
        if ((long)(n + 1) * (m + 1) > 4000000) throw new Exception("Слишком большой изменённый фрагмент. Сравните отдельные запросы или процедуры.");
        var lengths = new int[n + 1, m + 1];
        for (int x = n - 1; x >= 0; x--) for (int y = m - 1; y >= 0; y--)
            lengths[x, y] = a[prefix + x] == b[prefix + y] ? 1 + lengths[x + 1, y + 1] : Math.Max(lengths[x + 1, y], lengths[x, y + 1]);
        var rows = new List<SqlDifference>();
        for (int k = 0; k < prefix; k++) rows.Add(new SqlDifference { Left = k + 1, Right = k + 1, Before = a[k], After = b[k] });
        int i = 0, j = 0;
        while (i < n || j < m) {
            if (i < n && j < m && a[prefix + i] == b[prefix + j]) {
                rows.Add(new SqlDifference { Left = prefix + i + 1, Right = prefix + j + 1, Before = a[prefix + i], After = b[prefix + j] }); i++; j++;
            } else if (i < n && (j == m || lengths[i + 1, j] >= lengths[i, j + 1])) {
                rows.Add(new SqlDifference { Left = prefix + i + 1, Before = a[prefix + i] }); i++;
            } else { rows.Add(new SqlDifference { Right = prefix + j + 1, After = b[prefix + j] }); j++; }
        }
        for (int k = suffix; k > 0; k--) rows.Add(new SqlDifference { Left = a.Length - k + 1, Right = b.Length - k + 1, Before = a[a.Length - k], After = b[b.Length - k] });
        return rows;
    }
}
public class MainWindow : Form {
    TextBox compareLeft = new TextBox(), compareRight = new TextBox();
    DataGridView differenceGrid = new DataGridView();
    Label compareStatus = new Label();
    List<SqlDifference> differenceRows = new List<SqlDifference>();
    List<SqlDifference> visibleDifferenceRows = new List<SqlDifference>();
    HashSet<int> expandedDifferenceBlocks = new HashSet<int>();
    CheckBox collapseEqual = new CheckBox();
    TabControl modes = new TabControl();
    ScrollTextBox codeInput = new ScrollTextBox(), codeOutput = new ScrollTextBox();
    CheckBox syncScroll = new CheckBox();
    bool syncingScroll;
    Button formatCode = new Button(), copyCode = new Button(), saveCode = new Button();
    Label codeStatus = new Label();
    List<string> unsupportedOptions;
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
        Text = "SQL Atelier — форматирование SQL"; Width = 1200; Height = 850;
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
        choosePreset.Click += delegate { using (var dialog = new OpenFileDialog { Filter = "INI-пресеты (*.ini)|*.ini|Все файлы (*.*)|*.*", FileName = selectedPresetFile }) if (dialog.ShowDialog(this) == DialogResult.OK) try { preset = File.ReadAllText(dialog.FileName, Encoding.UTF8); selectedPresetFile = dialog.FileName; presetPath.Text = dialog.FileName; InvalidatePlan(); InvalidateCode(); } catch (Exception e) { ShowError(e); } };
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
        InitializeCodeMode(layout, row2);
        InitializeComparisonMode();
        RestoreSettings();
    }
    void InitializeCodeMode(TableLayoutPanel folderLayout, TableLayoutPanel presetRow) {
        Controls.Remove(folderLayout);
        folderLayout.RowStyles[1].Height = 0;
        folderLayout.RowStyles[3].Height = 0;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(presetRow, 0, 0);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        options.Controls.Add(charCase);
        compatibility.AutoSize = true; compatibility.Dock = DockStyle.None; compatibility.Margin = new Padding(12, 6, 0, 0);
        options.Controls.Add(compatibility); root.Controls.Add(options, 0, 1);
        modes.Name = "workModes"; modes.Dock = DockStyle.Fill;
        var codePage = new TabPage("SQL-код");
        var folderPage = new TabPage("Обработка папки");
        modes.TabPages.AddRange(new TabPage[] { codePage, folderPage });
        modes.SelectedIndex = 0;
        folderPage.Controls.Add(folderLayout);
        root.Controls.Add(modes, 0, 2); Controls.Add(root);
        var codeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        codeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        codeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        codeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        codeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        formatCode.Name = "formatCode"; formatCode.Text = "Форматировать · Ctrl+Enter"; formatCode.Width = 250; formatCode.Height = 34; formatCode.BackColor = Color.FromArgb(200, 234, 180);
        copyCode.Name = "copyCode"; copyCode.Text = "Копировать результат"; copyCode.Width = 200; copyCode.Height = 34; copyCode.Enabled = false;
        saveCode.Name = "saveCode"; saveCode.Text = "Сохранить результат…"; saveCode.Width = 200; saveCode.Height = 34; saveCode.Enabled = false;
        toolbar.Controls.AddRange(new Control[] { formatCode, copyCode, saveCode }); codeLayout.Controls.Add(toolbar, 0, 0);
        syncScroll.Name = "syncScroll"; syncScroll.Text = "Синхронная прокрутка"; syncScroll.AutoSize = true;
        codeLayout.Controls.Add(syncScroll, 0, 1);
        codeInput.ScrollPositionChanged += delegate { SyncEditors(codeInput, codeOutput); };
        codeOutput.ScrollPositionChanged += delegate { SyncEditors(codeOutput, codeInput); };
        syncScroll.CheckedChanged += delegate { if (syncScroll.Checked) SyncEditors(codeInput, codeOutput); };
        var editors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editors.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); editors.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editors.Controls.Add(new Label { Text = "Исходный SQL · вставьте код через Ctrl+V", AutoSize = true }, 0, 0);
        editors.Controls.Add(new Label { Text = "После форматирования", AutoSize = true }, 1, 0);
        codeInput.Name = "codeInput"; codeOutput.Name = "codeOutput";
        foreach (var box in new TextBox[] { codeInput, codeOutput }) {
            box.Dock = DockStyle.Fill; box.Multiline = true; box.WordWrap = false; box.ScrollBars = ScrollBars.Both;
            box.Font = new Font("Consolas", 11); box.BackColor = Color.White; box.AcceptsTab = true; box.MaxLength = 2000000;
            box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.A) { ((TextBox)sender).SelectAll(); e.SuppressKeyPress = true; } };
        }
        codeOutput.ReadOnly = true;
        editors.Controls.Add(codeInput, 0, 1); editors.Controls.Add(codeOutput, 1, 1); codeLayout.Controls.Add(editors, 0, 2);
        codeStatus.Name = "codeStatus"; codeStatus.Dock = DockStyle.Fill; codeStatus.Padding = new Padding(0, 8, 0, 0);
        codeStatus.Text = "Вставьте SQL слева и нажмите «Форматировать» или Ctrl+Enter.";
        codeLayout.Controls.Add(codeStatus, 0, 3); codePage.Controls.Add(codeLayout);
        codeInput.TextChanged += delegate { InvalidateCode(); };
        charCase.SelectedIndexChanged += delegate { InvalidateCode(); };
        formatCode.Click += async delegate { await FormatCode(); };
        copyCode.Click += delegate { try { Clipboard.SetText(codeOutput.Text); codeStatus.Text = "Результат скопирован."; } catch (Exception e) { codeStatus.Text = "Не удалось скопировать: " + e.Message; } };
        saveCode.Click += delegate {
            using (var dialog = new SaveFileDialog { Filter = "SQL-файлы (*.sql)|*.sql|Все файлы (*.*)|*.*", FileName = "script.formatted.sql", DefaultExt = "sql", AddExtension = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, codeOutput.Text, new UTF8Encoding(false)); codeStatus.Text = "Сохранено: " + dialog.FileName; }
                catch (Exception e) { codeStatus.Text = "Не удалось сохранить: " + e.Message; }
            }
        };
        Shown += delegate { codeInput.Focus(); };
    }
    void InitializeComparisonMode() {
        var page = new TabPage("Сравнение SQL"); modes.TabPages.Add(page);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); page.Controls.Add(layout);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        var compare = new Button { Text = "Сравнить · Ctrl+Enter", AutoSize = true, Height = 34 };
        var format = new Button { Text = "Выровнять и сравнить", AutoSize = true, Height = 34, BackColor = Color.FromArgb(200, 234, 180) };
        var previous = new Button { Text = "↑ Различие", AutoSize = true, Height = 34 };
        var next = new Button { Text = "↓ Различие", AutoSize = true, Height = 34 };
        toolbar.Controls.AddRange(new Control[] { compare, format, previous, next }); layout.Controls.Add(toolbar, 0, 0);
        collapseEqual.Name = "collapseEqual"; collapseEqual.Text = "Скрывать совпадающие строки"; collapseEqual.Checked = true; collapseEqual.AutoSize = true;
        var collapseAll = new Button { Name = "collapseAll", Text = "Свернуть все совпадения", AutoSize = true, Height = 34 };
        toolbar.Controls.Add(collapseEqual); toolbar.Controls.Add(collapseAll);
        collapseEqual.CheckedChanged += delegate { RefreshDifferenceRows(); };
        collapseAll.Click += delegate { expandedDifferenceBlocks.Clear(); collapseEqual.Checked = true; RefreshDifferenceRows(); };
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1000, 500), SplitterDistance = 220, Panel1MinSize = 80, Panel2MinSize = 80 };
        var editors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editors.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); editors.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editors.Controls.Add(new Label { Text = "Версия 1 · вставьте SQL", AutoSize = true }, 0, 0);
        editors.Controls.Add(new Label { Text = "Версия 2 · вставьте SQL", AutoSize = true }, 1, 0);
        compareLeft.Name = "compareLeft"; compareRight.Name = "compareRight";
        foreach (var box in new TextBox[] { compareLeft, compareRight }) {
            box.Dock = DockStyle.Fill; box.Multiline = true; box.AcceptsTab = true; box.WordWrap = false;
            box.ScrollBars = ScrollBars.Both; box.Font = new Font("Consolas", 11); box.MaxLength = 2000000;
            box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.A) { ((TextBox)sender).SelectAll(); e.SuppressKeyPress = true; } };
            box.TextChanged += delegate { ClearDifferences(); compareStatus.Text = "Текст изменён. Нажмите «Сравнить» или «Выровнять и сравнить»."; };
        }
        editors.Controls.Add(compareLeft, 0, 1); editors.Controls.Add(compareRight, 1, 1); split.Panel1.Controls.Add(editors);
        differenceGrid.Name = "differenceGrid"; differenceGrid.Dock = DockStyle.Fill; differenceGrid.ReadOnly = true;
        differenceGrid.AllowUserToAddRows = differenceGrid.AllowUserToDeleteRows = differenceGrid.AllowUserToOrderColumns = false;
        differenceGrid.RowHeadersVisible = false; differenceGrid.VirtualMode = true; differenceGrid.AutoGenerateColumns = false;
        differenceGrid.Font = new Font("Consolas", 10); differenceGrid.BackgroundColor = Color.White;
        differenceGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        differenceGrid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        foreach (string title in new string[] { "Стр. 1", "Версия 1 · удалено красным", "Стр. 2", "Версия 2 · добавлено зелёным" }) {
            var column = new DataGridViewTextBoxColumn { HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (differenceGrid.Columns.Count % 2 == 0) column.Width = 65;
            else { column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; column.MinimumWidth = 250; }
            differenceGrid.Columns.Add(column);
        }
        differenceGrid.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e) {
            var row = visibleDifferenceRows[e.RowIndex];
            if (row.HiddenCount > 0) { e.Value = e.ColumnIndex % 2 == 0 ? "…" : "▶ Совпадает строк: " + row.HiddenCount + " · нажмите, чтобы раскрыть"; return; }
            e.Value = e.ColumnIndex == 0 ? (object)(row.Left == 0 ? "" : row.Left.ToString()) : e.ColumnIndex == 1 ? row.Before : e.ColumnIndex == 2 ? (object)(row.Right == 0 ? "" : row.Right.ToString()) : row.After;
        };
        differenceGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e) {
            var row = visibleDifferenceRows[e.RowIndex];
            e.CellStyle.BackColor = row.HiddenCount > 0 ? Color.FromArgb(225, 237, 250) : row.Right == 0 && e.ColumnIndex < 2 ? Color.MistyRose : row.Left == 0 && e.ColumnIndex >= 2 ? Color.FromArgb(218, 245, 218) : Color.White;
        };
        differenceGrid.CellClick += delegate(object sender, DataGridViewCellEventArgs e) {
            if (e.RowIndex >= 0) ExpandDifferenceBlock(e.RowIndex);
        };
        differenceGrid.KeyDown += delegate(object sender, KeyEventArgs e) {
            if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && differenceGrid.CurrentCell != null && visibleDifferenceRows[differenceGrid.CurrentCell.RowIndex].HiddenCount > 0) {
                ExpandDifferenceBlock(differenceGrid.CurrentCell.RowIndex); e.Handled = true; e.SuppressKeyPress = true;
            }
        };
        split.Panel2.Controls.Add(differenceGrid); layout.Controls.Add(split, 0, 1);
        compareStatus.Name = "compareStatus"; compareStatus.Dock = DockStyle.Fill;
        compareStatus.Text = "Вставьте две версии SQL. Выравнивание заменяет тексты в полях; результат можно выделить и скопировать.";
        layout.Controls.Add(compareStatus, 0, 2);
        compare.Click += async delegate { await CompareSql(false); };
        format.Click += async delegate { await CompareSql(true); };
        previous.Click += delegate { MoveDifference(-1); }; next.Click += delegate { MoveDifference(1); };
    }
    internal async Task CompareSql(bool format) {
        if (busy) return;
        string left = compareLeft.Text, right = compareRight.Text, selectedPreset = preset;
        int? selectedCase = charCase.SelectedIndex == 0 ? (int?)null : charCase.SelectedIndex - 1;
        ClearDifferences();
        Busy(true); compareStatus.Text = format ? "Выравнивание и сравнение…" : "Сравнение…";
        try {
            var rows = await Task.Run(() => {
                if (format) {
                    if (!String.IsNullOrWhiteSpace(left)) left = WindowsLines(Engine.Call(new { operation = "format", text = left, presetText = selectedPreset, caseMode = selectedCase }).sql);
                    if (!String.IsNullOrWhiteSpace(right)) right = WindowsLines(Engine.Call(new { operation = "format", text = right, presetText = selectedPreset, caseMode = selectedCase }).sql);
                }
                return SqlComparison.Compare(left, right);
            });
            if (format) { compareLeft.Text = left; compareRight.Text = right; }
            differenceRows = rows; RefreshDifferenceRows();
            int removed = rows.FindAll(r => r.Right == 0).Count, added = rows.FindAll(r => r.Left == 0).Count;
            compareStatus.Text = removed == 0 && added == 0 ? "Тексты совпадают (переносы CRLF/LF считаются одинаковыми)." : String.Format("Удалено строк: {0} · добавлено: {1}. Красный — версия 1, зелёный — версия 2. Ctrl+C копирует выбранные ячейки.", removed, added);
        } catch (Exception e) { compareStatus.Text = "Ошибка: " + e.Message; }
        finally { Busy(false); }
    }
    void ClearDifferences() {
        differenceGrid.RowCount = 0; differenceRows.Clear(); visibleDifferenceRows.Clear(); expandedDifferenceBlocks.Clear();
    }
    void RefreshDifferenceRows() {
        int top = differenceGrid.FirstDisplayedScrollingRowIndex;
        differenceGrid.RowCount = 0;
        visibleDifferenceRows = SqlComparison.Fold(differenceRows, collapseEqual.Checked, expandedDifferenceBlocks);
        differenceGrid.RowCount = visibleDifferenceRows.Count;
        if (top >= 0 && visibleDifferenceRows.Count > 0) differenceGrid.FirstDisplayedScrollingRowIndex = Math.Min(top, visibleDifferenceRows.Count - 1);
        differenceGrid.Invalidate();
    }
    internal void ExpandDifferenceBlock(int index) {
        var row = visibleDifferenceRows[index];
        if (row.HiddenCount == 0) return;
        expandedDifferenceBlocks.Add(row.SourceIndex); RefreshDifferenceRows();
        differenceGrid.CurrentCell = differenceGrid.Rows[index].Cells[1];
    }
    void MoveDifference(int direction) {
        int count = visibleDifferenceRows.Count;
        if (count == 0) return;
        int start = differenceGrid.CurrentCell == null ? (direction > 0 ? -1 : 0) : differenceGrid.CurrentCell.RowIndex;
        for (int step = 1; step <= count; step++) {
            int index = (start + direction * step + count * 2) % count;
            var row = visibleDifferenceRows[index];
            if (row.HiddenCount > 0) continue;
            if (row.Left != 0 && row.Right != 0) continue;
            differenceGrid.CurrentCell = differenceGrid.Rows[index].Cells[row.Left == 0 ? 3 : 1];
            differenceGrid.FirstDisplayedScrollingRowIndex = index; return;
        }
    }
    void SyncEditors(ScrollTextBox source, ScrollTextBox target) {
        if (!syncScroll.Checked || syncingScroll || busy || source.TextLength == 0 || target.TextLength == 0) return;
        syncingScroll = true;
        try {
            double progress = source.MaximumFirstLine == 0 ? 0 : (double)source.FirstVisibleLine / source.MaximumFirstLine;
            target.ScrollToLine((int)Math.Round(progress * target.MaximumFirstLine));
            double horizontal = source.MaximumHorizontalPosition == 0 ? 0 : (double)source.HorizontalPosition / source.MaximumHorizontalPosition;
            target.ScrollHorizontallyTo((int)Math.Round(horizontal * target.MaximumHorizontalPosition));
        } finally { syncingScroll = false; }
    }
    void InvalidateCode() {
        codeOutput.Clear(); copyCode.Enabled = saveCode.Enabled = false;
        codeStatus.Text = "Нажмите «Форматировать» или Ctrl+Enter для получения результата.";
    }
    internal async Task FormatCode() {
        if (busy) return;
        InvalidateCode();
        if (String.IsNullOrWhiteSpace(codeInput.Text)) { codeStatus.Text = "Вставьте SQL в левое поле."; codeInput.Focus(); return; }
        var request = new { operation = "format", text = codeInput.Text, presetText = preset, caseMode = charCase.SelectedIndex == 0 ? (int?)null : charCase.SelectedIndex - 1 };
        Busy(true); codeStatus.Text = "Форматирование…";
        try {
            var result = await Task.Run(() => Engine.Call(request));
            codeOutput.Text = WindowsLines(result.sql);
            unsupportedOptions = result.unsupported;
            codeStatus.Text = "Готово. Исходный SQL сохранён слева. Результат можно скопировать или сохранить.";
            compatibility.Text = "Не применяются параметры INI: " + result.unsupported.Count + ".";
        } catch (Exception e) { codeStatus.Text = "Ошибка: " + e.Message; }
        finally { Busy(false); if (syncScroll.Checked) SyncEditors(codeInput, codeOutput); }
    }
    async void FormatCodeCommand() { await FormatCode(); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
        if (keyData == (Keys.Control | Keys.Enter) && modes.SelectedIndex == 2) { CompareSqlCommand(); return true; }
        if (keyData == (Keys.Control | Keys.Enter) && modes.SelectedIndex == 0) { FormatCodeCommand(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    async void CompareSqlCommand() { await CompareSql(false); }
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
    void Busy(bool value) { busy = value; chooseFolder.Enabled = choosePreset.Enabled = scan.Enabled = folder.Enabled = recursive.Enabled = charCase.Enabled = !value; modes.Enabled = formatCode.Enabled = codeInput.Enabled = !value; copyCode.Enabled = saveCode.Enabled = !value && codeOutput.TextLength > 0; save.Enabled = !value && plan != null && plan.rows.Exists(r => r.status == "changed"); progress.Style = value ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks; }
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
            unsupportedOptions = plan.unsupported;
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
        compatibility.Click += delegate { if (unsupportedOptions != null) MessageBox.Show(this, "Неподдерживаемые параметры:\r\n\r\n" + String.Join("\r\n", unsupportedOptions), "Поддержка пресета", MessageBoxButtons.OK, MessageBoxIcon.Information); };
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
            CheckComparison(window);
            var tabs = (TabControl)window.Controls.Find("workModes", true)[0];
            var input = (TextBox)window.Controls.Find("codeInput", true)[0];
            var output = (TextBox)window.Controls.Find("codeOutput", true)[0];
            var copy = (Button)window.Controls.Find("copyCode", true)[0];
            if (tabs.TabCount != 3 || tabs.SelectedIndex != 0 || input.ReadOnly || !output.ReadOnly) throw new Exception("Code must be the default editable mode");
            string pasted = "SELECT a,b,'Русский текст' FROM t WHERE x=1;";
            input.Text = pasted; Wait(window.FormatCode());
            if (input.Text != pasted || !output.Text.Contains("'Русский текст'") || !copy.Enabled || !output.Text.Contains("\r\n")) throw new Exception("Text formatting or source preservation failed");
            input.Text += "\r\n";
            if (output.TextLength != 0 || copy.Enabled) throw new Exception("Stale output was not cleared");
            Wait(window.FormatCode());
            ((ComboBox)window.Controls.Find("caseSelection", true)[0]).SelectedIndex = 3;
            if (output.TextLength != 0 || copy.Enabled) throw new Exception("Case change did not invalidate output");
            Wait(window.FormatCode());
            if (!output.Text.Contains("SELECT")) throw new Exception("Text case override failed");
            input.Text = "select 'unclosed"; Wait(window.FormatCode());
            if (output.TextLength != 0 || copy.Enabled || !window.Controls.Find("codeStatus", true)[0].Text.StartsWith("Ошибка:")) throw new Exception("Invalid SQL left a usable result");
            input.Clear(); Wait(window.FormatCode());
            if (output.TextLength != 0 || copy.Enabled) throw new Exception("Empty SQL produced a usable result");
            var linked = (CheckBox)window.Controls.Find("syncScroll", true)[0];
            if (linked.Checked) throw new Exception("Scroll synchronization should start disabled");
            var left = (ScrollTextBox)input; var right = (ScrollTextBox)output;
            left.ClientSize = right.ClientSize = new Size(400, 200);
            var leftHandle = left.Handle; var rightHandle = right.Handle;
            var longText = new StringBuilder();
            for (int line = 0; line < 240; line++) longText.Append("select ").Append(line).Append(";\r\n");
            left.Text = longText.ToString(); right.Text = longText.ToString() + longText.ToString();
            if (left.MaximumFirstLine < 20 || right.MaximumFirstLine < 20) throw new Exception("Native test scroll range is missing");
            left.ScrollToLine(left.MaximumFirstLine / 2);
            if (right.FirstVisibleLine != 0) throw new Exception("Unchecked editors should scroll independently");
            linked.Checked = true;
            CheckScroll(left, right);
            right.ScrollToLine(right.MaximumFirstLine * 3 / 4);
            CheckScroll(right, left);
            linked.Checked = false; int unchangedRight = right.FirstVisibleLine;
            left.ScrollToLine(0);
            if (right.FirstVisibleLine != unchangedRight) throw new Exception("Disabling linked scrolling did not take effect");
            if (left.Text != longText.ToString() || right.Text != longText.ToString() + longText.ToString()) throw new Exception("Scrolling changed editor text");
            left.Text = new string('x', 500); right.Text = new string('y', 900);
            if (left.MaximumHorizontalPosition < 100 || right.MaximumHorizontalPosition < 100) throw new Exception("Native horizontal scroll range is missing");
            left.ScrollHorizontallyTo(left.MaximumHorizontalPosition / 2);
            if (right.HorizontalPosition != 0) throw new Exception("Unchecked horizontal scrolling must be independent");
            linked.Checked = true; CheckHorizontalScroll(left, right);
            right.ScrollHorizontallyTo(right.MaximumHorizontalPosition * 3 / 4); CheckHorizontalScroll(right, left);
            linked.Checked = false; int unchangedHorizontal = right.HorizontalPosition;
            left.ScrollHorizontallyTo(0);
            if (right.HorizontalPosition != unchangedHorizontal) throw new Exception("Unchecked horizontal scrolling remained linked");
            if (left.Text != new string('x', 500) || right.Text != new string('y', 900)) throw new Exception("Horizontal scrolling changed text");
            left.Text = "select 1;"; right.Text = "select 2;"; linked.Checked = true;
            left.ScrollToLine(10); right.ScrollToLine(10);
            if (left.FirstVisibleLine != 0 || right.FirstVisibleLine != 0) throw new Exception("Short texts must remain at the top");
            linked.Checked = false;
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
        File.WriteAllText(report, "PASS: optional vertical and horizontal bidirectional proportional scrolling, enable/disable, different text lengths, short text, scroll preserves text; default code tab, pasted SQL formatting, original text preservation, Unicode, CRLF, case override, stale result invalidation, invalid and empty SQL, window initialization, automatic settings save, settings roundtrip across restart, cached preset, corrupt settings preservation/recovery, embedded runtime, INI, analyze, save, exact backup, backup exclusion, idempotence.\r\nTest folder: " + root, Encoding.UTF8);
    }
    static void CheckComparison(MainWindow window) {
        var equalLines = new List<string>();
        for (int i = 0; i < 30; i++) equalLines.Add("select " + i + ";");
        string originalSql = String.Join("\n", equalLines);
        equalLines[15] = "select 999;";
        var fullDiff = SqlComparison.Compare(originalSql, String.Join("\n", equalLines));
        var expanded = new HashSet<int>();
        var folded = SqlComparison.Fold(fullDiff, true, expanded);
        if (folded.Count != 10 || folded[0].HiddenCount != 12 || folded[9].HiddenCount != 11) throw new Exception("Equal context was not folded around changes");
        if (folded.FindAll(r => r.HiddenCount == 0 && (r.Left == 0 || r.Right == 0)).Count != 2) throw new Exception("Folding hid changes");
        expanded.Add(folded[0].SourceIndex);
        if (SqlComparison.Fold(fullDiff, true, expanded).Count != 21 || SqlComparison.Fold(fullDiff, false, expanded).Count != fullDiff.Count) throw new Exception("Unfolding lost rows");
        var allEqual = SqlComparison.Fold(SqlComparison.Compare(originalSql, originalSql), true, new HashSet<int>());
        if (allEqual.Count != 1 || allEqual[0].HiddenCount != 30) throw new Exception("Identical SQL folding failed");
        var rows = SqlComparison.Compare("a\nb\nc", "a\nx\nb\nc");
        if (rows.Count != 4 || rows[1].Left != 0 || rows[1].After != "x" || rows[2].Left != 2 || rows[2].Right != 3) throw new Exception("Inserted line shifted comparison alignment");
        if (SqlComparison.Compare("", "").Count != 0 || SqlComparison.Compare("a\r\nb", "a\nb").Exists(r => r.Left == 0 || r.Right == 0)) throw new Exception("Empty text or newline normalization failed");
        var random = new Random(27);
        for (int sample = 0; sample < 100; sample++) {
            var a = new List<string>(); var b = new List<string>();
            for (int i = 0; i < 20; i++) { a.Add(random.Next(4).ToString()); b.Add(random.Next(4).ToString()); }
            rows = SqlComparison.Compare(String.Join("\n", a), String.Join("\n", b));
            var recoveredA = new List<string>(); var recoveredB = new List<string>();
            foreach (var row in rows) {
                if (row.Left > 0) { if (row.Left != recoveredA.Count + 1) throw new Exception("Invalid left line number"); recoveredA.Add(row.Before); }
                if (row.Right > 0) { if (row.Right != recoveredB.Count + 1) throw new Exception("Invalid right line number"); recoveredB.Add(row.After); }
                if (row.Left > 0 && row.Right > 0 && row.Before != row.After) throw new Exception("Changed lines marked equal");
            }
            if (String.Join("\n", recoveredA) != String.Join("\n", a) || String.Join("\n", recoveredB) != String.Join("\n", b)) throw new Exception("Comparison lost text");
        }
        bool limited = false;
        try { SqlComparison.Compare(new string('\n', 10001), ""); } catch { limited = true; }
        if (!limited) throw new Exception("Comparison line limit missing");
        var left = (TextBox)window.Controls.Find("compareLeft", true)[0];
        var right = (TextBox)window.Controls.Find("compareRight", true)[0];
        var grid = (DataGridView)window.Controls.Find("differenceGrid", true)[0];
        left.Text = originalSql; right.Text = String.Join("\n", equalLines);
        Wait(window.CompareSql(false));
        if (grid.RowCount != 10) throw new Exception("Collapsed view not enabled by default");
        window.ExpandDifferenceBlock(0);
        if (grid.RowCount != 21 || left.Text != originalSql) throw new Exception("Expanding a block modified SQL");
        var collapse = (CheckBox)window.Controls.Find("collapseEqual", true)[0];
        collapse.Checked = false;
        if (grid.RowCount != fullDiff.Count) throw new Exception("Full comparison toggle failed");
        collapse.Checked = true;
        left.Text = "select a from t;"; right.Text = "select b from t;";
        Wait(window.CompareSql(false));
        if (grid.RowCount != 2 || left.ReadOnly || right.ReadOnly || left.Text != "select a from t;") throw new Exception("Editable comparison UI failed");
        right.Text = "SELECT a FROM t;";
        if (grid.RowCount != 0) throw new Exception("Stale comparison remained visible");
        Wait(window.CompareSql(true));
        if (left.Text != right.Text || grid.RowCount == 0 || !window.Controls.Find("compareStatus", true)[0].Text.StartsWith("Тексты совпадают")) throw new Exception("Format and compare failed");
        left.Text = "select 'broken"; string unchanged = right.Text;
        Wait(window.CompareSql(true));
        if (left.Text != "select 'broken" || right.Text != unchanged || grid.RowCount != 0 || !window.Controls.Find("compareStatus", true)[0].Text.StartsWith("Ошибка:")) throw new Exception("Comparison error did not preserve inputs");
        left.Clear(); right.Clear();
    }
    static void CheckScroll(ScrollTextBox source, ScrollTextBox target) {
        int expected = (int)Math.Round((double)source.FirstVisibleLine / source.MaximumFirstLine * target.MaximumFirstLine);
        if (Math.Abs(target.FirstVisibleLine - expected) > 1) throw new Exception("Linked scrolling position mismatch");
    }
    static void CheckHorizontalScroll(ScrollTextBox source, ScrollTextBox target) {
        int expected = (int)Math.Round((double)source.HorizontalPosition / source.MaximumHorizontalPosition * target.MaximumHorizontalPosition);
        if (Math.Abs(target.HorizontalPosition - expected) > 15) throw new Exception("Linked horizontal scrolling position mismatch");
    }
    static void Wait(Task task) {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted) { if (timeout.ElapsedMilliseconds > 30000) throw new Exception("Self-test operation timed out"); Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        task.GetAwaiter().GetResult();
    }
}
