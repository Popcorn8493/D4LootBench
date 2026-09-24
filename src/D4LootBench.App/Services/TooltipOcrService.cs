using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using WinRtBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace D4LootBench.App.Services;

/// <summary>
/// Reads text lines off a screenshot with the in-box Windows OCR engine (no cloud, no NuGet).
/// The expected flow: the user snips the in-game tooltip (Win+Shift+S), then scans the clipboard.
/// Game tooltips are small light-on-dark text, which the engine reads poorly as-is, so the image
/// is preprocessed first: grayscale, contrast-stretched, inverted to dark-on-light, and upscaled.
/// </summary>
public sealed record OcrLineInfo(string Text, double CenterY, double Height);

public static class TooltipOcrService
{
    /// <summary>Upscaling beyond ~3× adds blur, not glyph detail.</summary>
    private const double MaxUpscale = 3.0;

    public static async Task<IReadOnlyList<string>> ReadLinesAsync(BitmapSource image) =>
        [.. (await ReadLineInfosAsync(image)).Select(l => l.Text)];

    /// <summary>
    /// Lines with their vertical geometry (in preprocessed-image pixels — only relative
    /// distances are meaningful). Columnar layouts like the in-game stat sheet come back as
    /// separate label and value lines; position is what lets a caller rebuild the rows.
    /// </summary>
    public static async Task<IReadOnlyList<OcrLineInfo>> ReadLineInfosAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "Windows OCR isn't available — install a language pack with OCR support in Windows settings.");

        // The pixel work (grayscale, stretch, upscale, PNG encode) takes noticeable time on a
        // large snip — run it off the UI thread. A frozen bitmap is readable from any thread.
        var source = Frozen(image);
        var lines = await RecognizeAsync(engine, await Task.Run(() => EncodePng(Preprocess(source))));
        if (lines.Count == 0)
        {
            lines = await RecognizeAsync(engine,
                await Task.Run(() => EncodePng(Scaled(source, ScaleFor(source, upscale: 1.0)))));
        }
        return lines;
    }

    /// <summary>The image itself when freezable (clipboard bitmaps are), else a frozen copy.</summary>
    private static BitmapSource Frozen(BitmapSource image)
    {
        if (image.IsFrozen)
            return image;
        if (image.CanFreeze)
        {
            image.Freeze();
            return image;
        }
        var copy = new WriteableBitmap(image);
        copy.Freeze();
        return copy;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static async Task<IReadOnlyList<OcrLineInfo>> RecognizeAsync(OcrEngine engine, byte[] png)
    {
        using var stream = new MemoryStream(png);
        var decoder = await WinRtBitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.Select(l =>
        {
            double top = l.Words.Min(w => w.BoundingRect.Y);
            double bottom = l.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            return new OcrLineInfo(l.Text, (top + bottom) / 2, bottom - top);
        }).ToList();
    }

    private static BitmapSource Preprocess(BitmapSource image)
    {
        var gray = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
        int width = gray.PixelWidth, height = gray.PixelHeight;
        var pixels = new byte[width * height];
        gray.CopyPixels(pixels, width, 0);

        var histogram = new int[256];
        long sum = 0;
        foreach (byte b in pixels)
        {
            histogram[b]++;
            sum += b;
        }

        // Stretch the 2nd–98th percentile band to full range (tooltip panels are low-contrast),
        // then flip light-on-dark to dark-on-light, which the engine reads markedly better.
        int low = Percentile(histogram, pixels.Length, 0.02);
        int high = Percentile(histogram, pixels.Length, 0.98);
        bool invert = sum / pixels.Length < 128;
        double range = Math.Max(1, high - low);
        for (int i = 0; i < pixels.Length; i++)
        {
            int stretched = (int)Math.Clamp((pixels[i] - low) * 255 / range, 0, 255);
            pixels[i] = (byte)(invert ? 255 - stretched : stretched);
        }

        var cleaned = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        return Scaled(cleaned, ScaleFor(cleaned, MaxUpscale));
    }

    private static int Percentile(int[] histogram, int total, double fraction)
    {
        long target = (long)(total * fraction);
        long seen = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (seen >= target)
                return value;
        }
        return histogram.Length - 1;
    }

    /// <summary>The largest uniform scale up to <paramref name="upscale"/> that stays within the
    /// engine's input dimension cap (snips are usually far below; huge images scale down).</summary>
    private static double ScaleFor(BitmapSource image, double upscale)
    {
        int max = (int)OcrEngine.MaxImageDimension;
        return Math.Min(upscale,
            Math.Min((double)max / image.PixelWidth, (double)max / image.PixelHeight));
    }

    private static BitmapSource Scaled(BitmapSource image, double scale) =>
        Math.Abs(scale - 1.0) < 0.01 ? image : new TransformedBitmap(image, new ScaleTransform(scale, scale));
}
