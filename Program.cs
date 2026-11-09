using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Forms;

namespace PaintAutoDrawer256;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new PaintForm());
    }
}

public sealed class PaintForm : Form
{
    sealed class AppConfig
    {
        public int PaletteSize { get; set; } = 256;
        public bool Dither { get; set; } = true;
        public int PreviewZoom { get; set; } = 100;
        public int WindowWidth { get; set; } = 1100;
        public int WindowHeight { get; set; } = 760;
        public string LastFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    readonly string configFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaintAutoDrawer256");
    readonly string configFile;
    AppConfig config = new();
    Bitmap? original;
    Bitmap? result;
    readonly PictureBox canvas = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(31, 34, 40),
        BorderStyle = BorderStyle.FixedSingle
    };
    readonly Label status = new() { AutoSize = true, Text = "Open an image to start." };
    readonly NumericUpDown palette = new() { Minimum = 2, Maximum = 256, Width = 82 };
    readonly CheckBox dither = new() { Text = "Floyd–Steinberg dithering", AutoSize = true };
    readonly Button processButton = new() { Text = "Convert to palette", AutoSize = true };
    readonly Button exportButton = new() { Text = "Export PNG", AutoSize = true, Enabled = false };
    readonly ComboBox targetSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly Button drawButton = new() { Text = "AUTODRAW", AutoSize = true, Enabled = false };
    readonly Button paintOpenButton = new() { Text = "Open Windows Paint", AutoSize = true };
    readonly Label calibrationInfo = new() { AutoSize = true, Text = "Windows Paint: calibrate RGB controls + canvas first." };
    readonly Dictionary<string, Point> paintPoints = new();
    string? captureTarget;
    volatile bool cancelDrawing;
    readonly Label info = new() { AutoSize = true, Text = "No image loaded." };
    bool loadingConfig;

