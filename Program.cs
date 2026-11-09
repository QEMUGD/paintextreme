using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
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

        var area = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = BackColor };
        area.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        area.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(area, 0, 1);
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
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configFile)) ?? new AppConfig();
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
            config.WindowWidth = Width;
            config.WindowHeight = Height;
            File.WriteAllText(configFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
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

        var pixels = new Color[width * height];
        var histogram = new Dictionary<int, int>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Color c = normalized.GetPixel(x, y);
                pixels[y * width + x] = c;
                int key = (c.R << 16) | (c.G << 8) | c.B;
                histogram[key] = histogram.TryGetValue(key, out int count) ? count + 1 : 1;
            }

        var points = histogram.Select(kv => new ColorPoint(Color.FromArgb((kv.Key >> 16) & 255, (kv.Key >> 8) & 255, kv.Key & 255), kv.Value)).ToList();
        var palette = BuildPalette(points, maxColors);
        var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var work = pixels.Select(c => new double[] { c.R, c.G, c.B }).ToArray();
        var paletteRgb = palette.Select(c => new double[] { c.R, c.G, c.B }).ToArray();

        for (int y = 0; y < height; y++)
        {
            bool reverse = useDither && (y % 2 == 1);
            for (int step = 0; step < width; step++)
            {
                int x = reverse ? width - 1 - step : step;
                int i = y * width + x;
                var p = work[i];
                int best = 0;
                double bestDist = double.MaxValue;
                for (int k = 0; k < paletteRgb.Length; k++)
                {
                    double dr = p[0] - paletteRgb[k][0], dg = p[1] - paletteRgb[k][1], db = p[2] - paletteRgb[k][2];
                    double distance = dr * dr * 0.30 + dg * dg * 0.59 + db * db * 0.11;
                    if (distance < bestDist) { bestDist = distance; best = k; }
                }
                Color q = palette[best];
                output.SetPixel(x, y, q);
                if (!useDither) continue;
                double er = p[0] - q.R, eg = p[1] - q.G, eb = p[2] - q.B;
                AddError(work, width, height, x + (reverse ? -1 : 1), y, er, eg, eb, 7.0 / 16);
                AddError(work, width, height, x + (reverse ? 1 : -1), y + 1, er, eg, eb, 3.0 / 16);
                AddError(work, width, height, x, y + 1, er, eg, eb, 5.0 / 16);
                AddError(work, width, height, x + (reverse ? -1 : 1), y + 1, er, eg, eb, 1.0 / 16);
            }
        }
        input.Dispose();
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
                (int)b.Sum(p => (long)p.Color.R * p.Count) / (int)count,
                (int)b.Sum(p => (long)p.Color.G * p.Count) / (int)count,
                (int)b.Sum(p => (long)p.Color.B * p.Count) / (int)count);
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
