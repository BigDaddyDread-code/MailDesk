using System.Globalization;

namespace MailDesk.Core.Text;

/// <summary>
/// A parsed search box query. Operators: in:/folder: name, from:, to:, subject:, has:attachment, is:unread, is:read,
/// is:flagged, before:/after: yyyy-mm-dd (or yyyy-mm, yyyy), larger:/smaller: 10mb. Whatever is left is free text,
/// where AND, OR, NOT (upper case) and a leading minus are honoured by the full-text index.
/// </summary>
public sealed class SearchQuery
{
    public string Text { get; private set; } = "";
    public List<string> Folders { get; } = [];
    public List<string> From { get; } = [];
    public List<string> To { get; } = [];
    public List<string> Subject { get; } = [];
    public bool? HasAttachment { get; private set; }
    public bool? IsRead { get; private set; }
    public bool? IsFlagged { get; private set; }
    /// <summary>Exclusive upper bound, Unix milliseconds UTC.</summary>
    public long? BeforeUtcMs { get; private set; }
    /// <summary>Inclusive lower bound, Unix milliseconds UTC.</summary>
    public long? AfterUtcMs { get; private set; }
    public long? MinSize { get; private set; }
    public long? MaxSize { get; private set; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasFilters => Folders.Count > 0 || From.Count > 0 || To.Count > 0 || Subject.Count > 0 || HasAttachment is not null
                              || IsRead is not null || IsFlagged is not null || BeforeUtcMs is not null || AfterUtcMs is not null
                              || MinSize is not null || MaxSize is not null;
    public bool IsEmpty => !HasText && !HasFilters;

    public static SearchQuery Parse(string? query)
    {
        var q = new SearchQuery();
        var rest = new List<string>();
        foreach (var token in (query ?? "").Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1) { rest.Add(token); continue; }
            var op = token[..colon].ToLowerInvariant();
            var value = token[(colon + 1)..].Trim('"');
            switch (op)
            {
                case "in" or "folder": q.Folders.Add(value.Replace('_', ' ')); break;
                case "from": q.From.Add(value); break;
                case "to" or "cc": q.To.Add(value); break;
                case "subject": q.Subject.Add(value.Replace('_', ' ')); break;
                case "has":
                    if (value.StartsWith("attach", StringComparison.OrdinalIgnoreCase) || value.Equals("file", StringComparison.OrdinalIgnoreCase)) q.HasAttachment = true;
                    else rest.Add(token);
                    break;
                case "is":
                    switch (value.ToLowerInvariant())
                    {
                        case "unread": q.IsRead = false; break;
                        case "read": q.IsRead = true; break;
                        case "flagged" or "starred": q.IsFlagged = true; break;
                        case "unflagged": q.IsFlagged = false; break;
                        default: rest.Add(token); break;
                    }
                    break;
                case "before":
                    if (TryParseDate(value, out var b, out _)) q.BeforeUtcMs = b; else rest.Add(token);
                    break;
                case "after" or "since":
                    if (TryParseDate(value, out var a, out _)) q.AfterUtcMs = a; else rest.Add(token);
                    break;
                case "on" or "date":
                    if (TryParseDate(value, out var start, out var end)) { q.AfterUtcMs = start; q.BeforeUtcMs = end; } else rest.Add(token);
                    break;
                case "larger" or "bigger" or "size":
                    if (TryParseSize(value, out var min)) q.MinSize = min; else rest.Add(token);
                    break;
                case "smaller":
                    if (TryParseSize(value, out var max)) q.MaxSize = max; else rest.Add(token);
                    break;
                default: rest.Add(token); break;
            }
        }
        q.Text = string.Join(' ', rest);
        return q;
    }

    /// <summary>Accepts yyyy-mm-dd, yyyy-mm or yyyy (local time). Returns the start of that span and the start of the next one.</summary>
    public static bool TryParseDate(string value, out long startUtcMs, out long endUtcMs)
    {
        startUtcMs = endUtcMs = 0;
        var v = value.Trim().ToLowerInvariant();
        DateTime start, end;
        var today = DateTime.Today;
        if (v is "today") { start = today; end = today.AddDays(1); }
        else if (v is "yesterday") { start = today.AddDays(-1); end = today; }
        else if (v.EndsWith('d') && int.TryParse(v[..^1], out var days)) { start = today.AddDays(-days); end = today.AddDays(1); }
        else if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) end = start.AddDays(1);
        else if (DateTime.TryParseExact(v, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) end = start.AddMonths(1);
        else if (DateTime.TryParseExact(v, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) end = start.AddYears(1);
        else if (DateTime.TryParseExact(v, "d/M/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)) end = start.AddDays(1);
        else return false;
        startUtcMs = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)).ToUnixTimeMilliseconds();
        endUtcMs = new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)).ToUnixTimeMilliseconds();
        return true;
    }

    /// <summary>Accepts 500, 500k, 10mb, 2m, 1g (case-insensitive).</summary>
    public static bool TryParseSize(string value, out long bytes)
    {
        bytes = 0;
        var v = value.Trim().ToLowerInvariant().Replace("bytes", "").Replace("b", "");
        long mult = 1;
        if (v.EndsWith('k')) { mult = 1024; v = v[..^1]; }
        else if (v.EndsWith('m')) { mult = 1024 * 1024; v = v[..^1]; }
        else if (v.EndsWith('g')) { mult = 1024L * 1024 * 1024; v = v[..^1]; }
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0) return false;
        bytes = (long)(n * mult);
        return true;
    }
}
