using Microsoft.AspNetCore.DataProtection;

namespace ChangeDetection.Services.Security;

/// <summary>
/// Encrypts API keys with the ASP.NET Core Data Protection key ring.
/// </summary>
public sealed class DataProtectionApiKeyProtector(IDataProtectionProvider dataProtectionProvider)
    : IApiKeyProtector
{
    private const string Prefix = "cd-dp:v1:";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "ChangeDetection.LlmProviderApiKeys.v1");

    public bool IsProtected(string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Prefix + _protector.Protect(value);
    }

    public string Unprotect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!IsProtected(value))
        {
            throw new InvalidOperationException("The API key value is not protected.");
        }

        return _protector.Unprotect(value[Prefix.Length..]);
    }
}
