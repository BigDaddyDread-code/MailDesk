using MailDesk.Core.Commands;
using MailDesk.Core.Text;

namespace MailDesk.Tests;

public sealed class CommandPaletteTests
{
    private static PaletteCommand Cmd(string title, string group = "Actions", string detail = "") =>
        new(title, group, () => Task.CompletedTask, detail);

    [Theory]
    [InlineData("reply", "Reply all", true)]
    [InlineData("rpa", "Reply all", true)]     // subsequence
    [InlineData("ra", "Reply all", true)]      // word initials
    [InlineData("all reply", "Reply all", true)] // every word must match, in any order
    [InlineData("replay", "Reply all", false)]
    [InlineData("", "Anything", true)]
    [InlineData("x", "", false)]
    public void Fuzzy_matches_runs_and_subsequences(string query, string text, bool matches) =>
        Assert.Equal(matches, Fuzzy.Score(query, text) > 0);

    [Fact]
    public void Fuzzy_prefers_prefix_then_word_start_then_anywhere_then_subsequence()
    {
        var prefix = Fuzzy.Score("in", "Inbox");
        var wordStart = Fuzzy.Score("in", "All inboxes");
        var inside = Fuzzy.Score("in", "Waiting");
        var scattered = Fuzzy.Score("in", "Important news"); // no "in" run; matches only as I...n
        Assert.True(prefix > wordStart, "prefix beats word start");
        Assert.True(wordStart > inside, "word start beats a hit in the middle");
        Assert.True(inside > scattered, "a contiguous run beats a subsequence");
        Assert.True(scattered > 0);
        Assert.True(Fuzzy.Score("inbox", "Inbox") > Fuzzy.Score("inbox", "Inbox archive"), "an exact title wins");
    }

    [Fact]
    public void Filter_returns_everything_in_order_for_an_empty_query()
    {
        var all = new[] { Cmd("New message"), Cmd("Reply"), Cmd("Inbox", "Go to") };
        var result = CommandPalette.Filter("", all, q => Cmd("Search for " + q));
        Assert.Equal(["New message", "Reply", "Inbox"], result.Select(c => c.Title).ToArray());
    }

    [Fact]
    public void Filter_ranks_titles_above_details_and_keeps_order_on_ties()
    {
        var all = new[]
        {
            Cmd("Sync", detail: "Inbox"),
            Cmd("Inbox", "Go to", "Work account"),
            Cmd("Inbox", "Go to", "Home account"),
            Cmd("Reply"),
        };
        var result = CommandPalette.Filter("inbox", all);
        Assert.Equal(3, result.Count);
        Assert.Equal("Inbox", result[0].Title);
        Assert.Equal("Work account", result[0].Detail); // first of the equal pair stays first
        Assert.Equal("Home account", result[1].Detail);
        Assert.Equal("Sync", result[2].Title);          // matched only through its detail
    }

    [Fact]
    public void Filter_appends_the_fallback_so_typing_always_leads_somewhere()
    {
        var all = new[] { Cmd("Reply"), Cmd("Forward") };
        var result = CommandPalette.Filter("invoice from pat", all, q => Cmd($"Search mail for \"{q}\"", "Search"));
        Assert.Single(result);
        Assert.Equal("Search mail for \"invoice from pat\"", result[0].Title);

        var matched = CommandPalette.Filter("rep", all, q => Cmd("Search " + q, "Search"));
        Assert.Equal(["Reply", "Search rep"], matched.Select(c => c.Title).ToArray());
    }
}
