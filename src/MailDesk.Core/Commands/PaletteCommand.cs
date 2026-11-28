using MailDesk.Core.Text;

namespace MailDesk.Core.Commands;

/// <summary>
/// One entry in the command palette: something the user can do or somewhere they can go. Running it is whatever the
/// caller supplied; the palette itself never touches mail.
/// </summary>
public sealed record PaletteCommand(string Title, string Group, Func<Task> Run, string Detail = "", string Shortcut = "")
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasShortcut => Shortcut.Length > 0;
}

public static class CommandPalette
{
    /// <summary>
    /// The commands that match <paramref name="query"/>, best first; an empty query returns everything in the given order.
    /// Titles count for more than details. With a non-empty query, <paramref name="fallback"/> (for example "Search mail
    /// for ...") is appended so typing anything always leads somewhere.
    /// </summary>
    public static IReadOnlyList<PaletteCommand> Filter(string query, IEnumerable<PaletteCommand> commands, Func<string, PaletteCommand?>? fallback = null)
    {
        query = query?.Trim() ?? "";
        var list = commands.ToList();
        if (query.Length == 0) return list;

        var ranked = list
            .Select((c, index) => (command: c, score: Math.Max(Fuzzy.Score(query, c.Title) * 2, Fuzzy.Score(query, c.Detail)), index))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.command)
            .ToList();
        if (fallback?.Invoke(query) is { } extra) ranked.Add(extra);
        return ranked;
    }
}
