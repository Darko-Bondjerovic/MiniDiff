using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;

public class Program
{
    public static string[] StartupArgs = new string[0];
    [STAThread]
    public static void Main(string[] args)
    {
        StartupArgs = args ?? new string[0];
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => { };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { };
        Application.Run(new DiffForm(StartupArgs));
    }
}

public class DiffForm : Form
{
    // ========== PODESI OVDE ==========
    // Fajl u koji se cuva spisak poredjenih parova (Save dugme dodaje, Load dugme cita).
    // Format: prosto, po 2 reda po paru (ime1, ime2, ime1, ime2, ...). Nema Open dialoga za ovaj fajl - putanja je zakucana ovde.
    private const string RecentPairsFilePath = @"c:\SNIMAK\MiniDiff\Recent.txt";
    // ==================================

    TextBox txtPathL, txtPathR;
    Button btnLoadL, btnLoadR, btnCompare, btnSwap;
    SyncRichTextBox rtbL, rtbR;
    VScrollBar vsb;                       // jedan zajednicki scroll bar izmedju dva teksta
    Button btnFont, btnOrientation, btnNextDiff, btnPrevDiff;
    Font editorFont = new Font("Consolas", 14f);   // default font editora (14)
    bool syncing = false;
    bool inCompare = false;
    Encoding encL, encR;          // enkoding ucitanih fajlova (da se snimi isto)
    bool lastFocusLeft = true;    // koji je panel poslednji imao fokus (za Ctrl+S, Ctrl+F/H)
    Form findReplaceDlg;          // trenutno otvoren Find/Replace dijalog (jedan po jedan, ne-modalan)
    TextBox txtFind, txtReplace;
    CheckBox chkFindMatchCase, chkFindWrap;
    RadioButton radFindUp, radFindDown;
    CheckBox chkOnlyDiff, chkIgnoreWhite, chkIgnoreCase, chkAlign;
    Label lblStatus;
    SplitContainer split;
    LineNumberGutter gutterL, gutterR;    // kolona s brojevima linija (odvojeno od teksta, nije deo sadrzaja)
    int gutterWidth = 50;

    string[] masterLinesL = new string[0];
    string[] masterLinesR = new string[0];
    bool isShowingOnlyDiff = false;
    List<DiffBlock> curDiffBlocks = new List<DiffBlock>();   // poslednji izracunati blokovi (za dugmad Sledeća/Prethodna razlika)
    int[] curBlockLine = new int[0];                          // linija (u prikazu) na kojoj svaki blok pocinje
    bool comparing = false;
    System.Windows.Forms.Timer autoCompareTimer;
    const int LargeFileAutoCompareLimit = 6000;   // zbir linija oba fajla preko ovoga = samo rucno poredjenje (F5)
    string[] initialArgs;

    public DiffForm() : this(new string[0]) { }

    public DiffForm(string[] args)
    {
        initialArgs = args ?? new string[0];
        Text = "MiniDiff - Compare Text for 2 Files (MiniTC style)";
        Width = 1650;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        BackColor = Color.FromArgb(30, 30, 30);
        Font = new Font("Consolas", 9.5f);

        //this.Icon = new Icon(Assembly.GetExecutingAssembly()
        //    .GetManifestResourceStream("DiffForm.ico"));

        Load += (s, e) =>
        {
            try { split.SplitterDistance = SplitFullExtent() / 2; } catch { }
            LayoutLeftPanel(); UpdateScrollRange();
            if (initialArgs != null && initialArgs.Length > 0)
            {
                try
                {
                    if (initialArgs.Length >= 1 && !string.IsNullOrWhiteSpace(initialArgs[0]))
                    {
                        txtPathL.Text = initialArgs[0];
                        LoadFileFromPath(initialArgs[0], true);
                    }
                    if (initialArgs.Length >= 2 && !string.IsNullOrWhiteSpace(initialArgs[1]))
                    {
                        txtPathR.Text = initialArgs[1];
                        LoadFileFromPath(initialArgs[1], false);
                    }
                    var t = new System.Windows.Forms.Timer { Interval = 700 };
                    t.Tick += (ss, ee) => { t.Stop(); t.Dispose(); DoCompare(); };
                    t.Start();
                }
                catch { }
            }
        };
        Resize += (s, e) => { try { if (split != null) split.SplitterDistance = SplitFullExtent() / 2; } catch { } };

        var top = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Color.FromArgb(45, 45, 48) };
        Controls.Add(top);

        var lblL = new Label { Text = "Fajl 1 / Tekst 1:", ForeColor = Color.White, Location = new Point(10, 8), AutoSize = true };
        top.Controls.Add(lblL);
        txtPathL = new TextBox { Location = new Point(10, 26), Width = 480, BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        top.Controls.Add(txtPathL);
        btnLoadL = new Button { Text = "Učitaj", Location = new Point(495, 25), Width = 70, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
        btnLoadL.Click += (s, e) => LoadFile(true);
        top.Controls.Add(btnLoadL);

        // Swap (⇄) je ovde, izmedju 1. Ucitaj dugmeta i 2. edit boksa - tacno izmedju 2 panela
        btnSwap = new Button { Text = "⇄", Location = new Point(575, 25), Width = 35, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 80, 80), ForeColor = Color.White };
        btnSwap.Click += (s, e) => Swap();
        top.Controls.Add(btnSwap);

        var lblR = new Label { Text = "Fajl 2 / Tekst 2:", ForeColor = Color.White, Location = new Point(620, 8), AutoSize = true };
        top.Controls.Add(lblR);
        txtPathR = new TextBox { Location = new Point(620, 26), Width = 480, BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        top.Controls.Add(txtPathR);
        btnLoadR = new Button { Text = "Učitaj", Location = new Point(1105, 25), Width = 70, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
        btnLoadR.Click += (s, e) => LoadFile(false);
        top.Controls.Add(btnLoadR);

        var btnClear = new Button { Text = "🗑 Clear All", Location = new Point(1200, 25), Width = 90, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 40, 40), ForeColor = Color.White };
        btnClear.Click += (s, e) => ClearAll();
        top.Controls.Add(btnClear);

        autoCompareTimer = new System.Windows.Forms.Timer { Interval = 900 };
        autoCompareTimer.Tick += (s, e) => { autoCompareTimer.Stop(); DoCompare(); };

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(35, 35, 38) };
        Controls.Add(toolbar);

        btnCompare = new Button { Text = "⚡ UPOREDI (F5)", Location = new Point(10, 4), Width = 150, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(16, 185, 129), ForeColor = Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        btnCompare.Click += (s, e) => DoCompare();
        toolbar.Controls.Add(btnCompare);

        btnNextDiff = new Button { Text = "▼ Sledeća razlika", Location = new Point(170, 4), Width = 150, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 65), ForeColor = Color.White };
        btnNextDiff.Click += (s, e) => GoToDiff(true);
        toolbar.Controls.Add(btnNextDiff);

        btnPrevDiff = new Button { Text = "▲ Prethodna razlika", Location = new Point(325, 4), Width = 150, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 65), ForeColor = Color.White };
        btnPrevDiff.Click += (s, e) => GoToDiff(false);
        toolbar.Controls.Add(btnPrevDiff);

        chkOnlyDiff = new CheckBox { Text = "Prikaži samo razlike", Location = new Point(490, 8), ForeColor = Color.White, AutoSize = true, Checked = false };
        chkOnlyDiff.CheckedChanged += (s, e) => DoCompare();
        toolbar.Controls.Add(chkOnlyDiff);

        chkIgnoreWhite = new CheckBox { Text = "Ignoriši whitespace", Location = new Point(670, 8), ForeColor = Color.Gray, AutoSize = true };
        chkIgnoreWhite.CheckedChanged += (s, e) => DoCompare();
        toolbar.Controls.Add(chkIgnoreWhite);

        chkIgnoreCase = new CheckBox { Text = "Ignoriši velika/mala", Location = new Point(830, 8), ForeColor = Color.Gray, AutoSize = true };
        chkIgnoreCase.CheckedChanged += (s, e) => DoCompare();
        toolbar.Controls.Add(chkIgnoreCase);

        chkAlign = new CheckBox { Text = "Poravnaj razmakom", Location = new Point(1130, 8), ForeColor = Color.White, AutoSize = true, Checked = false };
        chkAlign.CheckedChanged += (s, e) => DoCompare();
        toolbar.Controls.Add(chkAlign);

