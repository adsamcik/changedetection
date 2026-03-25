using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using System.Reflection;

namespace ChangeDetection.Tests.Services.Search;

[Category("Unit")]
public class CopilotSearchProviderTests : TestBase
{
    [Test]
    public async Task IsAvailable_WhenNoCopilotClient_ReturnsFalse()
    {
        var multiProvider = new MultiProviderSearchService(
            [], CreateLogger<MultiProviderSearchService>());

        var sut = new CopilotSearchProvider(
            null,
            multiProvider,
            CreateLogger<CopilotSearchProvider>());

        sut.IsAvailable.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchAsync_WhenNotConfigured_ReturnsNotSuccess()
    {
        var multiProvider = new MultiProviderSearchService(
            [], CreateLogger<MultiProviderSearchService>());

        var sut = new CopilotSearchProvider(
            null,
            multiProvider,
            CreateLogger<CopilotSearchProvider>());

        var result = await sut.SearchAsync(new SearchQuery { Query = "test" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("not configured");
    }

    // --- Parser tests via reflection (ParseResponseForResults is private static) ---

    private static List<T> InvokeParser<T>(string response)
    {
        var method = typeof(CopilotSearchProvider)
            .GetMethod("ParseResponseForResults", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [response]);
        // The return type uses a private nested record, so we check count via dynamic
        var list = (System.Collections.IList)result!;
        return list.Cast<object>()
            .Select(item =>
            {
                var urlProp = item.GetType().GetProperty("Url")!;
                return urlProp; // Just verifying structure
            })
            .ToList()
            .Count > 0
            ? Enumerable.Range(0, list.Count).Select(_ => default(T)!).ToList()
            : [];
    }

    private static int ParseAndCount(string response)
    {
        var method = typeof(CopilotSearchProvider)
            .GetMethod("ParseResponseForResults", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (System.Collections.IList)method.Invoke(null, [response])!;
        return result.Count;
    }

    [Test]
    public async Task Parser_PureJsonArray_ReturnsResults()
    {
        var json = """[{"url":"https://stepstone.de/jobs","title":"StepStone","snippet":"Jobs"}]""";
        ParseAndCount(json).ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Parser_MarkdownFencedJson_ReturnsResults()
    {
        var input = "```json\n" +
            """[{"url":"https://indeed.de","title":"Indeed","snippet":"Search"}]""" +
            "\n```";
        ParseAndCount(input).ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Parser_ProseWithJsonArray_ReturnsResults()
    {
        var input = "Here are the results I found:\n\n" +
            """[{"url":"https://linkedin.com/jobs","title":"LinkedIn","snippet":"Jobs"}]""" +
            "\n\nHope that helps!";
        ParseAndCount(input).ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Parser_ProseWithStrayBrackets_DoesNotConfuse()
    {
        // GPT flagged: prose containing "[1]" or "[see results]" could confuse naive IndexOf parser
        var input = "I found these results [1] from searching:\n\n" +
            """[{"url":"https://jobs.de","title":"Jobs","snippet":"Results"}]""" +
            "\n\nSource: [2] web search";
        ParseAndCount(input).ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Parser_EmptyResponse_ReturnsEmpty()
    {
        ParseAndCount("").ShouldBe(0);
        ParseAndCount("   ").ShouldBe(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Parser_NoParsableJson_ReturnsEmpty()
    {
        ParseAndCount("I couldn't find any results for that query.").ShouldBe(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Parser_InvalidUrls_Filtered()
    {
        var input = """[{"url":"not-a-url","title":"Bad"},{"url":"https://good.com","title":"Good","snippet":"OK"}]""";
        ParseAndCount(input).ShouldBe(1); // Only valid URL kept
        await Task.CompletedTask;
    }

    [Test]
    public async Task Parser_MultipleArrays_TakesFirst()
    {
        // Edge case: response has two JSON arrays — parser should find the correct one
        var input = """Some text [1] then [{"url":"https://real.com","title":"Real","snippet":"Yes"}] and [2] end.""";
        ParseAndCount(input).ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AwaitResultsOrTimeoutAsync_DefaultTimeoutIs60Seconds_AndTimeoutReturnsEmptyResults()
    {
        CopilotSearchProvider.DefaultSearchTimeout.ShouldBe(TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource();
        var neverCompletes = new TaskCompletionSource<List<int>>();

        var (timedOut, results) = await CopilotSearchProvider.AwaitResultsOrTimeoutAsync(
            neverCompletes.Task,
            TimeSpan.FromMilliseconds(25),
            cts.Token);

        timedOut.ShouldBeTrue();
        results.ShouldBeEmpty();
    }
}
