using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// El atajo de lectura, que hasta ahora vivía en config.json sin ninguna manera de
/// moverlo: Ajustes anunciaba Ctrl+Alt+L con un texto fijo aunque el archivo dijera otra
/// cosa.
///
/// <para>
/// Lo que se prueba acá no es que haya un control nuevo — eso es XAML — sino que las dos
/// capturas comparten UNA sola máquina de estados. Dos máquinas armadas a la vez son dos
/// editores esperando la misma tecla, y el manejador de KeyDown de la ventana marca como
/// manejada cada tecla mientras haya una captura activa.
/// </para>
/// </summary>
public class MainViewModelReadingHotkeyTests
{
    private const uint L = 0x4C;
    private const uint Space = 0x20;
    private const uint K = 0x4B;
    private const uint Escape = 0x1B;

    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    private static MainViewModel Build(Settings? stored = null, IHotkeyAvailability? availability = null)
    {
        if (availability is null)
        {
            availability = Substitute.For<IHotkeyAvailability>();
            availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);
        }

        return new(
            Substitute.For<INoteRepository>(),
            BuildPipeline(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
            stored ?? new Settings(),
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
            },
            availability);
    }

    private static DictationPipeline BuildPipeline() => new(
        Substitute.For<IHotkeyService>(),
        Substitute.For<IAudioCapture>(),
        Substitute.For<ITranscriber>(),
        Substitute.For<ITextInjector>(),
        Substitute.For<IForegroundWindow>(),
        Substitute.For<INoteRepository>(),
        new NullPostProcessor(),
        NullLogger<DictationPipeline>.Instance);

    [Fact]
    public void El_editor_arranca_mostrando_lo_que_hay_en_el_config_no_un_literal()
    {
        var view = Build(new Settings { ReadingModifiers = HotkeyModifiers.Control, ReadingVirtualKey = K });

        Assert.Equal(HotkeyLabels.For(new HotkeyBinding(HotkeyModifiers.Control, K)), view.ReadingHotkeyLabel);
    }

    [Fact]
    public void Capturar_la_lectura_no_abre_tambien_el_editor_de_dictado()
    {
        var view = Build();

        view.StartReadingHotkeyCaptureCommand.Execute(null);

        Assert.True(view.IsCapturingReadingHotkey);
        Assert.False(view.IsCapturingDictationHotkey);
    }

    [Fact]
    public void Capturar_el_dictado_no_abre_tambien_el_editor_de_lectura()
    {
        var view = Build();

        view.StartHotkeyCaptureCommand.Execute(null);

        Assert.True(view.IsCapturingDictationHotkey);
        Assert.False(view.IsCapturingReadingHotkey);
    }

    [Fact]
    public void La_tecla_capturada_va_al_atajo_que_se_estaba_editando_y_no_al_otro()
    {
        var view = Build();
        var dictationBefore = view.HotkeyLabel;

        view.StartReadingHotkeyCaptureCommand.Execute(null);
        view.OfferKey(CtrlAlt, K);

        Assert.Equal(HotkeyLabels.For(new HotkeyBinding(CtrlAlt, K)), view.ReadingHotkeyLabel);
        Assert.Equal(dictationBefore, view.HotkeyLabel);
    }

    [Fact]
    public void No_se_puede_poner_la_lectura_encima_del_atajo_de_dictado()
    {
        // El caso que IHotkeyAvailability no puede contestar bien: la combinación está
        // tomada, sí, pero por Otto. Sin este chequeo se acepta acá y la rechaza Windows
        // en el próximo arranque, donde App.SetReadingEnabled se come el error a
        // propósito: el usuario queda con una tecla que no hace nada y sin explicación.
        var view = Build();

        view.StartReadingHotkeyCaptureCommand.Execute(null);
        view.OfferKey(CtrlAlt, Space);

        Assert.Equal(HotkeyLabels.For(HotkeyBinding.DefaultReading), view.ReadingHotkeyLabel);
        Assert.True(view.IsCapturingReadingHotkey);
        Assert.Contains("dictar", view.HotkeyHint);
    }

    [Fact]
    public void No_se_puede_poner_el_dictado_encima_del_atajo_de_lectura()
    {
        var view = Build();

        view.StartHotkeyCaptureCommand.Execute(null);
        view.OfferKey(CtrlAlt, L);

        Assert.Equal(HotkeyLabels.For(HotkeyBinding.Default), view.HotkeyLabel);
        Assert.True(view.IsCapturingDictationHotkey);
        Assert.Contains("leer", view.HotkeyHint);
    }

    [Fact]
    public void Volver_a_elegir_el_atajo_que_la_lectura_ya_tiene_no_se_reporta_como_ajeno()
    {
        // Auto-conflicto: la sonda dice "tomada" porque la tiene Otto. Rechazarla sería
        // decirle al usuario que otra aplicación le robó su propio atajo.
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(false);

        var view = Build(availability: availability);

        view.StartReadingHotkeyCaptureCommand.Execute(null);
        view.OfferKey(CtrlAlt, L);

        Assert.False(view.IsCapturingReadingHotkey);
        Assert.Equal("", view.HotkeyHint);
    }

    [Fact]
    public void Cancelar_devuelve_el_atajo_de_lectura_al_que_estaba_guardado()
    {
        var view = Build();

        view.StartReadingHotkeyCaptureCommand.Execute(null);
        view.OfferKey(CtrlAlt, K);
        view.CancelHotkeyCaptureCommand.Execute(null);

        Assert.Equal(HotkeyLabels.For(HotkeyBinding.DefaultReading), view.ReadingHotkeyLabel);
    }

    [Fact]
    public void Cerrar_Ajustes_desarma_tambien_la_captura_de_lectura()
    {
        var view = Build();

        view.IsSettingsOpen = true;
        view.StartReadingHotkeyCaptureCommand.Execute(null);
        view.IsSettingsOpen = false;

        Assert.False(view.IsCapturingHotkey);
        Assert.False(view.IsCapturingReadingHotkey);
    }

    [Fact]
    public void Escape_cancela_la_captura_de_lectura_sin_tocar_nada()
    {
        var view = Build();

        view.StartReadingHotkeyCaptureCommand.Execute(null);
        view.OfferKey(HotkeyModifiers.None, Escape);

        Assert.False(view.IsCapturingHotkey);
        Assert.Equal(HotkeyLabels.For(HotkeyBinding.DefaultReading), view.ReadingHotkeyLabel);
    }

    [Fact]
    public void ApplyTo_persiste_el_atajo_de_lectura_y_su_rotulo()
    {
        var view = Build();

        view.StartReadingHotkeyCaptureCommand.Execute(null);
        view.OfferKey(CtrlAlt, K);

        var saved = view.ApplyTo(new Settings());

        Assert.Equal(CtrlAlt, saved.ReadingModifiers);
        Assert.Equal(K, saved.ReadingVirtualKey);
        Assert.Equal(view.ReadingHotkeyLabel, saved.ReadingHotkeyLabel);

        // El de dictado no se movió: son dos campos independientes, y un ApplyTo que
        // escribiera los dos desde la misma captura es exactamente el bug que "los ajustes
        // se enmiendan, nunca se reconstruyen" existe para prevenir.
        Assert.Equal(HotkeyBinding.Default.VirtualKey, saved.VirtualKey);
    }

    [Fact]
    public void ApplyTo_no_toca_el_atajo_de_lectura_cuando_nadie_lo_edito()
    {
        var view = Build(new Settings { ReadingModifiers = HotkeyModifiers.Shift, ReadingVirtualKey = K });

        var saved = view.ApplyTo(new Settings());

        Assert.Equal(HotkeyModifiers.Shift, saved.ReadingModifiers);
        Assert.Equal(K, saved.ReadingVirtualKey);
    }

    [Fact]
    public void Guardar_no_se_lleva_puesta_la_posicion_del_personaje()
    {
        // La regla de "los ajustes se enmienda, nunca se reconstruyen", aplicada al campo
        // nuevo. La posición la escribe App cuando terminás de arrastrar; esta ventana no
        // sabe que existe, y justamente por eso un ApplyTo que reconstruyera el record la
        // borraría en el próximo Guardar de cualquier otra cosa — el personaje volvería solo
        // al rincón cada vez que tocaras el idioma.
        var view = Build(new Settings { CharacterX = 300, CharacterY = 200 });

        var saved = view.ApplyTo(new Settings { CharacterX = 300, CharacterY = 200 });

        Assert.Equal(300, saved.CharacterX);
        Assert.Equal(200, saved.CharacterY);
    }

    [Fact]
    public void Nunca_haberlo_movido_no_es_la_esquina_de_arriba_a_la_izquierda()
    {
        // Null y no 0,0: cero es una posición real — arriba a la izquierda — y con un
        // default no nulo cada instalación nueva abriría ahí en vez de donde el diseño lo
        // pone.
        var recien = new Settings();

        Assert.Null(recien.CharacterX);
        Assert.Null(recien.CharacterY);
    }

    [Fact]
    public void Sin_instalador_de_voces_no_se_promete_nada_sobre_la_descarga()
    {
        // Con nada por donde instalar no hay respuesta honesta, y adivinar "no está
        // descargada" ofrecería una tranquilidad que nada puede cumplir.
        Assert.Equal("", Build().ReadingVoiceAvailability);
    }
}
