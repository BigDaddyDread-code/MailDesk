using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Core.Models;
using MailDesk.Core.Rules;

namespace MailDesk.App.ViewModels;

public sealed record FolderChoice(string FullName, string DisplayName);

public sealed partial class ConditionItem : ObservableObject
{
    [ObservableProperty] private RuleField _field = RuleField.FromAddress;
    [ObservableProperty] private RuleOperator _operator = RuleOperator.Contains;
    [ObservableProperty] private string _value = "";

    public RuleCondition ToCondition() => new(Field, Operator, Value.Trim());
}

/// <summary>Backs the rule editor window. Converts to and from MailRule; validation lives here.</summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    private readonly long _id;
    private readonly long _accountId;
    private readonly long _createdUtcMs;
    private readonly int _sortOrder;
    private readonly int _hitCount;

    public RuleEditorViewModel(MailRule? existing, long accountId, IEnumerable<FolderChoice> folders)
    {
        Folders = folders.ToList();
        _accountId = existing?.AccountId ?? accountId;
        _id = existing?.Id ?? 0;
        _createdUtcMs = existing?.CreatedUtcMs ?? 0;
        _sortOrder = existing?.SortOrder ?? 0;
        _hitCount = existing?.HitCount ?? 0;
        IsNew = existing is null || existing.Id == 0;

        if (existing is not null)
        {
            _name = existing.Name;
            _enabled = existing.Enabled;
            _action = existing.Action;
            _targetFolder = existing.TargetFolder;
            foreach (var c in existing.Conditions)
                Conditions.Add(new ConditionItem { Field = c.Field, Operator = c.Operator, Value = c.Value });
        }
        if (Conditions.Count == 0) Conditions.Add(new ConditionItem());
    }

    public bool IsNew { get; }
    public IReadOnlyList<FolderChoice> Folders { get; }
    public IReadOnlyList<RuleField> Fields { get; } = Enum.GetValues<RuleField>();
    public IReadOnlyList<RuleOperator> Operators { get; } = Enum.GetValues<RuleOperator>();
    public IReadOnlyList<RuleAction> Actions { get; } = Enum.GetValues<RuleAction>();
    public ObservableCollection<ConditionItem> Conditions { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMoveToFolder))] private RuleAction _action = RuleAction.MoveToFolder;
    [ObservableProperty] private string? _targetFolder;
    [ObservableProperty] private bool _applyNow = true;
    [ObservableProperty] private string _error = "";

    public bool IsMoveToFolder => Action == RuleAction.MoveToFolder;

    [RelayCommand]
    private void AddCondition() => Conditions.Add(new ConditionItem());

    [RelayCommand]
    private void RemoveCondition(ConditionItem? item)
    {
        if (item is not null && Conditions.Count > 1) Conditions.Remove(item);
    }

    /// <summary>Returns null and sets Error when the rule is not valid.</summary>
    public MailRule? Validate()
    {
        Error = "";
        var conditions = Conditions.Select(c => c.ToCondition()).Where(c => c.Value.Length > 0).ToList();
        if (conditions.Count == 0) { Error = "Add at least one condition with a value."; return null; }
        foreach (var c in conditions.Where(c => c.Operator == RuleOperator.Regex))
        {
            var msg = RuleEngine.ValidateRegex(c.Value);
            if (msg is not null) { Error = $"Invalid regex \"{c.Value}\": {msg}"; return null; }
        }
        if (Action == RuleAction.MoveToFolder && string.IsNullOrWhiteSpace(TargetFolder)) { Error = "Choose a folder to move to."; return null; }

        var name = string.IsNullOrWhiteSpace(Name) ? AutoName(conditions) : Name.Trim();
        return new MailRule
        {
            Id = _id, AccountId = _accountId, Name = name, Enabled = Enabled, SortOrder = _sortOrder,
            Conditions = conditions, Action = Action,
            TargetFolder = Action == RuleAction.MoveToFolder ? TargetFolder : null,
            CreatedUtcMs = _createdUtcMs, HitCount = _hitCount,
        };
    }

    private string AutoName(List<RuleCondition> conditions)
    {
        var first = conditions[0];
        var what = $"{MailRule.Label(first.Field)} {MailRule.Label(first.Operator)} {first.Value}";
        return Action switch
        {
            RuleAction.MoveToFolder => $"{what} → {Folders.FirstOrDefault(f => f.FullName == TargetFolder)?.DisplayName ?? TargetFolder}",
            RuleAction.MoveToTrash => $"{what} → Trash",
            RuleAction.MarkRead => $"{what}: mark read",
            RuleAction.Flag => $"{what}: flag",
            _ => what,
        };
    }
}
