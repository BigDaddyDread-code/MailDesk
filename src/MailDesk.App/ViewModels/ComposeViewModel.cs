using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Agent;
using MailDesk.Core.Models;
using MailDesk.Core.Services;

namespace MailDesk.App.ViewModels;

/// <summary>Backs the compose window. Owns the draft state and the send/save calls.</summary>
public sealed partial class ComposeViewModel : ObservableObject
{
    private readonly IMailSender _sender;
    private readonly IDraftingService? _drafter;
    private readonly Account _account;
    private readonly string? _inReplyTo;
    private readonly List<string> _references;

    public ComposeViewModel(IMailSender sender, Account account, OutgoingMessage seed, IDraftingService? drafter = null)
    {
        _sender = sender;
        _drafter = drafter;
        _account = account;
        _to = seed.To;
        _cc = seed.Cc;
        _bcc = seed.Bcc;
        _subject = seed.Subject;
        _body = seed.TextBody;
        _inReplyTo = seed.InReplyTo;
        _references = seed.References;
        _showCcBcc = !string.IsNullOrWhiteSpace(seed.Cc) || !string.IsNullOrWhiteSpace(seed.Bcc);
        foreach (var a in seed.Attachments) Attachments.Add(a);
    }

    public string FromDisplay => $"{_account.Name} <{_account.Email}>";
    public ObservableCollection<string> Attachments { get; } = [];

    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _cc = "";
    [ObservableProperty] private string _bcc = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private bool _showCcBcc;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _error = "";

    /// <summary>Set by the window: what happened, for the main window's status bar.</summary>
    public string? Outcome { get; private set; }

    public OutgoingMessage ToMessage() => new()
    {
        To = To.Trim(), Cc = Cc.Trim(), Bcc = Bcc.Trim(), Subject = Subject.Trim(), TextBody = Body,
        Attachments = Attachments.ToList(), InReplyTo = _inReplyTo, References = _references,
    };

    public bool HasContent => !ToMessage().IsEmpty;

    [RelayCommand]
    private void ToggleCcBcc() => ShowCcBcc = !ShowCcBcc;

    // ---------------- AI drafting ----------------

    [ObservableProperty] private string _aiInstructions = "";
    [ObservableProperty] private bool _isDrafting;

    public bool CanDraft => _drafter is not null;

    /// <summary>True when the body holds a quoted original (reply or forward), so the model has something to answer.</summary>
    public bool HasQuotedOriginal => QuoteStart(Body) >= 0;

    /// <summary>Index where the quoted original begins ("On ... wrote:" or the forwarded-message header), or -1.</summary>
    private static int QuoteStart(string body) => Core.Text.ComposeBuilder.QuoteStart(body);

    /// <summary>
    /// Asks the configured model for a body. The quoted original and anything already typed go to the model as context;
    /// the result replaces the text above the quote and keeps the quote, so the thread stays readable.
    /// </summary>
    [RelayCommand]
    private async Task DraftWithAiAsync()
    {
        if (_drafter is null) return;
        var body = Body.Replace("\r\n", "\n");
        var quoteAt = QuoteStart(body);
        var quote = quoteAt >= 0 ? body[quoteAt..] : "";
        var typed = quoteAt >= 0 ? body[..quoteAt] : body;
        if (string.IsNullOrWhiteSpace(AiInstructions) && string.IsNullOrWhiteSpace(quote) && string.IsNullOrWhiteSpace(typed))
        {
            Error = "Tell the assistant what the email should say.";
            return;
        }
        Error = "";
        IsDrafting = true;
        try
        {
            var context = string.IsNullOrWhiteSpace(typed) ? quote : (typed.Trim() + "\n\n" + quote).Trim();
            var draft = await _drafter.DraftAsync(new DraftRequest
            {
                Instructions = AiInstructions, Subject = Subject, To = To, Context = context, SenderName = _account.Name,
            });
            Body = draft.Trim() + (quote.Length > 0 ? "\n\n" + quote : "\n");
            if (string.IsNullOrWhiteSpace(Subject) && !string.IsNullOrWhiteSpace(AiInstructions))
                Subject = SubjectFrom(AiInstructions);
        }
        catch (Exception ex) { Error = $"Draft failed: {ex.Message}"; }
        finally { IsDrafting = false; }
    }

    private static string SubjectFrom(string instructions)
    {
        var s = instructions.Trim().TrimEnd('.');
        return s.Length <= 60 ? char.ToUpper(s[0]) + s[1..] : "";
    }

    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (File.Exists(p) && !Attachments.Contains(p)) Attachments.Add(p);
    }

    [RelayCommand]
    private void RemoveAttachment(string? path)
    {
        if (path is not null) Attachments.Remove(path);
    }

    /// <summary>Returns true when the window should close.</summary>
    public async Task<bool> SendAsync()
    {
        if (string.IsNullOrWhiteSpace(To) && string.IsNullOrWhiteSpace(Cc) && string.IsNullOrWhiteSpace(Bcc))
        {
            Error = "Add at least one recipient.";
            return false;
        }
        return await RunAsync("Sending...", async () =>
        {
            await _sender.SendAsync(_account, ToMessage());
            Outcome = $"Sent: {(string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject)}";
        });
    }

    public async Task<bool> SaveDraftAsync()
    {
        return await RunAsync("Saving draft...", async () =>
        {
            await _sender.SaveDraftAsync(_account, ToMessage());
            Outcome = "Draft saved to the Drafts folder.";
        });
    }

    private async Task<bool> RunAsync(string what, Func<Task> work)
    {
        Error = "";
        IsBusy = true;
        try { await work(); return true; }
        catch (Exception ex) { Error = $"{what.TrimEnd('.')} failed: {ex.Message}"; return false; }
        finally { IsBusy = false; }
    }
}
