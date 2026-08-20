using System.Globalization;
using Avalonia.Data.Converters;

namespace Otto.App.Views;

/// <summary>
/// How wide one note should be, given how wide the list is.
///
/// <para>
/// Past a point the single column stops being a list and becomes a column of very
/// long lines: a dictation is prose, and prose set nine hundred pixels wide is
/// hard to read for the same reason a newspaper is not one column. The design
/// answers that by splitting the list in two once the window is wide enough.
/// </para>
/// <para>
/// A converter rather than a property on the view model, because the width of the
/// window is not something the view model knows or should have to be told. This is
/// the view answering a question about itself.
/// </para>
/// </summary>
public sealed class NoteColumns : IValueConverter
{
    public static NoteColumns ItemWidth { get; } = new();

    /// <summary>
    /// Between the design's two drawn sizes — it draws one column at 560 and two at
    /// 900 — and far enough above the window's 420 minimum that resizing does not
    /// flip back and forth over the boundary.
    /// </summary>
    public const double Splits = 760;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // NaN is WrapPanel's "size to the item", which is what the first layout pass
        // needs: at that point the list has no width yet, and answering 0 would give
        // every note a width of nothing and an empty screen that never recovers.
        if (value is not double width || width <= 0) return double.NaN;

        return width >= Splits ? width / 2 : width;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The list reports its width; nothing sets it back.");
}