        btnFont = new Button { Text = "Aa Font: " + editorFont.SizeInPoints.ToString("0.#"), Location = new Point(1010, 4), Width = 110, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 80, 80), ForeColor = Color.White };
        btnFont.Click += (s, e) => ChooseFont();
        toolbar.Controls.Add(btnFont);

        btnOrientation = new Button { Text = "⇕ Gore/Dole", Location = new Point(1280, 4), Width = 110, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 80, 80), ForeColor = Color.White };
        btnOrientation.Click += (s, e) => ToggleSplitOrientation();
        toolbar.Controls.Add(btnOrientation);

        var btnSaveList = new Button { Text = "💾 Save", Location = new Point(1400, 4), Width = 80, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
        btnSaveList.Click += (s, e) => SaveRecentPair();
        toolbar.Controls.Add(btnSaveList);

        var btnLoadList = new Button { Text = "📂 Load", Location = new Point(1490, 4), Width = 80, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
        btnLoadList.Click += (s, e) => ShowRecentList();
        toolbar.Controls.Add(btnLoadList);

        // Prava statusna traka na dnu prozora (umesto label-e ugurane u toolbar)
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Color.FromArgb(35, 35, 38) };
        Controls.Add(statusBar);
        lblStatus = new Label { Text = "Spremno - učitaj 2 fajla ili nalepi tekst pa F5", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), ForeColor = Color.Silver };
        statusBar.Controls.Add(lblStatus);

        split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 660, BackColor = Color.FromArgb(60, 60, 60) };
        Controls.Add(split);
        split.BringToFront();   // Fill kontrola se dokuje poslednja, da je gornje trake ne prekrivaju

        rtbL = CreateRTB();
        rtbR = CreateRTB();
        rtbR.Dock = DockStyle.None;
        rtbR.ScrollBars = RichTextBoxScrollBars.None;   // donji horizontalni scroll bar se uklanja - gornji (rtbL) sada vodi oba teksta
        rtbL.Enter += (s, e) => lastFocusLeft = true;
        rtbR.Enter += (s, e) => lastFocusLeft = false;
        txtPathL.Enter += (s, e) => lastFocusLeft = true;
        txtPathR.Enter += (s, e) => lastFocusLeft = false;

        // Levi panel: [ gutterL | rtbL | vsb ]  -> scroll bar je tacno izmedju dva teksta, brojevi linija su posebna kolona
        vsb = new VScrollBar { Width = SystemInformation.VerticalScrollBarWidth, Minimum = 0, Maximum = 100 };
        rtbL.Dock = DockStyle.None;

        gutterL = new LineNumberGutter { Target = rtbL, LineHeightProvider = () => LineHeightPx(rtbL) };
        gutterR = new LineNumberGutter { Target = rtbR, LineHeightProvider = () => LineHeightPx(rtbR) };

        split.Panel1.Controls.Add(rtbL);
        split.Panel1.Controls.Add(gutterL);
        split.Panel1.Controls.Add(vsb);
        split.Panel2.Controls.Add(rtbR);
        split.Panel2.Controls.Add(gutterR);
        split.Panel1.Resize += (s, e) => { LayoutLeftPanel(); UpdateScrollRange(); };
        split.Panel2.Resize += (s, e) => { LayoutLeftPanel(); UpdateScrollRange(); };
        LayoutLeftPanel();
        vsb.ValueChanged += (s, e) =>
        {
            if (syncing) return;
            syncing = true;
            try { rtbL.ScrollY = vsb.Value; rtbR.ScrollY = vsb.Value; }
            finally { syncing = false; }
            RefreshGutters();
        };

        TryEnableDragDrop(rtbL, true);
        TryEnableDragDrop(rtbR, false);
        TryEnableDragDrop(txtPathL, true, true);
        TryEnableDragDrop(txtPathR, false, true);

        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.F5) { DoCompare(); e.Handled = true; }
            if (e.Control && e.KeyCode == Keys.O) { LoadFile(true); }
            if (e.Control && e.KeyCode == Keys.S) { SaveFile(lastFocusLeft); e.Handled = true; e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.F) { ShowFindDialog(false); e.Handled = true; e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.H) { ShowFindDialog(true); e.Handled = true; e.SuppressKeyPress = true; }
        };

    }

    SyncRichTextBox CreateRTB()
    {
        var r = new SyncRichTextBox
        {
            Dock = DockStyle.Fill,
            Font = editorFont,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            WordWrap = false,
            AcceptsTab = true,
            ScrollBars = RichTextBoxScrollBars.ForcedHorizontal,   // vertikalni je zamenjen zajednickim vsb
            BorderStyle = BorderStyle.None,
            HideSelection = false      // selekcija ostaje vidljiva i kad panel izgubi fokus
        };
        r.ScrollChanged += (s, e) => OnRtbScrolled((SyncRichTextBox)s);
        r.UndoRequested += () => DoUndoRedo(r, false);
        r.RedoRequested += () => DoUndoRedo(r, true);
        r.CtrlWheel += delta =>
        {
            float sz = Math.Max(6f, Math.Min(48f, editorFont.SizeInPoints + (delta > 0 ? 1 : -1)));
            if (Math.Abs(sz - editorFont.SizeInPoints) > 0.01f) ApplyEditorFont(new Font(editorFont.FontFamily, sz, editorFont.Style));
        };
        r.TextChanged += (s, e) =>
        {
            UpdateScrollRange();
            if (comparing) return;
            if (isShowingOnlyDiff) return;
            var rtb = s as SyncRichTextBox;
            var disp = DisplayLines(rtb);
            var lines = StripFillers(disp);   // razmaci (FILL linije) nisu deo teksta
            if (rtb == rtbL) { masterLinesL = lines; RecordUndo(rtb, upL, disp, lines); }
            else if (rtb == rtbR) { masterLinesR = lines; RecordUndo(rtb, upR, disp, lines); }

            // Kod velikih fajlova auto-poredjenje na SVAKI taster (i rebuild celog prikaza posle) zna da "zakoci"
            // kucanje - za njih se ceka na rucno UPOREDI (F5), umesto automatskog poredjenja na svaku izmenu.
            if (masterLinesL.Length + masterLinesR.Length > LargeFileAutoCompareLimit)
            {
                lblStatus.Text = "Veliki fajl - pritisni UPOREDI (F5) da ponovo uporediš";
                autoCompareTimer.Stop();
                return;
            }

            lblStatus.Text = "Kucanje... auto-poredim";
            try { autoCompareTimer.Stop(); autoCompareTimer.Start(); } catch { DoCompare(); }
        };
        return r;
    }

    void TryEnableDragDrop(Control c, bool left, bool isPathBox = false)
    {
        try
        {
            c.AllowDrop = true;
            c.DragEnter += DragEnterFile;
            if (isPathBox) c.DragDrop += (s, e) => DropFilePath(e, left);
            else c.DragDrop += (s, e) => DropFile(e, left);
        }
        catch { try { c.AllowDrop = false; } catch { } }
    }

    void DragEnterFile(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; }
    void DropFile(DragEventArgs e, bool left) { try { var files = (string[])e.Data.GetData(DataFormats.FileDrop); if (files.Length > 0) { if (left) txtPathL.Text = files[0]; else txtPathR.Text = files[0]; LoadFileFromPath(files[0], left); DoCompare(); } } catch { } }
    void DropFilePath(DragEventArgs e, bool left) { try { var files = (string[])e.Data.GetData(DataFormats.FileDrop); if (files.Length > 0) { if (left) { txtPathL.Text = files[0]; LoadFileFromPath(files[0], true); } else { txtPathR.Text = files[0]; LoadFileFromPath(files[0], false); } DoCompare(); } } catch { } }

    void LoadFileFromPath(string file, bool left)
    {
        try
        {
            if (!File.Exists(file)) return;
            string content = ReadTextKeepEncoding(file, left);
            var lines = content.Replace("\r\n", "\n").Split('\n');
            if (left) masterLinesL = lines; else masterLinesR = lines;
            if (isShowingOnlyDiff) { chkOnlyDiff.Checked = false; isShowingOnlyDiff = false; }
            comparing = true;
            if (left) rtbL.Text = content; else rtbR.Text = content;
            comparing = false;
            ResetUndo(left);
            lblStatus.Text = $"Učitano: {Path.GetFileName(file)} ({content.Length} chars)";
        }
        catch (Exception ex) { MessageBox.Show("Greška:\n" + ex.Message); }
    }

    void LoadFile(bool left)
    {
        string path = left ? txtPathL.Text.Trim() : txtPathR.Text.Trim();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            LoadFileFromPath(path, left);
            DoCompare();
            return;
        }

        var f = this;
        var thread = new Thread(() =>
        {
            OpenFileDialog dlg = new OpenFileDialog()
            {
                Filter = "All files|*.*|Text files|*.txt;*.cs;*.log|All|*.*",
                Title = "Izaberi fajl"
            };
            string existing = left ? f.txtPathL.Text : f.txtPathR.Text;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                try
                {
                    string dir = Path.GetDirectoryName(existing);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                    if (File.Exists(existing)) { dlg.InitialDirectory = Path.GetDirectoryName(existing); dlg.FileName = Path.GetFileName(existing); }
                }
                catch { }
            }

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                string file = dlg.FileName;
                f.BeginInvoke(new Action(() =>
                {
                    f.txtPathL.Text = left ? file : f.txtPathL.Text;
                    f.txtPathR.Text = !left ? file : f.txtPathR.Text;
                    f.LoadFileFromPath(file, left);
                    f.DoCompare();
                }));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    string ReadTextKeepEncoding(string file, bool left)
    {
        byte[] bytes = File.ReadAllBytes(file);
        string text; Encoding enc;
        using (var ms = new MemoryStream(bytes))
        using (var sr = new StreamReader(ms, Encoding.UTF8, true))
        {
            text = sr.ReadToEnd();
            enc = sr.CurrentEncoding;
        }
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (enc is UTF8Encoding) enc = new UTF8Encoding(bom);   // ne dodaj BOM ako ga fajl nije imao
        if (left) encL = enc; else encR = enc;
        return text;
    }

    // Ctrl+S: snimi panel koji je poslednji imao fokus; ako nema putanje, pita (SaveFileDialog u STA threadu)
    void SaveFile(bool left)
    {
        string path = (left ? txtPathL.Text : txtPathR.Text).Trim();
        try
        {
            string dir = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                WriteFileFor(path, left);
                return;
            }
        }
        catch { }

        var f = this;
        var thread = new Thread(() =>
        {
            using (SaveFileDialog dlg = new SaveFileDialog()
            {
                Filter = "All files|*.*|Text files|*.txt;*.cs;*.log",
                Title = "Sačuvaj fajl"
            })
            {
                try
                {
                    string other = left ? f.txtPathR.Text : f.txtPathL.Text;
                    string od = string.IsNullOrWhiteSpace(other) ? null : Path.GetDirectoryName(other);
                    if (!string.IsNullOrWhiteSpace(od) && Directory.Exists(od)) dlg.InitialDirectory = od;
                }
                catch { }

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    string file = dlg.FileName;
                    f.BeginInvoke(new Action(() => f.WriteFileFor(file, left)));
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    void WriteFileFor(string file, bool left)
    {
        try
        {
            // uvek se snima "cist" tekst (bez razmaka), i kad je ukljucen prikaz samo razlika
            string content = string.Join("\r\n", left ? masterLinesL : masterLinesR);
            Encoding enc = (left ? encL : encR) ?? new UTF8Encoding(false);
            File.WriteAllText(file, content, enc);
            if (left) { txtPathL.Text = file; encL = enc; } else { txtPathR.Text = file; encR = enc; }
            lblStatus.Text = $"Sačuvano: {Path.GetFileName(file)} ({content.Length} chars)";
        }
        catch (Exception ex) { MessageBox.Show("Greška pri snimanju:\n" + ex.Message); }
    }

    // ---------- Save/Load spiska poredjenih parova (RecentPairsFilePath) ----------
    void SaveRecentPair()
    {
        string p1 = txtPathL.Text.Trim(), p2 = txtPathR.Text.Trim();
        if (string.IsNullOrWhiteSpace(p1) || string.IsNullOrWhiteSpace(p2))
        {
            MessageBox.Show("Oba polja (Fajl 1 i Fajl 2) moraju imati putanju da bi se par sačuvao.");
            return;
        }
        try
        {
            string dir = Path.GetDirectoryName(RecentPairsFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllLines(RecentPairsFilePath, new[] { p1, p2 });
            lblStatus.Text = "Par sačuvan u listu: " + RecentPairsFilePath;
        }
        catch (Exception ex) { MessageBox.Show("Greška pri snimanju liste:\n" + ex.Message); }
    }

    void ShowRecentList()
    {
        List<string> lines;
        try
        {
            if (!File.Exists(RecentPairsFilePath))
            {
                MessageBox.Show("Lista još ne postoji:\n" + RecentPairsFilePath + "\n\nSačuvaj bar jedan par (Save) da bi se napravila.");
                return;
            }
            lines = File.ReadAllLines(RecentPairsFilePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        }
        catch (Exception ex) { MessageBox.Show("Greška pri čitanju liste:\n" + ex.Message); return; }

        if (lines.Count < 2)
        {
            MessageBox.Show("Lista je prazna.");
            return;
        }

        using (var dlg = new Form())
        {
            dlg.Text = "Učitaj sačuvani par";
            dlg.Width = 760; dlg.Height = 520;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.BackColor = Color.FromArgb(30, 30, 30);
            dlg.Font = this.Font;
            dlg.KeyPreview = true;
            dlg.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) dlg.Close(); };

            var lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                GridLines = true,
                BackColor = Color.FromArgb(25, 25, 25),
                ForeColor = Color.White
            };
            lv.Columns.Add("Fajl", 220);
            lv.Columns.Add("Putanja", 480);
            dlg.Controls.Add(lv);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.FromArgb(35, 35, 38) };
            var btnLoad = new Button { Text = "Učitaj izabrani par", Left = 10, Top = 8, Width = 160, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(16, 185, 129), ForeColor = Color.White };
            // "Obriši par" ide skroz desno, i ostaje uz desnu ivicu i ako se dijalog raširi (Anchor)
            var btnDelete = new Button { Text = "Obriši par", Top = 8, Width = 110, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 40, 40), ForeColor = Color.White, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            
            bottom.Controls.Add(btnLoad);
            bottom.Controls.Add(btnDelete);
            dlg.Controls.Add(bottom);
            
            // position must be set after Client area is defined
            btnDelete.Left = dlg.ClientSize.Width - btnDelete.Width - 12;
            

            // (ponovo) puni listu iz trenutnog "lines" - koristi se i pri prvom prikazu i posle brisanja (bez zatvaranja dijaloga)
            Action populate = () =>
            {
                lv.Items.Clear();
                lv.Groups.Clear();
                for (int i = 0; i + 1 < lines.Count; i += 2)
                {
                    string path1 = lines[i], path2 = lines[i + 1];
                    var grp = new ListViewGroup("Par " + (i / 2 + 1));
                    lv.Groups.Add(grp);

                    // Tag nosi i indeks (i) u "lines" - brisanje gadja tacno taj par, ne bilo koji sa istim imenima
                    var it1 = new ListViewItem(new[] { Path.GetFileName(path1), path1 }) { Group = grp, Tag = new object[] { path1, path2, i } };
                    var it2 = new ListViewItem(new[] { Path.GetFileName(path2), path2 }) { Group = grp, Tag = new object[] { path1, path2, i } };
                    lv.Items.Add(it1);
                    lv.Items.Add(it2);
                }
            };
            populate();

            Action loadSelected = () =>
            {
                if (lv.SelectedItems.Count == 0) { MessageBox.Show("Izaberi par iz liste."); return; }
                var tag = (object[])lv.SelectedItems[0].Tag;
                string p1 = (string)tag[0], p2 = (string)tag[1];
                txtPathL.Text = p1;
                txtPathR.Text = p2;
                LoadFileFromPath(p1, true);
                LoadFileFromPath(p2, false);
                DoCompare();
                dlg.Close();
            };
            btnLoad.Click += (s, e) => loadSelected();
            lv.DoubleClick += (s, e) => loadSelected();

            btnDelete.Click += (s, e) =>
            {
                if (lv.SelectedItems.Count == 0) { MessageBox.Show("Izaberi par iz liste."); return; }
                var tag = (object[])lv.SelectedItems[0].Tag;
                int idx = (int)tag[2];
                try
                {
                    lines.RemoveRange(idx, 2);           // ukloni tacno ova 2 reda (taj par), ne po sadrzaju
                    File.WriteAllLines(RecentPairsFilePath, lines);
                    populate();                           // osvezi prikaz U ISTOM dijalogu (bez zatvaranja/otvaranja)
                }
                catch (Exception ex) { MessageBox.Show("Greška pri brisanju:\n" + ex.Message); }
            };

            dlg.ShowDialog(this);
        }
    }

    // ---------- Find (Ctrl+F) / Replace (Ctrl+H) - radi u panelu koji je poslednji imao fokus ----------
    SyncRichTextBox ActiveRtbForFind() { return lastFocusLeft ? rtbL : rtbR; }

    void ShowFindDialog(bool replaceMode)
    {
        // ako je vec otvoren, samo prebaci rezim/fokusiraj umesto da otvoris drugi
        if (findReplaceDlg != null && !findReplaceDlg.IsDisposed)
        {
            findReplaceDlg.Close();
        }

        var dlg = new Form
        {
            Text = replaceMode ? "Replace" : "Find",
            Width = 430,
            Height = replaceMode ? 210 : 200,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.FromArgb(30, 30, 30),
            Font = this.Font,
            KeyPreview = true
        };
        dlg.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) dlg.Close(); };
        dlg.FormClosed += (s, e) => { findReplaceDlg = null; };

        var lblFind = new Label { Text = "Find what:", Location = new Point(12, 18), AutoSize = true, ForeColor = Color.White };
        dlg.Controls.Add(lblFind);
        txtFind = new TextBox { Location = new Point(100, 15), Width = 200, BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        dlg.Controls.Add(txtFind);

        var btnFindNext = new Button { Text = "Find Next", Location = new Point(310, 13), Width = 95, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
        btnFindNext.Click += (s, e) => DoFind(true);
        dlg.Controls.Add(btnFindNext);

        int y = 50;
        if (replaceMode)
        {
            var lblRepl = new Label { Text = "Replace with:", Location = new Point(12, y + 3), AutoSize = true, ForeColor = Color.White };
            dlg.Controls.Add(lblRepl);
            txtReplace = new TextBox { Location = new Point(100, y), Width = 200, BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            dlg.Controls.Add(txtReplace);

            var btnReplace = new Button { Text = "Replace", Location = new Point(310, y - 2), Width = 95, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 80, 80), ForeColor = Color.White };
            btnReplace.Click += (s, e) => DoReplaceOne();
            dlg.Controls.Add(btnReplace);

            var btnReplaceAll = new Button { Text = "Replace All", Location = new Point(310, y + 34), Width = 95, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 80, 80), ForeColor = Color.White };
            btnReplaceAll.Click += (s, e) => DoReplaceAll();
            dlg.Controls.Add(btnReplaceAll);

            y += 40;
        }
        else
        {
            // Direction (Up/Down) - samo Find dijalog ima ovo
            var grp = new GroupBox { Text = "Direction", Location = new Point(190, y - 5), Width = 110, Height = 55, ForeColor = Color.White };
            radFindUp = new RadioButton { Text = "Up", Location = new Point(10, 20), AutoSize = true, ForeColor = Color.White };
            radFindDown = new RadioButton { Text = "Down", Location = new Point(55, 20), AutoSize = true, Checked = true, ForeColor = Color.White };
            grp.Controls.Add(radFindUp);
            grp.Controls.Add(radFindDown);
            dlg.Controls.Add(grp);
        }

        y += 42;
        chkFindMatchCase = new CheckBox { Text = "Match case", Location = new Point(12, y), AutoSize = true, ForeColor = Color.White };
        dlg.Controls.Add(chkFindMatchCase);
        chkFindWrap = new CheckBox { Text = "Wrap around", Location = new Point(12, y + 24), AutoSize = true, ForeColor = Color.White, Checked = true };
        dlg.Controls.Add(chkFindWrap);

        var btnCancel = new Button { Text = "Cancel", Location = new Point(310, y - 2), Width = 95, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 40, 40), ForeColor = Color.White };
        btnCancel.Click += (s, e) => dlg.Close();
        dlg.Controls.Add(btnCancel);

        txtFind.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { DoFind(true); e.Handled = true; e.SuppressKeyPress = true; } };

        findReplaceDlg = dlg;
        dlg.Show(this);
        txtFind.Focus();
    }

    // Trazi sledece poklapanje (napred ili unazad, zavisno od Direction u Find dijalogu) u panelu
    // koji je bio aktivan kad je Ctrl+F/Ctrl+H pritisnut. Vraca true ako je nadjeno.
    bool DoFind(bool showMessageIfNotFound)
    {
        var rtb = ActiveRtbForFind();
        string term = txtFind.Text;
        if (string.IsNullOrEmpty(term)) return false;

        bool down = radFindDown == null || radFindDown.Checked;   // u Replace dijalogu nema Up/Down - uvek napred
        bool matchCase = chkFindMatchCase.Checked;
        bool wrap = chkFindWrap.Checked;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = rtb.Text;

        int idx;
        if (down)
        {
            int start = rtb.SelectionStart + rtb.SelectionLength;
            idx = start <= text.Length ? text.IndexOf(term, start, cmp) : -1;
            if (idx < 0 && wrap) idx = text.IndexOf(term, 0, cmp);
        }
        else
        {
            int start = rtb.SelectionStart;
            idx = start > 0 ? text.Substring(0, start).LastIndexOf(term, cmp) : -1;
            if (idx < 0 && wrap) idx = text.LastIndexOf(term, cmp);
        }

        if (idx < 0)
        {
            if (showMessageIfNotFound) MessageBox.Show(findReplaceDlg, "Nije pronađeno: \"" + term + "\"", "Find");
            return false;
        }

        rtb.Select(idx, term.Length);
        rtb.ScrollToCaret();
        return true;
    }

    void DoReplaceOne()
    {
        var rtb = ActiveRtbForFind();
        if (isShowingOnlyDiff) { MessageBox.Show(findReplaceDlg, "Isključi 'Prikaži samo razlike' pre zamene teksta."); return; }

        string term = txtFind.Text;
        var cmp = chkFindMatchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!string.IsNullOrEmpty(term) && rtb.SelectionLength == term.Length &&
            string.Equals(rtb.SelectedText, term, cmp))
        {
            rtb.SelectedText = txtReplace.Text;   // fires TextChanged - normalna izmena, ulazi u undo i auto-poredjenje
        }
        DoFind(true);
    }

    void DoReplaceAll()
    {
        var rtb = ActiveRtbForFind();
        if (isShowingOnlyDiff) { MessageBox.Show(findReplaceDlg, "Isključi 'Prikaži samo razlike' pre zamene teksta."); return; }

        string term = txtFind.Text;
        if (string.IsNullOrEmpty(term)) return;
        string repl = txtReplace.Text ?? "";
        var cmp = chkFindMatchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = rtb.Text;

        var sb = new System.Text.StringBuilder();
        int pos = 0, count = 0;
        while (true)
        {
            int idx = text.IndexOf(term, pos, cmp);
            if (idx < 0) { sb.Append(text, pos, text.Length - pos); break; }
            sb.Append(text, pos, idx - pos);
            sb.Append(repl);
            pos = idx + term.Length;
            count++;
        }

        if (count > 0)
        {
            rtb.Text = sb.ToString();   // fires TextChanged - normalna izmena (undo + auto-poredjenje)
            autoCompareTimer.Stop();
            DoCompare();
        }
        MessageBox.Show(findReplaceDlg, count + " zamena izvršeno.", "Replace All");
    }

    void Swap()
    {
        var tmpPath = txtPathL.Text; txtPathL.Text = txtPathR.Text; txtPathR.Text = tmpPath;
        var tmpMaster = masterLinesL; masterLinesL = masterLinesR; masterLinesR = tmpMaster;
        var tmpEnc = encL; encL = encR; encR = tmpEnc;
        isShowingOnlyDiff = false; chkOnlyDiff.Checked = false;
        comparing = true; rtbL.Text = string.Join("\n", masterLinesL); rtbR.Text = string.Join("\n", masterLinesR); comparing = false;
        ResetUndo(true); ResetUndo(false);
        DoCompare();
    }

    void ClearAll()
    {
        if (isShowingOnlyDiff) { chkOnlyDiff.Checked = false; isShowingOnlyDiff = false; }
        autoCompareTimer.Stop();
        comparing = true;
        txtPathL.Text = "";
        txtPathR.Text = "";
        rtbL.Text = "";
        rtbR.Text = "";
        masterLinesL = new string[0];
        masterLinesR = new string[0];
        encL = null; encR = null;
        comparing = false;
        ResetUndo(true); ResetUndo(false);
        lblStatus.Text = "Obrisano - učitaj nove fajlove ili nalepi tekst";
        rtbL.SelectAll(); rtbL.SelectionBackColor = Color.FromArgb(30, 30, 30); rtbL.SelectionColor = Color.FromArgb(220, 220, 220);
        rtbR.SelectAll(); rtbR.SelectionBackColor = Color.FromArgb(30, 30, 30); rtbR.SelectionColor = Color.FromArgb(220, 220, 220);
        rtbL.SelectionStart = 0; rtbL.SelectionLength = 0;
        rtbR.SelectionStart = 0; rtbR.SelectionLength = 0;
    }

    void DoCompare()
    {
        if (inCompare) return;
        inCompare = true;
        comparing = true;
        bool modeSwitch = chkOnlyDiff.Checked != isShowingOnlyDiff;
        int keepY = (vsb != null && !modeSwitch) ? vsb.Value : 0;   // zapamti poziciju scrolla
        BeginRedraw(rtbL); BeginRedraw(rtbR);
        try
        {
            if (masterLinesL.Length == 0 && rtbL.TextLength > 0) masterLinesL = StripFillers(DisplayLines(rtbL));
            if (masterLinesR.Length == 0 && rtbR.TextLength > 0) masterLinesR = StripFillers(DisplayLines(rtbR));
            RecalcGutterWidth(); LayoutLeftPanel();
            string[] compL = masterLinesL.Select(NormalizeForCompare).ToArray();
            string[] compR = masterLinesR.Select(NormalizeForCompare).ToArray();
            var diff = ComputeDiff(compL, compR);
            curDiffBlocks = diff;
            int added = diff.Count(d => d.Type == DiffType.Insert);
            int removed = diff.Count(d => d.Type == DiffType.Delete);
            int changed = diff.Count(d => d.Type == DiffType.Replace);
            int equal = diff.Count(d => d.Type == DiffType.Equal);
            lblStatus.Text = $"Razlike: {added + removed + changed} | Dodato: {added} | Obrisano: {removed} | Izmenjeno: {changed} | Isto: {equal} | Ukupno: {diff.Count} blokova";
            if (chkOnlyDiff.Checked) ShowOnlyDifferences(diff); else ShowFullDiff(diff);
        }
        finally
        {
            comparing = false;
            // vrati scroll tacno gde je bio (bojenje/selekcija ga inace pomeraju) pa tek onda ukljuci iscrtavanje
            rtbL.ScrollY = keepY; rtbR.ScrollY = keepY;
            EndRedraw(rtbL); EndRedraw(rtbR);
            inCompare = false;
            UpdateScrollRange();
            if (vsb != null)
            {
                bool old = syncing; syncing = true;
                try { SetVsbValue(keepY); } finally { syncing = old; }
            }
        }
    }

    string NormalizeForCompare(string s) { if (chkIgnoreCase.Checked) s = s.ToLowerInvariant(); if (chkIgnoreWhite.Checked) s = s.Trim(); return s; }
    enum DiffType { Equal, Delete, Insert, Replace }
    class DiffBlock { public DiffType Type; public int StartL, CountL; public int StartR, CountR; }

    // ---------- racunanje razlika - optimizovano za VELIKE fajlove ----------
    // Problem koji je ovo resio: stari kod je radio O(n*m) DP tabelu na CELOM fajlu, a za fajlove
    // preko 4000 linija je upadao u "ComputeDiffSimple" koji je pravio JEDAN blok PO LINIJI (bez
    // poravnanja) - kod velikog fajla sa samo par izmenjenih linija to je znacilo hiljade "lažnih"
    // Replace blokova (jer se sve linije posle izmene pomere za jednu), a onda se za SVAKI od tih
    // blokova zasebno zove HighlightLines/GetFirstCharIndexFromLine - to je ono sto je "zaglavljivalo"
    // ceo program (UI thread blokiran i nekoliko desetina sekundi na vecim fajlovima).
    // Resenje: (1) najpre se odseca zajednicki prefiks/sufiks (linije koje su identicne na pocetku i
    // na kraju oba fajla) - kod tipicne izmene (dodata/obrisana/izmenjena 1 linija u fajlu od 20000
    // linija) ono sto ostaje "u sredini" za pravo poredjenje je ogromno manje; (2) poredjenje se radi
    // nad brojevima (hash linije -> int), ne nad stringovima, sto je brze pri svakom poredjenju;
    // (3) tacan O(n*m) DP se koristi SAMO na toj (obicno maloj) sredini; ako i sredina ostane
    // prevelika (fajlovi koji su GENUINE potpuno razliciti), koristi se brzi fallback koji i dalje
    // spaja susedne razlike u jedan blok (ne pravi hiljade sitnih blokova).
    const int MaxDpCells = 4_000_000;   // ~2000x2000 - DP tabela preko ovoga postaje spora/zahtevna po memoriji

    int[] HashLines(string[] lines, Dictionary<string, int> map)
    {
        var ids = new int[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            string s = lines[i]; int id;
            if (!map.TryGetValue(s, out id)) { id = map.Count; map[s] = id; }
            ids[i] = id;
        }
        return ids;
    }

    List<DiffBlock> ComputeDiff(string[] aStr, string[] bStr)
    {
        // hash-uj linije u int-ove (brze poredjenje, posebno za duge linije/velike fajlove)
        var map = new Dictionary<string, int>();
        int[] a = HashLines(aStr, map);
        int[] b = HashLines(bStr, map);

        int n = a.Length, m = b.Length;
        int prefix = 0, max0 = Math.Min(n, m);
        while (prefix < max0 && a[prefix] == b[prefix]) prefix++;
        int suffix = 0, maxSuf = max0 - prefix;
        while (suffix < maxSuf && a[n - 1 - suffix] == b[m - 1 - suffix]) suffix++;

        var blocks = new List<DiffBlock>();
        if (prefix > 0) blocks.Add(new DiffBlock { Type = DiffType.Equal, StartL = 0, CountL = prefix, StartR = 0, CountR = prefix });

        int n2 = n - prefix - suffix, m2 = m - prefix - suffix;
        if (n2 > 0 || m2 > 0)
        {
            var a2 = new int[n2]; Array.Copy(a, prefix, a2, 0, n2);
            var b2 = new int[m2]; Array.Copy(b, prefix, b2, 0, m2);
            List<DiffBlock> middle = ((long)n2 * m2 > MaxDpCells) ? ComputeDiffSimple(a2, b2) : ComputeDiffDP(a2, b2);
            foreach (var bl in middle)
                blocks.Add(new DiffBlock { Type = bl.Type, StartL = bl.StartL + prefix, CountL = bl.CountL, StartR = bl.StartR + prefix, CountR = bl.CountR });
        }

        if (suffix > 0) blocks.Add(new DiffBlock { Type = DiffType.Equal, StartL = n - suffix, CountL = suffix, StartR = m - suffix, CountR = suffix });
        return blocks;
    }

    // tacan LCS-baziran DP (kao i pre) - sada se zove samo na (obicno malom) delu posle odsecanja prefiksa/sufiksa
    List<DiffBlock> ComputeDiffDP(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length;
        int[,] dp = new int[n + 1, m + 1]; for (int i = n - 1; i >= 0; i--) for (int j = m - 1; j >= 0; j--) dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var blocks = new List<DiffBlock>(); int x = 0, y = 0;
        while (x < n && y < m) { if (a[x] == b[y]) { int sx = x, sy = y; while (x < n && y < m && a[x] == b[y]) { x++; y++; } blocks.Add(new DiffBlock { Type = DiffType.Equal, StartL = sx, CountL = x - sx, StartR = sy, CountR = y - sy }); } else { if (dp[x + 1, y] >= dp[x, y + 1]) { int sx = x; while (x < n && (y >= m || dp[x, y] == dp[x + 1, y])) x++; blocks.Add(new DiffBlock { Type = DiffType.Delete, StartL = sx, CountL = x - sx, StartR = y, CountR = 0 }); } else { int sy = y; while (y < m && (x >= n || dp[x, y] == dp[x, y + 1])) y++; blocks.Add(new DiffBlock { Type = DiffType.Insert, StartL = x, CountL = 0, StartR = sy, CountR = y - sy }); } } }
        if (x < n) blocks.Add(new DiffBlock { Type = DiffType.Delete, StartL = x, CountL = n - x, StartR = y, CountR = 0 }); if (y < m) blocks.Add(new DiffBlock { Type = DiffType.Insert, StartL = x, CountL = 0, StartR = y, CountR = m - y });
        var merged = new List<DiffBlock>(); for (int i = 0; i < blocks.Count; i++) { if (i + 1 < blocks.Count && blocks[i].Type == DiffType.Delete && blocks[i + 1].Type == DiffType.Insert) { merged.Add(new DiffBlock { Type = DiffType.Replace, StartL = blocks[i].StartL, CountL = blocks[i].CountL, StartR = blocks[i + 1].StartR, CountR = blocks[i + 1].CountR }); i++; } else if (i + 1 < blocks.Count && blocks[i].Type == DiffType.Insert && blocks[i + 1].Type == DiffType.Delete) { merged.Add(new DiffBlock { Type = DiffType.Replace, StartL = blocks[i + 1].StartL, CountL = blocks[i + 1].CountL, StartR = blocks[i].StartR, CountR = blocks[i].CountR }); i++; } else merged.Add(blocks[i]); } return merged;
    }

    // Fallback za slucaj da i "sredina" (posle odsecanja prefiksa/sufiksa) ostane prevelika za tacan DP
    // (fajlovi koji su GENUINE skoro potpuno razliciti). Za razliku od stare verzije, ovaj pravi JEDAN
    // blok po NIZU susednih razlicitih/istih linija umesto jednog bloka po liniji - bitno manje blokova
    // za iscrtavanje kod velikih fajlova.
    List<DiffBlock> ComputeDiffSimple(int[] a, int[] b)
    {
        var blocks = new List<DiffBlock>();
        int n = Math.Max(a.Length, b.Length), i = 0;
        while (i < n)
        {
            bool aHas = i < a.Length, bHas = i < b.Length;
            if (aHas && bHas && a[i] == b[i])
            {
                int s = i; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
                blocks.Add(new DiffBlock { Type = DiffType.Equal, StartL = s, CountL = i - s, StartR = s, CountR = i - s });
            }
            else if (aHas && bHas)
            {
                int s = i; while (i < a.Length && i < b.Length && a[i] != b[i]) i++;
                blocks.Add(new DiffBlock { Type = DiffType.Replace, StartL = s, CountL = i - s, StartR = s, CountR = i - s });
            }
            else if (aHas)
            {
                int s = i; while (i < a.Length && i >= b.Length) i++;
                blocks.Add(new DiffBlock { Type = DiffType.Delete, StartL = s, CountL = i - s, StartR = b.Length, CountR = 0 });
            }
            else
            {
                int s = i; while (i < b.Length && i >= a.Length) i++;
                blocks.Add(new DiffBlock { Type = DiffType.Insert, StartL = a.Length, CountL = 0, StartR = s, CountR = i - s });
            }
        }
        return blocks;
    }

    // ---------- prikaz sa razmacima (filler linije) ----------
    const string FILL = SyncRichTextBox.Filler;   // nevidljiva "linija" koja samo drzi mesto

    class MPos { public int Line, Col; }

    static string[] DisplayLines(RichTextBox r) { return r.Text.Replace("\r\n", "\n").Split('\n'); }

    static string[] StripFillers(string[] lines)
    {
        var list = new List<string>(lines.Length);
        foreach (var l in lines)
        {
            if (l == FILL) continue;
            list.Add(l.IndexOf('\u200B') >= 0 ? l.Replace(FILL, "") : l);
        }
        if (list.Count == 0) list.Add("");
        return list.ToArray();
    }

    // pozicija u tekstu -> (linija, kolona) u "cistom" tekstu (bez razmaka)
    MPos ToMaster(RichTextBox r, string[] disp, int idx)
    {
        idx = Math.Max(0, Math.Min(idx, r.TextLength));
        int line = r.GetLineFromCharIndex(idx);
        int first = r.GetFirstCharIndexFromLine(line); if (first < 0) first = 0;
        int col = idx - first;
        int real = 0;
        for (int i = 0; i < line && i < disp.Length; i++) if (disp[i] != FILL) real++;
        string cur = line < disp.Length ? disp[line] : "";
        if (cur == FILL) col = 0;
        else if (cur.IndexOf('\u200B') >= 0)
        {
            int k = Math.Min(col, cur.Length);
            col -= cur.Substring(0, k).Count(ch => ch == '\u200B');
            if (col < 0) col = 0;
        }
        return new MPos { Line = real, Col = col };
    }

    int FromMaster(RichTextBox r, int[] dispIdx, string[] master, MPos p)
    {
        if (dispIdx.Length == 0) return 0;
        int ml = Math.Max(0, Math.Min(p.Line, dispIdx.Length - 1));
        int idx = r.GetFirstCharIndexFromLine(dispIdx[ml]);
        if (idx < 0) return r.TextLength;
        int col = p.Line > ml ? master[ml].Length : Math.Min(p.Col, master[ml].Length);
        return idx + col;
    }

    void RestoreSel(RichTextBox r, bool mapped, int[] dispIdx, string[] master, MPos a, MPos b, int rawStart, int rawLen)
    {
        if (mapped)
        {
            int st = FromMaster(r, dispIdx, master, a);
            int en = FromMaster(r, dispIdx, master, b);
            r.SelectionStart = st;
            r.SelectionLength = Math.Max(0, Math.Min(en - st, r.TextLength - st));
        }
        else
        {
            int st = Math.Min(rawStart, r.TextLength);
            r.SelectionStart = st;
            r.SelectionLength = Math.Min(rawLen, r.TextLength - st);
        }
    }

    void ShowFullDiff(List<DiffBlock> diff)
    {
        isShowingOnlyDiff = false;

        // kursor/selekcija pre promene teksta, kao pozicija u cistom tekstu
        int selSL = rtbL.SelectionStart, selLL = rtbL.SelectionLength;
        int selSR = rtbR.SelectionStart, selLR = rtbR.SelectionLength;
        bool forcedL = caretOverrideL != null, forcedR = caretOverrideR != null;
        MPos aL, bL, aR, bR;
        if (forcedL) { aL = bL = caretOverrideL; }
        else { var d = DisplayLines(rtbL); aL = ToMaster(rtbL, d, selSL); bL = selLL > 0 ? ToMaster(rtbL, d, selSL + selLL) : aL; }
        if (forcedR) { aR = bR = caretOverrideR; }
        else { var d = DisplayLines(rtbR); aR = ToMaster(rtbR, d, selSR); bR = selLR > 0 ? ToMaster(rtbR, d, selSR + selLR) : aR; }
        caretOverrideL = null; caretOverrideR = null;

        // gradnja prikaza: gde jedna strana nema liniju, druga dobija prazno mesto (FILL)
        var dL = new List<string>(); var dR = new List<string>();
        var numL = new List<int>(); var numR = new List<int>();   // broj (originalne) linije za gutter; 0 = filler/praznina, bez broja
        var idxL = new int[masterLinesL.Length]; var idxR = new int[masterLinesR.Length];
        var hiL = new List<Tuple<int, int, Color, Color>>(); var hiR = new List<Tuple<int, int, Color, Color>>();
        bool align = chkAlign.Checked;
        Color fillBack = Color.FromArgb(45, 45, 50), plain = Color.FromArgb(220, 220, 220);
        var blockLine = new int[diff.Count];   // linija na kojoj pocinje svaki blok (za Sledeća/Prethodna razlika)
        int bi = 0;
        foreach (var b in diff)
        {
            blockLine[bi++] = Math.Max(dL.Count, dR.Count);
            int n = b.Type == DiffType.Equal ? b.CountL : Math.Max(b.CountL, b.CountR);
            // bez poravnanja (default) nema razmaka: prikaz je tacno tekst koji je ukucan
            int nL = align ? n : b.CountL, nR = align ? n : b.CountR;
            int sL = dL.Count, sR = dR.Count;
            for (int i = 0; i < nL; i++)
            {
                if (i < b.CountL) { idxL[b.StartL + i] = dL.Count; dL.Add(masterLinesL[b.StartL + i]); numL.Add(b.StartL + i + 1); } else { dL.Add(FILL); numL.Add(0); }
            }
            for (int i = 0; i < nR; i++)
            {
                if (i < b.CountR) { idxR[b.StartR + i] = dR.Count; dR.Add(masterLinesR[b.StartR + i]); numR.Add(b.StartR + i + 1); } else { dR.Add(FILL); numR.Add(0); }
            }
            if (b.Type == DiffType.Equal) continue;
            Color backL = b.Type == DiffType.Delete ? Color.FromArgb(100, 50, 50) : Color.FromArgb(100, 80, 20);
            Color backR = b.Type == DiffType.Insert ? Color.FromArgb(30, 90, 60) : Color.FromArgb(100, 80, 20);
            if (b.CountL > 0) hiL.Add(Tuple.Create(sL, b.CountL, backL, Color.FromArgb(255, 120, 120)));
            if (nL > b.CountL) hiL.Add(Tuple.Create(sL + b.CountL, nL - b.CountL, fillBack, plain));
            if (b.CountR > 0) hiR.Add(Tuple.Create(sR, b.CountR, backR, Color.FromArgb(120, 255, 150)));
            if (nR > b.CountR) hiR.Add(Tuple.Create(sR + b.CountR, nR - b.CountR, fillBack, plain));
        }

        string fullL = string.Join("\n", dL);
        string fullR = string.Join("\n", dR);
        bool needSetL = rtbL.Text != fullL;
        bool needSetR = rtbR.Text != fullR;

        if (needSetL || needSetR)
        {
            comparing = true;
            if (needSetL) rtbL.Text = fullL;
            if (needSetR) rtbR.Text = fullR;
            comparing = false;
        }

        rtbL.SelectAll(); rtbL.SelectionBackColor = Color.FromArgb(30, 30, 30); rtbL.SelectionColor = plain;
        rtbR.SelectAll(); rtbR.SelectionBackColor = Color.FromArgb(30, 30, 30); rtbR.SelectionColor = plain;
        foreach (var h in hiL) HighlightLines(rtbL, h.Item1, h.Item2, h.Item3, h.Item4);
        foreach (var h in hiR) HighlightLines(rtbR, h.Item1, h.Item2, h.Item3, h.Item4);

        try
        {
            RestoreSel(rtbL, needSetL || forcedL, idxL, masterLinesL, aL, bL, selSL, selLL);
            RestoreSel(rtbR, needSetR || forcedR, idxR, masterLinesR, aR, bR, selSR, selLR);
        }
        catch { }
        curBlockLine = blockLine;
        gutterL.LineNumbers = numL.ToArray(); gutterR.LineNumbers = numR.ToArray();
        RefreshGutters();
    }

    // ---------- sopstveni undo/redo (nativni bi ponistavao bojenje i razmake) ----------
    class UndoState { public string Text; public int Line, Col; }
    class UndoPanel
    {
        public List<UndoState> Undo = new List<UndoState>(), Redo = new List<UndoState>();
        public string PrevText = ""; public int PrevLine, PrevCol; public DateTime LastEdit = DateTime.MinValue;
    }
    UndoPanel upL = new UndoPanel(), upR = new UndoPanel();
    MPos caretOverrideL, caretOverrideR;

    void ResetUndo(bool left)
    {
        var up = left ? upL : upR;
        up.Undo.Clear(); up.Redo.Clear();
        up.PrevText = string.Join("\n", left ? masterLinesL : masterLinesR);
        up.PrevLine = 0; up.PrevCol = 0; up.LastEdit = DateTime.MinValue;
    }

    void RecordUndo(SyncRichTextBox rtb, UndoPanel up, string[] disp, string[] lines)
    {
        string now = string.Join("\n", lines);
        if (up.PrevText != now)
        {
            // nova "grupa" izmena posle >1s pauze = jedan korak undo
            if ((DateTime.UtcNow - up.LastEdit).TotalMilliseconds > 1000)
            {
                up.Undo.Add(new UndoState { Text = up.PrevText, Line = up.PrevLine, Col = up.PrevCol });
                if (up.Undo.Count > 200) up.Undo.RemoveAt(0);
            }
            up.Redo.Clear();
        }
        up.LastEdit = DateTime.UtcNow;
        up.PrevText = now;
        var c = ToMaster(rtb, disp, rtb.SelectionStart);
        up.PrevLine = c.Line; up.PrevCol = c.Col;
    }

    void DoUndoRedo(SyncRichTextBox r, bool redo)
    {
        if (isShowingOnlyDiff || inCompare) return;
        var up = (r == rtbL) ? upL : upR;
        var from = redo ? up.Redo : up.Undo;
        var to = redo ? up.Undo : up.Redo;
        if (from.Count == 0) return;
        var st = from[from.Count - 1]; from.RemoveAt(from.Count - 1);

        var cur = ToMaster(r, DisplayLines(r), r.SelectionStart);
        to.Add(new UndoState { Text = up.PrevText, Line = cur.Line, Col = cur.Col });
        up.PrevText = st.Text; up.PrevLine = st.Line; up.PrevCol = st.Col;
        up.LastEdit = DateTime.MinValue;

        var lines = st.Text.Split('\n');
        var mp = new MPos { Line = st.Line, Col = st.Col };
        if (r == rtbL) { masterLinesL = lines; caretOverrideL = mp; } else { masterLinesR = lines; caretOverrideR = mp; }
        autoCompareTimer.Stop();
        DoCompare();
        try { r.ScrollToCaret(); } catch { }
    }

    // Prikaz "samo razlike": nekada je broj originalne linije (npr. "[  10] - ...") bio upisan direktno u tekst,
    // sto znaci da je bio deo pravog sadrzaja (kopirao se, mogao se pomesati sa pravim brojem u zagradi u tekstu).
    // Sada je tekst u RichTextBox-u cist (samo stvarna linija, bez prefiksa), a broj linije ide u posebnu
    // gutter kolonu (gutterL/gutterR) - vidi se, ali nije deo teksta.
    void ShowOnlyDifferences(List<DiffBlock> diff)
    {
        isShowingOnlyDiff = true;
        var sbL = new System.Text.StringBuilder(); var sbR = new System.Text.StringBuilder();
        var numL = new List<int>(); var numR = new List<int>();
        var rangesL = new List<Tuple<int, int, DiffType>>(); var rangesR = new List<Tuple<int, int, DiffType>>();
        var blockLine = new int[diff.Count];   // linija na kojoj pocinje svaki blok (za Sledeća/Prethodna razlika)
        int lineCounterL = 0, lineCounterR = 0;
        for (int bi = 0; bi < diff.Count; bi++)
        {
            var b = diff[bi];
            blockLine[bi] = Math.Max(lineCounterL, lineCounterR);
            if (b.Type == DiffType.Equal) continue;
            if (b.Type == DiffType.Delete)
            {
                for (int i = 0; i < b.CountL; i++) { string line = masterLinesL[b.StartL + i]; int start = sbL.Length; sbL.AppendLine(line); rangesL.Add(Tuple.Create(start, sbL.Length - start, b.Type)); numL.Add(b.StartL + i + 1); lineCounterL++; }
            }
            else if (b.Type == DiffType.Insert)
            {
                for (int i = 0; i < b.CountR; i++) { string line = masterLinesR[b.StartR + i]; int start = sbR.Length; sbR.AppendLine(line); rangesR.Add(Tuple.Create(start, sbR.Length - start, b.Type)); numR.Add(b.StartR + i + 1); lineCounterR++; }
            }
            else if (b.Type == DiffType.Replace)
            {
                int max = Math.Max(b.CountL, b.CountR);
                for (int i = 0; i < max; i++)
                {
                    if (i < b.CountL) { string line = masterLinesL[b.StartL + i]; int start = sbL.Length; sbL.AppendLine(line); rangesL.Add(Tuple.Create(start, sbL.Length - start, b.Type)); numL.Add(b.StartL + i + 1); lineCounterL++; }
                    if (i < b.CountR) { string line = masterLinesR[b.StartR + i]; int start = sbR.Length; sbR.AppendLine(line); rangesR.Add(Tuple.Create(start, sbR.Length - start, b.Type)); numR.Add(b.StartR + i + 1); lineCounterR++; }
                }
            }
        }
        if (sbL.Length == 0) { sbL.AppendLine("=== NEMA RAZLIKA U LEVOM FAJLU ==="); numL.Add(0); }
        if (sbR.Length == 0) { sbR.AppendLine("=== NEMA RAZLIKA U DESNOM FAJLU ==="); numR.Add(0); }
        comparing = true; rtbL.Text = sbL.ToString(); rtbR.Text = sbR.ToString(); comparing = false;
        foreach (var r in rangesL) HighlightRange(rtbL, r.Item1, r.Item2, r.Item3 == DiffType.Delete ? Color.FromArgb(100, 50, 50) : Color.FromArgb(100, 80, 20));
        foreach (var r in rangesR) HighlightRange(rtbR, r.Item1, r.Item2, r.Item3 == DiffType.Insert ? Color.FromArgb(30, 90, 60) : Color.FromArgb(100, 80, 20));
        curBlockLine = blockLine;
        gutterL.LineNumbers = numL.ToArray(); gutterR.LineNumbers = numR.ToArray();
        RefreshGutters();
    }

    // ================= Zajednicki scroll + font =================
    void BeginRedraw(Control c) { if (c.IsHandleCreated) NativeMethods.SendMessage(c.Handle, NativeMethods.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero); }
    void EndRedraw(Control c) { if (c.IsHandleCreated) { NativeMethods.SendMessage(c.Handle, NativeMethods.WM_SETREDRAW, new IntPtr(1), IntPtr.Zero); c.Invalidate(); } }

    // Raspored teksta i zajednickog scroll bara. Kad je splitter vertikalan (levo/desno), scroll bar je
    // fizicki IZMEDJU dva teksta, unutar Panel1. Kad je horizontalan (gore/dole), jedan vertikalni scroll bar
    // ne moze fizicki da bude "izmedju" gore i dole, pa se umesto toga prostire preko CELE visine splitera
    // (i gornjeg i donjeg teksta), na desnoj ivici - jednim potezom i dalje pomera oba teksta.
    void LayoutLeftPanel()
    {
        if (vsb == null || rtbL == null || rtbR == null) return;
        int sw = vsb.Width;
        int gw = gutterWidth;

        if (split.Orientation == Orientation.Vertical)
        {
            if (vsb.Parent != split.Panel1) vsb.Parent = split.Panel1;
            var p1 = split.Panel1;
            int w = p1.ClientSize.Width, h = p1.ClientSize.Height;
            gutterL.SetBounds(0, 0, gw, Math.Max(1, h));
            rtbL.SetBounds(gw, 0, Math.Max(1, w - sw - gw), Math.Max(1, h));
            vsb.SetBounds(Math.Max(0, w - sw), 0, sw, Math.Max(1, h - SystemInformation.HorizontalScrollBarHeight));

            var p2 = split.Panel2;
            gutterR.SetBounds(0, 0, gw, Math.Max(1, p2.ClientSize.Height));
            rtbR.SetBounds(gw, 0, Math.Max(1, p2.ClientSize.Width - gw), Math.Max(1, p2.ClientSize.Height));
        }
        else
        {
            if (vsb.Parent != this) { vsb.Parent = this; vsb.BringToFront(); }
            var p1 = split.Panel1; var p2 = split.Panel2;
            gutterL.SetBounds(0, 0, gw, Math.Max(1, p1.ClientSize.Height));
            rtbL.SetBounds(gw, 0, Math.Max(1, p1.ClientSize.Width - sw - gw), Math.Max(1, p1.ClientSize.Height));
            gutterR.SetBounds(0, 0, gw, Math.Max(1, p2.ClientSize.Height));
            rtbR.SetBounds(gw, 0, Math.Max(1, p2.ClientSize.Width - sw - gw), Math.Max(1, p2.ClientSize.Height));

            int h = Math.Max(1, split.Height - SystemInformation.HorizontalScrollBarHeight);
            vsb.SetBounds(split.Right - sw, split.Top, sw, h);
        }
        RefreshGutters();
    }

    // Ukupna duzina duz koje se pomera splitter: sirina prozora kad je levo/desno, visina kad je gore/dole.
    int SplitFullExtent()
    {
        return split.Orientation == Orientation.Vertical ? ClientSize.Width : ClientSize.Height;
    }

    // Okrece raspored: levo/desno (Vertical) <-> gore/dole (Horizontal). Isti sadrzaj (Panel1 = bivsi levi/gornji,
    // Panel2 = bivsi desni/donji) samo se panel1/panel2 sada slazu jedan iznad drugog umesto jedan pored drugog.
    void ToggleSplitOrientation()
    {
        bool toHorizontal = split.Orientation == Orientation.Vertical;
        split.Orientation = toHorizontal ? Orientation.Horizontal : Orientation.Vertical;
        btnOrientation.Text = toHorizontal ? "⇔ Levo/Desno" : "⇕ Gore/Dole";
        try { split.SplitterDistance = SplitFullExtent() / 2; } catch { }
        LayoutLeftPanel();
        UpdateScrollRange();
    }

    int lineH = 0;   // visina jedne linije u pikselima (kesirano za trenutni font)

    int MeasureLineHeight(Font f)
    {
        try
        {
            using (var t = new RichTextBox { Font = f, WordWrap = false, ScrollBars = RichTextBoxScrollBars.None, Width = 300, Height = 200 })
            {
                var h = t.Handle;
                t.Text = "A\nA\nA";
                int d = t.GetPositionFromCharIndex(t.GetFirstCharIndexFromLine(1)).Y - t.GetPositionFromCharIndex(0).Y;
                if (d > 0) return d;
            }
        }
        catch { }
        return Math.Max(1, f.Height);
    }

    int LineHeightPx(RichTextBox r)
    {
        if (lineH <= 0) lineH = MeasureLineHeight(editorFont);
        return lineH;
    }

    // stvarna visina sadrzaja (racuna i praznine ubacene izmedju linija)
    int ContentHeightPx(SyncRichTextBox r)
    {
        try
        {
            int line = r.GetLineFromCharIndex(r.TextLength);
            int idx = Math.Max(0, r.GetFirstCharIndexFromLine(line));
            return r.ScrollY + r.GetPositionFromCharIndex(idx).Y + LineHeightPx(r);
        }
        catch { return 0; }
    }

    void SetVsbValue(int y)
    {
        int max = vsb.Maximum - vsb.LargeChange + 1;
        if (max < vsb.Minimum) max = vsb.Minimum;
        y = Math.Max(vsb.Minimum, Math.Min(y, max));
        if (vsb.Value != y) vsb.Value = y;
    }

    void UpdateScrollRange()
    {
        if (vsb == null || rtbL == null || rtbR == null || !rtbL.IsHandleCreated || !rtbR.IsHandleCreated) return;
        bool old = syncing; syncing = true;
        try
        {
            int view = Math.Max(1, rtbL.ClientSize.Height);
            int content = Math.Max(ContentHeightPx(rtbL), ContentHeightPx(rtbR));
            vsb.SmallChange = LineHeightPx(rtbL);
            vsb.Maximum = Math.Max(content, view) - 1;
            vsb.LargeChange = view;
        }
        catch { }
        finally { syncing = old; }
    }

    // Skoci na sledecu/prethodnu razliku. Racuna se u odnosu na trenutnu poziciju deljenog scroll bara,
    // ne pamti se posebna "trenutna razlika" - tako uvek radi ispravno i posle novog poredjenja.
    void GoToDiff(bool forward)
    {
        if (vsb == null) return;
        if (curDiffBlocks == null || curDiffBlocks.Count == 0 || curBlockLine.Length != curDiffBlocks.Count)
        {
            MessageBox.Show("Nema izračunatih razlika. Prvo pritisni UPOREDI (F5).");
            return;
        }

        int lh = Math.Max(1, LineHeightPx(rtbL));
        int curLine = vsb != null ? vsb.Value / lh : 0;
        int bestIdx = -1;

        if (forward)
        {
            for (int i = 0; i < curDiffBlocks.Count; i++)
                if (curDiffBlocks[i].Type != DiffType.Equal && curBlockLine[i] > curLine) { bestIdx = i; break; }
            if (bestIdx < 0)   // nema vise ispod - vrati se na prvu (wrap-around)
                for (int i = 0; i < curDiffBlocks.Count; i++)
                    if (curDiffBlocks[i].Type != DiffType.Equal) { bestIdx = i; break; }
        }
        else
        {
            for (int i = curDiffBlocks.Count - 1; i >= 0; i--)
                if (curDiffBlocks[i].Type != DiffType.Equal && curBlockLine[i] < curLine) { bestIdx = i; break; }
            if (bestIdx < 0)   // nema vise iznad - idi na poslednju (wrap-around)
                for (int i = curDiffBlocks.Count - 1; i >= 0; i--)
                    if (curDiffBlocks[i].Type != DiffType.Equal) { bestIdx = i; break; }
        }

        if (bestIdx < 0) { lblStatus.Text = "Nema razlika za obilazak."; return; }

        int y = curBlockLine[bestIdx] * lh;
        if (y > vsb.Maximum - vsb.LargeChange + 1) vsb.Maximum = y + vsb.LargeChange - 1;
        bool old = syncing; syncing = true;
        try { rtbL.ScrollY = y; rtbR.ScrollY = y; SetVsbValue(y); }
        finally { syncing = old; }
        RefreshGutters();
    }

    void OnRtbScrolled(SyncRichTextBox src)
    {
        if (syncing || inCompare || vsb == null) return;
        syncing = true;
        try
        {
            var other = (src == rtbL) ? rtbR : rtbL;

            int y = src.ScrollY;
            if (other.ScrollY != y) other.ScrollY = y;
            if (y > vsb.Maximum - vsb.LargeChange + 1) vsb.Maximum = y + vsb.LargeChange - 1;
            SetVsbValue(y);

            // horizontalno (levo-desno): jedini vidljivi scroll bar (na rtbL) vodi oba teksta
            int x = src.ScrollX;
            if (other.ScrollX != x) other.ScrollX = x;
        }
        catch { }
        finally { syncing = false; }
        RefreshGutters();
    }

    // Kolona s brojevima linija je potpuno odvojena od teksta (ne upisuje se u RichTextBox), pa se samo
    // ponovo iscrtava kad se promeni scroll/font/sadrzaj - sam broj se cuva u gutterL/gutterR.LineNumbers.
    void RefreshGutters()
    {
        try { gutterL?.Invalidate(); gutterR?.Invalidate(); } catch { }
    }

    void RecalcGutterWidth()
    {
        try
        {
            int maxNum = Math.Max(masterLinesL.Length, masterLinesR.Length);
            int digits = Math.Max(4, maxNum.ToString().Length);
            using (var g = CreateGraphics())
            {
                var sz = g.MeasureString(new string('9', digits), editorFont);
                gutterWidth = (int)Math.Ceiling(sz.Width) + 14;
            }
        }
        catch { gutterWidth = 50; }
    }

    void ChooseFont()
    {
        try
        {
            using (var dlg = new FontDialog { Font = editorFont, ShowEffects = false, MinSize = 6, MaxSize = 48 })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) ApplyEditorFont(dlg.Font);
            }
        }
        catch (Exception ex) { MessageBox.Show("Greška:\n" + ex.Message); }
    }

    void ApplyEditorFont(Font f)
    {
        // zapamti na kojoj je liniji bio prikaz, pa je zadrzi i posle promene velicine
        int topLine = vsb.Value / Math.Max(1, LineHeightPx(rtbL));
        editorFont = f;
        BeginRedraw(rtbL); BeginRedraw(rtbR);
        rtbL.Font = f; rtbR.Font = f;
        lineH = 0;
        EndRedraw(rtbL); EndRedraw(rtbR);
        btnFont.Text = "Aa Font: " + f.SizeInPoints.ToString("0.#");
        gutterL.Font = f; gutterR.Font = f;
        RecalcGutterWidth();
        LayoutLeftPanel();
        UpdateScrollRange();
        bool old = syncing; syncing = true;
        try { SetVsbValue(topLine * LineHeightPx(rtbL)); } finally { syncing = old; }
        DoCompare();
    }

    void HighlightLines(RichTextBox rtb, int startLine, int count, Color back, Color fore)
    {
        if (count <= 0) return;
        try
        {
            int a = rtb.GetFirstCharIndexFromLine(startLine);
            if (a < 0 || a >= rtb.TextLength) return;
            int e = rtb.GetFirstCharIndexFromLine(startLine + count);
            if (e < 0 || e > rtb.TextLength) e = rtb.TextLength;
            if (e <= a) return;
            rtb.Select(a, e - a);
            rtb.SelectionBackColor = back; rtb.SelectionColor = fore;
        }
        catch { }
    }
    void HighlightRange(RichTextBox rtb, int start, int len, Color back) { try { if (start < rtb.TextLength) { rtb.Select(start, Math.Min(len, rtb.TextLength - start)); rtb.SelectionBackColor = back; } } catch { } }
}

