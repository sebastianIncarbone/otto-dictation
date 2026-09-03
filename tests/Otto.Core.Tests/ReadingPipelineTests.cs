using Microsoft.Extensions.Logging.Abstractions;

namespace Otto.Core.Tests;

/// <summary>
/// La lectura entera, sin parlantes, sin motor y sin portapapeles.
///
/// Igual que <see cref="DictationPipelineTests"/>: si esto deja de poder ejercitarse en
/// memoria, la separación por puertos se volvió decoración.
/// </summary>
public class ReadingPipelineTests
{
    /// <summary>Largo a propósito: con un texto corto `Sentences.Split` devuelve un solo fragmento.</summary>
    private const string LongText =
        "Cuando termina la dictada, Otto guarda la nota. " +
        "Después la podés buscar por texto completo, editarla o exportarla. " +
        "Y ahora también escucharla, que es de lo que se trata todo esto. " +
        "El audio se genera, suena y se borra.";

    [Fact]
    public async Task Lee_la_seleccion_y_la_reproduce()
    {
        var world = new World { Selection = { Text = "Hola, esto hay que leerlo en voz alta." } };

        await world.ToggleAndWaitAsync();

        Assert.Equal(ReadingState.Idle, world.Pipeline.State);
        Assert.NotEmpty(world.Player.Played);
        Assert.Contains(world.Synthesizer.Spoken, text => text.Contains("leerlo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Genera_el_siguiente_fragmento_mientras_suena_el_anterior()
    {
        // Este solapamiento es la razón entera por la que el texto se parte. Sin él, el
        // que escucha espera la generación de todo el documento antes del primer sonido.
        var world = new World { Selection = { Text = LongText } };

        var pendingWhilePlayingFirst = 0;

        world.Player.OnPlay = _ =>
        {
            if (world.Player.Played.Count == 1) pendingWhilePlayingFirst = world.Synthesizer.Spoken.Count;

            return Task.CompletedTask;
        };

        await world.ToggleAndWaitAsync();

        Assert.True(world.Player.Played.Count > 1, "el texto tiene que partirse en varios fragmentos");

        // Dos: el que está sonando y el que ya se está generando detrás.
        Assert.Equal(2, pendingWhilePlayingFirst);
    }

    [Fact]
    public async Task No_genera_todo_el_documento_de_una()
    {
        // Uno adelante, no todos. Generar el documento entero gastaría un proceso y un
        // archivo temporal por fragmento de un texto que el usuario está por cortar a las
        // tres oraciones, que es exactamente lo que hace alguien que hojea una página.
        var world = new World { Selection = { Text = LongText } };

        var maximum = 0;

        world.Player.OnPlay = _ =>
        {
            maximum = Math.Max(maximum, world.Synthesizer.Spoken.Count - world.Player.Played.Count);

            return Task.CompletedTask;
        };

        await world.ToggleAndWaitAsync();

        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task Un_segundo_disparo_corta_la_lectura()
    {
        // La única regla del pipeline: mientras hay una lectura en curso, cualquier cosa
        // que arrancaría una la frena.
        var world = new World { Selection = { Text = LongText } };

        var playing = new TaskCompletionSource();

        world.Player.Block = true;
        world.Player.OnPlay = _ => { playing.TrySetResult(); return Task.CompletedTask; };

        var idle = world.WhenIdle();

        world.Pipeline.Toggle();
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        world.Pipeline.Toggle();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReadingState.Idle, world.Pipeline.State);
        Assert.Single(world.Player.Played);
    }

    [Fact]
    public async Task El_boton_de_la_ventana_de_notas_tambien_corta()
    {
        var world = new World { Selection = { Text = LongText } };

        var playing = new TaskCompletionSource();

        world.Player.Block = true;
        world.Player.OnPlay = _ => { playing.TrySetResult(); return Task.CompletedTask; };

        var idle = world.WhenIdle();

        world.Pipeline.Read("Un texto largo cualquiera para que se parta en varios pedazos y suene.");
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        world.Pipeline.Read("Otro texto distinto.");
        await idle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReadingState.Idle, world.Pipeline.State);
    }

    [Fact]
    public async Task Sin_voz_instalada_avisa_y_no_toca_el_portapapeles()
    {
        // Degradación a nada: sin voz no hay lectura, pero tampoco hay un Ctrl+C sintético
        // pisándole la selección al usuario para nada.
        var world = new World { Selection = { Text = "algo" } };

        world.Synthesizer.Available = false;

        var unavailable = false;
        world.Pipeline.Unavailable += () => unavailable = true;

        await world.ToggleAndWaitAsync();

        Assert.True(unavailable);
        Assert.Equal(0, world.Selection.Reads);
        Assert.Empty(world.Player.Played);
    }

    [Fact]
    public async Task Avisa_cuando_no_habia_nada_para_leer()
    {
        // Sin esto el usuario aprieta la tecla, no pasa nada, y no tiene forma de
        // distinguir "Otto está roto" de "no había nada ahí".
        var world = new World { Selection = { Text = null } };

        var nothing = false;
        world.Pipeline.NothingToRead += () => nothing = true;

        await world.ToggleAndWaitAsync();

        Assert.True(nothing);
        Assert.Empty(world.Player.Played);
        Assert.Equal(ReadingState.Idle, world.Pipeline.State);
    }

    [Fact]
    public async Task Borra_el_audio_temporal_al_terminar()
    {
        // El audio no vive en la máquina del usuario. Se genera, suena y se borra.
        var world = new World { Selection = { Text = LongText } };

        await world.ToggleAndWaitAsync();

        Assert.NotEmpty(world.Synthesizer.Folders);
        Assert.All(world.Synthesizer.Folders, folder => Assert.False(Directory.Exists(folder)));
    }

    [Fact]
    public async Task Borra_el_audio_temporal_aunque_la_lectura_se_corte()
    {
        var world = new World { Selection = { Text = LongText } };

        var playing = new TaskCompletionSource();

        world.Player.Block = true;
        world.Player.OnPlay = _ => { playing.TrySetResult(); return Task.CompletedTask; };

        var idle = world.WhenIdle();

        world.Pipeline.Toggle();
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        world.Pipeline.Stop();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(world.Synthesizer.Folders, folder => Assert.False(Directory.Exists(folder)));
    }

    [Fact]
    public async Task Un_fallo_del_motor_no_deja_el_pipeline_trabado()
    {
        // Misma decisión que DictationPipeline: una herramienta de fondo que se muere en
        // una lectura mala deja al usuario sin nada corriendo.
        var world = new World { Selection = { Text = LongText } };

        world.Synthesizer.Fail = true;

        await world.ToggleAndWaitAsync();

        Assert.Equal(ReadingState.Idle, world.Pipeline.State);

        // Y arranca de nuevo sin reiniciar nada.
        world.Synthesizer.Fail = false;
        await world.ToggleAndWaitAsync();

        Assert.NotEmpty(world.Player.Played);
    }

    [Fact]
    public async Task El_atajo_dispara_la_lectura()
    {
        var world = new World { Selection = { Text = "Leeme esto que está seleccionado." } };

        world.Pipeline.Register(HotkeyBinding.DefaultReading);

        Assert.Equal(HotkeyBinding.DefaultReading, world.Pipeline.RegisteredHotkey);

        var idle = world.WhenIdle();
        world.Hotkey.Press();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(world.Player.Played);
    }

    [Fact]
    public async Task Read_no_toca_la_seleccion_del_usuario()
    {
        // El botón de la ventana de notas ya sabe qué texto quiere. Mandar un Ctrl+C
        // sintético ahí sería pisarle el portapapeles al usuario sin motivo.
        var world = new World { Selection = { Text = "esto no se tiene que usar" } };

        var idle = world.WhenIdle();
        world.Pipeline.Read("Este es el texto de la nota que hay que leer en voz alta ahora.");
        await idle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, world.Selection.Reads);
        Assert.Contains(world.Synthesizer.Spoken, text => text.Contains("nota", StringComparison.Ordinal));
    }

    // ---- Transporte: pausa, repetir, velocidad ----

    [Fact]
    public async Task Pausar_frena_la_lectura_y_seguir_la_reanuda()
    {
        var world = new World { Selection = { Text = "Algo para leer en voz alta." } };
        world.Player.Block = true;

        var idle = world.WhenIdle();
        world.Pipeline.Toggle();

        await WaitUntilAsync(() => world.Player.Played.Count > 0);

        world.Pipeline.Pause();

        Assert.Equal(ReadingState.Paused, world.Pipeline.State);
        Assert.True(world.Player.IsPaused);

        world.Pipeline.Resume();

        Assert.Equal(ReadingState.Reading, world.Pipeline.State);
        Assert.False(world.Player.IsPaused);

        world.Pipeline.Stop();
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Pausar_no_es_una_tercera_forma_de_cortar()
    {
        // La única regla de esta clase: mientras hay una lectura, cualquier cosa que
        // arrancaría una la corta. Tiene que seguir queriendo decir lo mismo con la
        // lectura pausada, o el usuario necesita acordarse de en qué estado la dejó para
        // saber qué hace el atajo.
        var world = new World { Selection = { Text = "Algo para leer en voz alta." } };
        world.Player.Block = true;

        var idle = world.WhenIdle();
        world.Pipeline.Toggle();

        await WaitUntilAsync(() => world.Player.Played.Count > 0);

        world.Pipeline.Pause();
        world.Pipeline.Toggle();

        await idle.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ReadingState.Idle, world.Pipeline.State);
    }

    [Fact]
    public async Task Repetir_vuelve_a_reproducir_la_misma_frase()
    {
        var world = new World { Selection = { Text = "Algo para leer en voz alta." } };
        world.Player.Block = true;

        var idle = world.WhenIdle();
        world.Pipeline.Toggle();

        await WaitUntilAsync(() => world.Player.Played.Count == 1);

        world.Pipeline.Repeat();

        await WaitUntilAsync(() => world.Player.Played.Count == 2);

        // El mismo archivo las dos veces: repetir es un segundo PlayAsync sobre el WAV que
        // ya está en disco, no un segundo viaje por Piper.
        Assert.Equal(world.Player.Played[0], world.Player.Played[1]);
        Assert.Single(world.Synthesizer.Spoken, text => text.Contains("leer", StringComparison.Ordinal));

        world.Pipeline.Stop();
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Repetir_desde_la_pausa_vuelve_a_sonar()
    {
        // El usuario pidió volver a escucharlo. Un repetir que dejara el fragmento armado
        // pero en silencio se ve exactamente igual que un botón muerto.
        var world = new World { Selection = { Text = "Algo para leer en voz alta." } };
        world.Player.Block = true;

        var idle = world.WhenIdle();
        world.Pipeline.Toggle();

        await WaitUntilAsync(() => world.Player.Played.Count == 1);

        world.Pipeline.Pause();
        world.Pipeline.Repeat();

        await WaitUntilAsync(() => world.Player.Played.Count == 2);

        Assert.Equal(ReadingState.Reading, world.Pipeline.State);
        Assert.False(world.Player.IsPaused);

        world.Pipeline.Stop();
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cortar_no_se_confunde_con_repetir()
    {
        // Los dos cancelan el fragmento. Si el catch se comiera las dos cancelaciones,
        // cada corte se volvería un avance silencioso al fragmento siguiente.
        var world = new World { Selection = { Text = LongText } };
        world.Player.Block = true;

        var idle = world.WhenIdle();
        world.Pipeline.Toggle();

        await WaitUntilAsync(() => world.Player.Played.Count == 1);

        world.Pipeline.Stop();
        await idle.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(world.Player.Played);
    }

    [Fact]
    public async Task Empezar_una_lectura_nueva_limpia_una_pausa_vieja()
    {
        // Cortar deja el reproductor pausado a propósito — reanudar algo que se está por
        // desarmar es un ruido en cada corte. El precio es que la lectura siguiente tiene
        // que limpiarlo, o nace muda.
        var world = new World { Selection = { Text = "Algo para leer en voz alta." } };
        world.Player.Block = true;

        var first = world.WhenIdle();
        world.Pipeline.Toggle();

        await WaitUntilAsync(() => world.Player.Played.Count == 1);

        world.Pipeline.Pause();
        world.Pipeline.Stop();

        await first.WaitAsync(TimeSpan.FromSeconds(10));

        world.Player.Block = false;
        await world.ToggleAndWaitAsync();

        Assert.False(world.Player.IsPaused);
    }

    [Fact]
    public void La_velocidad_elegida_llega_al_reproductor()
    {
        var world = new World();

        world.Pipeline.Speed = ReadingSpeed.Faster;

        Assert.Equal(2.0, world.Player.Speed);
        Assert.Equal(ReadingSpeed.Faster, world.Pipeline.Speed);
    }

    [Fact]
    public void Elegir_la_velocidad_que_ya_estaba_no_avisa_a_nadie()
    {
        var world = new World();
        var changes = 0;

        world.Pipeline.SpeedChanged += _ => changes++;

        world.Pipeline.Speed = ReadingSpeed.Normal;

        Assert.Equal(0, changes);
    }

    [Fact]
    public void La_velocidad_cicla_y_vuelve_a_x1()
    {
        // Envolver importa tanto como ciclar: un control que se queda en x2 deja al usuario
        // sin manera de volver a x1 sin ir a Ajustes en el medio de una lectura.
        Assert.Equal(ReadingSpeed.Fast, ReadingSpeed.Normal.Next());
        Assert.Equal(ReadingSpeed.Faster, ReadingSpeed.Fast.Next());
        Assert.Equal(ReadingSpeed.Normal, ReadingSpeed.Faster.Next());
    }

    [Fact]
    public void Una_velocidad_desconocida_en_el_config_no_rompe_nada()
    {
        Assert.Equal(ReadingSpeed.Normal, ReadingSpeed.Resolve("x7"));
        Assert.Equal(ReadingSpeed.Normal, ReadingSpeed.Resolve(null));
        Assert.Equal(ReadingSpeed.Faster, ReadingSpeed.Resolve("x2"));
    }

    /// <summary>
    /// El pipeline dispara y se olvida, así que los controles de transporte se prueban
    /// contra un estado que aparece cuando aparece. Sondear es feo pero es honesto: la
    /// alternativa es un Delay fijo, que en una máquina cargada falla sin decir por qué.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("La condición nunca se cumplió.");

            await Task.Delay(5);
        }
    }

    private sealed class World
    {
        public FakeSynthesizer Synthesizer { get; } = new();
        public FakePlayer Player { get; } = new();
        public FakeSelection Selection { get; } = new();
        public FakeHotkey Hotkey { get; } = new();

        public ReadingPipeline Pipeline { get; }

        public World() =>
            Pipeline = new ReadingPipeline(Synthesizer, Player, Selection, Hotkey,
                NullLogger<ReadingPipeline>.Instance);

        /// <summary>
        /// El pipeline dispara y se olvida a propósito — lo llama el bombeo de mensajes del
        /// atajo, y bloquearlo congelaría todos los atajos del sistema. Así que los tests
        /// esperan la vuelta a Idle, que es la única señal de que terminó.
        /// </summary>
        public Task WhenIdle()
        {
            var idle = new TaskCompletionSource();

            void Watch(ReadingState state)
            {
                if (state != ReadingState.Idle) return;

                Pipeline.StateChanged -= Watch;
                idle.TrySetResult();
            }

            Pipeline.StateChanged += Watch;

            return idle.Task;
        }

        public async Task ToggleAndWaitAsync()
        {
            var idle = WhenIdle();

            Pipeline.Toggle();

            await idle.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class FakeSynthesizer : ISpeechSynthesizer
    {
        private readonly Lock gate = new();

        public bool Available { get; set; } = true;

        public bool Fail { get; set; }

        public List<string> Spoken { get; } = [];

        /// <summary>Las carpetas temporales que tocó, para verificar que se borren.</summary>
        public List<string> Folders { get; } = [];

        public bool IsAvailable => Available;

        public Task<SynthesizedSpeech> SpeakAsync(string text, string destinationPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Fail) throw new InvalidOperationException("piper se cayó");

            var folder = Path.GetDirectoryName(destinationPath)!;

            lock (gate)
            {
                Spoken.Add(text);
                if (!Folders.Contains(folder)) Folders.Add(folder);
            }

            File.WriteAllText(destinationPath, "");

            return Task.FromResult(new SynthesizedSpeech(destinationPath, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.5)));
        }
    }

    private sealed class FakePlayer : IAudioPlayer
    {
        private readonly Lock gate = new();

        public List<string> Played { get; } = [];

        public Func<string, Task>? OnPlay { get; set; }

        /// <summary>Se queda sonando hasta que lo cancelen, para poder probar el corte.</summary>
        public bool Block { get; set; }

        /// <summary>
        /// La velocidad, tal cual la deja el pipeline. El adaptador de verdad la aplica
        /// con SoundTouch; acá alcanza con registrar que llegó, que es lo único que el
        /// pipeline promete.
        /// </summary>
        public double Speed { get; set; } = 1.0;

        public bool IsPaused { get; private set; }

        /// <summary>Cuántas veces se pidió seguir, para distinguir un Resume real de ninguno.</summary>
        public int Resumes { get; private set; }

        public void Pause() { lock (gate) IsPaused = true; }

        public void Resume()
        {
            lock (gate)
            {
                IsPaused = false;
                Resumes++;
            }
        }

        public async Task PlayAsync(string wavPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (gate) Played.Add(wavPath);

            if (OnPlay is not null) await OnPlay(wavPath);

            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class FakeSelection : ISelectionReader
    {
        public string? Text { get; set; }

        public int Reads { get; private set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;

            return Task.FromResult(Text);
        }
    }

    private sealed class FakeHotkey : ISingleShotHotkey
    {
        public event Action? Pressed;

        public HotkeyBinding? Registered { get; private set; }

        public void Press() => Pressed?.Invoke();

        public void Register(HotkeyBinding binding) => Registered = binding;

        public void Unregister() => Registered = null;

        public void Dispose() { }
    }
}
