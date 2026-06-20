namespace NinaOtel.Plugin.Options;

public interface ISecretProtector
{
    string Protect(string secret);
    bool TryUnprotect(string protectedSecret, out string secret);
}
