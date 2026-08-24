using Otto.App;

namespace Otto.Core.Tests;

/// <summary>
/// The gate between a file downloaded off the internet and that file being
/// executed. Nothing else in Otto runs something it fetched, so nothing else has
/// this much riding on a comparison being right.
///
/// <para>
/// The download itself and the <c>CreateProcess</c> call are not exercised here —
/// they need a network and a real installer. What is exercised is every decision
/// made around them: which attachment to trust, what the published hash is, and
/// whether two hashes match. Those are the parts that can be wrong quietly.
/// </para>
/// </summary>
public class UpdateInstallerTests
{
    private const string SetupHash = "9f2c1d4e5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d";
    private const string ZipHash = "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b";

    /// <summary>What <c>sha256sum a b</c> writes: two spaces between hash and name.</summary>
    private static string Checksums() =>
        $"{SetupHash}  Otto-Setup.exe\n{ZipHash}  Otto-windows-x64.zip\n";

    // ---- Leer el archivo de hashes ----

    [Fact]
    public void Lee_el_hash_del_archivo_que_le_piden() =>
        Assert.Equal(SetupHash, UpdateInstaller.HashFor(Checksums(), "Otto-Setup.exe"));

    [Fact]
    public void No_confunde_un_archivo_con_el_otro() =>
        Assert.Equal(ZipHash, UpdateInstaller.HashFor(Checksums(), "Otto-windows-x64.zip"));

    [Fact]
    public void Acepta_el_formato_binario_con_asterisco()
    {
        // sha256sum marca el modo binario con "*" en vez de un segundo espacio.
        // Cuál de los dos aparece depende de cómo se generó el archivo, y una
        // release que no se puede leer en la máquina del usuario fallaría cerrada
        // sin que nadie pueda ver por qué.
        var texto = $"{SetupHash} *Otto-Setup.exe\n";

        Assert.Equal(SetupHash, UpdateInstaller.HashFor(texto, "Otto-Setup.exe"));
    }

    [Fact]
    public void Tolera_los_finales_de_linea_de_Windows()
    {
        var texto = $"{SetupHash}  Otto-Setup.exe\r\n{ZipHash}  Otto-windows-x64.zip\r\n";

        Assert.Equal(SetupHash, UpdateInstaller.HashFor(texto, "Otto-Setup.exe"));
    }

    [Fact]
    public void Un_archivo_que_no_figura_no_tiene_hash() =>
        Assert.Null(UpdateInstaller.HashFor(Checksums(), "Otro.exe"));

    [Fact]
    public void Un_archivo_de_hashes_vacio_no_devuelve_nada() =>
        Assert.Null(UpdateInstaller.HashFor("", "Otto-Setup.exe"));

    [Fact]
    public void Un_hash_de_largo_equivocado_se_descarta()
    {
        // Truncado a la mitad. Devolverlo dejaría que la comparación de más abajo
        // decida sobre algo que ya se sabe que no es un SHA-256.
        var texto = $"{SetupHash[..32]}  Otto-Setup.exe\n";

        Assert.Null(UpdateInstaller.HashFor(texto, "Otto-Setup.exe"));
    }

    [Fact]
    public void Un_hash_que_no_es_hexadecimal_se_descarta()
    {
        var texto = $"{new string('z', 64)}  Otto-Setup.exe\n";

        Assert.Null(UpdateInstaller.HashFor(texto, "Otto-Setup.exe"));
    }

    [Fact]
    public void Una_linea_sin_separador_no_rompe_la_lectura()
    {
        // Basura antes de la línea buena: leer el archivo entero no puede depender
        // de que todas sus líneas tengan sentido.
        var texto = $"esto-no-es-una-linea\n\n{SetupHash}  Otto-Setup.exe\n";

        Assert.Equal(SetupHash, UpdateInstaller.HashFor(texto, "Otto-Setup.exe"));
    }

    // ---- Comparar ----

    [Fact]
    public void Coincide_cuando_son_el_mismo_hash() =>
        Assert.True(UpdateInstaller.HashesMatch(SetupHash, SetupHash));

    [Fact]
    public void No_coincide_cuando_son_distintos() =>
        Assert.False(UpdateInstaller.HashesMatch(SetupHash, ZipHash));

    [Fact]
    public void La_comparacion_no_distingue_mayusculas() =>
        // sha256sum escribe minúscula y Get-FileHash mayúscula. Que el mismo
        // archivo no se valide según quién escribió el hash sería un fallo cerrado
        // permanente y sin explicación visible.
        Assert.True(UpdateInstaller.HashesMatch(SetupHash.ToUpperInvariant(), SetupHash));

