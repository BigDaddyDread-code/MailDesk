using System.Net;
using System.Text.RegularExpressions;

namespace MailDesk.Core.Text;

/// <summary>Good-enough HTML to plain text for quoting and previews. Not a renderer.</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"</p\s*>|</h[1-6]\s*>|</blockquote\s*>|</table\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphBreaks();

    [GeneratedRegex(@"<br\s*/?>|</div\s*>|</tr\s*>|</li\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraNewlines();

    public static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = Blocks().Replace(html, "");
        s = ParagraphBreaks().Replace(s, "\n\n");
        s = LineBreaks().Replace(s, "\n");
        s = Tags().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ');
        s = TrailingSpaces().Replace(s, "\n");
        s = ExtraNewlines().Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>Plain text to minimal HTML so recipients' clients render line breaks.</summary>
    public static string ToSimpleHtml(string text)
    {
        var escaped = WebUtility.HtmlEncode(text ?? "").Replace("\r\n", "\n").Replace("\n", "<br>\n");
        return $"<!doctype html><html><body style=\"font-family:Segoe UI,sans-serif;font-size:14px\">{escaped}</body></html>";
    }
}
