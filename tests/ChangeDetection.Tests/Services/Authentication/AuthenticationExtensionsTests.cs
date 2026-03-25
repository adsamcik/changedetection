using ChangeDetection.Core.Entities;
using ChangeDetection.Services.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.Authentication;

[Category("Unit")]
public class AuthenticationExtensionsTests
{
    [Test]
    public async Task AddChangeDetectionAuthentication_TrustAllProxiesConfig_IsIgnored()
    {
        var staleConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AuthenticationSettings.SectionName}:TrustAllProxies"] = "true"
            })
            .Build();
        var baselineConfiguration = new ConfigurationBuilder().Build();

        var staleServices = new ServiceCollection();
        staleServices.AddLogging();
        staleServices.AddChangeDetectionAuthentication(staleConfiguration);

        var baselineServices = new ServiceCollection();
        baselineServices.AddLogging();
        baselineServices.AddChangeDetectionAuthentication(baselineConfiguration);

        using var staleProvider = staleServices.BuildServiceProvider();
        using var baselineProvider = baselineServices.BuildServiceProvider();

        var staleOptions = staleProvider.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;
        var baselineOptions = baselineProvider.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;

        typeof(AuthenticationSettings).GetProperty("TrustAllProxies").ShouldBeNull();
        staleOptions.ForwardedHeaders.ShouldBe(baselineOptions.ForwardedHeaders);
        staleOptions.KnownProxies.ShouldBe(baselineOptions.KnownProxies, ignoreOrder: true);
        staleOptions.KnownIPNetworks.ShouldBe(baselineOptions.KnownIPNetworks, ignoreOrder: true);
        await Task.CompletedTask;
    }
}
