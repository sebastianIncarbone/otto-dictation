using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Otto.Core;

namespace Otto.App.Views;

/// <summary>
/// The window the overlay lives in: borderless, transparent, always on top, and
/// transparent to input.
///
/// Avalonia gets it most of the way there. The rest — clicks passing through and,
/// critically, never taking focus — needs platform styles applied once the native
/// handle exists, which is what <see cref="IOverlayStyler"/> is for.
/// </summary>
public partial class CharacterWindow : Window
{
    /// <summary>The character's canvas. The glyph carries its own, and it is smaller.</summary>
    private const double CharacterSide = 144;

    private readonly IOverlayStyler? styler;

    private OttoCharacter? character;
    private OttoRing? ring;
    private OttoGlyph? glyph;

    /// <summary>
    /// Null until the first <see cref="SetAppearance"/>, so that call always builds
    /// something rather than matching a default it was never told about.
    /// </summary>
    private CharacterAppearance? current;

    /// <summary>The last state seen, so a swap mid-dictation does not start from Idle.</summary>
    private DictationState state;

    /// <summary>
    /// The last reading seen, for the same reason <see cref="state"/> exists: swapping the
    /// appearance while Otto is talking must not leave the new overlay standing there
    /// silently as though nothing were happening.
    /// </summary>
    private ReadingState? readingState;

    // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated version
    // wires up the x:Name fields, and this window needs a reference to Host.
    //
    // Deliberately builds no overlay. OttoCharacter starts its animation timer in
    // its own constructor and stops it on detach, so a control that is constructed
    // and then replaced before the window ever opens is never attached, never
    // detached, and leaves a timer running for the life of the process.
    public CharacterWindow() => InitializeComponent();

    public CharacterWindow(IOverlayStyler styler, CharacterAppearance appearance) : this()
    {
        this.styler = styler;
        SetAppearance(appearance);
    }

    /// <summary>
    /// Swaps the overlay in place, and resizes the window to whichever one is now
    /// in it.
    ///
    /// <para>
    /// In place, rather than by building a new window, for the same reason hiding
    /// is <c>Hide()</c> and never <c>Close()</c>: the click-through and never-focus
    /// styles are applied to the native handle in <see cref="OnOpened"/>, and
    /// destroying the window would take them with it. It also keeps
    /// <see cref="Follow"/>'s subscriptions valid — a rebuilt window would have to
    /// re-subscribe, leaving the old handlers writing into a discarded control.
    /// </para>
    /// <para>
    /// Clearing the host detaches the outgoing control, which is what stops its
    /// timer. Nothing else does.
    /// </para>
    /// </summary>
    public void SetAppearance(CharacterAppearance appearance)
    {
        if (current == appearance) return;

        current = appearance;

        Host.Children.Clear();
        character = null;
        ring = null;
        glyph = null;

        switch (appearance)
        {
            case CharacterAppearance.Minimal:
                glyph = new OttoGlyph { State = state };
                Host.Children.Add(glyph);

                Width = OttoGlyph.DesignWidth;
                Height = OttoGlyph.DesignHeight;
                break;

            case CharacterAppearance.Discreet:
                ring = new OttoRing { State = state };
                Host.Children.Add(ring);

                Width = OttoRing.WindowSize;
                Height = OttoRing.WindowSize;
                break;

            default:
                character = new OttoCharacter { State = state, Reading = readingState };
                Host.Children.Add(character);

                Width = CharacterSide;
                Height = CharacterSide;
                break;
        }

        // The corner is measured from the window's own size, so a swap that changed
        // it leaves the overlay floating away from the edge until this re-runs.
        // Only meaningful once the window is open; OnOpened covers the other case.
        if (IsVisible) MoveToCorner();
    }

