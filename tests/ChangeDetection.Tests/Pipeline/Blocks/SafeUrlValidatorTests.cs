using ChangeDetection.Services.BlockExecution;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks;

[Category("Unit")]
public class SafeUrlValidatorTests
{
    private readonly SafeUrlValidator _sut;

    public SafeUrlValidatorTests()
    {
        var logger = new FakeLogger<SafeUrlValidator>();
        _sut = new SafeUrlValidator(logger);
    }

    [Test]
    [Arguments("https://example.com")]
    [Arguments("https://www.google.com/search?q=test")]
    [Arguments("http://example.org/path")]
    public async Task Validate_ValidHttpsAndHttpUrls_ReturnsNull(string url)
    {
        _sut.Validate(url).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://93.184.216.34")]
    [Arguments("http://8.8.8.8")]
    public async Task Validate_ValidExternalIp_ReturnsNull(string url)
    {
        _sut.Validate(url).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://169.254.169.254/latest/meta-data/", "AWS metadata endpoint")]
    [Arguments("http://169.254.0.1", "link-local")]
    public async Task Validate_LinkLocalIp_ReturnsError(string url, string description)
    {
        _ = description;
        _sut.Validate(url).ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_Localhost_ReturnsError()
    {
        _sut.Validate("http://localhost:8080").ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://127.0.0.1")]
    [Arguments("http://127.0.0.1:3000/api")]
    public async Task Validate_LoopbackIp_ReturnsError(string url)
    {
        _sut.Validate(url).ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://192.168.1.1")]
    [Arguments("http://192.168.0.100:8080")]
    public async Task Validate_PrivateIp192_ReturnsError(string url)
    {
        _sut.Validate(url).ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://10.0.0.1/admin")]
    [Arguments("http://10.255.255.255")]
    public async Task Validate_PrivateIp10_ReturnsError(string url)
    {
        _sut.Validate(url).ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://172.16.0.1")]
    [Arguments("http://172.31.255.255")]
    public async Task Validate_PrivateIp172_ReturnsError(string url)
    {
        _sut.Validate(url).ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("file:///etc/passwd")]
    [Arguments("gopher://evil.com")]
    [Arguments("ftp://files.example.com")]
    public async Task Validate_NonHttpScheme_ReturnsError(string url)
    {
        var error = _sut.Validate(url);
        error.ShouldNotBeNull();
        error.ShouldContain("not allowed");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Validate_EmptyNullWhitespace_ReturnsError(string? url)
    {
        _sut.Validate(url!).ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_Ipv6Loopback_ReturnsError()
    {
        _sut.Validate("http://[::1]").ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_LocalTld_ReturnsError()
    {
        _sut.Validate("http://myserver.local/api").ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_InternalTld_ReturnsError()
    {
        _sut.Validate("http://api.internal/v1").ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_MalformedUrl_ReturnsError()
    {
        _sut.Validate("not-a-url").ShouldNotBeNull();
        await Task.CompletedTask;
    }
}
