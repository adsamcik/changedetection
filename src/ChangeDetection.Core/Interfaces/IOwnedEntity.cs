namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Marker interface for entities that are owned by a user (tenant).
/// </summary>
public interface IOwnedEntity
{
    /// <summary>
    /// The ID of the user who owns this entity.
    /// Guid.Empty represents the default single-user mode owner.
    /// </summary>
    Guid OwnerId { get; set; }
}
