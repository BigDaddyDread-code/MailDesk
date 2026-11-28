namespace MailDesk.Core.Models;

/// <summary>What kind of mail a message is, decided locally from headers at sync time. Stored on the message.</summary>
public enum MessageCategory
{
    /// <summary>Correspondence: a person or an organisation writing to you.</summary>
    Primary = 0,
    /// <summary>Mailing lists, marketing and other bulk sends (List-Unsubscribe / List-Id / Precedence: bulk).</summary>
    Newsletter = 1,
    /// <summary>Machine-generated: receipts, sign-in codes, build alerts, bounces, no-reply senders.</summary>
    Notification = 2,
}

/// <summary>A dynamic list built from the cache rather than a server folder. Shown in the sidebar under Views.</summary>
public enum SmartView
{
    /// <summary>Inbox mail from people: everything that is not a newsletter or a notification.</summary>
    Priority = 0,
    /// <summary>Mail you sent that nobody has answered yet.</summary>
    Waiting = 1,
    Newsletters = 2,
    Notifications = 3,
    /// <summary>Every account's Inbox in one list. Rows carry the account name.</summary>
    AllInboxes = 4,
    /// <summary>Inbox mail from senders the user nearly always moves to Trash, learned from what they did before.</summary>
    CleanUp = 5,
}
