using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using WinRtBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace D4LootBench.App.Services;

/// <summary>
/// Reads text lines off a screenshot with the in-box Windows OCR engine (no cloud, no NuGet).
/// The expected flow: the user snips the in-game tooltip (Win+Shift+S), then scans the clipboard.
/// </summary>
public static class TooltipOcrService
{
    public static async Task<IReadOnlyList<string>> ReadLinesAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "Windows OCR isn't available — install a language pack with OCR support in Windows settings.");

        // The engine caps input dimensions; snips are usually far below, but scale down just in case.
        int max = (int)OcrEngine.MaxImageDimension;
        if (image.PixelWidth > max || image.PixelHeight > max)
        {
            double scale = Math.Min((double)max / image.PixelWidth, (double)max / image.PixelHeight);
            image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        }

        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        stream.Position = 0;

        var decoder = await WinRtBitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.Select(l => l.Text).ToList();
    }
}
