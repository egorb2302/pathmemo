using PathMemo.Tui.Screens;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Dialogs;

/// <summary>
/// The sort menu behind <c>s</c> (README section 14.3).
/// </summary>
/// <remarks>
/// A menu rather than <c>Ctrl+1..6</c> as v2 proposed: Ctrl with a digit has no VT
/// sequence, so cmd.exe never delivers it and the binding would simply not exist for half
/// the users.
/// </remarks>
internal sealed class SortMenu(SortKey current, bool ascending, Action<SortKey, bool> apply) : ITuiView
{
    public void Render(Screen screen, TuiSession session)
    {
        var rows = new List<Draw.PanelRow>
        {
            Option('s', "size", SortKey.Size),
            Option('n', "name", SortKey.Name),
            Option('f', "file count", SortKey.Files),
            Option('t', "modified", SortKey.Modified),
            new(""),
            new($"  r   reverse        currently {(ascending ? "ascending" : "descending")}", Style.Dim),
        };

        Draw.Panel(screen, "Sort", rows, "Esc closes");
    }

    private Draw.PanelRow Option(char key, string label, SortKey value) =>
        new($"  {key}   {TextWidth.Pad(label, 14)}{(value == current ? Glyphs.CurrentSort : "")}",
            value == current ? Style.Accent : Style.Plain);

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        if (key.Is('r'))
        {
            apply(current, !ascending);
            session.Modal = null;
            return true;
        }

        SortKey? chosen = key switch
        {
            _ when key.Is('s') => SortKey.Size,
            _ when key.Is('n') => SortKey.Name,
            _ when key.Is('f') => SortKey.Files,
            _ when key.Is('t') => SortKey.Modified,
            _ => null,
        };

        if (chosen is { } value)
        {
            // Size means largest first; a name that starts at Z is not what anyone means
            // by sorting by name, so each key carries its own natural direction.
            apply(value, value is SortKey.Name);
        }

        session.Modal = null;
        return true;
    }
}
