using ChangeDetection.Services.Authentication;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.Authentication;

[Category("Unit")]
public class SsoHeaderValidatorTests
{
    // --- Sanitize ---

    [Test]
    public async Task Sanitize_WithNull_ReturnsNull()
    {
        SsoHeaderValidator.Sanitize(null).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_WithEmpty_ReturnsNull()
    {
        SsoHeaderValidator.Sanitize("").ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_WithWhitespace_ReturnsNull()
    {
        SsoHeaderValidator.Sanitize("   ").ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_WithControlCharacters_RemovesThem()
    {
        var result = SsoHeaderValidator.Sanitize("user\r\nname");
        result.ShouldBe("username");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_WithNullByte_RemovesIt()
    {
        var result = SsoHeaderValidator.Sanitize("user\0name");
        result.ShouldBe("username");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_WithTabCharacter_RemovesIt()
    {
        var result = SsoHeaderValidator.Sanitize("user\tname");
        result.ShouldBe("username");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_ExceedingMaxLength_Truncates()
    {
        var longValue = new string('a', 300);
        var result = SsoHeaderValidator.Sanitize(longValue, maxLength: 100);
        result.ShouldNotBeNull();
        result.Length.ShouldBe(100);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_AtMaxLength_NotTruncated()
    {
        var value = new string('a', 256);
        var result = SsoHeaderValidator.Sanitize(value);
        result.ShouldNotBeNull();
        result.Length.ShouldBe(256);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_ValidValue_ReturnsTrimmed()
    {
        var result = SsoHeaderValidator.Sanitize("  alice  ");
        result.ShouldBe("alice");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_OnlyControlCharacters_ReturnsNull()
    {
        SsoHeaderValidator.Sanitize("\r\n\t\0").ShouldBeNull();
        await Task.CompletedTask;
    }

    // --- ContainsControlCharacters ---

    [Test]
    public async Task ContainsControlCharacters_WithNull_ReturnsFalse()
    {
        SsoHeaderValidator.ContainsControlCharacters(null).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ContainsControlCharacters_WithCleanValue_ReturnsFalse()
    {
        SsoHeaderValidator.ContainsControlCharacters("alice").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ContainsControlCharacters_WithNewline_ReturnsTrue()
    {
        SsoHeaderValidator.ContainsControlCharacters("user\nname").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ContainsControlCharacters_WithNullByte_ReturnsTrue()
    {
        SsoHeaderValidator.ContainsControlCharacters("user\0name").ShouldBeTrue();
        await Task.CompletedTask;
    }

    // --- IsValidEmail ---

    [Test]
    public async Task IsValidEmail_WithNull_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidEmail(null).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidEmail_WithEmpty_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidEmail("").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidEmail_WithValidEmail_ReturnsTrue()
    {
        SsoHeaderValidator.IsValidEmail("alice@example.com").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidEmail_WithMissingAt_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidEmail("aliceexample.com").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidEmail_WithMissingDomain_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidEmail("alice@").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidEmail_WithSpaces_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidEmail("alice @example.com").ShouldBeFalse();
        await Task.CompletedTask;
    }

    // --- IsValidUsername ---

    [Test]
    public async Task IsValidUsername_WithNull_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidUsername(null).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidUsername_WithEmpty_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidUsername("").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidUsername_WithAlphanumeric_ReturnsTrue()
    {
        SsoHeaderValidator.IsValidUsername("alice123").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidUsername_WithAllowedSpecialChars_ReturnsTrue()
    {
        SsoHeaderValidator.IsValidUsername("alice.bob-charlie_delta@org").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidUsername_WithSpaces_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidUsername("alice bob").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidUsername_WithNewline_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidUsername("alice\nbob").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValidUsername_WithSlash_ReturnsFalse()
    {
        SsoHeaderValidator.IsValidUsername("alice/bob").ShouldBeFalse();
        await Task.CompletedTask;
    }

    // --- Injection attempts ---

    [Test]
    public async Task Sanitize_HeaderInjectionAttempt_Neutralized()
    {
        var result = SsoHeaderValidator.Sanitize("admin\r\nX-Injected: evil");
        result.ShouldBe("adminX-Injected: evil");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Sanitize_NullByteInjection_Neutralized()
    {
        var result = SsoHeaderValidator.Sanitize("admin\0hidden");
        result.ShouldBe("adminhidden");
        await Task.CompletedTask;
    }
}
