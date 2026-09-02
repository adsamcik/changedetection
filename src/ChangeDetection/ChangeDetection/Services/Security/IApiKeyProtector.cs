namespace ChangeDetection.Services.Security;

/// <summary>
/// Protects API keys before persistence and restores them for runtime use.
/// </summary>
public interface IApiKeyProtector
{
    bool IsProtected(string value);
    string Protect(string value);
    string Unprotect(string value);
}
