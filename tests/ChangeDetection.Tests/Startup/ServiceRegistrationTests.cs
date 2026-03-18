using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.AutoHealing;
using ChangeDetection.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ChangeDetection.Tests.Startup;

[Category("Unit")]
public class ServiceRegistrationTests : TestBase, IAsyncDisposable
{
    private WebApplicationFactory<Program> _factory = null!;

    [Before(Test)]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    [Test]
    [Arguments(typeof(IWatchService))]
    [Arguments(typeof(ICategoryService))]
    [Arguments(typeof(IDiffService))]
    [Arguments(typeof(IContentFetcher))]
    [Arguments(typeof(IContentExtractor))]
    [Arguments(typeof(IStructuredDataExtractor))]
    [Arguments(typeof(IDomCompactor))]
    [Arguments(typeof(INotificationService))]
    [Arguments(typeof(INotificationOutboxService))]
    [Arguments(typeof(INotificationTemplateEngine))]
    [Arguments(typeof(ILlmProviderChain))]
    [Arguments(typeof(ILlmLogService))]
    [Arguments(typeof(IInputProcessor))]
    [Arguments(typeof(IChangeAnalyzer))]
    [Arguments(typeof(IContentEnricher))]
    [Arguments(typeof(IDeduplicationService))]
    [Arguments(typeof(IWatchSetupPipeline))]
    [Arguments(typeof(IPipelineQueueService))]
    [Arguments(typeof(IPipelineEventService))]
    [Arguments(typeof(IObjectExtractionService))]
    [Arguments(typeof(IObjectDiffService))]
    [Arguments(typeof(IFilterEvaluationService))]
    [Arguments(typeof(IErrorResolutionService))]
    [Arguments(typeof(IAutoHealingService))]
    [Arguments(typeof(IPriceTrackingService))]
    [Arguments(typeof(IAlertThresholdEvaluator))]
    [Arguments(typeof(IFieldHistoryService))]
    [Arguments(typeof(IDatabaseBackupService))]
    [Arguments(typeof(ISessionPersistenceService))]
    [Arguments(typeof(IInputAnchorValidator))]
    [Arguments(typeof(IPlatformDetector))]
    [Arguments(typeof(IPipelineTemplateRegistry))]
    public async Task CoreService_ShouldResolveFromDI(Type serviceType)
    {
        Log("Resolving {0} from DI container", serviceType.Name);

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetService(serviceType);

        service.ShouldNotBeNull($"{serviceType.Name} should be registered in DI");
        Log("Resolved {0} as {1}", serviceType.Name, service.GetType().Name);

        await Task.CompletedTask;
    }
}