// Posebna kolona za brojeve linija, odvojena od RichTextBox-a: broj se samo ISCRTAVA preko Graphics.DrawString,
// nikad se ne upisuje u tekst, tako da ne moze da se pomesa sa pravim sadrzajem (npr. brojem u zagradi u samom
// tekstu) i ne ulazi u copy/paste, pretragu ili zamenu. LineNumbers[i] je broj za i-tu PRIKAZANU liniju
// (redni broj reda u RichTextBox-u, racunajuci od 0); 0 ili manje znaci "bez broja" (filler/prazan red).
public class LineNumberGutter : Control
{
    public SyncRichTextBox Target;
    public int[] LineNumbers = new int[0];
    public Func<int> LineHeightProvider;
    public Color NumberColor = Color.FromArgb(120, 120, 128);
    public Color GutterBackColor = Color.FromArgb(37, 37, 40);

    public LineNumberGutter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = GutterBackColor;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(GutterBackColor)) g.FillRectangle(bg, ClientRectangle);
        if (Target == null || LineNumbers == null || LineNumbers.Length == 0) return;

        int lh = LineHeightProvider != null ? Math.Max(1, LineHeightProvider()) : Math.Max(1, Font.Height);
        int scrollY = 0;
        try { scrollY = Target.ScrollY; } catch { }

        var f = Target.Font;
        int firstLine = Math.Max(0, scrollY / lh - 1);
        int lastLine = Math.Min(LineNumbers.Length - 1, (scrollY + Height) / lh + 1);
        using (var br = new SolidBrush(NumberColor))
        {
            for (int ln = firstLine; ln <= lastLine; ln++)
            {
                int num = LineNumbers[ln];
                if (num <= 0) continue;   // filler/praznina - bez broja
                int y = ln * lh - scrollY;
                string txt = num.ToString();
                var sz = g.MeasureString(txt, f);
                g.DrawString(txt, f, br, Width - sz.Width - 6, y);
            }
        }
    }
}

