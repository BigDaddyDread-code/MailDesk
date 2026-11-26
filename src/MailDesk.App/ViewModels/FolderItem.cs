using CommunityToolkit.Mvvm.ComponentModel;
using MailDesk.Core.Models;

namespace MailDesk.App.ViewModels;

/// <summary>A folder row in the sidebar, carrying its account so commands know where to connect.</summary>
public sealed partial class FolderItem : ObservableObject
{
    public FolderItem(Account account, MailFolder folder)
    {
        Account = account;
        Folder = folder;
        _unreadCount = folder.UnreadCount;
    }

    public Account Account { get; }
    public MailFolder Folder { get; }

    public string DisplayName => Folder.DisplayName;
    public FolderKind Kind => Folder.Kind;
    public string Icon => Kind switch
    {
        FolderKind.Inbox => "",
        FolderKind.Sent => "",
        FolderKind.Drafts => "",
        FolderKind.Trash => "",
        FolderKind.Junk => "",
        FolderKind.Archive => "",
        _ => "",
    };

    [ObservableProperty]
    private int _unreadCount;

    public string UnreadText => UnreadCount > 0 ? UnreadCount.ToString() : "";
    public bool HasUnread => UnreadCount > 0;

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(UnreadText));
        OnPropertyChanged(nameof(HasUnread));
    }
}
