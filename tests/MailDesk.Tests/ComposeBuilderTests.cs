using MailDesk.Core.Models;
using MailDesk.Core.Text;

namespace MailDesk.Tests;

public sealed class ComposeBuilderTests
{
    private static MessageSummary Original() => new()
    {
        MessageId = "<abc@example.test>",
        Subject = "Lunch?",
        FromName = "Pat Example",
        FromAddress = "pat@example.test",
        ToAddresses = "me@mine.test, sam@example.test",
        DateUtcMs = DateTimeOffset.Parse("2026-11-26T10:00:00Z").ToUnixTimeMilliseconds(),
    };

    [Fact]
    public void Reply_addresses_sender_quotes_body_and_threads()
    {
        var reply = ComposeBuilder.Reply(Original(), new MessageBody { TextBody = "Are you free?\r\n\r\nPat" }, "me@mine.test", replyAll: false);

        Assert.Equal("\"Pat Example\" <pat@example.test>", reply.To);
        Assert.Equal("", reply.Cc);
        Assert.Equal("Re: Lunch?", reply.Subject);
        Assert.Equal("<abc@example.test>", reply.InReplyTo);
        Assert.Contains("<abc@example.test>", reply.References);
        Assert.Contains("wrote:", reply.TextBody);
        Assert.Contains("> Are you free?", reply.TextBody);
        Assert.Contains("\n>\n> Pat", reply.TextBody);
    }

    [Fact]
    public void Reply_all_copies_other_recipients_but_not_self_or_sender()
    {
        var reply = ComposeBuilder.Reply(Original(), null, "me@mine.test", replyAll: true);
        Assert.Equal("sam@example.test", reply.Cc);
    }

    [Fact]
    public void Prefixes_are_not_stacked()
    {
        Assert.Equal("Re: Lunch?", ComposeBuilder.Prefix("Re: Lunch?", "Re:"));
        Assert.Equal("RE: Lunch?", ComposeBuilder.Prefix("RE: Lunch?", "Re:"));
        Assert.Equal("Fwd: Lunch?", ComposeBuilder.Prefix("Lunch?", "Fwd:"));
        Assert.Equal("FW: Lunch?", ComposeBuilder.Prefix("FW: Lunch?", "Fwd:"));
    }

    [Fact]
    public void Forward_includes_original_headers_and_html_is_flattened()
    {
        var fwd = ComposeBuilder.Forward(Original(), new MessageBody { HtmlBody = "<html><head><style>p{}</style></head><body><p>Hello&nbsp;there</p><p>Bye</p></body></html>" });
        Assert.Equal("Fwd: Lunch?", fwd.Subject);
        Assert.Contains("From: \"Pat Example\" <pat@example.test>", fwd.TextBody);
        Assert.Contains("Subject: Lunch?", fwd.TextBody);
        Assert.Contains("Hello there\n\nBye", fwd.TextBody);
        Assert.DoesNotContain("p{}", fwd.TextBody);
        Assert.Equal("", fwd.To);
    }

    [Fact]
    public void Html_to_text_handles_breaks_entities_and_scripts()
    {
        var text = HtmlText.ToPlainText("<div>Line 1<br>Line 2</div><script>alert(1)</script><div>&amp; done</div>");
        Assert.Equal("Line 1\nLine 2\n& done", text);
        Assert.Contains("a &lt; b<br>", HtmlText.ToSimpleHtml("a < b\nc"));
    }
}
