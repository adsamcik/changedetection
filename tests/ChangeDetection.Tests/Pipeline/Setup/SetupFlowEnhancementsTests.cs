using ChangeDetection.Services.Pipeline;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Net;

namespace ChangeDetection.Tests.Pipeline.Setup;

[Category("Unit")]
public class SetupFlowEnhancementsTests : TestBase
{
    [Test]
    public async Task TransformWorkdayUrl_CareerPageUrl_ReturnsJobsApiUrl()
    {
        const string careerUrl = "https://zealandpharma.wd3.myworkdayjobs.com/en-US/External";
        const string expectedApiUrl = "https://zealandpharma.wd3.myworkdayjobs.com/wday/cxs/zealandpharma/External/jobs";

        var transformed = SetupFlowEnhancements.TransformWorkdayUrl(careerUrl);

        transformed.ShouldBe(expectedApiUrl);
        await Task.CompletedTask;
    }

    [Test]
    public async Task TransformWorkdayUrl_ApiBaseUrl_NormalizesToJobsEndpoint()
    {
        const string apiBaseUrl = "https://zealandpharma.wd3.myworkdayjobs.com/wday/cxs/zealandpharma/External";
        const string expectedApiUrl = "https://zealandpharma.wd3.myworkdayjobs.com/wday/cxs/zealandpharma/External/jobs";

        var transformed = SetupFlowEnhancements.TransformWorkdayUrl(apiBaseUrl);

        transformed.ShouldBe(expectedApiUrl);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ClassifyContent_WorkdayCareerPage_DetectsWorkdayPlatform()
    {
        var sut = CreateSut();
        var classification = sut.ClassifyContent(
            "<html><body>Apply now</body></html>",
            "https://zealandpharma.wd3.myworkdayjobs.com/en-US/External",
            "text/html");

        classification.DetectedPlatform.ShouldBe("workday");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetPlatformTemplate_WorkdayCareerPage_UsesApiPipelineConfiguration()
    {
        const string careerUrl = "https://zealandpharma.wd3.myworkdayjobs.com/en-US/External";
        const string expectedApiUrl = "https://zealandpharma.wd3.myworkdayjobs.com/wday/cxs/zealandpharma/External/jobs";
        var sut = CreateSut(request =>
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.RequestUri!.ToString().ShouldBe(expectedApiUrl);
            request.Headers.Accept.Single().MediaType.ShouldBe("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"total":0,"jobPostings":[]}""")
            };
        });

        var template = await sut.GetPlatformTemplateAsync("workday", careerUrl);

        template.ShouldNotBeNull();
        template.Blocks.ShouldContain(block => block.Type == "HttpRequest");
        template.Blocks.ShouldNotContain(block => block.Type == "LlmExtract");

        var inputBlock = template.Blocks.Single(block => block.Id == "input-1");
        inputBlock.Config.ShouldNotBeNull();
        inputBlock.Config!.Value.GetProperty("url").GetString().ShouldBe(expectedApiUrl);

        var httpRequestBlock = template.Blocks.Single(block => block.Id == "httprequest-1");
        httpRequestBlock.Config.ShouldNotBeNull();
        httpRequestBlock.Config!.Value.GetProperty("method").GetString().ShouldBe("POST");
        httpRequestBlock.Config!.Value.GetProperty("body").GetString()
            .ShouldBe("""{"appliedFacets":{},"limit":20,"offset":0,"searchText":""}""");

        var jsonExtractBlock = template.Blocks.Single(block => block.Id == "jsonextract-1");
        var extractions = jsonExtractBlock.Config!.Value.GetProperty("extractions").EnumerateArray().ToArray();
        extractions.ShouldContain(extraction =>
            extraction.GetProperty("name").GetString() == "items" &&
            extraction.GetProperty("jsonpath").GetString() == "$.pages[*].jobPostings[*]");
        extractions.ShouldContain(extraction =>
            extraction.GetProperty("name").GetString() == "total" &&
            extraction.GetProperty("jsonpath").GetString() == "$.pages[0].total");

        var dataFilterBlock = template.Blocks.Single(block => block.Id == "datafilter-1");
        var conditions = dataFilterBlock.Config!.Value.GetProperty("conditions").EnumerateArray().ToArray();
        conditions.ShouldContain(condition =>
            condition.GetProperty("field").GetString() == "locationsText" &&
            condition.GetProperty("operator").GetString() == "contains" &&
            condition.GetProperty("value").GetString() == "Copenhagen");
        conditions.ShouldContain(condition =>
            condition.GetProperty("field").GetString() == "locationsText" &&
            condition.GetProperty("operator").GetString() == "contains" &&
            condition.GetProperty("value").GetString() == "Denmark");
        dataFilterBlock.Config!.Value.GetProperty("mode").GetString().ShouldBe("any");

        var relevanceScoreBlock = template.Blocks.Single(block => block.Id == "relevancescore-1");
        relevanceScoreBlock.Config!.Value.GetProperty("targetFields").EnumerateArray().Select(x => x.GetString())
            .ShouldBe(["title", "locationsText"], ignoreOrder: true);
        relevanceScoreBlock.Config!.Value.GetProperty("positiveKeywords").GetArrayLength().ShouldBe(4);
        relevanceScoreBlock.Config!.Value.GetProperty("negativeKeywords").GetArrayLength().ShouldBe(2);
        relevanceScoreBlock.Config!.Value.GetProperty("minScore").GetInt32().ShouldBe(0);

        var listDiffBlock = template.Blocks.Single(block => block.Id == "listdiff-1");
        listDiffBlock.Config!.Value.GetProperty("identityKey").GetString().ShouldBe("title");

        template.Connections.ShouldContain(connection =>
            connection.FromBlockId == "jsonextract-1" &&
            connection.FromPort == "data" &&
            connection.ToBlockId == "foreach-1" &&
            connection.ToPort == "items");
        template.Connections.ShouldContain(connection =>
            connection.FromBlockId == "foreach-1" &&
            connection.FromPort == "data" &&
            connection.ToBlockId == "datafilter-1" &&
            connection.ToPort == "data");
        template.Connections.ShouldContain(connection =>
            connection.FromBlockId == "datafilter-1" &&
            connection.FromPort == "filtered" &&
            connection.ToBlockId == "relevancescore-1" &&
            connection.ToPort == "data");
        template.Connections.ShouldContain(connection =>
            connection.FromBlockId == "relevancescore-1" &&
            connection.FromPort == "result" &&
            connection.ToBlockId == "listdiff-1" &&
            connection.ToPort == "data");

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetPlatformTemplate_WorkdayCareerPage_UsesProvidedRelevanceKeywords()
    {
        const string careerUrl = "https://zealandpharma.wd3.myworkdayjobs.com/en-US/External";
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"total":0,"jobPostings":[]}""")
        });
        var positiveKeywords = new[]
        {
            new RelevanceKeyword("molecular biology", 15),
            new RelevanceKeyword("PCR", 13)
        };
        var negativeKeywords = new[]
        {
            new RelevanceKeyword("director", -15),
            new RelevanceKeyword("manager", -5)
        };

        var template = await sut.GetPlatformTemplateAsync("workday", careerUrl, positiveKeywords, negativeKeywords);

        template.ShouldNotBeNull();
        var relevanceScoreBlock = template.Blocks.Single(block => block.Id == "relevancescore-1");
        var configuredPositiveKeywords = relevanceScoreBlock.Config!.Value.GetProperty("positiveKeywords").EnumerateArray().ToArray();
        var configuredNegativeKeywords = relevanceScoreBlock.Config!.Value.GetProperty("negativeKeywords").EnumerateArray().ToArray();

        configuredPositiveKeywords.Length.ShouldBe(2);
        configuredPositiveKeywords.ShouldContain(keyword =>
            keyword.GetProperty("keyword").GetString() == "molecular biology" &&
            keyword.GetProperty("weight").GetInt32() == 15);
        configuredPositiveKeywords.ShouldContain(keyword =>
            keyword.GetProperty("keyword").GetString() == "PCR" &&
            keyword.GetProperty("weight").GetInt32() == 13);

        configuredNegativeKeywords.Length.ShouldBe(2);
        configuredNegativeKeywords.ShouldContain(keyword =>
            keyword.GetProperty("keyword").GetString() == "manager" &&
            keyword.GetProperty("weight").GetInt32() == -5);

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetPlatformTemplateAsync_WorkdayCareerPage_ProbesCommonSiteIdVariants()
    {
        const string careerUrl = "https://agcbio.wd5.myworkdayjobs.com/en-US/agcbio";
        const string initialApiUrl = "https://agcbio.wd5.myworkdayjobs.com/wday/cxs/agcbio/agcbio/jobs";
        const string resolvedApiUrl = "https://agcbio.wd5.myworkdayjobs.com/wday/cxs/agcbio/agcbio_careers/jobs";
        var requestedUrls = new List<string>();

        var sut = CreateSut(request =>
        {
            requestedUrls.Add(request.RequestUri!.ToString());
            request.Method.ShouldBe(HttpMethod.Post);
            request.Content!.ReadAsStringAsync().Result.ShouldBe("""{"appliedFacets":{},"limit":20,"offset":0,"searchText":""}""");

            var statusCode = request.RequestUri!.ToString() switch
            {
                initialApiUrl => HttpStatusCode.NotFound,
                resolvedApiUrl => HttpStatusCode.OK,
                _ => HttpStatusCode.NotFound
            };

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("""{"total":0,"jobPostings":[]}""")
            };
        });

        var template = await sut.GetPlatformTemplateAsync("workday", careerUrl);

        template.ShouldNotBeNull();
        template.Blocks.Single(block => block.Id == "input-1")
            .Config!.Value.GetProperty("url").GetString()
            .ShouldBe(resolvedApiUrl);
        requestedUrls.ShouldBe([initialApiUrl, resolvedApiUrl]);
    }

    [Test]
    public async Task TransformPlatsbankenUrl_ArbetsformedlingenUrl_ReturnsJobtechApiUrl()
    {
        const string frontendUrl = "https://arbetsformedlingen.se/platsbanken/annonser?q=scientist";

        var transformed = SetupFlowEnhancements.TransformPlatsbankenUrl(frontendUrl);

        transformed.ShouldNotBeNull();
        transformed.ShouldContain("jobsearch.api.jobtechdev.se/search");
        transformed.ShouldContain("q=scientist");
        transformed.ShouldContain("offset=0");
        transformed.ShouldContain("limit=100");
        await Task.CompletedTask;
    }

    [Test]
    public async Task TransformPlatsbankenUrl_ArbetsformedlingenNoQuery_ReturnsApiUrlWithoutQ()
    {
        const string frontendUrl = "https://arbetsformedlingen.se/platsbanken/annonser";

        var transformed = SetupFlowEnhancements.TransformPlatsbankenUrl(frontendUrl);

        transformed.ShouldNotBeNull();
        transformed.ShouldContain("jobsearch.api.jobtechdev.se/search");
        transformed.ShouldNotContain("q=");
        transformed.ShouldContain("offset=0");
        transformed.ShouldContain("limit=100");
        await Task.CompletedTask;
    }

    [Test]
    public async Task TransformPlatsbankenUrl_AlreadyApiUrl_NormalizesOffsetAndLimit()
    {
        const string apiUrl = "https://jobsearch.api.jobtechdev.se/search?q=biologist";

        var transformed = SetupFlowEnhancements.TransformPlatsbankenUrl(apiUrl);

        transformed.ShouldNotBeNull();
        transformed.ShouldContain("q=biologist");
        transformed.ShouldContain("offset=0");
        transformed.ShouldContain("limit=100");
        await Task.CompletedTask;
    }

    [Test]
    public async Task TransformPlatsbankenUrl_NullOrEmpty_ReturnsNull()
    {
        SetupFlowEnhancements.TransformPlatsbankenUrl(null!).ShouldBeNull();
        SetupFlowEnhancements.TransformPlatsbankenUrl("").ShouldBeNull();
        SetupFlowEnhancements.TransformPlatsbankenUrl("   ").ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task TransformPlatsbankenUrl_UnrelatedUrl_ReturnsNull()
    {
        SetupFlowEnhancements.TransformPlatsbankenUrl("https://example.com/jobs").ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task TransformPlatsbankenUrl_UrlWithFragment_StripsFragment()
    {
        const string urlWithFragment = "https://arbetsformedlingen.se/platsbanken/annonser?q=lab#results";

        var transformed = SetupFlowEnhancements.TransformPlatsbankenUrl(urlWithFragment);

        transformed.ShouldNotBeNull();
        transformed.ShouldContain("q=lab");
        transformed.ShouldNotContain("#");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetPlatformTemplate_Platsbanken_UsesTransformedApiUrl()
    {
        const string frontendUrl = "https://arbetsformedlingen.se/platsbanken/annonser?q=scientist";
        var sut = CreateSut();

        var template = await sut.GetPlatformTemplateAsync("platsbanken", frontendUrl);

        template.ShouldNotBeNull();
        var inputBlock = template.Blocks.Single(block => block.Id == "input-1");
        inputBlock.Config!.Value.GetProperty("url").GetString()
            .ShouldContain("jobsearch.api.jobtechdev.se/search");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetPlatformTemplate_Workable_ReturnsNavigateTemplate()
    {
        const string workableUrl = "https://apply.workable.com/ascendis-pharma/";
        var sut = CreateSut();

        var template = await sut.GetPlatformTemplateAsync("workable", workableUrl);

        template.ShouldNotBeNull();
        template.Blocks.ShouldContain(block => block.Type == "Navigate");
        template.Blocks.ShouldContain(block => block.Type == "ExtractSchema");
        template.Blocks.ShouldNotContain(block => block.Type == "HttpRequest");

        var navigateBlock = template.Blocks.Single(block => block.Id == "navigate-1");
        navigateBlock.Config!.Value.GetProperty("useJavaScript").GetBoolean().ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task DetectPlatform_ArbetsformedlingenUrl_DetectsPlatsbanken()
    {
        var sut = CreateSut();
        var classification = sut.ClassifyContent(
            "<html><body>Platsbanken</body></html>",
            "https://arbetsformedlingen.se/platsbanken/annonser?q=scientist",
            "text/html");

        classification.DetectedPlatform.ShouldBe("platsbanken");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DetectPlatform_WorkableUrl_DetectsWorkable()
    {
        var sut = CreateSut();
        var classification = sut.ClassifyContent(
            "<html><body>Jobs</body></html>",
            "https://apply.workable.com/ascendis-pharma/",
            "text/html");

        classification.DetectedPlatform.ShouldBe("workable");
        await Task.CompletedTask;
    }

    private static SetupFlowEnhancements CreateSut(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        responder ??= _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"total":0,"jobPostings":[]}""")
        };

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHttpMessageHandler(responder)));
        return new SetupFlowEnhancements(NullLogger<SetupFlowEnhancements>.Instance, httpClientFactory);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
