using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Dtos;

/// <summary>
/// Advanced DTO tests covering default values and edge cases.
/// </summary>
[Category("Unit")]
public class DtoAdvancedTests
{
    [Test]
    public async Task FetchSettingsDto_CustomHeaders_EmptyByDefault()
    {
        // Act
        var dto = new FetchSettingsDto();

        // Assert
        dto.CustomHeaders.ShouldNotBeNull();
        dto.CustomHeaders.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmProviderDto_HasCorrectDefaults()
    {
        // Act
        var dto = new LlmProviderDto();

        // Assert
        dto.Id.ShouldBe("");
        dto.ProviderType.ShouldBe("OpenAI");
        dto.ModelId.ShouldBeNull();
        dto.IsHealthy.ShouldBeFalse();
        dto.Priority.ShouldBe(0);
        await Task.CompletedTask;
    }

}
