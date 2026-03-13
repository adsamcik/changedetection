using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Generates formatted alert content for tracked item notifications.
/// Produces plain text, HTML, and webhook JSON variants.
/// Domain-agnostic: content is driven by the item's display fields and dimensions.
/// </summary>
public interface IAlertContentGenerator
{
    /// <summary>
    /// Generate formatted alert content from a tracked item and its policy result.
    /// </summary>
    AlertContent Generate(TrackedItem item, AlertPolicyResult policyResult);
}

/// <summary>
/// Formatted alert content in multiple output formats.
/// </summary>
public class AlertContent
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
