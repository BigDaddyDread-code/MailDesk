namespace MailDesk.Core.Models;

/// <summary>A configured mail account. Passwords are stored protected (see ICredentialProtector).</summary>
public sealed class Account
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string Username { get; set; } = "";
    /// <summary>Password encrypted by the platform credential protector, base64.</summary>
    public string ProtectedPassword { get; set; } = "";
    public long CreatedUtcMs { get; set; }
}
