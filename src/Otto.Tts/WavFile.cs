using System.Buffers.Binary;
using System.Text;

namespace Otto.Tts;

/// <summary>
/// Just enough of the RIFF/WAVE container to answer "how long is this?".
///
/// <para>
/// The reading pipeline needs the duration of every fragment for two reasons: to know
/// when to start the next one, and to compute the real-time factor that decides whether
/// the reading can keep up at all. Taking a whole audio library for one number is not a
/// trade worth making, and the format is a header.
/// </para>
/// <para>
/// The chunks are walked rather than assumed to sit at fixed offsets. A canonical
/// 44-byte header would let this be four <c>ReadInt32</c> calls, but Piper is not the
/// only thing that will ever write one of these files, and a <c>LIST</c> or <c>fact</c>
/// chunk ahead of <c>data</c> is perfectly legal — an implementation that read offset 40
/// and called it the data size would return a plausible, wrong duration rather than an
/// error, which is the worst way for this to fail.
/// </para>
/// </summary>
public static class WavFile
{
    public static TimeSpan Duration(string path)
    {
        using var stream = File.OpenRead(path);

        return Duration(stream);
    }

    public static TimeSpan Duration(Stream stream)
    {
        var header = new byte[12];
        ReadExactly(stream, header);

        if (Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
            throw new InvalidDataException("The file is not a RIFF/WAVE container.");

        var byteRate = 0u;
        var descriptor = new byte[8];

        while (true)
        {
            var read = stream.Read(descriptor);

            // Ran out of file without ever seeing a data chunk. A truncated render is
            // exactly what a killed piper.exe leaves behind, so this is a real case and
            // not a defensive nicety.
            if (read < descriptor.Length)
                throw new InvalidDataException("The WAVE container has no data chunk.");

            var id = Encoding.ASCII.GetString(descriptor, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(4));

            if (id == "fmt ")
            {
                var format = new byte[size];
                ReadExactly(stream, format);

                // Bytes 8..11 of a fmt chunk are the average byte rate, which already
                // folds together channels, sample rate and bit depth — the three numbers
                // the alternative computation would multiply back together anyway.
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
                continue;
            }

            if (id == "data")
            {
                if (byteRate == 0)
                    throw new InvalidDataException("The WAVE container has a data chunk before its fmt chunk.");

                return TimeSpan.FromSeconds(size / (double)byteRate);
            }

            // Chunks are word-aligned: an odd size carries a pad byte that is not counted
            // in the size field. Skipping only `size` would leave the reader one byte out
            // of step and every subsequent chunk id would be garbage.
            Skip(stream, size + (size % 2));
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);

            if (read == 0) throw new InvalidDataException("The WAVE container ends mid-header.");

            offset += read;
        }
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var buffer = new byte[8192];

        while (count > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));

            if (read == 0) throw new InvalidDataException("The WAVE container ends inside a chunk.");

            count -= read;
        }
    }
}
