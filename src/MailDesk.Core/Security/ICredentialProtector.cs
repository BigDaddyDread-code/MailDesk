namespace MailDesk.Core.Security;

/// <summary>Encrypts secrets at rest. The WPF app implements this with Windows DPAPI scoped to the current user.</summary>
public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedBase64);
}
