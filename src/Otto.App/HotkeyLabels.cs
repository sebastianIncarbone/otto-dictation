using Otto.Core;

namespace Otto.App;

/// <summary>
/// Turns a <see cref="HotkeyBinding"/> into the Rioplatense label the user reads, and
/// exposes the modifier-key predicates the capture state machine needs.
///
/// Deliberately pure and static: <see cref="Otto.App.ViewModels.MainViewModel"/> must
/// stay constructible with no Avalonia <c>Application</c> (see <c>SettingsTests</c> and
/// <c>MainViewModelProvisioningTests</c>), and computing the label here — instead of
/// letting a free-text field hold its own copy — is what makes "Otto shows one hotkey
/// and listens on another" structurally impossible from now on.
/// </summary>
public static class HotkeyLabels
{
    /// <summary>
    /// Total: every representable binding produces a non-empty Spanish label, never an
    /// exception. An unknown key falls back to "Tecla 0x{vk:X2}" rather than being
    /// dropped silently — ugly-but-honest beats a blank label that looks like nothing
    /// was captured at all.
    /// </summary>
    public static string For(HotkeyBinding binding)
    {
        var modifiers = ForModifiers(binding.Modifiers);
        var key = KeyName(binding.VirtualKey);

        return modifiers.Length == 0 ? key : $"{modifiers}+{key}";
    }

    /// <summary>
    /// Modifier-only label used while capture is still open, e.g. "Ctrl+Alt". Fixed
    /// Ctrl+Alt+Shift+Win order — the same order <see cref="For"/> uses — so two equal
    /// bindings always render as the same string, regardless of the order the keys
    /// physically went down in.
    /// </summary>
    public static string ForModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);

        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");

        return string.Join("+", parts);
    }

    /// <summary>
    /// True for a Win32 virtual-key code that is itself a modifier — the generic code
    /// (e.g. <c>VK_CONTROL</c>) or one of its left/right variants. Capture must stay
    /// open on these: a chord is not finished until a non-modifier key arrives.
    /// </summary>
    public static bool IsModifierKey(uint virtualKey) => virtualKey is
        0x10 or 0xA0 or 0xA1 or  // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        0x11 or 0xA2 or 0xA3 or  // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        0x12 or 0xA4 or 0xA5 or  // VK_MENU, VK_LMENU, VK_RMENU (Alt)
        0x5B or 0x5C;            // VK_LWIN, VK_RWIN

    /// <summary>
    /// Which <see cref="HotkeyModifiers"/> flag a raw modifier virtual key implies, so
    /// holding just Ctrl shows up in the live capture hint immediately instead of
    /// waiting on a separately-tracked modifier state. <see cref="HotkeyModifiers.None"/>
    /// for anything that is not itself a modifier key.
    /// </summary>
    public static HotkeyModifiers ImpliedModifier(uint virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Control,
        0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
        0x5B or 0x5C => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };

    private static string KeyName(uint virtualKey) => virtualKey switch
    {
        0x20 => "Espacio",
        0x0D => "Enter",
        0x1B => "Escape",
        0x09 => "Tab",
        0x08 => "Backspace",
        0x2E => "Supr",
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(), // 0-9
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(), // A-Z
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",       // F1-F24
        _ => $"Tecla 0x{virtualKey:X2}",
    };
}
