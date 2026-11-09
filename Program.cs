using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace PaintAutoDrawer256;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEKEYBDATA data; }
    [StructLayout(LayoutKind.Explicit)] struct MOUSEKEYBDATA
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT
    {
        public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra;
    }
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, LEFTDOWN = 0x0002, LEFTUP = 0x0004;
    const uint KEYUP = 0x0002;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const int HK_START = 1, HK_CAPTURE = 2, HK_PAUSE = 3, HK_CANCEL = 4;

    sealed class Config
    {
        public Point RgbButton { get; set; }
        public Point RedField { get; set; }
        public Point GreenField { get; set; }
        public Point BlueField { get; set; }
        public Point OkButton { get; set; }
        public Point CanvasTopLeft { get; set; }
        public Point CanvasBottomRight { get; set; }
    }

    readonly string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaintAutoDrawer256");
    readonly string configPath;
    Config cfg = new();
    Bitmap? source;
    Bitmap? rendered;
    volatile bool cancel, paused, drawing;
    string? captureTarget;
    Label status = new() { AutoSize = true, Text = "Load an image to begin." };
    PictureBox preview = new() { Width = 300, Height = 220, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
    NumericUpDown colors = new() { Minimum = 2, Maximum = 256, Value = 256, Width = 80 };
    NumericUpDown delay = new() { Minimum = 0, Maximum = 100, Value = 4, Width = 80 };
    Button start = new() { Text = "Start drawing (F6)", AutoSize = true };
    readonly Dictionary<string, Button> captureButtons = new();

    public MainForm()
    {
        Text = "Paint AutoDrawer 256";
        Width = 760; Height = 650; StartPosition = FormStartPosition.CenterScreen;
        configPath = Path.Combine(configDir, "config.json");
        LoadConfig();
        BuildUi();
        KeyPreview = true;
        Shown += (_, _) =>
        {
            bool startHotkey = RegisterHotKey(Handle, HK_START, 0, (uint)Keys.F6);
            bool captureHotkey = RegisterHotKey(Handle, HK_CAPTURE, 0, (uint)Keys.F8);
            RegisterHotKey(Handle, HK_PAUSE, 0, (uint)Keys.Space);
            RegisterHotKey(Handle, HK_CANCEL, 0, (uint)Keys.Escape);
            if (!captureHotkey)
                SetStatus("F8 is already in use by another app. Focus AutoDrawer and press F8, or use the Capture button again.");
            else if (!startHotkey)
                SetStatus("F6 is unavailable; use Start drawing.");
        };
        FormClosed += (_, _) =>
        {
            foreach (int id in new[] { HK_START, HK_CAPTURE, HK_PAUSE, HK_CANCEL }) UnregisterHotKey(Handle, id);
            source?.Dispose(); rendered?.Dispose();
        };
    }

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        Controls.Add(root);
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        root.Controls.Add(left, 0, 0); root.Controls.Add(right, 1, 0);
        var load = new Button { Text = "Load image…", AutoSize = true };
        load.Click += (_, _) => LoadImage();
        left.Controls.Add(load); left.Controls.Add(preview);
        left.Controls.Add(new Label { Text = "Maximum palette colours:", AutoSize = true });
        left.Controls.Add(colors);
        left.Controls.Add(new Label { Text = "Pause between strokes (ms):", AutoSize = true });
        left.Controls.Add(delay);
        start.Click += async (_, _) => await StartDrawing();
        left.Controls.Add(start);
        var cancelButton = new Button { Text = "Cancel (Esc)", AutoSize = true };
        cancelButton.Click += (_, _) => { cancel = true; SetStatus("Cancelling…"); };
        var pauseButton = new Button { Text = "Pause / resume (Space)", AutoSize = true };
        pauseButton.Click += (_, _) => TogglePause();
        left.Controls.Add(pauseButton); left.Controls.Add(cancelButton);
        right.Controls.Add(new Label { Text = "PAINT CALIBRATION", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        right.Controls.Add(new Label { Text = "Click Capture, move the mouse to the target in Paint, then press F8.", AutoSize = true, MaximumSize = new Size(340, 0) });
        AddCapture(right, "RGB dialog button", nameof(Config.RgbButton));
        AddCapture(right, "Red field", nameof(Config.RedField));
        AddCapture(right, "Green field", nameof(Config.GreenField));
        AddCapture(right, "Blue field", nameof(Config.BlueField));
        AddCapture(right, "OK button", nameof(Config.OkButton));
        right.Controls.Add(new Label { Text = "CANVAS CALIBRATION", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 12, 0, 0) });
        AddCapture(right, "Canvas top-left", nameof(Config.CanvasTopLeft));
        AddCapture(right, "Canvas bottom-right", nameof(Config.CanvasBottomRight));
        right.Controls.Add(status);
        right.Controls.Add(new Label { Text = "Paint: select Pencil, size 1, 100% zoom. Keep Paint in the same position after calibration.", AutoSize = true, MaximumSize = new Size(340, 0) });
    }

    void AddCapture(Control parent, string label, string property)
    {
        var b = new Button { Text = "Capture " + label, AutoSize = true };
        b.Click += (_, _) => { captureTarget = property; SetStatus($"Move pointer to {label} in Paint, then press F8."); };
        captureButtons[property] = b; parent.Controls.Add(b);
    }

    void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(configDir);
            if (File.Exists(configPath)) cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath)) ?? new Config();
        }
        catch { cfg = new Config(); }
    }
    void SaveConfig()
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(configPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    void LoadImage()
    {
        using var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            source?.Dispose(); rendered?.Dispose();
            using var temp = new Bitmap(dialog.FileName);
            source = new Bitmap(temp);
            preview.Image = source;
            SetStatus($"Loaded {source.Width} × {source.Height}. Calibrate the Paint canvas before drawing.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Image error"); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.F8 && captureTarget != null)
        {
            CapturePoint();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            int id = m.WParam.ToInt32();
            if (id == HK_CAPTURE && captureTarget != null) CapturePoint();
            else if (id == HK_START && !drawing) _ = StartDrawing();
            else if (id == HK_PAUSE) TogglePause();
            else if (id == HK_CANCEL) { cancel = true; }
        }
        base.WndProc(ref m);
    }

    void CapturePoint()
    {
        if (!GetCursorPos(out var p)) return;
        var value = new Point(p.X, p.Y);
        switch (captureTarget)
        {
            case nameof(Config.RgbButton): cfg.RgbButton = value; break;
            case nameof(Config.RedField): cfg.RedField = value; break;
            case nameof(Config.GreenField): cfg.GreenField = value; break;
            case nameof(Config.BlueField): cfg.BlueField = value; break;
            case nameof(Config.OkButton): cfg.OkButton = value; break;
            case nameof(Config.CanvasTopLeft): cfg.CanvasTopLeft = value; break;
            case nameof(Config.CanvasBottomRight): cfg.CanvasBottomRight = value; break;
        }
        string target = captureTarget;
        captureTarget = null; SaveConfig();
        SetStatus($"{target} saved at {value.X}, {value.Y}.");
    }

    void TogglePause()
    {
        if (!drawing) return;
        paused = !paused; SetStatus(paused ? "Paused. Press Space to resume." : "Drawing…");
    }
    void SetStatus(string value)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(new Action(() => status.Text = value));
        else status.Text = value;
    }

    async Task StartDrawing()
    {
        if (drawing) return;
        if (source == null) { SetStatus("Load an image first."); return; }
        if (cfg.CanvasBottomRight.X <= cfg.CanvasTopLeft.X || cfg.CanvasBottomRight.Y <= cfg.CanvasTopLeft.Y ||
            cfg.RgbButton == Point.Empty || cfg.RedField == Point.Empty || cfg.GreenField == Point.Empty ||
            cfg.BlueField == Point.Empty || cfg.OkButton == Point.Empty)
        { SetStatus("Finish RGB and canvas calibration first."); return; }

        drawing = true; cancel = false; paused = false; start.Enabled = false;
        try
        {
            int w = cfg.CanvasBottomRight.X - cfg.CanvasTopLeft.X + 1;
            int h = cfg.CanvasBottomRight.Y - cfg.CanvasTopLeft.Y + 1;
            SetStatus($"Preparing {w} × {h} image and up to {(int)colors.Value} colours…");
            rendered?.Dispose();
            rendered = Quantize(source, w, h, (int)colors.Value);
            int actionDelay = (int)delay.Value;
            var bitmapToDraw = rendered;
            await Task.Run(() => DrawBitmap(bitmapToDraw, actionDelay));
            SetStatus(cancel ? "Cancelled." : "Finished.");
        }
        catch (Exception ex) { SetStatus("Error: " + ex.Message); MessageBox.Show(this, ex.ToString(), "AutoDrawer error"); }
        finally { drawing = false; start.Enabled = true; }
    }

    Bitmap Quantize(Bitmap input, int width, int height, int maxColors)
    {
        using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(input, new Rectangle(0, 0, width, height));
        }
        var pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixels[y * width + x] = resized.GetPixel(x, y);
        var palette = MedianCut(pixels, maxColors);
        var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var work = pixels.Select(c => new double[] { c.R, c.G, c.B }).ToArray();
        var paletteRgb = palette.Select(c => new double[] { c.R, c.G, c.B }).ToArray();
        for (int y = 0; y < height; y++)
        {
            bool reverse = (y & 1) != 0;
            for (int step = 0; step < width; step++)
            {
                int x = reverse ? width - 1 - step : step;
                int i = y * width + x;
                var p = work[i];
                int best = 0; double bestDist = double.MaxValue;
                for (int k = 0; k < paletteRgb.Length; k++)
                {
                    double dr = p[0] - paletteRgb[k][0], dg = p[1] - paletteRgb[k][1], db = p[2] - paletteRgb[k][2];
                    double d = dr * dr * 0.30 + dg * dg * 0.59 + db * db * 0.11;
                    if (d < bestDist) { bestDist = d; best = k; }
                }
                Color q = palette[best]; output.SetPixel(x, y, q);
                double er = p[0] - q.R, eg = p[1] - q.G, eb = p[2] - q.B;
                AddError(work, width, height, x + (reverse ? -1 : 1), y, er, eg, eb, 7.0 / 16);
                AddError(work, width, height, x + (reverse ? 1 : -1), y + 1, er, eg, eb, 3.0 / 16);
                AddError(work, width, height, x, y + 1, er, eg, eb, 5.0 / 16);
                AddError(work, width, height, x + (reverse ? -1 : 1), y + 1, er, eg, eb, 1.0 / 16);
            }
        }
        return output;
    }

    static void AddError(double[][] a, int w, int h, int x, int y, double r, double g, double b, double factor)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        int i = y * w + x;
        a[i][0] = Math.Clamp(a[i][0] + r * factor, 0, 255);
        a[i][1] = Math.Clamp(a[i][1] + g * factor, 0, 255);
        a[i][2] = Math.Clamp(a[i][2] + b * factor, 0, 255);
    }

    static List<Color> MedianCut(Color[] pixels, int max)
    {
        var boxes = new List<List<Color>> { pixels.ToList() };
        while (boxes.Count < max)
        {
            int index = -1, range = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                int r = Math.Max(boxes[i].Max(c => c.R) - boxes[i].Min(c => c.R),
                    Math.Max(boxes[i].Max(c => c.G) - boxes[i].Min(c => c.G), boxes[i].Max(c => c.B) - boxes[i].Min(c => c.B)));
                if (r > range) { range = r; index = i; }
            }
            if (index < 0) break;
            var box = boxes[index];
            int rr = box.Max(c => c.R) - box.Min(c => c.R);
            int gg = box.Max(c => c.G) - box.Min(c => c.G);
            int bb = box.Max(c => c.B) - box.Min(c => c.B);
            if (rr >= gg && rr >= bb) box = box.OrderBy(c => c.R).ToList();
            else if (gg >= bb) box = box.OrderBy(c => c.G).ToList();
            else box = box.OrderBy(c => c.B).ToList();
            int mid = box.Count / 2;
            boxes[index] = box.Take(mid).ToList();
            boxes.Add(box.Skip(mid).ToList());
        }
        return boxes.Select(b => Color.FromArgb(
            (int)b.Average(c => c.R), (int)b.Average(c => c.G), (int)b.Average(c => c.B))).Distinct().ToList();
    }

    void DrawBitmap(Bitmap bmp, int actionDelay)
    {
        int w = bmp.Width, h = bmp.Height;
        var rows = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) rows[y * w + x] = bmp.GetPixel(x, y);

        var groups = new Dictionary<Color, List<(int y, int x1, int x2)>>();
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                Color c = rows[y * w + x];
                int end = x;
                while (end + 1 < w && rows[y * w + end + 1].ToArgb() == c.ToArgb()) end++;
                if (!groups.TryGetValue(c, out var list)) groups[c] = list = new();
                list.Add((y, x, end));
                x = end + 1;
            }
        }

        int colorIndex = 0;
        foreach (var group in groups)
        {
            if (cancel) break;
            WaitIfPaused();
            if (cancel) break;
            SetStatus($"Colour {++colorIndex}/{groups.Count} — {group.Value.Count} scanline runs");
            SetPaintColor(group.Key);
            foreach (var run in group.Value)
            {
                if (cancel) break;
                WaitIfPaused();
                if (cancel) break;
                int sx = cfg.CanvasTopLeft.X + run.x1, sy = cfg.CanvasTopLeft.Y + run.y;
                int ex = cfg.CanvasTopLeft.X + run.x2;
                SetCursorPos(sx, sy); MouseDown();
                if (ex != sx) SetCursorPos(ex, sy);
                MouseUp();
                if (actionDelay > 0) Thread.Sleep(actionDelay);
            }
        }
    }

    void WaitIfPaused()
    {
        while (paused && !cancel) Thread.Sleep(80);
    }

    void SetPaintColor(Color c)
    {
        // Wait for Paint to finish opening the RGB editor.
        Click(cfg.RgbButton);
        Thread.Sleep(180);

        SetField(cfg.RedField, c.R);
        SetField(cfg.GreenField, c.G);
        SetField(cfg.BlueField, c.B);

        Thread.Sleep(60);
        Click(cfg.OkButton);
        Thread.Sleep(100);
    }

    void SetField(Point p, int value)
    {
        // Focus the field, select all digits, and type using Windows SendInput.
        Click(p);
        Thread.Sleep(35);
        KeyDown(0x11); // Ctrl
        KeyTap(0x41);  // A
        KeyUp(0x11);
        Thread.Sleep(20);

        string digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (char digit in digits)
        {
            KeyTap((ushort)(0x30 + (digit - '0')));
            Thread.Sleep(8);
        }
        Thread.Sleep(35);
    }

    static void Click(Point p)
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(25);
        MouseDown();
        Thread.Sleep(18);
        MouseUp();
        Thread.Sleep(25);
    }

    static void KeyDown(ushort vk) => SendKey(vk, 0);
    static void KeyUp(ushort vk) => SendKey(vk, KEYEVENTF_KEYUP);
    static void KeyTap(ushort vk)
    {
        SendKey(vk, 0);
        SendKey(vk, KEYEVENTF_KEYUP);
    }
    static void SendKey(ushort vk, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            data = new MOUSEKEYBDATA
            {
                ki = new KEYBDINPUT { wVk = vk, dwFlags = flags }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
    static void MouseDown() => SendMouse(LEFTDOWN);
    static void MouseUp() => SendMouse(LEFTUP);
    static void SendMouse(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE, data = new MOUSEKEYBDATA { mi = new MOUSEINPUT { dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}