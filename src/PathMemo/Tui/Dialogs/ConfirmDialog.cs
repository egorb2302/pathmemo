using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Dialogs;

/// <summary>
/// A yes/no question, with the facts the answer depends on shown above it.
/// </summary>
/// <remarks>
/// <c>y</c> confirms and <b>Enter does not</b>: a dialog that appears under a finger
/// already heading for Enter is not a confirmation. The same rule will carry into the
/// delete dialog in P6, where the stakes are real (README section 14.3).
/// </remarks>
internal sealed class ConfirmDialog(string title, IReadOnlyList<string> lines, string question, Action onYes)
    : ITuiView
{
    public void Render(Screen screen, TuiSession session)
    {
        var rows = new List<Draw.PanelRow>(lines.Count + 2);
        foreach (var line in lines) rows.Add(new Draw.PanelRow(line));
        rows.Add(new Draw.PanelRow(""));
        rows.Add(new Draw.PanelRow(question, Style.Warning));

        Draw.Panel(screen, title, rows, "y yes   n / Esc no");
    }

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        session.Modal = null;

        if (key.Is('y')) onYes();
        else session.Say("cancelled");

        return true;
    }
}
