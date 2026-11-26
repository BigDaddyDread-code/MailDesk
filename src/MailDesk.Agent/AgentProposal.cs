using MailDesk.Core.Models;

namespace MailDesk.Agent;

public enum ProposalKind
{
    MoveMessages,
    CreateFolder,
    MarkRead,
    Flag,
    CreateRule,
    CreateTask,
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
    /// <summary>Rules to save for CreateRule proposals.</summary>
    public IReadOnlyList<MailRule> Rules { get; init; } = [];
    /// <summary>Tasks to add to the local to-do list for CreateTask proposals.</summary>
    public IReadOnlyList<TaskItem> Tasks { get; init; } = [];
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public int Count => Kind switch { ProposalKind.CreateRule => Rules.Count, ProposalKind.CreateTask => Tasks.Count, _ => MessageIds.Count };
}
