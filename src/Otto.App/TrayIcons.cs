using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Otto.Core;

namespace Otto.App;

/// <summary>
/// Tray icons drawn in code instead of shipped as .ico files.
///
/// The tray icon is the only thing on screen while Otto runs in the background, so
/// it has to answer "is it on, and what is it doing?" at a glance. Generating them
/// keeps binary assets out of the repository and, more importantly, lets the icon
/// share <see cref="StateShapes"/> with the window header — the state reads as the
/// same figure in both places instead of two drawings that happen to agree.
/// </summary>
public static class TrayIcons
{
    /// <summary>
    /// Rasterised at the shape canvas's own size, so a canvas unit is a pixel and
    /// the design's numbers need no conversion.
    /// </summary>
    private const int Size = (int)StateShapes.Canvas;

    private static readonly Dictionary<DictationState, WindowIcon> Cache = [];

    public static WindowIcon For(DictationState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;

        return Cache[state] = new WindowIcon(Draw(StateShapes.For(state)));
    }

    /// <summary>
    /// Written straight into the pixel buffer, so no rendering platform needs to be
    /// up before the tray icon exists — the icon is wanted at startup, before the
    /// first window has ever been shown.
    /// </summary>
    private static Bitmap Draw(in StateShape shape)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(Size, Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using var buffer = bitmap.Lock();

        // Rows can be padded, so the stride comes from the framebuffer rather than
        // being assumed to be width * 4.
        var stride = buffer.RowBytes;
        var pixels = new byte[stride * Size];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                // Sampled at the pixel's centre, which is what the half-pixel
                // feathering inside Coverage is measured against.
                var coverage = StateShapes.Coverage(shape, x + 0.5, y + 0.5);

                var offset = y * stride + x * 4;

                // Premultiplied alpha, so the channels scale with coverage.
                pixels[offset + 0] = (byte)(shape.Colour.B * coverage);
                pixels[offset + 1] = (byte)(shape.Colour.G * coverage);
                pixels[offset + 2] = (byte)(shape.Colour.R * coverage);
                pixels[offset + 3] = (byte)(coverage * 255);
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);

        return bitmap;
    }
}
