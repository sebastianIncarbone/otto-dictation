using Microsoft.Extensions.Logging.Abstractions;
using Otto.Core;
using Otto.Storage;

namespace Otto.Core.Tests;

/// <summary>
/// Against a real SQLite file, not a substitute. The interesting behaviour here —
/// migrations, the FTS index staying in sync, accent-insensitive search — lives in
/// SQL, and a mock would assert nothing about it.
/// </summary>
public class SqliteNoteRepositoryTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"otto-test-{Guid.NewGuid():N}.db");
    private readonly SqliteNoteRepository repository;

    public SqliteNoteRepositoryTests() =>
        repository = new SqliteNoteRepository(path, NullLogger<SqliteNoteRepository>.Instance);

    private static readonly DictationContext Code = new("Code", "Program.cs");

    [Fact]
    public async Task Guarda_y_devuelve_una_nota()
    {
        var saved = await repository.AddAsync("hola mundo", Code, TimeSpan.FromSeconds(3));

        var found = await repository.GetAsync(saved.Id);

        Assert.NotNull(found);
        Assert.Equal("hola mundo", found.Text);
        Assert.Equal("Code", found.Context.ProcessName);
        Assert.Equal(3, found.AudioDuration.TotalSeconds, precision: 1);
    }

    [Fact]
    public async Task Las_notas_recientes_vienen_de_la_mas_nueva_a_la_mas_vieja()
    {
        await repository.AddAsync("primera", Code, TimeSpan.Zero);
        await Task.Delay(10);
        await repository.AddAsync("segunda", Code, TimeSpan.Zero);

        var recent = await repository.RecentAsync();

        Assert.Equal("segunda", recent[0].Text);
        Assert.Equal("primera", recent[1].Text);
    }

    [Fact]
    public async Task La_busqueda_ignora_acentos()
    {
        // Someone searching "recien" should find "recién". In Spanish this is what
        // people expect, and it is the reason the tokenizer strips diacritics.
        await repository.AddAsync("el pull request que subí recién", Code, TimeSpan.Zero);

        var results = await repository.SearchAsync("recien");

        Assert.Single(results);
    }

    [Fact]
    public async Task La_busqueda_encuentra_por_prefijo()
    {
        await repository.AddAsync("hay que hacer el deploy a producción", Code, TimeSpan.Zero);

        var results = await repository.SearchAsync("produc");

        Assert.Single(results);
    }

    [Fact]
    public async Task La_busqueda_tolera_texto_que_no_es_sintaxis_de_fts()
    {
        // A user typing a quote or an operator must get no results, never a crash.
        await repository.AddAsync("algo", Code, TimeSpan.Zero);

        var results = await repository.SearchAsync("\"OR NOT (");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Editar_una_nota_actualiza_el_indice_de_busqueda()
    {
        var note = await repository.AddAsync("texto original", Code, TimeSpan.Zero);

        await repository.UpdateAsync(note.Id, "mi título", "texto corregido");

        Assert.Empty(await repository.SearchAsync("original"));
        Assert.Single(await repository.SearchAsync("corregido"));
        Assert.Single(await repository.SearchAsync("título"));
    }

    [Fact]
    public async Task Borrar_una_nota_la_saca_de_la_busqueda()
    {
        var note = await repository.AddAsync("efímera", Code, TimeSpan.Zero);

        await repository.DeleteAsync(note.Id);

        Assert.Null(await repository.GetAsync(note.Id));
        Assert.Empty(await repository.SearchAsync("efímera"));
    }

    [Fact]
    public void Las_migraciones_se_aplican_una_sola_vez()
    {
        // Reopening the same file must not try to recreate the tables.
        using var reopened = new SqliteNoteRepository(path, NullLogger<SqliteNoteRepository>.Instance);

        Assert.NotNull(reopened);
    }

    public void Dispose()
    {
        repository.Dispose();

        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
            File.Delete(file);
    }
}