static class NativeMethods
{
    public const int WM_SETREDRAW = 0x000B;
    public const int EM_GETSCROLLPOS = 0x0400 + 221;
    public const int EM_SETSCROLLPOS = 0x0400 + 222;
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

// RichTextBox bez svog vertikalnog scroll bara; javlja kad se vertikalni scroll promeni
public class SyncRichTextBox : RichTextBox
{
    public event EventHandler ScrollChanged;
    public event Action<int> CtrlWheel;
    public event Action UndoRequested, RedoRequested;
    public const string Filler = "\u200B";   // "linija" koja samo drzi mesto u poredjenju

    // Clipboard trazi STA thread; glavni thread nije uvek STA, pa se radi u privremenom STA threadu
    static void ClipSetText(string text)
    {
        var t = new Thread(() => { try { Clipboard.SetDataObject(text, true, 5, 50); } catch { } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(3000);
    }

    static string ClipGetText()
    {
        string result = null;
        var t = new Thread(() => { try { if (Clipboard.ContainsText()) result = Clipboard.GetText(); } catch { } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(3000);
        return result;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z) || keyData == (Keys.Alt | Keys.Back))
        { var h = UndoRequested; if (h != null) h(); return true; }
        if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z))
        { var h = RedoRequested; if (h != null) h(); return true; }

        // Copy / Cut: kopira selektovani tekst bez razmaka (FILL linija) kao cist tekst
        if (keyData == (Keys.Control | Keys.C) || keyData == (Keys.Control | Keys.Insert) ||
            keyData == (Keys.Control | Keys.X) || keyData == (Keys.Shift | Keys.Delete))
        {
            string t = SelectedText;
            if (t.Length > 0)
            {
                var keep = t.Replace("\r\n", "\n").Split('\n').Where(l => l != Filler).Select(l => l.Replace(Filler, ""));
                string res = string.Join(Environment.NewLine, keep);
                if (res.Length > 0) ClipSetText(res);
                if (keyData == (Keys.Control | Keys.X) || keyData == (Keys.Shift | Keys.Delete)) SelectedText = "";
            }
            return true;
        }

        // Paste: uvek kao obican tekst (bez tudjeg formatiranja/boja)
        if (keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert))
        {
            string clip = ClipGetText();
            if (!string.IsNullOrEmpty(clip)) SelectedText = clip;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    int lastY = int.MinValue;
    int lastX = int.MinValue;

    const int WM_PAINT = 0x000F, WM_KEYDOWN = 0x0100, WM_CHAR = 0x0102, WM_HSCROLL = 0x0114, WM_VSCROLL = 0x0115,
              WM_LBUTTONUP = 0x0202, WM_MOUSEWHEEL = 0x020A, WM_PASTE = 0x0302;

    public int ScrollY
    {
        get
        {
            if (!IsHandleCreated) return 0;
            var p = new NativeMethods.POINT();
            NativeMethods.SendMessage(Handle, NativeMethods.EM_GETSCROLLPOS, IntPtr.Zero, ref p);
            return p.Y;
        }
        set
        {
            if (!IsHandleCreated) return;
            var p = new NativeMethods.POINT();
            NativeMethods.SendMessage(Handle, NativeMethods.EM_GETSCROLLPOS, IntPtr.Zero, ref p);
            p.Y = Math.Max(0, value);
            NativeMethods.SendMessage(Handle, NativeMethods.EM_SETSCROLLPOS, IntPtr.Zero, ref p);
            lastY = ScrollY;   // stvarna (ogranicena) pozicija, da ne izazove povratnu petlju
        }
    }

    // Horizontalni (levo-desno) scroll. Samo gornji/levi tekst zadrzava svoj nativni scroll bar;
    // drugi tekst nema svoj, ali prati ovu vrednost programski (vidi OnRtbScrolled).
    public int ScrollX
    {
        get
        {
            if (!IsHandleCreated) return 0;
            var p = new NativeMethods.POINT();
            NativeMethods.SendMessage(Handle, NativeMethods.EM_GETSCROLLPOS, IntPtr.Zero, ref p);
            return p.X;
        }
        set
        {
            if (!IsHandleCreated) return;
            var p = new NativeMethods.POINT();
            NativeMethods.SendMessage(Handle, NativeMethods.EM_GETSCROLLPOS, IntPtr.Zero, ref p);
            p.X = Math.Max(0, value);
            NativeMethods.SendMessage(Handle, NativeMethods.EM_SETSCROLLPOS, IntPtr.Zero, ref p);
            lastX = ScrollX;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Ctrl+tockic: menja velicinu fonta (nativni zoom bi pokvario poravnanje)
        if (m.Msg == WM_MOUSEWHEEL && (Control.ModifierKeys & Keys.Control) != 0)
        {
            int cd = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            var h = CtrlWheel; if (h != null && cd != 0) h(cd);
            return;
        }
        // skroluj sami (nema nativnog vertikalnog bara)
        if (m.Msg == WM_MOUSEWHEEL)
        {
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            ScrollY = ScrollY - delta * lines * Math.Max(1, Font.Height) / 120;
            CheckScroll();
            return;
        }
        base.WndProc(ref m);
        switch (m.Msg)
        {
            case WM_PAINT: case WM_KEYDOWN: case WM_CHAR: case WM_VSCROLL: case WM_HSCROLL: case WM_LBUTTONUP: case WM_PASTE:
                CheckScroll();
                break;
        }
    }

    void CheckScroll()
    {
        if (!IsHandleCreated) return;
        int y = ScrollY, x = ScrollX;
        if (y != lastY || x != lastX)
        {
            lastY = y; lastX = x;
            var h = ScrollChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
