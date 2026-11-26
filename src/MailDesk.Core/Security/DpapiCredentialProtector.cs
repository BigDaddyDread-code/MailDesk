using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MailDesk.Core.Security;

/// <summary>Windows DPAPI, current-user scope. Secrets can only be read by this Windows account on this machine.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MailDesk.v1");

    public string Protect(string plaintext)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