    /// <summary>
    /// Wires the overlay to the pipeline.
    ///
    /// The state drives what Otto is doing; the two events drive what he thinks
    /// about how it went. Those are reactions, not states — the pipeline is back
    /// to idle either way — so they are shown for a moment and then dropped.
    ///
    /// Called once per window, never per appearance: the handlers read whichever
    /// control is current at the time they fire, so swapping does not disturb them.
    /// </summary>
    public void Follow(DictationPipeline pipeline)
    {
        Apply(pipeline.State);

        pipeline.StateChanged += next =>
            Dispatcher.UIThread.Post(() => Apply(next));

        pipeline.Dictated += (_, _) =>
            Dispatcher.UIThread.Post(() => character?.React(OttoPose.Pleased));

        // Held the key and said nothing — a muted microphone looks exactly like
        // this, and without a reaction the user gets no signal at all.
        //
        // Both reactions are poses, so they are the character's alone. Neither the
        // ring nor the glyph has anything to say them with — that is most of what
        // being a quieter overlay means — and inventing a flash for either would be
        // adding behaviour the design did not ask for.
        pipeline.HeardNothing += () =>
            Dispatcher.UIThread.Post(() => character?.React(OttoPose.Annoyed, 1.8));
    }

    /// <summary>
    /// The reading's two dead ends, given a face.
    ///
    /// <para>
    /// Reactions only, and no state — deliberately. <see cref="Apply"/> takes a
    /// <see cref="DictationState"/>, and a reading is not one of those: Otto is Idle for
    /// dictation the whole time he is talking. Inventing a state here would mean the
    /// overlay claiming a dictation is happening whenever a reading is.
    /// </para>
    /// <para>
    /// Two poses because the pipeline is careful to raise two events, and collapsing them
    /// would throw that away: "you selected nothing" and "there is no voice on this
    /// machine" need different answers from the user, so they get different faces.
    /// Startled rather than Annoyed for the missing voice — Otto was asked for something
    /// he does not have, which is not the same as being asked for nothing.
    /// </para>
    /// <para>
    /// Both are the character's alone, exactly as <c>HeardNothing</c> already is: the ring
    /// and the glyph have nothing to say them with, and that is most of what being a
    /// quieter overlay means.
    /// </para>
    /// </summary>
    public void FollowReading(ReadingPipeline reading)
    {
        Apply(reading.State);

        reading.StateChanged += next =>
            Dispatcher.UIThread.Post(() => Apply(next));

        reading.NothingToRead += () =>
            Dispatcher.UIThread.Post(() => character?.React(OttoPose.Annoyed, 1.8));

        reading.Unavailable += () =>
            Dispatcher.UIThread.Post(() => character?.React(OttoPose.Startled, 1.8));
    }

    /// <summary>
    /// Remembered as well as forwarded, so an overlay built later — by a swap —
    /// opens already showing what Otto is doing rather than waiting for the next
    /// change to tell it.
    /// </summary>
    private void Apply(DictationState next)
    {
        state = next;

        if (character is not null) character.State = next;
        if (ring is not null) ring.State = next;
        if (glyph is not null) glyph.State = next;
    }

    /// <summary>
    /// Idle becomes null rather than a third value the character has to interpret: "there
    /// is no reading" and "there is a reading, stopped" are the same thing on screen, and
    /// collapsing them here keeps <see cref="OttoCharacter.Reading"/> honest — it is a
    /// reading or it is nothing.
    ///
    /// <para>
    /// Only the character answers this. The ring and the glyph carry the dictation state
    /// and nothing else, which is most of what being a quieter overlay means — someone on
    /// "Punto mínimo" still gets the transport card and the tray, which is where the
    /// reading feedback that does not depend on a decorative setting lives.
    /// </para>
    /// </summary>
    private void Apply(ReadingState next)
    {
        readingState = next == ReadingState.Idle ? null : next;

        if (character is not null) character.Reading = readingState;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The handle only exists once the window is open, so the styles cannot be
        // applied any earlier.
        if (TryGetPlatformHandle() is { } handle)
            styler?.MakeClickThrough(handle.Handle);

        MoveToCorner();
    }

    /// <summary>
    /// Bottom-right, clear of the taskbar. Out of the way of what people read and
    /// type, and next to where the tray icon already is.
    /// </summary>
    private void MoveToCorner()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var area = screen.WorkingArea;
        var margin = 24;

        Position = new PixelPoint(
            area.X + area.Width - (int)(Width * screen.Scaling) - margin,
            area.Y + area.Height - (int)(Height * screen.Scaling) - margin);
    }
}
