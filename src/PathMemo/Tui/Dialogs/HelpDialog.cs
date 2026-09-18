using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Dialogs;

/// <summary>
/// The keymap (README section 14.3), verbatim enough that the README and the screen
/// cannot drift apart unnoticed.
/// </summary>
/// <remarks>
/// Single characters everywhere, and never <c>Ctrl+Shift+*</c> or <c>Ctrl+digit</c>: the
/// first is swallowed by Windows Terminal, VS Code and ConEmu before the application sees
/// it, and the second has no VT sequence at all in cmd.exe. Keys that only exist in a
/// later phase are listed with the phase, so the screen never pretends to do more than it
/// does.
/// </remarks>
internal sealed class HelpDialog : ITuiView
{
    private static readonly Draw.PanelRow[] Rows =
    [
        new("NAVIGATION", Style.Title),
        new("  j / down      move down            g / G     top / bottom"),
        new("  k / up        move up              Ctrl+D/U  page down / up"),
        new("  l / right     enter directory      ~         volume root"),
        new("  Enter         enter or details     1 / 2     overview / tree"),
        new("  h / left      parent directory     3 / 4 / 5 audit / reclaim / dupes"),
        new("", Style.Plain),
        new("VIEW", Style.Title),
        new("  s             sort menu            /         search this scan"),
        new("  m             size mode            n / N     next / previous match"),
        new("  t             directories / files / both     F5  rescan"),
        new("", Style.Plain),
        new("ACTIONS", Style.Title),
        new("  y             copy path            e         reveal in Explorer"),
        new("  Y             copy details         o         open file (confirmed)"),
        new("  x / space     mark                 a         mark all in view"),
        new("  X             clear marks          i         details"),
        new("  d             delete marked        Shift+D   delete permanently"),
        new("  K             keep: never recommend this path again"),
        new("", Style.Plain),
        new("RECLAIM (4)", Style.Title),
        new("  l / Enter     the paths behind a rule        t   risk ceiling"),
        new("  x             mark a rule or a path          K   keep / disable the rule"),
        new("", Style.Plain),
        new("DUPLICATES (5)", Style.Title),
        new("  r             search - reads the files, minutes not milliseconds"),
        new("  l / Enter     the files of a group           t   show hard-link sets"),
        new("  x             mark a copy to delete; the last one in a group cannot go"),
        new("", Style.Plain),
        new("MISC", Style.Title),
        new("  ?             this help            c         open config in an editor"),
        new("  q / Esc       back                 Q         quit"),
        new("  Ctrl+C        standard cancel - never hijacked as \"copy\"", Style.Dim),
    ];

    public void Render(Screen screen, TuiSession session) =>
        Draw.Panel(screen, "Keys", Rows, "any key closes this");

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        session.Modal = null;
        return true;
    }
}