    [Fact]
    public void Dos_hashes_vacios_NO_coinciden()
    {
        // ESTE es el test que justifica el chequeo de largo dentro de HashesMatch.
        // Sin él, un archivo de hashes ilegible produce un esperado vacío, un error
        // silencioso produce un calculado vacío, y la única puerta entre un
        // ejecutable descargado y correrlo se abre sola por igualdad accidental.
        Assert.False(UpdateInstaller.HashesMatch("", ""));
        Assert.False(UpdateInstaller.HashesMatch(null, null));
    }

    [Fact]
    public void Un_hash_truncado_no_coincide_con_su_original() =>
        Assert.False(UpdateInstaller.HashesMatch(SetupHash[..63], SetupHash));

    // ---- Elegir qué adjunto bajar ----

    private static ReleaseAsset Asset(string name, string url = "https://example.invalid/x", long bytes = 1) =>
        new(name, url, bytes);

    [Fact]
    public void Con_el_instalador_y_los_hashes_se_puede_instalar()
    {
        var elegido = UpdateChecker.InstallerFrom(
        [
            Asset("Otto-windows-x64.zip"),
            Asset("Otto-Setup.exe", "https://example.invalid/setup", 58_000_000),
            Asset("SHA256SUMS", "https://example.invalid/sums"),
        ]);

        Assert.NotNull(elegido);
        Assert.Equal("https://example.invalid/setup", elegido.Url);
        Assert.Equal("https://example.invalid/sums", elegido.ChecksumsUrl);
        Assert.Equal(58_000_000, elegido.Bytes);
    }

    [Fact]
    public void Sin_el_archivo_de_hashes_NO_se_ofrece_instalar() =>
        // Toda release anterior a esta función está exactamente en este caso.
        // Ofrecer instalar un ejecutable cuyo hash esperado no existe sería
        // correrlo confiando en nada.
        Assert.Null(UpdateChecker.InstallerFrom([Asset("Otto-Setup.exe"), Asset("Otto-windows-x64.zip")]));

    [Fact]
    public void Sin_el_instalador_no_se_ofrece_instalar() =>
        Assert.Null(UpdateChecker.InstallerFrom([Asset("SHA256SUMS"), Asset("Otto-windows-x64.zip")]));

    [Fact]
    public void Una_release_sin_adjuntos_no_se_ofrece_instalar()
    {
        Assert.Null(UpdateChecker.InstallerFrom([]));
        Assert.Null(UpdateChecker.InstallerFrom(null));
    }

    [Fact]
    public void El_nombre_del_adjunto_se_compara_exacto() =>
        // Un match laxo acá es una forma de bajar el archivo equivocado.
        Assert.Null(UpdateChecker.InstallerFrom([Asset("otto-setup.exe"), Asset("SHA256SUMS")]));

    [Fact]
    public void Un_adjunto_sin_URL_no_sirve() =>
        Assert.Null(UpdateChecker.InstallerFrom([new ReleaseAsset("Otto-Setup.exe", null, 1), Asset("SHA256SUMS")]));

    // ---- El estado que mira la interfaz ----

    [Fact]
    public void Estar_al_dia_nunca_se_puede_instalar() =>
        Assert.False(UpdateStatus.UpToDate("2.0.1").CanInstall);

    [Fact]
    public void No_haber_podido_verificar_nunca_se_puede_instalar() =>
        // "No pude averiguar" no es "hay una versión nueva", y menos todavía
        // "bajate esto y ejecutalo".
        Assert.False(UpdateStatus.CouldNotCheck("2.0.1").CanInstall);

    [Fact]
    public void Una_version_nueva_sin_adjuntos_se_avisa_pero_no_se_instala()
    {
        var status = new UpdateStatus(UpdateResult.Available, "2.0.1", "2.1.0", "https://example.invalid/r");

        Assert.False(status.CanInstall);
        Assert.Equal(UpdateResult.Available, status.Result);
    }

    [Fact]
    public void Una_version_nueva_con_adjuntos_se_puede_instalar()
    {
        var status = new UpdateStatus(
            UpdateResult.Available, "2.0.1", "2.1.0", "https://example.invalid/r",
            new InstallerAsset("https://example.invalid/setup", 1, "https://example.invalid/sums"));

        Assert.True(status.CanInstall);
    }
}
