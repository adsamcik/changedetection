using ChangeDetection.Services;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class StructuredDataExtractorTests
{
    private readonly StructuredDataExtractor _sut = new();

    [Test]
    public async Task ExtractJsonLd_ReturnsObjectsFromScripts()
    {
        const string html = """
            <html>
              <head>
                <script type="application/ld+json">
                { "name": "Senior Scientist", "offers": { "price": "499" } }
                </script>
                <script type="application/ld+json">
                [{ "headline": "Backup title" }]
                </script>
              </head>
            </html>
            """;

        var result = _sut.ExtractJsonLd(html);

        result.Count.ShouldBe(2);
        result[0].GetProperty("name").GetString().ShouldBe("Senior Scientist");
        result[1].GetProperty("headline").GetString().ShouldBe("Backup title");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractJsonLd_IgnoresMalformedJsonLd()
    {
        const string html = """
            <html>
              <head>
                <script type="application/ld+json">{ "name": "Valid" }</script>
                <script type="application/ld+json">{ invalid json }</script>
              </head>
            </html>
            """;

        var result = _sut.ExtractJsonLd(html);

        result.Count.ShouldBe(1);
        result[0].GetProperty("name").GetString().ShouldBe("Valid");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractJsonLd_CollectsGraphObjects()
    {
        const string html = """
            <html>
              <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@graph": [
                    { "@type": "JobPosting", "title": "Graph Title" },
                    { "@type": "Organization", "name": "Graph Org" }
                  ]
                }
                </script>
              </head>
            </html>
            """;

        var result = _sut.ExtractJsonLd(html);

        result.Count.ShouldBe(2);
        result[0].GetProperty("title").GetString().ShouldBe("Graph Title");
        result[1].GetProperty("name").GetString().ShouldBe("Graph Org");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractOpenGraph_ReturnsMetaValues()
    {
        const string html = """
            <html>
              <head>
                <meta property="og:title" content="OG Title" />
                <meta property="og:url" content="https://example.com/job" />
              </head>
            </html>
            """;

        var result = _sut.ExtractOpenGraph(html);

        result["og:title"].ShouldBe("OG Title");
        result["og:url"].ShouldBe("https://example.com/job");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractSchemaOrg_ReturnsMicrodataObjects()
    {
        const string html = """
            <div itemscope itemtype="https://schema.org/JobPosting">
              <span itemprop="title">Research Associate</span>
              <div itemprop="hiringOrganization" itemscope itemtype="https://schema.org/Organization">
                <span itemprop="name">Acme Labs</span>
              </div>
            </div>
            """;

        var result = _sut.ExtractSchemaOrg(html);

        result.Count.ShouldBe(2);
        result[0].GetProperty("title").GetString().ShouldBe("Research Associate");
        result[0].GetProperty("hiringOrganization").GetProperty("name").GetString().ShouldBe("Acme Labs");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DetectFeeds_ReturnsRssAndAtomLinks()
    {
        const string html = """
            <html>
              <head>
                <link rel="alternate" type="application/rss+xml" href="/feed.xml" />
                <link rel="alternate" type="application/atom+xml" href="/atom.xml" />
              </head>
            </html>
            """;

        var result = _sut.DetectFeeds(html);

        result.ShouldBe(["/feed.xml", "/atom.xml"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task TryExtractField_PrefersJsonLdThenOpenGraphThenSchemaOrg()
    {
        const string html = """
            <html>
              <head>
                <meta property="og:title" content="OG Title" />
                <script type="application/ld+json">
                { "name": "JSON-LD Title", "offers": { "price": "499" } }
                </script>
              </head>
              <body>
                <div itemscope itemtype="https://schema.org/Product">
                  <span itemprop="name">Microdata Title</span>
                </div>
              </body>
            </html>
            """;

        _sut.TryExtractField(html, "title").ShouldBe("JSON-LD Title");
        _sut.TryExtractField(html, "price").ShouldBe("499");
        await Task.CompletedTask;
    }
}
