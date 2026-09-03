namespace Otto.App.ViewModels;

/// <summary>
/// Which of Otto's two global hotkeys the settings window is editing.
///
/// <para>
/// A type rather than a bool because the two are not opposites of one another in any
/// useful sense, and because the capture machine reads it in five places: a
/// <c>isCapturingTheReadingOne</c> flag would have every one of those spell out the
/// negative case for dictation.
/// </para>
/// </summary>
public enum HotkeyTarget
{
    Dictation,
    Reading,
}
