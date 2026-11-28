using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MailDesk.Core.Commands;

namespace MailDesk.App.ViewModels;

/// <summary>Type-to-filter over the palette commands. The window moves the selection and reports what was chosen.</summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<PaletteCommand> _all;
    private readonly Func<string, PaletteCommand?>? _fallback;

    public PaletteViewModel(IReadOnlyList<PaletteCommand> commands, Func<string, PaletteCommand?>? fallback)
    {
        _all = commands;
        _fallback = fallback;
        Refilter();
    }

    public ObservableCollection<PaletteCommand> Results { get; } = [];
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteCommand? _selected;
    public bool IsEmpty => Results.Count == 0;

    partial void OnQueryChanged(string value) => Refilter();

    private void Refilter()
    {
        Results.Clear();
        foreach (var c in CommandPalette.Filter(Query, _all, _fallback)) Results.Add(c);
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        i = Math.Clamp(i + delta, 0, Results.Count - 1);
        Selected = Results[i];
    }
}