    public PaintForm()
    {
        configFile = Path.Combine(configFolder, "config.json");
        LoadConfig();
        Text = "PaintExtreme — 256 Colour Studio";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 520);
        ClientSize = new Size(Math.Max(760, config.WindowWidth), Math.Max(520, config.WindowHeight));
        BuildUi();
        FormClosing += (_, _) => SaveConfig();
    }

    void BuildUi()
    {
        BackColor = Color.FromArgb(24, 26, 31);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(12), BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, BackColor = Color.FromArgb(35, 38, 45), Padding = new Padding(10), Margin = new Padding(0, 0, 0, 10) };
        root.Controls.Add(bar, 0, 0);
        var open = MakeButton("Open image");
        open.Click += (_, _) => OpenImage();
        bar.Controls.Add(open);
        bar.Controls.Add(new Label { Text = "Colours", AutoSize = true, Padding = new Padding(12, 8, 0, 0), ForeColor = ForeColor });
        palette.Value = config.PaletteSize;
        palette.ValueChanged += (_, _) => { if (!loadingConfig) SaveConfig(); };
        bar.Controls.Add(palette);
        dither.Checked = config.Dither;
        dither.CheckedChanged += (_, _) => { if (!loadingConfig) SaveConfig(); };
        bar.Controls.Add(dither);
        processButton.Click += async (_, _) => await ProcessImage();
        bar.Controls.Add(processButton);
        exportButton.Click += (_, _) => ExportImage();
        bar.Controls.Add(exportButton);
        bar.Controls.Add(new Label { Text = "Draw in:", AutoSize = true, Padding = new Padding(8, 8, 0, 0), ForeColor = ForeColor });
        targetSelector.Items.AddRange(new object[] { "Windows Paint", "PaintExtreme (built-in)" });
        targetSelector.SelectedIndex = 0; // Windows Paint is the default target.
        bar.Controls.Add(targetSelector);
        bar.Controls.Add(paintOpenButton);
        paintOpenButton.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("mspaint.exe") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open Paint"); } };
        drawButton.Click += async (_, _) => await AutoDraw();
        bar.Controls.Add(drawButton);
        var calibration = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, BackColor = Color.FromArgb(35, 38, 45), Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
        calibration.Controls.Add(calibrationInfo);
        foreach (var item in new[] { ("RGB button", "rgb"), ("Red field", "red"), ("Green field", "green"), ("Blue field", "blue"), ("OK button", "ok"), ("Canvas top-left", "tl"), ("Canvas bottom-right", "br") })
        {
            var b = MakeButton("Capture " + item.Item1);
            b.Click += (_, _) => { captureTarget = item.Item2; calibrationInfo.Text = $"Move mouse to {item.Item1} in Windows Paint, then press F8."; };
            calibration.Controls.Add(b);
        }


        root.Controls.Add(calibration, 0, 1);

        var area = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = BackColor };
        area.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        area.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(area, 0, 2);
        area.Controls.Add(canvas, 0, 0);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4, 9, 4, 2), WrapContents = true };
        footer.Controls.Add(info);
        footer.Controls.Add(new Label { Text = "   ", AutoSize = true });
        footer.Controls.Add(status);
        area.Controls.Add(footer, 0, 1);
    }

    Button MakeButton(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(58, 103, 190), ForeColor = Color.White,
        Padding = new Padding(10, 5, 10, 5), Margin = new Padding(4)
    };

    void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(configFolder);
            if (File.Exists(configFile))
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configFile)) ?? new AppConfig();
                var saved = JsonSerializer.Deserialize<Dictionary<string, Point>>(File.Exists(configFile) ? File.ReadAllText(configFile) : "{}");
                // Older configs remain compatible; calibration points are saved separately below.
                if (File.Exists(configFile + ".paint")) paintPoints.Clear();
                if (File.Exists(configFile + ".paint"))
                    foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, Point>>(File.ReadAllText(configFile + ".paint")) ?? new()) paintPoints[kv.Key] = kv.Value;
            }
        }
        catch { config = new AppConfig(); }
    }

    void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(configFolder);
            config.PaletteSize = (int)palette.Value;
            config.Dither = dither.Checked;
            config.LastFolder = config.LastFolder;
            config.WindowWidth = Width;
            config.WindowHeight = Height;
            File.WriteAllText(configFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(configFile + ".paint", JsonSerializer.Serialize(paintPoints, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    void OpenImage()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            InitialDirectory = Directory.Exists(config.LastFolder) ? config.LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var temp = new Bitmap(dialog.FileName);
            original?.Dispose();
            result?.Dispose();
            original = new Bitmap(temp);
            result = null;
            canvas.Image = original;
            config.LastFolder = Path.GetDirectoryName(dialog.FileName) ?? config.LastFolder;
            exportButton.Enabled = false;
            drawButton.Enabled = false;
            info.Text = $"{original.Width} × {original.Height} px";
            SetStatus("Image loaded. Convert it or export the original.");
            SaveConfig();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open image", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async Task ProcessImage()
    {
        if (original == null) { SetStatus("Open an image first."); return; }
        processButton.Enabled = false;
        exportButton.Enabled = false;
        SetStatus("Building palette and converting pixels…");
        try
        {
            var input = new Bitmap(original);
            int max = (int)palette.Value;
            bool useDither = dither.Checked;
            var converted = await Task.Run(() => Quantize(input, max, useDither));
            result?.Dispose();
            result = converted;
            canvas.Image = result;
            exportButton.Enabled = true;
            drawButton.Enabled = true;
            info.Text = $"{result.Width} × {result.Height} px  |  up to {max} colours";
            SetStatus("Done. This is a real image now — no Paint automation or RGB dialogs.");
            SaveConfig();
        }
        catch (Exception ex) { SetStatus("Conversion failed: " + ex.Message); }
        finally { processButton.Enabled = true; }
    }

    static Bitmap Quantize(Bitmap input, int maxColors, bool useDither)
    {
        int width = input.Width, height = input.Height;
        using var normalized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(normalized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(input, new Rectangle(0, 0, width, height));
        }
        input.Dispose();

        // Read pixels in one block instead of calling GetPixel millions of times.
        var source = new byte[checked(width * height * 3)];
        var rect = new Rectangle(0, 0, width, height);
        var srcData = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = Math.Abs(srcData.Stride);
            var row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                IntPtr rowPtr = IntPtr.Add(srcData.Scan0, y * srcData.Stride);
                Marshal.Copy(rowPtr, row, 0, stride);
                for (int x = 0; x < width; x++)
                {
                    int si = x * 3, di = (y * width + x) * 3;
                    source[di] = row[si + 2];     // R
                    source[di + 1] = row[si + 1]; // G
                    source[di + 2] = row[si];     // B
                }
            }
        }
        finally { normalized.UnlockBits(srcData); }

        var histogram = new Dictionary<int, int>();
        for (int i = 0; i < source.Length; i += 3)
        {
            int key = (source[i] << 16) | (source[i + 1] << 8) | source[i + 2];
            histogram[key] = histogram.TryGetValue(key, out int count) ? count + 1 : 1;
        }
        var points = histogram.Select(kv => new ColorPoint(
            Color.FromArgb((kv.Key >> 16) & 255, (kv.Key >> 8) & 255, kv.Key & 255), kv.Value)).ToList();
        var palette = BuildPalette(points, maxColors);
        var paletteRgb = palette.Select(c => new double[] { c.R, c.G, c.B }).ToArray();
        var outputBytes = new byte[source.Length];

        // Floyd–Steinberg only needs the current and next error rows.
        var er = new double[width + 2];
        var eg = new double[width + 2];
        var eb = new double[width + 2];
        var nr = new double[width + 2];
        var ng = new double[width + 2];
        var nb = new double[width + 2];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                double r = Math.Clamp(source[i] + er[x + 1], 0, 255);
                double g = Math.Clamp(source[i + 1] + eg[x + 1], 0, 255);
                double b = Math.Clamp(source[i + 2] + eb[x + 1], 0, 255);
                int best = 0;
                double bestDist = double.MaxValue;
                for (int k = 0; k < paletteRgb.Length; k++)
                {
                    double dr = r - paletteRgb[k][0], dg = g - paletteRgb[k][1], db = b - paletteRgb[k][2];
                    double distance = dr * dr * 0.30 + dg * dg * 0.59 + db * db * 0.11;
                    if (distance < bestDist) { bestDist = distance; best = k; }
                }
                Color q = palette[best];
                outputBytes[i] = q.R; outputBytes[i + 1] = q.G; outputBytes[i + 2] = q.B;
                if (!useDither) continue;
                double drError = r - q.R, dgError = g - q.G, dbError = b - q.B;
                er[x + 2] += drError * 7.0 / 16; eg[x + 2] += dgError * 7.0 / 16; eb[x + 2] += dbError * 7.0 / 16;
                nr[x] += drError * 3.0 / 16; ng[x] += dgError * 3.0 / 16; nb[x] += dbError * 3.0 / 16;
                nr[x + 1] += drError * 5.0 / 16; ng[x + 1] += dgError * 5.0 / 16; nb[x + 1] += dbError * 5.0 / 16;
                nr[x + 2] += drError / 16; ng[x + 2] += dgError / 16; nb[x + 2] += dbError / 16;
            }
            if (useDither)
            {
                (er, nr) = (nr, er); (eg, ng) = (ng, eg); (eb, nb) = (nb, eb);
                Array.Clear(nr); Array.Clear(ng); Array.Clear(nb);
            }
        }

        var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var dstData = output.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = Math.Abs(dstData.Stride);
            var row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Array.Clear(row);
                for (int x = 0; x < width; x++)
                {
                    int si = (y * width + x) * 3, di = x * 3;
                    row[di] = outputBytes[si + 2];
                    row[di + 1] = outputBytes[si + 1];
                    row[di + 2] = outputBytes[si];
                }
                Marshal.Copy(row, 0, IntPtr.Add(dstData.Scan0, y * dstData.Stride), stride);
            }
        }
        finally { output.UnlockBits(dstData); }
        return output;
    }

    sealed record ColorPoint(Color Color, int Count);

    static List<Color> BuildPalette(List<ColorPoint> points, int max)
    {
        if (points.Count <= max) return points.Select(p => p.Color).ToList();
        var boxes = new List<List<ColorPoint>> { points };
        while (boxes.Count < max)
        {
            int selected = -1;
            double score = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                if (b.Count < 2) continue;
                int r = b.Max(p => p.Color.R) - b.Min(p => p.Color.R);
                int g = b.Max(p => p.Color.G) - b.Min(p => p.Color.G);
                int bl = b.Max(p => p.Color.B) - b.Min(p => p.Color.B);
                double s = Math.Max(r, Math.Max(g, bl)) * Math.Log(1 + b.Sum(p => (double)p.Count));
                if (s > score) { score = s; selected = i; }
            }
            if (selected < 0) break;
            var box = boxes[selected];
            int rr = box.Max(p => p.Color.R) - box.Min(p => p.Color.R);
            int gg = box.Max(p => p.Color.G) - box.Min(p => p.Color.G);
            int bb = box.Max(p => p.Color.B) - box.Min(p => p.Color.B);
            Func<ColorPoint, int> channel = rr >= gg && rr >= bb ? p => p.Color.R : gg >= bb ? p => p.Color.G : p => p.Color.B;
            var sorted = box.OrderBy(channel).ToList();
            long total = sorted.Sum(p => (long)p.Count), acc = 0;
            int split = 1;
            for (; split < sorted.Count; split++) { acc += sorted[split - 1].Count; if (acc >= total / 2) break; }
            split = Math.Clamp(split, 1, sorted.Count - 1);
            boxes[selected] = sorted.Take(split).ToList();
            boxes.Add(sorted.Skip(split).ToList());
        }
        return boxes.Select(b =>
        {
            long count = b.Sum(p => (long)p.Count);
            return Color.FromArgb(
                (int)(b.Sum(p => (long)p.Color.R * p.Count) / count),
                (int)(b.Sum(p => (long)p.Color.G * p.Count) / count),
                (int)(b.Sum(p => (long)p.Color.B * p.Count) / count));
        }).Distinct().ToList();
    }

    static void AddError(double[][] a, int w, int h, int x, int y, double r, double g, double b, double factor)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        int i = y * w + x;
        a[i][0] = Math.Clamp(a[i][0] + r * factor, 0, 255);
        a[i][1] = Math.Clamp(a[i][1] + g * factor, 0, 255);
        a[i][2] = Math.Clamp(a[i][2] + b * factor, 0, 255);
    }

    void ExportImage()
    {
        if (result == null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png",
            DefaultExt = "png",
            FileName = "paint-extreme.png",
            InitialDirectory = Directory.Exists(config.LastFolder) ? config.LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            result.Save(dialog.FileName, ImageFormat.Png);
            config.LastFolder = Path.GetDirectoryName(dialog.FileName) ?? config.LastFolder;
            SaveConfig();
            SetStatus("Exported PNG: " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }


    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == 0xF008 && captureTarget != null)
        {
            if (GetCursorPos(out var p))
            {
                paintPoints[captureTarget] = new Point(p.X, p.Y);
                calibrationInfo.Text = $"Saved {captureTarget}: {p.X}, {p.Y}.";
                captureTarget = null;
                SaveConfig();
            }
        }
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeInput { public uint type; public InputUnion data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mouse;
        [FieldOffset(0)] public KeyboardInput keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort vk, scan; public uint flags, time; public IntPtr extra; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RegisterHotKey(Handle, 88, 0, 0x77); // F8 capture
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, 88);
        base.OnFormClosed(e);
    }

    async Task AutoDraw()
    {
        if (result == null) { SetStatus("Convert an image first."); return; }
        if (targetSelector.SelectedIndex == 1)
        {
            // The built-in canvas is the native drawing target: render the converted pixels directly.
            canvas.Image = result;
            SetStatus("PaintExtreme drew the image on its built-in canvas.");
            return;
        }
        string[] required = { "rgb", "red", "green", "blue", "ok", "tl", "br" };
        if (required.Any(k => !paintPoints.ContainsKey(k)))
        {
            SetStatus("Capture all 7 Windows Paint points first. Open Paint, then use the Capture buttons and F8.");
            return;
        }
        if (paintPoints["br"].X <= paintPoints["tl"].X || paintPoints["br"].Y <= paintPoints["tl"].Y)
        {
            SetStatus("Canvas calibration is invalid. Capture top-left and bottom-right again.");
            return;
        }
        drawButton.Enabled = false;
        cancelDrawing = false;
        try
        {
            SetStatus("Drawing into Windows Paint… Do not move the Paint window or mouse.");
            await Task.Run(() => DrawIntoWindowsPaint(result));
            SetStatus(cancelDrawing ? "Drawing cancelled." : "Autodraw finished in Windows Paint.");
        }
        catch (Exception ex) { SetStatus("Autodraw failed: " + ex.Message); }
        finally { drawButton.Enabled = true; }
    }

    void DrawIntoWindowsPaint(Bitmap image)
    {
        int left = paintPoints["tl"].X, top = paintPoints["tl"].Y;
        int width = paintPoints["br"].X - left + 1, height = paintPoints["br"].Y - top + 1;
        using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(image, new Rectangle(0, 0, width, height));
        }
        var runsByColor = new Dictionary<int, List<(int y, int x1, int x2)>>();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                int argb = resized.GetPixel(x, y).ToArgb();
                int end = x;
                while (end + 1 < width && resized.GetPixel(end + 1, y).ToArgb() == argb) end++;
                if (!runsByColor.TryGetValue(argb, out var runs)) runsByColor[argb] = runs = new();
                runs.Add((y, x, end));
                x = end + 1;
            }
        }
        int colorNumber = 0;
        foreach (var entry in runsByColor)
        {
            if (cancelDrawing) break;
            var color = Color.FromArgb(entry.Key);
            SetWindowsPaintColor(color);
            colorNumber++;
            foreach (var run in entry.Value)
            {
                if (cancelDrawing) break;
                SetCursorPos(left + run.x1, top + run.y);
                SendMouseButton(true);
                if (run.x2 != run.x1) SetCursorPos(left + run.x2, top + run.y);
                SendMouseButton(false);
            }
            SetStatus($"Windows Paint: colour {colorNumber}/{runsByColor.Count}, {entry.Value.Count} strokes.");
        }
    }

    void SetWindowsPaintColor(Color c)
    {
        ClickAt(paintPoints["rgb"]);
        Thread.Sleep(100);
        SetRgbField(paintPoints["red"], c.R);
        SetRgbField(paintPoints["green"], c.G);
        SetRgbField(paintPoints["blue"], c.B);
        ClickAt(paintPoints["ok"]);
        Thread.Sleep(60);
    }

    static void SetRgbField(Point p, int value)
    {
        ClickAt(p);
        SendKey(0x11, false); SendKey(0x41, false); SendKey(0x41, true); SendKey(0x11, true);
        foreach (char ch in value.ToString())
        {
            ushort vk = (ushort)(0x30 + (ch - '0'));
            SendKey(vk, false); SendKey(vk, true);
        }
    }

    static void ClickAt(Point p)
    {
        SetCursorPos(p.X, p.Y);
        SendMouseButton(true); SendMouseButton(false);
    }
    static void SendMouseButton(bool down)
    {
        var input = new NativeInput { type = 0, data = new InputUnion { mouse = new MouseInput { flags = down ? 0x0002u : 0x0004u } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>());
    }
    static void SendKey(ushort vk, bool up)
    {
        var input = new NativeInput { type = 1, data = new InputUnion { keyboard = new KeyboardInput { vk = vk, flags = up ? 0x0002u : 0u } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>());
    }

    void SetStatus(string message)
    {
        if (InvokeRequired) BeginInvoke(new Action(() => status.Text = message));
        else status.Text = message;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { original?.Dispose(); result?.Dispose(); }
        base.Dispose(disposing);
    }
}
