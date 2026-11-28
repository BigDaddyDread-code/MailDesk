using CommunityToolkit.Mvvm.ComponentModel;
using MailDesk.Core.Models;

namespace MailDesk.App.ViewModels;

/// <summary>
/// A row in the sidebar: a server folder, or a smart view (Priority, Waiting, Newsletters, Notifications) built from the
/// cache. Either way it carries its account so commands know where to connect. A smart view has a synthetic folder with a
/// negative id so the rest of the view model can treat both alike where it does not matter.
/// </summary>
public sealed partial class FolderItem : ObservableObject
{
    public FolderItem(Account account, MailFolder folder)
    {
        Account = account;
        Folder = folder;
        _unreadCount = folder.UnreadCount;
    }

    public FolderItem(Account account, SmartView view)
    {
        Account = account;
        View = view;
        Folder = new MailFolder
        {
            Id = -(1 + (long)view), AccountId = account.Id, FullName = $"view:{view}", DisplayName = ViewName(view), Kind = FolderKind.Other,
        };
    }

    /// <summary>A saved search. Its Account is only an anchor when the search spans every account.</summary>
    public FolderItem(Account account, SavedSearch search)
    {
        Account = account;
        Search = search;
        Folder = new MailFolder
        {
            Id = -(100 + search.Id), AccountId = account.Id, FullName = $"search:{search.Id}", DisplayName = search.Name, Kind = FolderKind.Other,
        };
    }

    public Account Account { get; }
    public MailFolder Folder { get; }
    public SmartView? View { get; }
    public SavedSearch? Search { get; }
    /// <summary>Anything that is not a server folder: a smart view or a saved search.</summary>
    public bool IsView => View is not null || Search is not null;
    public bool IsSearch => Search is not null;

    /// <summary>After a rename or a query change on a saved search.</summary>
    public void Refresh()
    {
        if (Search is null) return;
        Folder.DisplayName = Search.Name;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Description));
    }
    /// <summary>The cross-account inbox. Its Account is only an anchor; rows resolve their own account by folder.</summary>
    public bool IsUnified => View == SmartView.AllInboxes;

    public string DisplayName => Folder.DisplayName;
    public FolderKind Kind => Folder.Kind;
    public string Icon => Search is not null ? "" : View switch
    {
        SmartView.AllInboxes => "",
        SmartView.Priority => "",
        SmartView.Waiting => "",
        SmartView.Newsletters => "",
        SmartView.Notifications => "",
        _ => Kind switch
        {
            FolderKind.Inbox => "",
            FolderKind.Sent => "",
            FolderKind.Drafts => "",
            FolderKind.Trash => "",
            FolderKind.Junk => "",
            FolderKind.Archive => "",
            _ => "",
        },
    };

    public string Description => Search is not null ? Search.Query : View switch
    {
        SmartView.AllInboxes => "Every account's Inbox in one list",
        SmartView.Priority => "Inbox mail from people: newsletters and notifications filtered out",
        SmartView.Waiting => "Mail you sent that has not been answered",
        SmartView.Newsletters => "Mailing lists and marketing",
        SmartView.Notifications => "Receipts, codes, alerts and other automatic mail",
        _ => Folder.FullName,
    };

    public static string ViewName(SmartView view) => view switch
    {
        SmartView.AllInboxes => "All inboxes",
        SmartView.Priority => "Priority",
        SmartView.Waiting => "Waiting",
        SmartView.Newsletters => "Newsletters",
        SmartView.Notifications => "Notifications",
        _ => view.ToString(),
    };

    [ObservableProperty]
    private int _unreadCount;

    /// <summary>True while messages are being dragged over this folder, for the highlight.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public string UnreadText => UnreadCount > 0 ? UnreadCount.ToString() : "";
    public bool HasUnread => UnreadCount > 0;

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(UnreadText));
        OnPropertyChanged(nameof(HasUnread));
    }
}
