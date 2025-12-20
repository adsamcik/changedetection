namespace ChangeDetection.Core.Entities;

/// <summary>
/// Normalized stock availability status for products.
/// LLM classifies raw stock text into these categories.
/// </summary>
public enum StockStatus
{
    /// <summary>Unknown or unclassified stock status.</summary>
    Unknown = 0,

    /// <summary>Product is in stock and available for purchase.</summary>
    InStock = 1,

    /// <summary>Product is out of stock / unavailable.</summary>
    OutOfStock = 2,

    /// <summary>Limited stock available (low quantity warning).</summary>
    LimitedStock = 3,

    /// <summary>Available for pre-order (not yet released).</summary>
    PreOrder = 4,

    /// <summary>Product has been discontinued / ended.</summary>
    Discontinued = 5,

    /// <summary>Product is on backorder (will ship when available).</summary>
    Backorder = 6
}
