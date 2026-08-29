using System.Buffers.Binary;
using System.Text;
using Otto.Tts;

namespace Otto.Core.Tests;

/// <summary>
/// Cuánto dura un fragmento renderizado.
///
/// El número decide cuándo arranca el siguiente fragmento y si la lectura le puede
/// seguir el ritmo al habla, así que una duración plausible pero equivocada es peor que
/// una excepción: se nota como audio que se pisa, mucho después y lejos de la causa.
/// </summary>
public class WavFileTests
{
    private const int SampleRate = 22_050;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int ByteRate = SampleRate * Channels * BitsPerSample / 8;

    [Fact]
    public void Un_segundo_de_audio_mide_un_segundo()
    {
        var wav = Build([("fmt ", Format()), ("data", new byte[ByteRate])]);

        Assert.Equal(1.0, WavFile.Duration(new MemoryStream(wav)).TotalSeconds, precision: 3);
    }

    [Fact]
    public void Saltea_un_chunk_desconocido_antes_del_data()
    {
        // Un LIST o un fact antes del data es perfectamente legal. Una implementación que
        // leyera el offset 40 y lo llamara tamaño de datos devolvería un número plausible
        // y equivocado en vez de un error.
        var wav = Build([("fmt ", Format()), ("LIST", Encoding.ASCII.GetBytes("INFOhecho por otto")), ("data", new byte[ByteRate / 2])]);

        Assert.Equal(0.5, WavFile.Duration(new MemoryStream(wav)).TotalSeconds, precision: 3);
    }

    [Fact]
    public void Un_chunk_de_tamano_impar_lleva_un_byte_de_relleno_que_no_cuenta()
    {
        // Los chunks están alineados a palabra: un tamaño impar arrastra un byte de padding
        // que el campo de tamaño no cuenta. Saltear solo `size` deja al lector corrido un
        // byte y todos los ids siguientes salen basura.
        var wav = Build([("fmt ", Format()), ("odd ", [1, 2, 3]), ("data", new byte[ByteRate * 2])]);

        Assert.Equal(2.0, WavFile.Duration(new MemoryStream(wav)).TotalSeconds, precision: 3);
    }

    [Fact]
    public void Un_render_cortado_a_la_mitad_falla_en_vez_de_devolver_cero()
    {
        // Esto es exactamente lo que deja un piper.exe matado a mitad de camino, así que
        // es un caso real y no una defensa teórica.
        var wav = Build([("fmt ", Format())]);

        Assert.Throws<InvalidDataException>(() => WavFile.Duration(new MemoryStream(wav)));
    }

    [Fact]
    public void Un_archivo_que_no_es_wav_falla()
    {
        var bytes = Encoding.ASCII.GetBytes("Esto no es un contenedor de audio, es texto.");

        Assert.Throws<InvalidDataException>(() => WavFile.Duration(new MemoryStream(bytes)));
    }

    [Fact]
    public void Un_data_antes_del_fmt_falla_en_vez_de_dividir_por_cero()
    {
        var wav = Build([("data", new byte[ByteRate]), ("fmt ", Format())]);

        Assert.Throws<InvalidDataException>(() => WavFile.Duration(new MemoryStream(wav)));
    }

    private static byte[] Format()
    {
        var format = new byte[16];

        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(0), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8), ByteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), BitsPerSample);

        return format;
    }

    private static byte[] Build((string Id, byte[] Data)[] chunks)
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream, Encoding.ASCII);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0); // El tamaño del RIFF no lo mira nadie acá.
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        foreach (var (id, data) in chunks)
        {
            writer.Write(Encoding.ASCII.GetBytes(id));
            writer.Write(data.Length);
            writer.Write(data);

            if (data.Length % 2 == 1) writer.Write((byte)0);
        }

        writer.Flush();

        return stream.ToArray();
    }
}
