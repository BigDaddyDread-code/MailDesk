using MailDesk.Agent;
using MailDesk.Core.Models;

namespace MailDesk.Tests;

public sealed class ConversationAnalystTests
{
    [Fact]
    public void Transcript_labels_messages_with_ids_and_strips_quoted_replies()
    {
        var thread = new List<MessageSummary>
        {
            new() { Id = 11, Subject = "Deck quote", FromName = "Pat", FromAddress = "pat@trade.test", ToAddresses = "me@example.test", DateUtcMs = 1_700_000_000_000 },
            new() { Id = 12, Subject = "Re: Deck quote", FromAddress = "me@example.test", ToAddresses = "pat@trade.test", DateUtcMs = 1_700_100_000_000 },
        };
        var bodies = new Dictionary<long, string>
        {
            [11] = "Hi, the deck will be $4,200 including GST. Can you confirm by Friday?",
            [12] = "Confirmed, go ahead.\n\nOn Mon, Pat wrote:\n> Hi, the deck will be $4,200 including GST.\n> Can you confirm by Friday?",
        };

        var t = ConversationAnalyst.BuildTranscript(thread, bodies, "me@example.test");

        Assert.Contains("Message id=11 | From: Pat <pat@trade.test>", t);
        Assert.Contains("Message id=12 | From: you", t);
        Assert.Contains("Confirmed, go ahead.", t);
        Assert.DoesNotContain("> Hi, the deck", t); // quoted original removed from the reply
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(t, "including GST")); // appears once, in message 11 only
    }

    [Fact]
    public void Parse_tolerates_fences_and_drops_unknown_message_ids()
    {
        const string raw = """
            Here you go:
            ```json
            {"summary": "A deck quote was accepted.",
             "decisions": [{"text": "Build the deck for $4,200 incl. GST", "who": "you", "message_id": 12, "basis": "stated"}],
             "commitments": [{"text": "Send the contract", "owner": "Pat", "due": "2026-12-05", "done": false, "message_id": 11, "basis": "inferred"},
                             {"text": "Pay the deposit", "owner": "you", "due": "", "done": false, "message_id": 999}],
             "open_questions": [{"text": "Which timber?", "who": "Pat", "message_id": 11}, {"text": "   "}],
             "uncertain": ["Whether the price includes the stairs"]}
            ```
            """;

        var insight = ConversationAnalyst.Parse(raw, new HashSet<long> { 11, 12 });

        Assert.Equal("A deck quote was accepted.", insight.Summary);
        var decision = Assert.Single(insight.Decisions);
        Assert.Equal("decision", decision.Kind);
        Assert.Equal(12, decision.MessageId);
        Assert.False(decision.IsInferred);
        Assert.False(decision.CanAddTask);

        Assert.Equal(2, insight.Commitments.Count);
        var pats = insight.Commitments[0];
        Assert.True(pats.IsInferred);
        Assert.Equal("Pat owes this · due 2026-12-05", pats.Meta);
        Assert.NotNull(pats.DueDate);
        Assert.False(pats.CanAddTask);
        var mine = insight.Commitments[1];
        Assert.Null(mine.MessageId); // 999 is not in the thread
        Assert.False(mine.HasSource);
        Assert.True(mine.CanAddTask);
        Assert.Equal("You owe this", mine.Meta);

        var q = Assert.Single(insight.OpenQuestions); // the blank one is dropped
        Assert.Equal("question", q.Kind);
        Assert.Equal("Pat is waiting", q.Meta);
        Assert.Equal("1 decision, 2 commitments, 1 open question", insight.Counts);
        Assert.Single(insight.Uncertain);

        // Round trip through the cache keeps kinds, so CanAddTask still works after reload.
        var again = ConversationAnalyst.Deserialize(ConversationAnalyst.Serialize(insight));
        Assert.NotNull(again);
        Assert.True(again!.Commitments[1].CanAddTask);
        Assert.Equal("decision", again.Decisions[0].Kind);
    }

    [Fact]
    public void Parse_without_json_keeps_the_text_as_summary()
    {
        var insight = ConversationAnalyst.Parse("I cannot read this thread.");
        Assert.Equal("I cannot read this thread.", insight.Summary);
        Assert.True(insight.IsEmpty);
        Assert.StartsWith("Nothing was decided", insight.Counts);
    }
}
