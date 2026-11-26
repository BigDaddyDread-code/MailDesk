using System.Net;
using System.Text.RegularExpressions;

namespace MailDesk.App.Services;

/// <summary>
/// Prepares message content for the reader. Scripts are disabled at the WebView2 level; this adds a
/// Content-Security-Policy that blocks remote images and external resources so senders cannot track opens.
/// The reader is always light: almost all HTML mail is designed for a white background, and forcing dark
/// makes sender-specified grey text unreadable.
/// </summary>
public static class MailHtml
{
    private static readonly Regex ScriptTag = new(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HeadOpen = new(@"<head\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string Csp = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data: cid:; style-src 'unsafe-inline'; font-src data:;\">";
    private const string BaseStyle = "<style>html,body{background:#ffffff;} body{font-family:Segoe UI,sans-serif;font-size:14px;color:#1d1c1d;margin:16px;} a{color:#1264a3;} pre{white-space:pre-wrap;font-family:Segoe UI,sans-serif;} img{max-width:100%;height:auto;}</style>";

    public static string FromHtml(string html)
    {
        var cleaned = ScriptTag.Replace(html, "");
        if (HeadOpen.IsMatch(cleaned))
            return HeadOpen.Replace(cleaned, m => m.Value + Csp + BaseStyle, 1);
        return $"<!doctype html><html><head><meta charset=\"utf-8\">{Csp}{BaseStyle}</head><body>{cleaned}</body></html>";
    }

    public static string FromText(string text)
    {
        var escaped = WebUtility.HtmlEncode(text);
        return $"<!doctype html><html><head><meta charset=\"utf-8\">{Csp}{BaseStyle}</head><body><pre>{escaped}</pre></body></html>";
    }

    public static string Empty(string message = "Select a message to read it.") =>
        $"<!doctype html><html><head><meta charset=\"utf-8\">{Csp}{BaseStyle}</head><body><p style=\"color:#616061\">{WebUtility.HtmlEncode(message)}</p></body></html>";
}
