namespace MailDesk.Core.Text;

/// <summary>
/// Scoring for type-to-filter lists such as the command palette. Every word of the query must appear in the text, either
/// as a run of letters or as a subsequence ("rpa" finds "Reply all"). Higher is better; 0 means no match.
/// </summary>
public static class Fuzzy
{
    public static int Score(string query, string text)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;
        if (string.IsNullOrEmpty(text)) return 0;
        var total = 0;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var s = ScoreWord(word, text);
            if (s == 0) return 0;
            total += s;
        }
        return total;
    }

    private static int ScoreWord(string word, string text)
    {
        var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            // A contiguous hit: best at the very start, good at the start of a word, fine anywhere.
            var score = 40;
            if (at == 0) score += 30;
            else if (IsWordStart(text, at)) score += 15;
            if (word.Length == text.Length) score += 30; // exact
            return score;
        }

        // Subsequence: each letter in order, preferring letters that start words ("ra" -> Reply All).
        var score2 = 10;
        var pos = 0;
        foreach (var ch in word)
        {
            var next = -1;
            for (var i = pos; i < text.Length; i++)
            {
                if (char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(ch)) continue;
                next = i;
                if (IsWordStart(text, i)) break; // take a word start if one exists
            }
            if (next < 0) return 0;
            if (IsWordStart(text, next)) score2 += 4;
            pos = next + 1;
        }
        return score2;
    }

    private static bool IsWordStart(string text, int i) =>
        i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
}
