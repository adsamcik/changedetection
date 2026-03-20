using System.Net;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.BlockExecution;

[Category("Unit")]
public class RuntimeSandboxTests : TestBase
{
    [Test]
    public async Task ExecutionBudget_RecordHttpRequest_ThrowsWhenLimitExceeded()
    {
        var budget = new ExecutionBudget { MaxHttpRequests = 1 };

        budget.RecordHttpRequest();

        var exception = Should.Throw<ResourceExhaustedException>(() => budget.RecordHttpRequest());
        exception.Message.ShouldContain("http_requests");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NamespacedStateStore_PrefixesWatchIdsAndRoundTripsWithinNamespace()
    {
        var innerStore = new RecordingStateStore();
        var budget = new ExecutionBudget();
        var sut = new NamespacedStateStore(innerStore, "pipe-123", budget);
        var output = CreateJsonElement(new { status = "ok" });

        await sut.SaveOutputAsync("watch-1", "block-1", output);
        var result = await sut.GetPreviousOutputAsync("watch-1", "block-1");

        innerStore.LastSavedWatchId.ShouldBe("pipeline:pipe-123:watch-1");
        result.ShouldNotBeNull();
        result.Value.GetProperty("status").GetString().ShouldBe("ok");
    }

    [Test]
    public async Task NamespacedStateStore_RejectsReservedNamespaceInjection()
    {
        var innerStore = new RecordingStateStore();
        var budget = new ExecutionBudget();
        var sut = new NamespacedStateStore(innerStore, "pipe-123", budget);
        var output = CreateJsonElement(new { status = "ok" });

        var exception = await Should.ThrowAsync<SecurityViolationException>(() =>
            sut.SaveOutputAsync("pipeline:other:watch-1", "block-1", output));

        exception.Message.ShouldContain("reserved namespace");
    }

    [Test]
    public async Task NamespacedStateStore_ThrowsWhenStateBudgetExceeded()
    {
        var innerStore = new RecordingStateStore();
        var budget = new ExecutionBudget { MaxStateSizeBytes = 8 };
        var sut = new NamespacedStateStore(innerStore, "pipe-123", budget);
        var output = CreateJsonElement(new { message = "too large" });

        var exception = await Should.ThrowAsync<ResourceExhaustedException>(() =>
            sut.SaveOutputAsync("watch-1", "block-1", output));

        exception.Message.ShouldContain("state_size");
    }

    [Test]
    public async Task PinnedHttpClient_FollowsPinnedRedirectsAndForcesIdentityEncoding()
    {
        var handler = new SequenceHttpMessageHandler((request, attempt) =>
        {
            if (attempt == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found)
                {
                    RequestMessage = request
                };
                redirect.Headers.Location = new Uri("/final", UriKind.Relative);
                redirect.Content = new StringContent("redirect");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("sandbox-ok", Encoding.UTF8, "text/plain")
            };
        });

        var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(client);

        var sut = new PinnedHttpClient(
            httpClientFactory,
            new DomainPinValidator(CreateLogger<DomainPinValidator>()),
            CreateLogger<PinnedHttpClient>());

        var budget = new ExecutionBudget();
        using var response = await sut.SendAsync(
            "https://example.com/start",
            DomainPin.FromUserUrl("https://example.com"),
            budget);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Headers.AcceptEncoding.Any(h => h.Value == "identity").ShouldBeTrue();
        handler.Requests[1].RequestUri.ShouldBe(new Uri("https://example.com/final"));
        budget.HttpRequestCount.ShouldBe(2);
        (await response.Content.ReadAsStringAsync()).ShouldBe("sandbox-ok");
    }

    [Test]
    public async Task PinnedHttpClient_BlocksRedirectOutsidePinnedDomain()
    {
        var handler = new SequenceHttpMessageHandler((request, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found)
            {
                RequestMessage = request,
                Content = new StringContent("redirect")
            };
            redirect.Headers.Location = new Uri("https://evil.example.net/final", UriKind.Absolute);
            return redirect;
        });

        var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(client);

        var sut = new PinnedHttpClient(
            httpClientFactory,
            new DomainPinValidator(CreateLogger<DomainPinValidator>()),
            CreateLogger<PinnedHttpClient>());

        var exception = await Should.ThrowAsync<SecurityViolationException>(() =>
            sut.SendAsync(
                "https://example.com/start",
                DomainPin.FromUserUrl("https://example.com"),
                new ExecutionBudget()));

        exception.Message.ShouldContain("Domain");
    }

    private static JsonElement CreateJsonElement(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    private sealed class RecordingStateStore : IBlockStateStore
    {
        private readonly Dictionary<(string WatchId, string BlockId, string? InputHash, string? PipelineHash), JsonElement> _stored = [];

        public string? LastSavedWatchId { get; private set; }

        public Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default)
        {
            var match = _stored
                .Where(entry => entry.Key.WatchId == watchId && entry.Key.BlockId == blockInstanceId)
                .Select(entry => (JsonElement?)entry.Value.Clone())
                .LastOrDefault();

            return Task.FromResult(match);
        }

        public Task<JsonElement?> GetCachedOutputAsync(
            string watchId,
            string blockInstanceId,
            string inputHash,
            string pipelineHash,
            CancellationToken ct = default)
        {
            var key = (watchId, blockInstanceId, inputHash, pipelineHash);
            var value = _stored.TryGetValue(key, out var stored)
                ? (JsonElement?)stored.Clone()
                : null;
            return Task.FromResult(value);
        }

        public Task SaveOutputAsync(
            string watchId,
            string blockInstanceId,
            JsonElement output,
            string? inputHash = null,
            string? pipelineHash = null,
            CancellationToken ct = default)
        {
            LastSavedWatchId = watchId;
            _stored[(watchId, blockInstanceId, inputHash, pipelineHash)] = output.Clone();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(
            string watchId,
            string blockInstanceId,
            int maxResults = 10,
            CancellationToken ct = default)
        {
            IReadOnlyList<BlockExecutionSnapshot> history = _stored
                .Where(entry => entry.Key.WatchId == watchId && entry.Key.BlockId == blockInstanceId)
                .Take(maxResults)
                .Select(entry => new BlockExecutionSnapshot
                {
                    WatchId = entry.Key.WatchId,
                    BlockInstanceId = entry.Key.BlockId,
                    Timestamp = DateTime.UtcNow,
                    Output = entry.Value.Clone()
                })
                .ToList();

            return Task.FromResult(history);
        }
    }

    private sealed class SequenceHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private int _attempt;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var attempt = Interlocked.Increment(ref _attempt);
            return Task.FromResult(responder(request, attempt));
        }
    }
}
