using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GhostCode.App.Diagnostics;

/// <summary>
/// Captures genuine rendered WinUI 3 XAML UI visuals using RenderTargetBitmap
/// with theme-accurate Mica base compositing into crisp PNG files.
/// </summary>
public static class WindowScreenshotCapture
{
    public static async Task<bool> CaptureElementToPngAsync(UIElement element, ElementTheme actualTheme, string outputPath)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0)
            return false;

        var pixelBuffer = await rtb.GetPixelsAsync();
        var pixels = new byte[pixelBuffer.Length];
        using (var dr = DataReader.FromBuffer(pixelBuffer))
            dr.ReadBytes(pixels);

        // Composite semi-transparent/transparent backdrop regions over theme Mica base tone (BGRA)
        byte bgB = actualTheme == ElementTheme.Dark ? (byte)36 : (byte)251;
        byte bgG = actualTheme == ElementTheme.Dark ? (byte)26 : (byte)246;
        byte bgR = actualTheme == ElementTheme.Dark ? (byte)24 : (byte)244;

        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a < 255)
            {
                pixels[i + 0] = (byte)Math.Min(255, pixels[i + 0] + (bgB * (255 - a)) / 255);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + (bgG * (255 - a)) / 255);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + (bgR * (255 - a)) / 255);
                pixels[i + 3] = 255;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            96.0,
            96.0,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        using var outReader = new DataReader(stream.GetInputStreamAt(0));
        await outReader.LoadAsync((uint)stream.Size);
        var encoded = new byte[stream.Size];
        outReader.ReadBytes(encoded);
        await File.WriteAllBytesAsync(outputPath, encoded);
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }
}
