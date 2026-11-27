using MailDesk.Core.Models;
using MailDesk.Core.Storage;
using MailDesk.Core.Text;

namespace MailDesk.Tests;

public sealed class ClassifierAndSearchQueryTests
{
    [Theory]
    [InlineData("pat@example.test", "Lunch on Friday?", null, null, null, null, MessageCategory.Primary)]
    [InlineData("news@shop.example", "Spring sale", "<list.shop.example>", "<mailto:unsub@shop.example>", null, null, MessageCategory.Newsletter)]
    [InlineData("hello@brand.example", "Weekly digest", null, null, "bulk", null, MessageCategory.Newsletter)]
    [InlineData("noreply@bank.example", "Your statement is ready", null, null, null, null, MessageCategory.Notification)]
    [InlineData("notifications@github.example", "[repo] Build failed", null, "<mailto:unsub@github.example>", null, null, MessageCategory.Notification)]
    [InlineData("mailer-daemon@mx.example", "Undelivered Mail Returned to Sender", null, null, null, null, MessageCategory.Notification)]
    [InlineData("robot@ci.example", "Nightly run", null, null, null, "auto-generated", MessageCategory.Notification)]
    [InlineData("info@dentist.example", "Appointment reminder", null, null, null, null, MessageCategory.Notification)]
    [InlineData("info@dentist.example", "Thanks for visiting us", null, null, null, null, MessageCategory.Primary)]
    public void Classifies_from_sender_and_headers(string from, string subject, string? listId, string? unsub, string? precedence, string? auto, MessageCategory expected)
    {
        Assert.Equal(expected, MessageClassifier.Classify(from, subject, new MessageClassifier.Headers(listId, unsub, precedence, auto)));
    }

    [Theory]
    [InlineData("Re: Quote for the deck", "quote for the deck")]
    [InlineData("RE: RE: Fwd:   Quote  for the deck ", "quote for the deck")]
    [InlineData("Fw: Quote", "quote")]
    [InlineData("Antw: Angebot", "angebot")]
    [InlineData("Research results", "research results")]
    [InlineData("", "")]
    public void Subject_key_strips_reply_prefixes_and_normalises(string subject, string expected)
    {
        Assert.Equal(expected, MessageClassifier.SubjectKey(subject));
    }

    [Fact]
    public void Search_query_parses_operators_and_leaves_text()
    {
        var q = SearchQuery.Parse("from:council to:me@example.test subject:rates has:attachment is:unread after:2026-01 before:2026-03-15 larger:2mb in:finance_bills invoice overdue");
        Assert.Equal("invoice overdue", q.Text);
        Assert.Equal(["council"], q.From);
        Assert.Equal(["me@example.test"], q.To);
        Assert.Equal(["rates"], q.Subject);
        Assert.Equal(["finance bills"], q.Folders);
        Assert.True(q.HasAttachment);
        Assert.False(q.IsRead);
        Assert.Equal(2 * 1024 * 1024, q.MinSize);
        Assert.NotNull(q.AfterUtcMs);
        Assert.NotNull(q.BeforeUtcMs);
        Assert.True(q.AfterUtcMs < q.BeforeUtcMs);

        // Unknown operators and bare colons stay as text; an empty query is empty.
        Assert.Equal("url:http://x see: this", SearchQuery.Parse("url:http://x see: this").Text);
        Assert.True(SearchQuery.Parse("   ").IsEmpty);
        Assert.False(SearchQuery.Parse("is:flagged").IsEmpty);
    }

    [Fact]
    public void Fts_query_keeps_boolean_operators_only_where_they_make_sense()
    {
        Assert.Equal("\"loan\"* OR \"mortgage\"*", SqliteMailStore.ToFtsQuery("loan OR mortgage"));
        Assert.Equal("\"loan\"* NOT \"car\"*", SqliteMailStore.ToFtsQuery("loan NOT car"));
        Assert.Equal("\"loan\"* AND \"rates\"*", SqliteMailStore.ToFtsQuery("loan AND OR rates"));
        Assert.Equal("\"loan\"*", SqliteMailStore.ToFtsQuery("NOT loan NOT"));
        Assert.Equal("\"and\"* \"or\"*", SqliteMailStore.ToFtsQuery("and or")); // lower case words are just words

        // "-word" is handled outside FTS so it works on its own too.
        var (positive, negatives) = SqliteMailStore.SplitNegations("loan -car -\"boat\"");
        Assert.Equal("loan", positive);
        Assert.Equal(["car", "boat"], negatives);
    }
}
