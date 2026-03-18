namespace ChangeDetection.Core.Pipeline;

using System.Text.RegularExpressions;

/// <summary>
/// Provides regex operations with a timeout to prevent ReDoS attacks.
/// </summary>
public static class SafeRegex
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Attempts a regex match with a timeout. Returns the match or null on timeout/error.
    /// </summary>
    public static Match? TryMatch(string input, string pattern, RegexOptions options = RegexOptions.None, TimeSpan? timeout = null)
    {
        try
        {
            var match = Regex.Match(input, pattern, options, timeout ?? DefaultTimeout);
            return match.Success ? match : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts Regex.IsMatch with a timeout. Returns false on timeout/error.
    /// </summary>
    public static bool TryIsMatch(string input, string pattern, RegexOptions options = RegexOptions.None, TimeSpan? timeout = null)
    {
        try
        {
            return Regex.IsMatch(input, pattern, options, timeout ?? DefaultTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts Regex.Replace using a precompiled regex. Returns null on timeout.
    /// </summary>
    public static string? TryReplace(string input, Regex compiledRegex, string replacement)
    {
        try
        {
            return compiledRegex.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
