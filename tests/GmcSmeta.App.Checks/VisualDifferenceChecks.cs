using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;

internal static class VisualDifferenceChecks
{
    internal sealed record Difference(int Width, int Height, long ChangedPixels, double ChangedPercent,
        int? Left, int? Top, int? Right, int? Bottom, long ComparedPixels, long MaskedPixels);
    internal sealed record MaskRect(int X, int Y, int Width, int Height);

    // Input must be aligned screenshots with identical pixel dimensions. No
    // rescaling, threshold or automatic visual PASS. Alpha is included.
    internal static Difference Compare(byte[] before, byte[] after, byte[] output, int width, int height, IReadOnlyList<MaskRect>? masks = null)
    {
        if (width <= 0 || height <= 0 || before.Length != checked(width * height * 4) ||
            after.Length != before.Length || output.Length != before.Length)
            throw new ArgumentException("Expected equally sized BGRA32 images.");
        masks ??= Array.Empty<MaskRect>();
        foreach (var mask in masks)
            if (mask.X < 0 || mask.Y < 0 || mask.Width <= 0 || mask.Height <= 0 ||
                (long)mask.X + mask.Width > width || (long)mask.Y + mask.Height > height)
                throw new ArgumentException("Mask must be inside the aligned image.");
        long changed = 0, masked = 0;
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            if (masks.Count > 0 && masks.Any(m => x >= m.X && y >= m.Y && x < m.X + m.Width && y < m.Y + m.Height))
            {
                masked++;
                output[i] = output[i + 1] = output[i + 2] = 180;
                output[i + 3] = 255;
                continue;
            }
            var differs = before[i] != after[i] || before[i + 1] != after[i + 1] ||
                before[i + 2] != after[i + 2] || before[i + 3] != after[i + 3];
            if (differs)
            {
                changed++;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                output[i] = 45; output[i + 1] = 45; output[i + 2] = 220;
            }
            else
            {
                var gray = (byte)((after[i] + after[i + 1] + after[i + 2]) / 3);
                output[i] = output[i + 1] = output[i + 2] = (byte)(128 + gray / 2);
            }
            output[i + 3] = 255;
        }
        var compared = (long)width * height - masked;
        if (compared == 0) throw new ArgumentException("Masks cover the entire image.");
        return new Difference(width, height, changed, changed * 100d / compared,
            changed == 0 ? null : left, changed == 0 ? null : top,
            changed == 0 ? null : right, changed == 0 ? null : bottom, compared, masked);
    }

    private static BitmapSource Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
    }

    internal static void CompareFiles(string beforePath, string afterPath, string directory, string? maskPath = null)
    {
        var before = Read(beforePath);
        var after = Read(afterPath);
        if (before.PixelWidth != after.PixelWidth || before.PixelHeight != after.PixelHeight)
            throw new InvalidOperationException("Screenshot sizes differ; capture the same viewport and DPI, do not rescale.");
        var width = before.PixelWidth;
        var height = before.PixelHeight;
        var stride = checked(width * 4);
        var a = new byte[checked(stride * height)];
        var b = new byte[a.Length];
        var pixels = new byte[a.Length];
        before.CopyPixels(a, stride, 0);
        after.CopyPixels(b, stride, 0);
        var masks = maskPath is null ? Array.Empty<MaskRect>() :
            JsonSerializer.Deserialize<MaskRect[]>(File.ReadAllText(maskPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("Expected mask rectangle array.");
        var difference = Compare(a, b, pixels, width, height, masks);
        Directory.CreateDirectory(directory);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, "difference.png"))) encoder.Save(stream);
        var overlay = new byte[a.Length];
        for (var i = 0; i < overlay.Length; i++) overlay[i] = (byte)((a[i] + b[i]) / 2);
        var overlayEncoder = new PngBitmapEncoder();
        overlayEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, overlay, stride)));
        using (var stream = File.Create(Path.Combine(directory, "overlay.png"))) overlayEncoder.Save(stream);
        File.WriteAllText(Path.Combine(directory, "difference.json"),
            JsonSerializer.Serialize(new
            {
                Difference = difference, Masks = masks, BeforeDpiX = before.DpiX, BeforeDpiY = before.DpiY,
                AfterDpiX = after.DpiX, AfterDpiY = after.DpiY,
                Status = "MEASURED_NOT_VISUAL_ACCEPTANCE"
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Difference measured; human review required.");
    }

    internal static void SelfCheck()
    {
        var a = new byte[24];
        var b = new byte[24];
        var output = new byte[24];
        var same = Compare(a, b, output, 3, 2);
        if (same.ChangedPixels != 0 || same.Left is not null)
            throw new InvalidOperationException("Identical image mismatch.");
        b[4] = 1; b[23] = 255; // pixels (1,0) and (2,1), including alpha
        var changed = Compare(a, b, output, 3, 2);
        if (changed.ChangedPixels != 2 || changed.Left != 1 || changed.Top != 0 ||
            changed.Right != 2 || changed.Bottom != 1 || output[6] != 220)
            throw new InvalidOperationException("Difference count or bounding rectangle mismatch.");
        var masked = Compare(a, b, output, 3, 2, new[] { new MaskRect(1, 0, 1, 1) });
        if (masked.ChangedPixels != 1 || masked.MaskedPixels != 1 || masked.ComparedPixels != 5 || masked.Left != 2)
            throw new InvalidOperationException("Mask must exclude only its covered pixels.");
    }
}
