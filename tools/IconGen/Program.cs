// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

// Renders the Apodofos Performance Report Analyzer icon at all Windows icon sizes and packs them into a PNG-based .ico.
// Usage: dotnet run --project tools/IconGen -- <outputDir>
// Design (256-unit grid, mirrors assets/apodofos.svg):
//   deep-teal rounded tile  -> "database / Oracle performance" context
//   white report sheet with folded corner -> "AWR report"
//   bar chart green->amber->red -> "metrics, severity"
//   magnifying glass with pulse line -> "analysis / diagnosis"
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var outDir = args.Length > 0 ? args[0] : Path.Combine("..", "..", "assets");
Directory.CreateDirectory(outDir);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var frames = new List<(int Size, byte[] Png)>();
foreach (var s in sizes)
{
    using var bmp = Render(s);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    frames.Add((s, ms.ToArray()));
}
WriteIco(Path.Combine(outDir, "apodofos.ico"), frames);
using (var big = Render(512)) big.Save(Path.Combine(outDir, "apodofos-512.png"), ImageFormat.Png);
using (var mid = Render(256)) mid.Save(Path.Combine(outDir, "apodofos-256.png"), ImageFormat.Png);
Console.WriteLine($"Icon written to {Path.GetFullPath(outDir)}");

static Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    g.Clear(Color.Transparent);
    var k = size / 256f;
    g.ScaleTransform(k, k);
    var simple = size <= 24;

    // Tile
    using (var tile = RoundRect(8, 8, 240, 240, 52))
    using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(256, 256), Color.FromArgb(0x0B, 0x2E, 0x4F), Color.FromArgb(0x0E, 0x76, 0x90)))
        g.FillPath(bg, tile);

    // Report sheet with folded corner
    float sx = simple ? 40 : 46, sy = simple ? 30 : 34, sw = simple ? 132 : 124, sh = simple ? 176 : 168, fold = simple ? 34 : 30;
    using (var sheet = new GraphicsPath())
    {
        sheet.AddLine(sx + 10, sy, sx + sw - fold, sy);
        sheet.AddLine(sx + sw - fold, sy, sx + sw, sy + fold);
        sheet.AddLine(sx + sw, sy + fold, sx + sw, sy + sh - 10);
        sheet.AddArc(sx + sw - 20, sy + sh - 20, 20, 20, 0, 90);
        sheet.AddArc(sx, sy + sh - 20, 20, 20, 90, 90);
        sheet.AddArc(sx, sy, 20, 20, 180, 90);
        sheet.CloseFigure();
        using var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
        g.TranslateTransform(0, 5);
        g.FillPath(shadow, sheet);
        g.TranslateTransform(0, -5);
        using var white = new SolidBrush(Color.FromArgb(0xF8, 0xFA, 0xFC));
        g.FillPath(white, sheet);
    }
    using (var foldPath = new GraphicsPath())
    {
        foldPath.AddPolygon([new PointF(sx + sw - fold, sy), new PointF(sx + sw - fold, sy + fold), new PointF(sx + sw, sy + fold)]);
        using var foldBrush = new SolidBrush(Color.FromArgb(0xCB, 0xD5, 0xE1));
        g.FillPath(foldBrush, foldPath);
    }

    // Text lines
    if (!simple)
    {
        using var line = new Pen(Color.FromArgb(0x94, 0xA3, 0xB8), 9) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(line, sx + 20, sy + 26, sx + 70, sy + 26);
        using var line2 = new Pen(Color.FromArgb(0xCB, 0xD5, 0xE1), 9) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(line2, sx + 20, sy + 46, sx + 88, sy + 46);
    }

    // Bar chart: healthy -> warning -> critical
    var baseY = sy + sh - 22;
    float bw = simple ? 24 : 20, gap = simple ? 8 : 8;
    (float H, Color C)[] bars = simple
        ? [(46, Color.FromArgb(0x22, 0xC5, 0x5E)), (78, Color.FromArgb(0xF5, 0x9E, 0x0B)), (112, Color.FromArgb(0xEF, 0x44, 0x44))]
        : [(34, Color.FromArgb(0x22, 0xC5, 0x5E)), (56, Color.FromArgb(0x22, 0xC5, 0x5E)), (78, Color.FromArgb(0xF5, 0x9E, 0x0B)), (100, Color.FromArgb(0xEF, 0x44, 0x44))];
    var bx = sx + (simple ? 14 : 18);
    foreach (var (h, c) in bars)
    {
        using var p = RoundRect(bx, baseY - h, bw, h, simple ? 4 : 4);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
        bx += bw + gap;
    }

    // Magnifying glass
    float cx = simple ? 168 : 170, cy = simple ? 164 : 160, r = simple ? 46 : 44;
    using (var handle = new Pen(Color.FromArgb(0xF5, 0x9E, 0x0B), simple ? 30 : 24) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawLine(handle, cx + r * 0.72f, cy + r * 0.72f, cx + r * 1.42f, cy + r * 1.42f);
    using (var lens = new SolidBrush(Color.FromArgb(150, 0xE0, 0xF2, 0xFE)))
        g.FillEllipse(lens, cx - r, cy - r, r * 2, r * 2);
    using (var ring = new Pen(Color.FromArgb(0x0B, 0x2E, 0x4F), simple ? 18 : 13))
        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
    using (var ring2 = new Pen(Color.White, simple ? 6 : 5))
        g.DrawEllipse(ring2, cx - r - (simple ? 11 : 8.5f), cy - r - (simple ? 11 : 8.5f), (r + (simple ? 11 : 8.5f)) * 2, (r + (simple ? 11 : 8.5f)) * 2);

    // Pulse (ECG) line inside the lens
    using (var pulse = new Pen(Color.FromArgb(0xDC, 0x26, 0x26), simple ? 11 : 8) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
    {
        PointF[] pts = simple
            ? [new(cx - 30, cy + 4), new(cx - 10, cy + 4), new(cx, cy - 20), new(cx + 12, cy + 20), new(cx + 30, cy + 4)]
            : [new(cx - 32, cy + 4), new(cx - 16, cy + 4), new(cx - 8, cy - 18), new(cx + 4, cy + 22), new(cx + 14, cy - 4), new(cx + 32, cy - 4)];
        g.DrawLines(pulse, pts);
    }
    return bmp;
}

static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
{
    var p = new GraphicsPath();
    var d = Math.Min(r * 2, Math.Min(w, h));
    p.AddArc(x, y, d, d, 180, 90);
    p.AddArc(x + w - d, y, d, d, 270, 90);
    p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    p.AddArc(x, y + h - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in frames) w.Write(png);
}
