using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Shouldly;
using System.Net;
using System.Reflection;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.BlockExecution;

[Category("Unit")]
public class DomainPinTests : TestBase
{
    [Test]
    public void FromUserUrl_ExtractsPrimaryDomain()
    {
        var pin = DomainPin.FromUserUrl("https://example.com/path");

        pin.PrimaryDomain.ShouldBe("example.com");
        pin.AllowedPatterns.ShouldContain("example.com");
        pin.AllowedPatterns.ShouldContain("*.example.com");
        pin.AllowedSchemes.ShouldBe(["https"]);
    }

    [Test]
    public void Validate_AllowsPinnedDomainAndSubdomains()
    {
        var pin = DomainPin.FromUserUrl("https://example.com");
        var sut = CreateSut();

        sut.Validate("https://example.com/jobs", pin).ShouldBeNull();
        sut.Validate("https://sub.example.com/jobs", pin).ShouldBeNull();
    }

    [Test]
    public void Validate_BlocksDifferentDomains()
    {
        var pin = DomainPin.FromUserUrl("https://example.com");

        var error = CreateSut().Validate("https://other-example.com/jobs", pin);

        error.ShouldNotBeNull();
        error.ShouldContain("not allowed");
    }

    [Test]
    public void Validate_WildcardPattern_DoesNotAllowSuffixSpoofing()
    {
        var pin = new DomainPin
        {
            PrimaryDomain = "example.com",
            AllowedPatterns = ["*.example.com"],
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https" }
        };

        var error = CreateSut().Validate("https://attacker.example.com.evil.com/jobs", pin);

        error.ShouldNotBeNull();
        error.ShouldContain("not allowed");
    }

    [Test]
    public void Validate_WildcardPattern_AllowsRealSubdomain()
    {
        var pin = new DomainPin
        {
            PrimaryDomain = "example.com",
            AllowedPatterns = ["*.example.com"],
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https" }
        };

        var error = CreateSut().Validate("https://sub.example.com/jobs", pin);

        error.ShouldBeNull();
    }

    [Test]
    public async Task ValidateWithDnsResolution_BlocksPrivateIps()
    {
        var pin = DomainPin.FromUserUrl("https://localhost");

        var error = await CreateSut().ValidateWithDnsResolution("https://localhost/private", pin);

        error.ShouldNotBeNull();
        error.ShouldContain("private/reserved IP");
    }

    [Test]
    public void ValidateRedirect_RejectsTargetsOutsideThePin()
    {
        var pin = DomainPin.FromUserUrl("https://example.com");
        var sut = CreateSut();

        sut.ValidateRedirect("https://jobs.example.com/next", pin, "https://example.com/start").ShouldBeNull();

        var error = sut.ValidateRedirect("https://evil.example.net/next", pin, "https://example.com/start");

        error.ShouldNotBeNull();
        error.ShouldContain("Redirect blocked");
    }

    [Test]
    public async Task IsPrivateOrReserved_Ipv6Loopback_ReturnsTrue()
    {
        var method = typeof(DomainPinValidator)
            .GetMethod("IsPrivateOrReserved", BindingFlags.NonPublic | BindingFlags.Static);

        method.ShouldNotBeNull();
        var isPrivate = (bool)method.Invoke(null, [IPAddress.Parse("::1")])!;

        isPrivate.ShouldBeTrue();
        await Task.CompletedTask;
    }

    private DomainPinValidator CreateSut() => new(CreateLogger<DomainPinValidator>());
}
