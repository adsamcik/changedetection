namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Defines the data types that flow between pipeline block ports.
/// Used for compile-time validation of block connections.
/// </summary>
public enum PortType
{
    HtmlContent,
    PlainText,
    ExtractedObjects,
    BooleanSignal,
    NumericValue,
    DiffResult,
    Notification,
    PageReference,
    Url,
    Configuration
}
