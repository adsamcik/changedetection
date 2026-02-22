using ChangeDetection.Services.Content;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class PiiRedactorTests
{
    private readonly PiiRedactor _sut = new();

    [Test]
    public async Task Redact_EmailAddresses_Replaces()
    {
        var result = _sut.Redact("Contact us at john@example.com or support@test.org");
        result.RedactedContent.ShouldNotContain("john@example.com");
        result.RedactedContent.ShouldNotContain("support@test.org");
        result.RedactedContent.ShouldContain("[REDACTED-EMAIL]");
        result.RedactionsApplied.ShouldBe(2);
        result.RedactedTypes.ShouldContain("email");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_PhoneNumbers_Replaces()
    {
        var result = _sut.Redact("Call 555-123-4567 or (800) 555-0199");
        result.RedactedContent.ShouldNotContain("555-123-4567");
        result.RedactedContent.ShouldContain("[REDACTED-PHONE]");
        result.RedactionsApplied.ShouldBeGreaterThanOrEqualTo(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_SSN_Replaces()
    {
        var result = _sut.Redact("SSN: 123-45-6789");
        result.RedactedContent.ShouldNotContain("123-45-6789");
        result.RedactedContent.ShouldContain("[REDACTED-SSN]");
        result.RedactionsApplied.ShouldBeGreaterThanOrEqualTo(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_CreditCard_Replaces()
    {
        var result = _sut.Redact("Card: 4111-1111-1111-1111");
        result.RedactedContent.ShouldNotContain("4111-1111-1111-1111");
        result.RedactedContent.ShouldContain("[REDACTED-CC]");
        result.RedactionsApplied.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_IPv4Address_Replaces()
    {
        var result = _sut.Redact("Server IP: 192.168.1.100");
        result.RedactedContent.ShouldNotContain("192.168.1.100");
        result.RedactedContent.ShouldContain("[REDACTED-IP]");
        result.RedactionsApplied.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_NoPII_ReturnsUnchanged()
    {
        var input = "The quick brown fox jumps over the lazy dog. Price: $29.99";
        var result = _sut.Redact(input);
        result.RedactedContent.ShouldBe(input);
        result.RedactionsApplied.ShouldBe(0);
        result.RedactedTypes.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_MultiplePIITypes_RedactsAll()
    {
        var input = "Email: test@example.com, Phone: 555-123-4567, IP: 10.0.0.1";
        var result = _sut.Redact(input);
        result.RedactedContent.ShouldNotContain("test@example.com");
        result.RedactedContent.ShouldNotContain("10.0.0.1");
        result.RedactedTypes.Count.ShouldBeGreaterThanOrEqualTo(2);
        result.RedactionsApplied.ShouldBeGreaterThanOrEqualTo(3);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_EmptyString_ReturnsEmpty()
    {
        var result = _sut.Redact("");
        result.RedactedContent.ShouldBeEmpty();
        result.RedactionsApplied.ShouldBe(0);
        await Task.CompletedTask;
    }
}
