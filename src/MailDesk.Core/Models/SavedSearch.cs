namespace MailDesk.Core.Models;

/// <summary>
/// A search the user keeps in the sidebar. The query uses the same operators as the search box (from:, after:, has:attachment,
/// AND/OR/NOT ...), so a saved search is a live view over the cache that re-runs each time it is opened. Lives only locally.
/// </summary>
public sealed class SavedSearch
{
    public long Id { get; set; }
    /// <summary>The account searched, or null for every account.</summary>
    public long? AccountId { get; set; }
    /// <summary>A single folder to search, or null for all folders of the account(s).</summary>
    public long? FolderId { get; set; }
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    /// <summary>Sidebar position; new searches go last.</summary>
    public int SortOrder { get; set; }
    public long CreatedUtcMs { get; set; }
}
