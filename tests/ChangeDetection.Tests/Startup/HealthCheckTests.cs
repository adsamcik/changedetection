using ChangeDetection.Services.Persistence;
using ChangeDetection.Services.Startup;
using LiteDB;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Startup;

[Category("Unit")]
public class HealthCheckTests : TestBase
{
    private static (string dbPath, LiteDbContext context, Action cleanup) CreateTempDb()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_hc_{Guid.NewGuid()}.db");
        var context = new LiteDbContext(dbPath);
        return (dbPath, context, () =>
        {
            try { context.Dispose(); } catch { }
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        });
    }

    [Test]
    public async Task CheckHealthAsync_WithValidDatabase_ReturnsHealthy()
    {
        var (_, dbContext, cleanup) = CreateTempDb();
        try
        {
            // Arrange
            var healthCheck = new LiteDbHealthCheck(dbContext);

            // Act
            var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

            // Assert
            result.Status.ShouldBe(HealthStatus.Healthy);
            result.Description.ShouldNotBeNull();
            result.Description.ShouldContain("collections");
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task CheckHealthAsync_WithDisposedDatabase_ReturnsUnhealthy()
    {
        // Arrange - use a mock that throws to simulate an inaccessible database
        var mockDb = Substitute.For<ILiteDatabase>();
        mockDb.GetCollectionNames().Returns(_ => throw new ObjectDisposedException("LiteDatabase"));
        var mockContext = Substitute.ForPartsOf<LiteDbContext>("Filename=:memory:");
        mockContext.Database.Returns(mockDb);

        var healthCheck = new LiteDbHealthCheck(mockContext);

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldBe("LiteDB is not responsive");
        result.Exception.ShouldNotBeNull();
    }
}
