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

    public Account Account { get; }
    public MailFolder Folder { get; }
    public SmartView? View { get; }
    public bool IsView => View is not null;

    public string DisplayName => Folder.DisplayName;
    public FolderKind Kind => Folder.Kind;
    public string Icon => View switch
    {
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

    public string Description => View switch
    {
        SmartView.Priority => "Inbox mail from people: newsletters and notifications filtered out",
        SmartView.Waiting => "Mail you sent that has not been answered",
        SmartView.Newsletters => "Mailing lists and marketing",
        SmartView.Notifications => "Receipts, codes, alerts and other automatic mail",
        _ => Folder.FullName,
    };

    public static string ViewName(SmartView view) => view switch
    {
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
