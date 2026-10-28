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
using System.Text.RegularExpressions;
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
    public Dictionary<string, Dictionary<string, int>> settings { get; set; }
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
    public bool PresetEdited { get; set; }
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
            if (new FileInfo(file).Length > 1048576) throw new Exception("Файл настроек больше 1 МБ.");
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
                { "PresetFile", value.PresetFile }, { "PresetText", value.PresetText }, { "PresetEdited", value.PresetEdited },
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
public class WorkspaceTabs : TabControl {
    public WorkspaceTabs() { Appearance = TabAppearance.FlatButtons; ItemSize = new Size(1, 1); SizeMode = TabSizeMode.Fixed; Multiline = true; }
    public override Rectangle DisplayRectangle { get { return ClientRectangle; } }
    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        foreach (TabPage page in TabPages) page.Bounds = ClientRectangle;
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x1328) { message.Result = new IntPtr(1); return; }
        base.WndProc(ref message);
    }
}
public class MainWindow : Form {
    readonly ToolTip hints = new ToolTip();
    readonly List<Button> navigation = new List<Button>();
    TextBox presetSearch = new TextBox(); ComboBox presetSection = new ComboBox();
    CheckBox presetSupportedOnly = new CheckBox(); Label presetFilterCount = new Label();
    Button compareCode = new Button();
    DataGridView presetGrid = new DataGridView();
    Label presetStatus = new Label();
    string presetEditorSource;
    bool presetEdited, fillingPresetGrid;
    TextBox compareLeft = new TextBox(), compareRight = new TextBox();
    DataGridView differenceGrid = new DataGridView();
    Label compareStatus = new Label();
    List<SqlDifference> differenceRows = new List<SqlDifference>();
    List<SqlDifference> visibleDifferenceRows = new List<SqlDifference>();
    HashSet<int> expandedDifferenceBlocks = new HashSet<int>();
    CheckBox collapseEqual = new CheckBox();
    TabControl modes = new WorkspaceTabs();
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
        scan.Text = "Анализировать"; scan.Width = 150; scan.Height = 34;
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
        choosePreset.Click += delegate { using (var dialog = new OpenFileDialog { Filter = "INI-пресеты (*.ini)|*.ini|Все файлы (*.*)|*.*", FileName = selectedPresetFile }) if (dialog.ShowDialog(this) == DialogResult.OK) try { SelectPreset(File.ReadAllText(dialog.FileName, Encoding.UTF8), dialog.FileName); } catch (Exception e) { ShowError(e); } };
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
        FormClosed += delegate { settingsTimer.Dispose(); hints.Dispose(); };
        InitializeCodeMode(layout, row2);
        InitializeComparisonMode();
        InitializePresetMode();
        StyleWorkspace(this);
        RestoreSettings();
        try { FillPresetGrid(preset); } catch (Exception e) { presetStatus.Text = "Ошибка пресета: " + e.Message; }
    }
    void InitializeCodeMode(TableLayoutPanel folderLayout, TableLayoutPanel presetRow) {
        Controls.Remove(folderLayout);
        folderLayout.RowStyles[1].Height = 0;
        folderLayout.RowStyles[3].Height = 0;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 194)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(27, 43, 54), Padding = new Padding(12), Margin = Padding.Empty };
        var brand = new Label { Text = "SQL Atelier", ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Bold), Dock = DockStyle.Top, Height = 48, Padding = new Padding(4, 8, 0, 0) };
        var menu = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 12, 0, 0) };
        string[] menuText = { "SQL-код\nФорматирование", "Папка SQL\nОбработка файлов", "Сравнение SQL\nРазличия двух версий", "Настройки пресета\nПравила форматирования" };
        for (int index = 0; index < menuText.Length; index++) {
            int target = index;
            var button = new Button { Name = "navigate" + index, Text = menuText[index], Width = 168, Height = 66, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(201, 216, 223), BackColor = sidebar.BackColor, Margin = new Padding(0, 0, 0, 8), CausesValidation = false };
            button.FlatAppearance.BorderSize = 0; button.Click += delegate { modes.SelectedIndex = target; };
            menu.Controls.Add(button); navigation.Add(button);
        }
        sidebar.Controls.Add(menu); sidebar.Controls.Add(brand); shell.Controls.Add(sidebar, 0, 0);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12), Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(presetRow, 0, 0);
        presetRow.ColumnStyles[0].Width = 80; presetRow.ColumnStyles[2].Width = 132;
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
        options.Controls.Add(new Label { Text = "Регистр", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        charCase.Dock = DockStyle.Fill; options.Controls.Add(charCase, 1, 0);
        compatibility.AutoSize = false; compatibility.AutoEllipsis = true; compatibility.Dock = DockStyle.Fill; compatibility.Margin = new Padding(12, 6, 0, 0);
        compatibility.Text = "Настройки общие для всех разделов";
        var editPreset = new Button { Text = "Настроить пресет", Dock = DockStyle.Fill, CausesValidation = false };
        editPreset.Click += delegate { if (!busy) modes.SelectedIndex = 3; }; options.Controls.Add(editPreset, 3, 0);
        options.Controls.Add(compatibility, 2, 0); root.Controls.Add(options, 0, 1);
        modes.Name = "workModes"; modes.Dock = DockStyle.Fill;
        var codePage = new TabPage("SQL-код");
        var folderPage = new TabPage("Обработка папки");
        modes.TabPages.AddRange(new TabPage[] { codePage, folderPage });
        modes.SelectedIndex = 0;
        folderPage.Controls.Add(folderLayout);
        root.Controls.Add(modes, 0, 2); shell.Controls.Add(root, 1, 0); Controls.Add(shell);
        modes.SelectedIndexChanged += delegate { UpdateNavigation(); }; UpdateNavigation();
        var codeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        codeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        codeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        codeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        codeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        formatCode.Name = "formatCode"; formatCode.Text = "Форматировать · Ctrl+Enter"; formatCode.Width = 228; formatCode.Height = 34; formatCode.BackColor = Color.FromArgb(200, 234, 180);
        copyCode.Name = "copyCode"; copyCode.Text = "Копировать"; copyCode.Width = 105; copyCode.Height = 32; copyCode.Enabled = false;
        saveCode.Name = "saveCode"; saveCode.Text = "Сохранить…"; saveCode.Width = 108; saveCode.Height = 32; saveCode.Enabled = false;
        compareCode.Name = "compareCode"; compareCode.Text = "Сравнить с исходником"; compareCode.Width = 204; compareCode.Height = 34; compareCode.Enabled = false;
        compareCode.Click += async delegate { compareLeft.Text = codeInput.Text; compareRight.Text = codeOutput.Text; modes.SelectedIndex = 2; await CompareSql(false); };
        toolbar.Controls.AddRange(new Control[] { formatCode, compareCode }); codeLayout.Controls.Add(toolbar, 0, 0);
        syncScroll.Name = "syncScroll"; syncScroll.Text = "Синхронная прокрутка"; syncScroll.AutoSize = true;
        codeLayout.Controls.Add(syncScroll, 0, 1);
        codeInput.ScrollPositionChanged += delegate { SyncEditors(codeInput, codeOutput); };
        codeOutput.ScrollPositionChanged += delegate { SyncEditors(codeOutput, codeInput); };
        syncScroll.CheckedChanged += delegate { if (syncScroll.Checked) SyncEditors(codeInput, codeOutput); };
        var editors = new SplitContainer { Name = "codeEditors", Dock = DockStyle.Fill, Size = new Size(900, 500), SplitterDistance = 450, Panel1MinSize = 260, Panel2MinSize = 260, SplitterWidth = 7 };
        var sourceActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, WrapContents = true };
        sourceActions.Controls.Add(new Label { Text = "Исходный SQL", AutoSize = true, Margin = new Padding(0, 8, 12, 0) });
        var paste = new Button { Name = "pasteCode", Text = "Вставить", AutoSize = true, Height = 32 };
        paste.Click += delegate { try { if (Clipboard.ContainsText()) codeInput.SelectedText = Clipboard.GetText(); codeInput.Focus(); } catch (Exception e) { codeStatus.Text = "Буфер обмена: " + e.Message; } };
        var open = new Button { Name = "openCode", Text = "Открыть…", AutoSize = true, Height = 32 };
        open.Click += delegate { OpenCodeFile(); };
        sourceActions.Controls.AddRange(new Control[] { open, paste });
        var resultActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, WrapContents = true };
        resultActions.Controls.Add(new Label { Text = "Результат", AutoSize = true, Margin = new Padding(0, 8, 12, 0) });
        resultActions.Controls.AddRange(new Control[] { copyCode, saveCode });
        codeInput.Name = "codeInput"; codeOutput.Name = "codeOutput";
        foreach (var box in new TextBox[] { codeInput, codeOutput }) {
            box.Dock = DockStyle.Fill; box.Multiline = true; box.WordWrap = false; box.ScrollBars = ScrollBars.Both;
            box.Font = new Font("Consolas", 11); box.BackColor = Color.White; box.AcceptsTab = true; box.MaxLength = 2000000;
            box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.A) { ((TextBox)sender).SelectAll(); e.SuppressKeyPress = true; } };
        }
        codeOutput.ReadOnly = true;
        editors.Panel1.Controls.Add(codeInput); editors.Panel1.Controls.Add(sourceActions);
        editors.Panel2.Controls.Add(codeOutput); editors.Panel2.Controls.Add(resultActions); codeLayout.Controls.Add(editors, 0, 2);
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
    void UpdateNavigation() {
        for (int index = 0; index < navigation.Count; index++) {
            bool selected = modes.SelectedIndex == index;
            navigation[index].BackColor = selected ? Color.FromArgb(52, 79, 92) : Color.FromArgb(27, 43, 54);
            navigation[index].ForeColor = selected ? Color.White : Color.FromArgb(201, 216, 223);
            navigation[index].AccessibleDescription = selected ? "Текущий раздел" : "Открыть раздел";
        }
    }
    void StyleWorkspace(Control parent) {
        foreach (Control control in parent.Controls) {
            var button = control as Button;
            if (button != null && !navigation.Contains(button)) {
                bool primary = button == formatCode || button == save || button.Name == "applyPreset" || button.BackColor == Color.FromArgb(200, 234, 180);
                button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = primary ? Color.FromArgb(168, 200, 155) : Color.FromArgb(206, 216, 221);
                button.BackColor = primary ? Color.FromArgb(211, 235, 197) : Color.White;
                button.ForeColor = Color.FromArgb(29, 51, 62); button.Cursor = Cursors.Hand;
                button.Font = new Font("Segoe UI", 9);
            }
            var grid = control as DataGridView;
            if (grid != null) {
                grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 241, 244);
                grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(38, 61, 73); grid.ColumnHeadersHeight = 36;
                grid.GridColor = Color.FromArgb(224, 231, 235); grid.BorderStyle = BorderStyle.None;
                grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(195, 221, 237); grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(20, 43, 58);
            }
            StyleWorkspace(control);
        }
        hints.SetToolTip(compareCode, "Открыть сравнение исходного SQL с результатом форматирования");
        hints.SetToolTip(presetPath, "Активный пресет. Настройки общие для всех разделов.");
    }
    void OpenCodeFile() {
        using (var dialog = new OpenFileDialog { Filter = "SQL-файлы (*.sql;*.txt)|*.sql;*.txt|Все файлы (*.*)|*.*" }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try {
                codeInput.Text = ReadCodeFile(dialog.FileName);
                codeStatus.Text = "Открыт: " + Path.GetFileName(dialog.FileName) + ". Нажмите «Форматировать».";
                codeInput.Focus();
            } catch (Exception e) { codeStatus.Text = "Не удалось открыть файл: " + e.Message; }
        }
    }
    internal static string ReadCodeFile(string file) {
        if (new FileInfo(file).Length > 8000000) throw new Exception("Файл больше 8 МБ.");
        string text;
        try { using (var reader = new StreamReader(file, new UTF8Encoding(false, true), true)) text = reader.ReadToEnd(); }
        catch (DecoderFallbackException) { text = File.ReadAllText(file, Encoding.GetEncoding(1251)); }
        if (text.Length > 2000000) throw new Exception("Максимум — 2 млн символов. Выберите меньший фрагмент.");
        return WindowsLines(text);
    }
    void InitializePresetMode() {
        var page = new TabPage("Настройки пресета"); modes.TabPages.Add(page);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); page.Controls.Add(layout);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        var apply = new Button { Name = "applyPreset", Text = "Применить настройки", AutoSize = true, Height = 34, BackColor = Color.FromArgb(200, 234, 180) };
        var export = new Button { Text = "Сохранить INI как…", AutoSize = true, Height = 34 };
        var undo = new Button { Text = "Отменить правки", AutoSize = true, Height = 34, CausesValidation = false };
        var reset = new Button { Text = "Встроенный пресет", AutoSize = true, Height = 34, CausesValidation = false };
        toolbar.Controls.AddRange(new Control[] { apply, export, undo, reset }); layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Измените значение и нажмите «Применить настройки». Настройки общие для всех разделов.\r\n0/1 — нет/да. Регистр: 0 — сохранить, 1 — нижний, 2 — верхний, 3 — с заглавной." }, 0, 1);
        presetGrid.Name = "presetGrid"; presetGrid.Dock = DockStyle.Fill; presetGrid.RowHeadersVisible = false;
        presetGrid.AllowUserToAddRows = presetGrid.AllowUserToDeleteRows = false; presetGrid.AutoGenerateColumns = false;
        presetGrid.BackgroundColor = Color.White; presetGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        foreach (string title in new string[] { "Секция", "Параметр", "Значение", "Описание", "Поддержка" }) {
            int index = presetGrid.Columns.Count;
            var column = new DataGridViewTextBoxColumn { HeaderText = title, ReadOnly = index != 2, SortMode = DataGridViewColumnSortMode.NotSortable };
            column.Width = index == 0 ? 130 : index == 1 ? 250 : index == 2 ? 80 : 155;
            if (index == 3) { column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; column.MinimumWidth = 200; }
            presetGrid.Columns.Add(column);
        }
        presetGrid.CellValidating += delegate(object sender, DataGridViewCellValidatingEventArgs e) {
            if (e.ColumnIndex != 2 || fillingPresetGrid) return;
            int value;
            if (!Int32.TryParse(Convert.ToString(e.FormattedValue), out value) || value < 0 || value > 1000) {
                presetGrid.Rows[e.RowIndex].ErrorText = "Введите целое число от 0 до 1000."; e.Cancel = true;
                presetStatus.Text = "Значение должно быть целым числом от 0 до 1000. Esc отменяет редактирование ячейки.";
            } else presetGrid.Rows[e.RowIndex].ErrorText = "";
        };
        presetGrid.CellValueChanged += delegate { if (!fillingPresetGrid) presetStatus.Text = "Есть неприменённые правки. Нажмите «Применить настройки»."; };
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
        filters.Controls.Add(new Label { Text = "Поиск", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
        presetSearch.Name = "presetSearch"; presetSearch.Width = 200;
        presetSection.Name = "presetSection"; presetSection.DropDownStyle = ComboBoxStyle.DropDownList; presetSection.Width = 160;
        presetSection.Items.Add("Все секции"); presetSection.SelectedIndex = 0;
        presetSupportedOnly.Name = "presetSupportedOnly"; presetSupportedOnly.Text = "Только поддерживаемые"; presetSupportedOnly.AutoSize = true;
        presetFilterCount.AutoSize = true; presetFilterCount.Margin = new Padding(8, 7, 0, 0);
        filters.Controls.AddRange(new Control[] { presetSearch, presetSection, presetSupportedOnly, presetFilterCount });
        layout.Controls.Add(filters, 0, 2);
        presetSearch.TextChanged += delegate { FilterPresetRows(); }; presetSection.SelectedIndexChanged += delegate { FilterPresetRows(); };
        presetSupportedOnly.CheckedChanged += delegate { FilterPresetRows(); };
        presetGrid.Columns[0].Width = 105; presetGrid.Columns[1].Width = 208; presetGrid.Columns[2].Width = 75; presetGrid.Columns[4].Width = 132;
        layout.Controls.Add(presetGrid, 0, 3);
        presetStatus.Name = "presetStatus"; presetStatus.Dock = DockStyle.Fill; layout.Controls.Add(presetStatus, 0, 4);
        apply.Click += delegate { ApplyPresetChanges(); };
        undo.Click += delegate { presetGrid.CancelEdit(); FillPresetGrid(preset); };
        reset.Click += delegate { try { SelectPreset(Engine.Preset, ""); } catch (Exception e) { presetStatus.Text = "Ошибка: " + e.Message; } };
        export.Click += delegate {
            using (var dialog = new SaveFileDialog { Filter = "INI-пресеты (*.ini)|*.ini", FileName = selectedPresetFile.Length == 0 ? "custom-preset.ini" : Path.GetFileName(selectedPresetFile), DefaultExt = "ini", AddExtension = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { SavePresetFile(dialog.FileName); } catch (Exception e) { presetStatus.Text = "Не удалось сохранить: " + e.Message; }
            }
        };
    }
    static string DescribePresetOption(string key) {
        switch (key) {
            case "BlockIndent": return "Размер отступа блока, пробелы";
            case "IdentCharCase": return "Регистр идентификаторов (0–3)";
            case "KeywordCharCase": return "Регистр ключевых слов SQL (0–3)";
            case "RightMargin": return "Мягкий предел длины строки (движок: 40–500)";
            case "KeepEmptyLines": return "Сохранять пустые строки (0/1)";
            case "EmptyLinesNumber": return "Пустые строки между операторами тела (кроме серии присваиваний и DECLARE)";
            case "SpaceAfterComma": return "Пробел после запятой (0/1)";
            case "NoSpaceAfterCommaInDatatypes": return "Без пробела после запятой в типах (0/1)";
            case "NoSpaceBetweenProcNameAndBracket": return "Без пробела между именем и скобкой (0/1)";
            case "StartSingleLineCommentsFromFirstColumn": return "Комментарии -- с первой колонки (0/1)";
            case "SpaceAfterOpeningBracket": return "Пробел после открывающей скобки (0/1)";
            case "SpaceBeforeClosingBracket": return "Пробел перед закрывающей скобкой (0/1)";
            case "AlignTypeDefinitions": return "Выравнивание определений типов (0/1)";
        }
        if (key.StartsWith("CRLFBefore")) return "Перенос строки перед " + key.Substring(10).Replace('_', ' ') + " (0/1)";
        if (key.StartsWith("CRLFAfter")) return "Перенос строки после " + key.Substring(9).Replace('_', ' ') + " (0/1)";
        if (key.StartsWith("IndentBefore")) return "Отступ перед " + key.Substring(12).Replace('_', ' ') + ", пробелы";
        if (key.StartsWith("NoCRLFBetween")) return "Без переноса между " + key.Substring(13).Replace('_', ' ') + " (0/1)";
        if (key.StartsWith("One")) return "По одному элементу на строку: " + key.Substring(3) + " (0/1)";
        if (key.EndsWith("Indent") || key == "Indent") return "Размер отступа, пробелы";
        return "Числовой параметр INI (0–1000)";
    }
    void FillPresetGrid(string text) { FillPresetGrid(text, Engine.Call(new { operation = "preset", presetText = text })); }
    void FillPresetGrid(string text, Plan metadata) {
        fillingPresetGrid = true;
        try {
            presetGrid.CancelEdit(); presetGrid.Rows.Clear();
            var ignored = new HashSet<string>(metadata.unsupported);
            string previousSection = Convert.ToString(presetSection.SelectedItem);
            presetSection.Items.Clear(); presetSection.Items.Add("Все секции");
            foreach (var section in metadata.settings.Keys) presetSection.Items.Add(section);
            presetSection.SelectedIndex = presetSection.Items.Contains(previousSection) ? presetSection.Items.IndexOf(previousSection) : 0;
            foreach (var section in metadata.settings) foreach (var entry in section.Value) {
                bool supported = !ignored.Contains("[" + section.Key + "] " + entry.Key);
                int row = presetGrid.Rows.Add(section.Key, entry.Key, entry.Value, DescribePresetOption(entry.Key), supported ? "Поддерживается" : "Не применяется");
                if (!supported) presetGrid.Rows[row].DefaultCellStyle.ForeColor = Color.FromArgb(145, 100, 25);
            }
            presetEditorSource = text;
            presetStatus.Text = "Параметров: " + presetGrid.Rows.Count + ". Не применяются движком: " + metadata.unsupported.Count + ". " + String.Join(" · ", metadata.warnings);
        } finally { fillingPresetGrid = false; }
        FilterPresetRows();
    }
    internal void FilterPresetRows() {
        if (fillingPresetGrid || !presetGrid.EndEdit()) return;
        string query = presetSearch.Text.Trim(), section = Convert.ToString(presetSection.SelectedItem);
        presetGrid.CurrentCell = null; int visible = 0;
        foreach (DataGridViewRow row in presetGrid.Rows) {
            string haystack = Convert.ToString(row.Cells[0].Value) + " " + row.Cells[1].Value + " " + row.Cells[3].Value;
            bool show = (presetSection.SelectedIndex <= 0 || Convert.ToString(row.Cells[0].Value) == section) &&
                (!presetSupportedOnly.Checked || Convert.ToString(row.Cells[4].Value) == "Поддерживается") &&
                (query.Length == 0 || haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            row.Visible = show; if (show) visible++;
        }
        presetFilterCount.Text = visible + " / " + presetGrid.Rows.Count;
    }
    internal void SelectPreset(string text, string file) {
        var metadata = Engine.Call(new { operation = "preset", presetText = text });
        preset = text; selectedPresetFile = file; presetEdited = false; UpdatePresetPath();
        FillPresetGrid(text, metadata); PresetChanged();
    }
    void UpdatePresetPath() { presetPath.Text = (selectedPresetFile.Length == 0 ? "Встроенный preset_ibeexpert.ini" : selectedPresetFile) + (presetEdited ? " (изменён в приложении)" : ""); }
    void PresetChanged() {
        InvalidatePlan(); InvalidateCode(); ClearDifferences();
        compareStatus.Text = "Пресет изменён. При необходимости повторите выравнивание и сравнение.";
        unsupportedOptions = null; compatibility.Text = "Пресет обновлён. Поддержка параметров указана во вкладке настроек.";
        QueueSettings(); PersistSettings();
    }
    internal bool ApplyPresetChanges() {
        try {
            if (!presetGrid.EndEdit()) return false;
            if (presetEditorSource == null) throw new Exception("Загрузите корректный INI-пресет.");
            var values = new Dictionary<string, string>();
            foreach (DataGridViewRow row in presetGrid.Rows) {
                int value;
                if (!Int32.TryParse(Convert.ToString(row.Cells[2].Value), out value) || value < 0 || value > 1000) throw new Exception("[" + row.Cells[0].Value + "] " + row.Cells[1].Value + ": требуется целое число от 0 до 1000.");
                values[Convert.ToString(row.Cells[0].Value) + "\0" + row.Cells[1].Value] = value.ToString();
            }
            string section = "";
            var lines = presetEditorSource.Split('\n');
            for (int i = 0; i < lines.Length; i++) {
                var header = Regex.Match(lines[i].Trim(), @"^\[([^\]]+)\]\s*(?:[;#].*)?$");
                if (header.Success) { section = header.Groups[1].Value; continue; }
                var pair = Regex.Match(lines[i], @"^(\s*([^=]+?)\s*=\s*)-?\d+(\s*)$");
                string value;
                if (pair.Success && values.TryGetValue(section + "\0" + pair.Groups[2].Value.Trim(), out value)) lines[i] = pair.Groups[1].Value + value + pair.Groups[3].Value;
            }
            string candidate = String.Join("\n", lines);
            var metadata = Engine.Call(new { operation = "preset", presetText = candidate });
            bool changed = candidate != preset;
            preset = candidate; presetEdited = presetEdited || changed; UpdatePresetPath();
            FillPresetGrid(candidate, metadata);
            if (changed) PresetChanged();
            presetStatus.Text = "Настройки применены ко всем режимам и сохранены для следующего запуска. INI на диске изменяется только через «Сохранить INI как…».";
            return true;
        } catch (Exception e) { presetStatus.Text = "Ошибка: " + e.Message; return false; }
    }
    internal void SavePresetFile(string file) {
        if (!ApplyPresetChanges()) return;
        File.WriteAllText(file, preset, new UTF8Encoding(false));
        selectedPresetFile = file; presetEdited = false; UpdatePresetPath(); PersistSettings();
        presetStatus.Text = "Пресет сохранён: " + file;
    }
    void InitializeComparisonMode() {
        var page = new TabPage("Сравнение SQL"); modes.TabPages.Add(page);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); page.Controls.Add(layout);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
        var compareToolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        compareToolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); compareToolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var compare = new Button { Text = "Сравнить · Ctrl+Enter", AutoSize = true, Height = 34 };
        var format = new Button { Text = "Выровнять и сравнить", AutoSize = true, Height = 34, BackColor = Color.FromArgb(200, 234, 180) };
        var previous = new Button { Text = "↑ Различие", AutoSize = true, Height = 34 };
        var next = new Button { Text = "↓ Различие", AutoSize = true, Height = 34 };
        toolbar.Controls.AddRange(new Control[] { compare, format, previous, next }); compareToolbar.Controls.Add(toolbar, 0, 0); layout.Controls.Add(compareToolbar, 0, 0);
        collapseEqual.Name = "collapseEqual"; collapseEqual.Text = "Скрывать совпадающие строки"; collapseEqual.Checked = true; collapseEqual.AutoSize = true;
        var collapseAll = new Button { Name = "collapseAll", Text = "Свернуть все совпадения", AutoSize = true, Height = 34 };
        var viewOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        viewOptions.Controls.Add(collapseEqual); viewOptions.Controls.Add(collapseAll); compareToolbar.Controls.Add(viewOptions, 0, 1);
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
        codeOutput.Clear(); copyCode.Enabled = saveCode.Enabled = compareCode.Enabled = false;
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
            if (preferences.PresetEdited && !String.IsNullOrEmpty(preferences.PresetText)) {
                preset = preferences.PresetText; presetEdited = true;
            } else if (selectedPresetFile.Length > 0) {
                if (File.Exists(selectedPresetFile)) { preset = File.ReadAllText(selectedPresetFile, Encoding.UTF8); presetPath.Text = selectedPresetFile; }
                else if (!String.IsNullOrEmpty(preferences.PresetText)) { preset = preferences.PresetText; presetPath.Text = selectedPresetFile + " (сохранённая копия)"; }
                else { selectedPresetFile = ""; notice += " Пресет не найден; используется встроенный."; }
            }
            if (presetEdited || selectedPresetFile.Length == 0 || File.Exists(selectedPresetFile)) UpdatePresetPath();
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
                PresetText = selectedPresetFile.Length > 0 || presetEdited ? preset : null, PresetEdited = presetEdited,
                IncludeSubfolders = recursive.Checked, CaseSelection = charCase.SelectedIndex,
                WindowWidth = size.Width, WindowHeight = size.Height,
                WindowMaximized = WindowState == FormWindowState.Maximized, LastBackupFolder = backupPath
            });
        } catch (Exception e) { summary.Text = "Не удалось сохранить настройки: " + e.Message; }
    }
    static string WindowsLines(string text) { return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n"); }
    void InvalidatePlan() { if (busy || restoringSettings) return; plan = null; save.Enabled = false; files.Items.Clear(); original.Clear(); formatted.Clear(); QueueSettings(); }
    void Busy(bool value) { busy = value; foreach (var button in navigation) button.Enabled = !value; chooseFolder.Enabled = choosePreset.Enabled = scan.Enabled = folder.Enabled = recursive.Enabled = charCase.Enabled = !value; modes.Enabled = formatCode.Enabled = codeInput.Enabled = !value; copyCode.Enabled = saveCode.Enabled = compareCode.Enabled = !value && codeOutput.TextLength > 0; save.Enabled = !value && plan != null && plan.rows.Exists(r => r.status == "changed"); progress.Style = value ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks; }
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
        CheckWorkspaceUi(root);
        CheckPresetSettings(root);
        using (var window = new MainWindow(settingsPath)) {
            window.CreateControl(); window.PerformLayout(); if (window.Controls.Count == 0) throw new Exception("Window layout initialization failed");
            CheckComparison(window);
            var tabs = (TabControl)window.Controls.Find("workModes", true)[0];
            var input = (TextBox)window.Controls.Find("codeInput", true)[0];
            var output = (TextBox)window.Controls.Find("codeOutput", true)[0];
            var copy = (Button)window.Controls.Find("copyCode", true)[0];
            if (tabs.TabCount != 4 || tabs.SelectedIndex != 0 || input.ReadOnly || !output.ReadOnly) throw new Exception("Code must be the default editable mode");
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
    static void LayoutTree(Control control) {
        control.PerformLayout();
        foreach (Control child in control.Controls) LayoutTree(child);
    }
    static void CheckWorkspaceUi(string root) {
        using (var window = new MainWindow(Path.Combine(root, "ui-test.settings.json"))) {
            window.CreateControl();
            var tabs = (TabControl)window.Controls.Find("workModes", true)[0];
            var split = (SplitContainer)window.Controls.Find("codeEditors", true)[0];
            foreach (int width in new int[] { 880, 1040, 1200, 1600 }) {
                window.Size = new Size(width, 800);
                for (int pass = 0; pass < 3; pass++) LayoutTree(window);
                if (split.Panel1.Width < 260 || split.Panel2.Width < 260 || split.Height < 180) throw new Exception("Editor panes are clipped at width " + width + ": " + split.Size + ", left=" + split.Panel1.Width + ", right=" + split.Panel2.Width + ", page=" + tabs.SelectedTab.Size + ", window=" + window.ClientSize);
                foreach (string name in new string[] { "formatCode", "compareCode", "copyCode", "saveCode", "openCode", "pasteCode" }) {
                    var control = window.Controls.Find(name, true)[0];
                    if (control.Left < 0 || control.Top < 0 || control.Right > control.Parent.ClientSize.Width || control.Bottom > control.Parent.ClientSize.Height) throw new Exception("Action clipped: " + name + " at width " + width);
                }
            }
            tabs.SelectedIndex = 3;
            if (window.Controls.Find("navigate3", true)[0].BackColor == window.Controls.Find("navigate0", true)[0].BackColor) throw new Exception("Navigation selection is not indicated");
            var search = (TextBox)window.Controls.Find("presetSearch", true)[0];
            var section = (ComboBox)window.Controls.Find("presetSection", true)[0];
            var grid = (DataGridView)window.Controls.Find("presetGrid", true)[0];
            section.SelectedItem = "General"; search.Text = "BlockIndent";
            int visible = 0;
            foreach (DataGridViewRow row in grid.Rows) if (row.Visible) visible++;
            if (visible != 1) throw new Exception("Preset search and section filter failed");
            PresetRow(window, "General", "BlockIndent").Cells[2].Value = 6;
            search.Text = "not-a-preset-option";
            if (!window.ApplyPresetChanges() || Convert.ToInt32(PresetRow(window, "General", "BlockIndent").Cells[2].Value) != 6) throw new Exception("Filtering discarded edited settings");
            search.Clear(); section.SelectedIndex = 0;
            window.SelectPreset(Engine.Preset + "\n[Future]\nUnknown=7\n", "");
            ((CheckBox)window.Controls.Find("presetSupportedOnly", true)[0]).Checked = true;
            if (PresetRow(window, "Future", "Unknown").Visible) throw new Exception("Unsupported preset filter failed");
        }
        string utf8 = Path.Combine(root, "editor-utf8.sql"), cp1251 = Path.Combine(root, "editor-cp1251.sql"), utf16 = Path.Combine(root, "editor-utf16.sql");
        const string text = "select 'Русский текст';\n";
        File.WriteAllText(utf8, text, new UTF8Encoding(false)); File.WriteAllText(cp1251, text, Encoding.GetEncoding(1251)); File.WriteAllText(utf16, text, Encoding.Unicode);
        foreach (string file in new string[] { utf8, cp1251, utf16 }) if (MainWindow.ReadCodeFile(file) != text.Replace("\n", "\r\n")) throw new Exception("Editor file encoding failed: " + file);
        // Keep the UI test SQL inputs separate from the folder-processing fixture.
        File.Delete(utf8); File.Delete(cp1251); File.Delete(utf16);
    }
    static DataGridViewRow PresetRow(MainWindow window, string section, string key) {
        var grid = (DataGridView)window.Controls.Find("presetGrid", true)[0];
        foreach (DataGridViewRow row in grid.Rows) if (Convert.ToString(row.Cells[0].Value) == section && Convert.ToString(row.Cells[1].Value) == key) return row;
        throw new Exception("Preset setting missing: " + section + "/" + key);
    }
    static void CheckPresetSettings(string root) {
        string settings = Path.Combine(root, "preset-test.settings.json"), file = Path.Combine(root, "chosen.ini"), exported = Path.Combine(root, "exported.ini");
        string source = "; Keep this comment\r\n" + Engine.Preset + "\r\n[Future]\r\nUnknown=7\r\n";
        File.WriteAllText(file, source, new UTF8Encoding(false));
        using (var window = new MainWindow(settings)) {
            window.SelectPreset(source, file);
            if (Convert.ToString(PresetRow(window, "DeclareSection", "CRLFBeforeSELECT").Cells[4].Value) != "Поддерживается" ||
                Convert.ToString(PresetRow(window, "InputOutput", "AlignTypeDefinitions").Cells[4].Value) != "Поддерживается") throw new Exception("Implemented preset rules still marked unsupported");
            if (Convert.ToString(PresetRow(window, "Future", "Unknown").Cells[4].Value) != "Не применяется") throw new Exception("Unsupported preset setting not marked");
            var input = (TextBox)window.Controls.Find("codeInput", true)[0];
            var output = (TextBox)window.Controls.Find("codeOutput", true)[0];
            input.Text = "select a from t;"; Wait(window.FormatCode());
            PresetRow(window, "General", "KeywordCharCase").Cells[2].Value = 2;
            if (!window.ApplyPresetChanges() || output.TextLength != 0) throw new Exception("Preset apply did not invalidate result");
            Wait(window.FormatCode());
            if (!output.Text.Contains("SELECT") || File.ReadAllText(file) != source) throw new Exception("Preset edits failed or original INI was overwritten");
            PresetRow(window, "General", "BlockIndent").Cells[2].Value = 1001;
            if (window.ApplyPresetChanges()) throw new Exception("Invalid preset value accepted");
            PresetRow(window, "General", "BlockIndent").Cells[2].Value = 4;
            window.SavePresetFile(exported);
            string saved = File.ReadAllText(exported);
            if (!saved.Contains("; Keep this comment") || !saved.Contains("Unknown=7") || !saved.Contains("KeywordCharCase=2") || !saved.Contains("BlockIndent=4")) throw new Exception("INI export lost settings or comments");
            PresetRow(window, "General", "KeywordCharCase").Cells[2].Value = 1;
            if (!window.ApplyPresetChanges()) throw new Exception("Custom preset update failed");
            if (File.ReadAllText(exported) != saved) throw new Exception("Applied edits overwrote exported INI");
            try { window.SelectPreset("[General]\nBlockIndent=bad", file); throw new Exception("Invalid INI selected"); }
            catch (Exception e) { if (e.Message == "Invalid INI selected") throw; }
        }
        using (var restored = new MainWindow(settings)) {
            if (Convert.ToInt32(PresetRow(restored, "General", "KeywordCharCase").Cells[2].Value) != 1 || Convert.ToInt32(PresetRow(restored, "General", "BlockIndent").Cells[2].Value) != 4) throw new Exception("Edited preset lost on restart");
            restored.SelectPreset(Engine.Preset, "");
            PresetRow(restored, "General", "BlockIndent").Cells[2].Value = 6;
            if (!restored.ApplyPresetChanges()) throw new Exception("Built-in preset edit failed");
        }
        using (var restored = new MainWindow(settings)) {
            if (Convert.ToInt32(PresetRow(restored, "General", "BlockIndent").Cells[2].Value) != 6) throw new Exception("Built-in preset changes lost on restart");
        }
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
