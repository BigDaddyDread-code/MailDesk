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

    /// <summary>
    /// A polite follow-up. For a message you sent, it goes back to its recipients and quotes it; for a message from someone
    /// else (a commitment they made), it replies to them. <paramref name="about"/> names the thing being chased, if known.
    /// </summary>
    public static OutgoingMessage Nudge(MessageSummary original, MessageBody? body, string selfAddress, string selfName, string? about = null)
    {
        var mine = original.FromAddress.Equals(selfAddress, StringComparison.OrdinalIgnoreCase);
        var recipients = mine
            ? SplitAddresses(original.ToAddresses).Where(a => !a.Equals(selfAddress, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [original.FromAddress];
        var greeting = Greeting(mine ? recipients.Select(a => FirstName("", a)).ToList() : [FirstName(original.FromName, original.FromAddress)]);
        var when = original.Date.ToString("dddd d MMMM");
        var what = string.IsNullOrWhiteSpace(about) ? $"my message below from {when} about \"{original.Subject.Trim()}\"" : about;
        var ask = mine
            ? $"Just following up on {what}. Could you let me know where this is at?"
            : $"Just following up on {what}. Could you let me know how that is going?";
        var sign = string.IsNullOrWhiteSpace(selfName) ? "" : $"\n{selfName.Split(' ')[0]}";
        return new OutgoingMessage
        {
            To = mine ? string.Join(", ", recipients) : Format(original.FromName, original.FromAddress),
            Cc = mine ? original.CcAddresses : "",
            Subject = Prefix(original.Subject, "Re:"),
            TextBody = $"{greeting}\n\n{ask}\n\nThanks,{sign}\n\n{QuoteHeader(original)}\n{Quote(BodyText(body))}",
            InReplyTo = string.IsNullOrWhiteSpace(original.MessageId) ? null : original.MessageId,
            References = string.IsNullOrWhiteSpace(original.MessageId) ? [] : [original.MessageId],
        };
    }

    /// <summary>"Hi Pat," / "Hi Pat and Sam," / "Hi all," for three or more / "Hi," when no name is known.</summary>
    public static string Greeting(IReadOnlyList<string> firstNames)
    {
        var names = firstNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return names.Count switch
        {
            0 => "Hi,",
            1 => $"Hi {names[0]},",
            2 => $"Hi {names[0]} and {names[1]},",
            _ => "Hi all,",
        };
    }

    /// <summary>A first name from a display name, else from the address's local part ("pat.jones@x" → "Pat"); "" for machine-looking addresses.</summary>
    public static string FirstName(string displayName, string address)
    {
        var name = (displayName ?? "").Trim().Trim('"');
        if (name.Length > 0 && !name.Contains('@'))
        {
            var first = name.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)[0];
            return first.Length > 1 ? first : "";
        }
        var at = address.IndexOf('@');
        var local = at > 0 ? address[..at] : address;
        var token = local.Split(['.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (token.Length < 2 || token.Any(char.IsDigit) || MessageClassifier.Classify(address, "", MessageClassifier.NoHeaders) != MessageCategory.Primary) return "";
        return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
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
