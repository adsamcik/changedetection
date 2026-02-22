namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Checks robots.txt compliance for a given URL.
/// </summary>
public interface IRobotsTxtChecker
{
    Task<RobotsTxtResult> CheckAsync(string url, CancellationToken ct = default);
}

public record RobotsTxtResult(RobotsTxtStatus Status, string? Reason = null);

public enum RobotsTxtStatus
{
    Allowed,
    Disallowed,
    Unclear
}
