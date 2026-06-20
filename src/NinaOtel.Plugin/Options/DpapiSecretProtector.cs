using System.Security.Cryptography;
using System.Text;

namespace NinaOtel.Plugin.Options;

internal sealed class DpapiSecretProtector : ISecretProtector
{
    public static DpapiSecretProtector Instance { get; } = new();

    private DpapiSecretProtector()
    {
    }

    public string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(secretBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public bool TryUnprotect(string protectedSecret, out string secret)
    {
        secret = string.Empty;
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return true;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedSecret);
            var secretBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            secret = Encoding.UTF8.GetString(secretBytes);
            return true;
        }
        catch
        {
            secret = string.Empty;
            return false;
        }
    }
}
