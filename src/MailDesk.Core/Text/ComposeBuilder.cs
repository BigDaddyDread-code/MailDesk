using MailDesk.Core.Models;

namespace MailDesk.Core.Text;

/// <summary>Builds reply and forward drafts from a cached message. Pure, so it is unit-tested without a server.</summary>
public static class ComposeBuilder
{
    public static OutgoingMessage Reply(MessageSummary original, MessageBody? body, string selfAddress, bool replyAll)
    {
        var to = string.IsNullOrWhiteSpace(original.FromAddress) ? "" : Format(original.FromName, original.FromAddress);
        var cc = "";
        if (replyAll)
        {
            var others = SplitAddresses(original.ToAddresses)
                .Where(a => !a.Equals(selfAddress, StringComparison.OrdinalIgnoreCase)
                         && !a.Equals(original.FromAddress, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            cc = string.Join(", ", others);
        }
        var refs = new List<string>();
        if (!string.IsNullOrWhiteSpace(original.MessageId)) refs.Add(original.MessageId);
        return new OutgoingMessage
        {
            To = to,
            Cc = cc,
            Subject = Prefix(original.Subject, "Re:"),
            TextBody = "\n\n" + QuoteHeader(original) + "\n" + Quote(BodyText(body)),
            InReplyTo = string.IsNullOrWhiteSpace(original.MessageId) ? null : original.MessageId,
            References = refs,
        };
    }

    public static OutgoingMessage Forward(MessageSummary original, MessageBody? body)
    {
        var header = string.Join("\n",
            "---------- Forwarded message ----------",
            $"From: {Format(original.FromName, original.FromAddress)}",
            $"Date: {original.Date:ddd, d MMM yyyy HH:mm}",
            $"Subject: {original.Subject}",
            $"To: {original.ToAddresses}");
        return new OutgoingMessage
        {
            Subject = Prefix(original.Subject, "Fwd:"),
            TextBody = "\n\n" + header + "\n\n" + BodyText(body),
        };
    }

    public static string BodyText(MessageBody? body)
    {
        if (body is null) return "";
        if (!string.IsNullOrWhiteSpace(body.TextBody)) return body.TextBody.Replace("\r\n", "\n").Trim();
        return HtmlText.ToPlainText(body.HtmlBody ?? "");
    }

    /// <summary>Index where a quoted original begins ("On ... wrote:", the forwarded-message header, or a run of "&gt;" lines), or -1.</summary>
    public static int QuoteStart(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var pos = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if ((t.StartsWith("On ") && t.TrimEnd().EndsWith("wrote:")) || t.StartsWith("---------- Forwarded message") || t.StartsWith("-----Original Message-----"))
                return pos;
            if (t.StartsWith('>') && i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('>')) return pos;
            pos += lines[i].Length + 1;
        }
        return -1;
    }

    /// <summary>The text above any quoted original: what this message itself says.</summary>
    public static string StripQuotedReply(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var at = QuoteStart(text);
        return (at < 0 ? text : text[..at]).Trim();
    }

    public static string Quote(string text)
    {
        if (string.IsNullOrEmpty(text)) return "> ";
        return string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));
    }

    public static string QuoteHeader(MessageSummary m)
        => $"On {m.Date:ddd, d MMM yyyy 'at' HH:mm}, {Format(m.FromName, m.FromAddress)} wrote:";

    /// <summary>Adds "Re:" or "Fwd:" unless an equivalent prefix is already there.</summary>
    public static string Prefix(string subject, string prefix)
    {
        var s = (subject ?? "").Trim();
        var p = prefix.TrimEnd(':').ToLowerInvariant();
        var lower = s.ToLowerInvariant();
        if (lower.StartsWith(p + ":") || (p == "fwd" && lower.StartsWith("fw:"))) return s;
        return $"{prefix} {s}";
    }

    public static string Format(string name, string address)
        => string.IsNullOrWhiteSpace(name) || name.Equals(address, StringComparison.OrdinalIgnoreCase)
            ? address
            : $"\"{name.Replace("\"", "")}\" <{address}>";

    public static IEnumerable<string> SplitAddresses(string list)
        => (list ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
