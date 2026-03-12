using System.Text.Json.Serialization;

namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// A structured job listing extracted from a change event by the LLM scorer.
/// Deserialized from <see cref="ChangeListItemDto.ExtractedEntitiesJson"/>
/// or <see cref="ChangeDetailDto.ExtractedEntitiesJson"/> on the client.
/// </summary>
public class ExtractedJobListingDto
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("company")]
    public string? Company { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    /// <summary>
    /// Deadline in ISO yyyy-MM-dd when parseable, otherwise the original string from the page.
    /// </summary>
    [JsonPropertyName("deadline")]
    public string? Deadline { get; set; }

    /// <summary>
    /// Education level mentioned (e.g. "PhD", "MSc", "BSc").
    /// </summary>
    [JsonPropertyName("education_required")]
    public string? EducationRequired { get; set; }

    /// <summary>
    /// Key skills or techniques mentioned in the listing.
    /// </summary>
    [JsonPropertyName("key_skills")]
    public List<string> KeySkills { get; set; } = [];

    /// <summary>
    /// Absolute or relative URL to the full listing, if found in the change content.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// LLM verdict on how well this listing matches the candidate profile.
    /// Examples: "PASS - strong fit", "REVIEW - partial fit", "SKIP - missing core requirement".
    /// </summary>
    [JsonPropertyName("match_assessment")]
    public string? MatchAssessment { get; set; }
}
