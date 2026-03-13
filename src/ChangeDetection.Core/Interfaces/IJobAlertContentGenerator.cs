using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Generates formatted alert content for job watch notifications.
/// Produces plain text, HTML, and webhook JSON variants.
/// </summary>
public interface IJobAlertContentGenerator
{
    /// <summary>
    /// Generate formatted alert content from a tracked listing and its policy result.
    /// </summary>
    JobAlertContent Generate(TrackedListing listing, JobAlertPolicyResult policyResult);
}

/// <summary>
/// Formatted alert content in multiple output formats.
/// </summary>
public class JobAlertContent
{
    /// <summary>Plain text version for email subjects and simple notifications.</summary>
    public required string PlainText { get; init; }

    /// <summary>HTML version for rich email body.</summary>
    public required string Html { get; init; }

    /// <summary>Short one-line summary for push notifications.</summary>
    public required string Summary { get; init; }

    /// <summary>Alert level emoji indicator.</summary>
    public required string LevelEmoji { get; init; }
}
