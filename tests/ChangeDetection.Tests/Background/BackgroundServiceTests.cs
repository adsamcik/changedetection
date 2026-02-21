using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Background;

[Category("Unit")]
public class ChangeDetectionHubTests
{
    [Test]
    public async Task Hub_CanBeInstantiated()
    {
        // Arrange
        var logger = Substitute.For<ILogger<ChangeDetectionHub>>();
        var userContext = Substitute.For<IUserContext>();
        
        // Act
        var hub = new ChangeDetectionHub(logger, userContext);

        // Assert
        hub.ShouldNotBeNull();
        await Task.CompletedTask;
    }
}
