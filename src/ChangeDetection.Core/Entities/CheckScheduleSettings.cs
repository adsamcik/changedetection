namespace ChangeDetection.Core.Entities;

/// <summary>
/// Settings for controlling check scheduling behavior.
/// </summary>
public class CheckScheduleSettings
{
    /// <summary>
    /// The scheduling mode to use.
    /// </summary>
    public CheckScheduleMode Mode { get; set; } = CheckScheduleMode.Fixed;
    
    /// <summary>
    /// Base interval used for fixed mode, and as the starting point for adaptive mode.
    /// </summary>
    public TimeSpan BaseInterval { get; set; } = TimeSpan.FromHours(1);
    
    /// <summary>
    /// Minimum interval between checks (adaptive mode only).
    /// Even rapidly changing resources won't be checked more frequently than this.
    /// Default: 1 hour.
    /// </summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromHours(1);
    
    /// <summary>
    /// Maximum interval between checks (adaptive mode only).
    /// Even rarely changing resources will be checked at least this often.
    /// Default: 7 days.
    /// </summary>
    public TimeSpan MaxInterval { get; set; } = TimeSpan.FromDays(7);
    
    /// <summary>
    /// The multiplier for adaptive scheduling.
    /// Check interval = (average time between changes) / FrequencyMultiplier.
    /// Must be &gt;= 1. A value of 3 means we check 3x faster than the content changes.
    /// Default: 3.
    /// </summary>
    public double FrequencyMultiplier { get; set; } = 3.0;
}

/// <summary>
/// Determines how check intervals are calculated.
/// </summary>
public enum CheckScheduleMode
{
    /// <summary>
    /// Use a fixed check interval (CheckInterval property on WatchedSite).
    /// </summary>
    Fixed,
    
    /// <summary>
    /// Automatically adjust check interval based on how often content changes.
    /// Checks more frequently for frequently changing content, less often for stable content.
    /// </summary>
    Adaptive
}
