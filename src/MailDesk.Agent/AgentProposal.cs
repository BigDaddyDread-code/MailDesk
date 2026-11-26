namespace MailDesk.Agent;

public enum ProposalKind
{
    MoveMessages,
    CreateFolder,
    MarkRead,
    Flag,
    CreateRule,
}

public enum ProposalStatus
{
    Pending,
    Approved,
    Rejected,
    Applied,
    Failed,
}

/// <summary>
/// One reviewable action the agent wants to take. Nothing in a proposal touches the mailbox until a human
/// approves it and the applier runs it. This is the contract that keeps the agent safe.
/// </summary>
public sealed class AgentProposal
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ProposalKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Rationale { get; init; } = "";
    public long AccountId { get; init; }
    public long SourceFolderId { get; init; }
    public IReadOnlyList<long> MessageIds { get; init; } = [];
    /// <summary>Target folder display name for MoveMessages / CreateFolder. Created if it does not exist.</summary>
    public string? TargetFolderName { get; init; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public int Count => MessageIds.Count;
}
