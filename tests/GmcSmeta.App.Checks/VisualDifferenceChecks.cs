using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;

internal static class VisualDifferenceChecks
{
    internal sealed record Difference(int Width, int Height, long ChangedPixels, double ChangedPercent,
        int? Left, int? Top, int? Right, int? Bottom);

    // Input must be aligned screenshots with identical pixel dimensions. No
    // rescaling, threshold or automatic visual PASS. Alpha is included.
    internal static Difference Compare(byte[] before, byte[] after, byte[] output, int width, int height)
    {
        if (width <= 0 || height <= 0 || before.Length != checked(width * height * 4) ||
            after.Length != before.Length || output.Length != before.Length)
            throw new ArgumentException("Expected equally sized BGRA32 images.");
        long changed = 0;
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
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
        return new Difference(width, height, changed, changed * 100d / ((long)width * height),
            changed == 0 ? null : left, changed == 0 ? null : top,
            changed == 0 ? null : right, changed == 0 ? null : bottom);
    }

    private static BitmapSource Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
    }

    internal static void CompareFiles(string beforePath, string afterPath, string directory)
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
        var difference = Compare(a, b, pixels, width, height);
        Directory.CreateDirectory(directory);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, "difference.png"))) encoder.Save(stream);
        File.WriteAllText(Path.Combine(directory, "difference.json"),
            JsonSerializer.Serialize(new
            {
                Difference = difference, BeforeDpiX = before.DpiX, BeforeDpiY = before.DpiY,
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
    }
}
