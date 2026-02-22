namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Device emulation profiles for content fetching.
/// Each profile defines viewport, user agent, touch support, and device scale factor.
/// </summary>
public enum DeviceProfile
{
    Desktop,
    MobilePortrait,
    MobileLandscape,
    Tablet
}

/// <summary>
/// Resolved device settings from a <see cref="DeviceProfile"/>.
/// </summary>
public record DeviceProfileSettings(
    int ViewportWidth,
    int ViewportHeight,
    string UserAgent,
    bool IsMobile,
    bool HasTouch,
    float DeviceScaleFactor)
{
    private static readonly DeviceProfileSettings DesktopSettings = new(
        1920, 1080,
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        false, false, 1.0f);

    private static readonly DeviceProfileSettings MobilePortraitSettings = new(
        390, 844,
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        true, true, 3.0f);

    private static readonly DeviceProfileSettings MobileLandscapeSettings = new(
        844, 390,
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        true, true, 3.0f);

    private static readonly DeviceProfileSettings TabletSettings = new(
        820, 1180,
        "Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        true, true, 2.0f);

    /// <summary>
    /// Resolves a <see cref="DeviceProfile"/> to concrete device settings.
    /// </summary>
    public static DeviceProfileSettings FromProfile(DeviceProfile profile) => profile switch
    {
        DeviceProfile.MobilePortrait => MobilePortraitSettings,
        DeviceProfile.MobileLandscape => MobileLandscapeSettings,
        DeviceProfile.Tablet => TabletSettings,
        _ => DesktopSettings
    };
}
