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
/// makes the state colour the single source of truth and keeps binary assets out
/// of the repository.
/// </summary>
public static class TrayIcons
{
    private const int Size = 32;

    private static readonly Dictionary<DictationState, WindowIcon> Cache = [];

    public static WindowIcon For(DictationState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;

        (byte r, byte g, byte b) = state switch
        {
            DictationState.Loading      => ((byte)0x8A, (byte)0x8A, (byte)0x8A),  // gris: todavía no sirve
            DictationState.Recording    => ((byte)0xE5, (byte)0x48, (byte)0x4A),  // rojo: te está escuchando
            DictationState.Transcribing => ((byte)0xE8, (byte)0xA3, (byte)0x3D),  // ámbar: pensando
            _                           => ((byte)0x4C, (byte)0xAF, (byte)0x76),  // verde: listo
        };

        return Cache[state] = new WindowIcon(Draw(r, g, b));
    }

    /// <summary>
    /// A filled circle, antialiased by hand. Written straight into the pixel buffer
    /// so no rendering platform needs to be up before the tray icon exists.
    /// </summary>
    private static Bitmap Draw(byte r, byte g, byte b)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(Size, Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using var buffer = bitmap.Lock();

        // Rows can be padded, so the stride comes from the framebuffer rather than
        // being assumed to be width * 4.
        var stride = buffer.RowBytes;
        var pixels = new byte[stride * Size];

        const double centre = (Size - 1) / 2.0;
        const double radius = Size / 2.0 - 2;

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var distance = Math.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));

                // One pixel of feathering at the edge; without it the circle reads
                // as a jagged blob at 16 px in the notification area.
                var coverage = Math.Clamp(radius - distance + 0.5, 0, 1);

                var offset = y * stride + x * 4;

                // Premultiplied alpha, so the channels scale with coverage.
                pixels[offset + 0] = (byte)(b * coverage);
                pixels[offset + 1] = (byte)(g * coverage);
                pixels[offset + 2] = (byte)(r * coverage);
                pixels[offset + 3] = (byte)(coverage * 255);
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);

        return bitmap;
    }
}
