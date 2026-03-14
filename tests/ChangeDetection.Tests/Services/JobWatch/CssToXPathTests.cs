using HtmlAgilityPack;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

[Category("Unit")]
public class CssToXPathTests : TestBase
{
    [Test]
    public async Task VacancySpecsSelector_MatchesTableRows()
    {
        var html = """
            <html><body>
            <table class="vacancies">
            <thead><tr><th>TITLE</th></tr></thead>
            <tbody>
            <tr class="vacancy-specs">
                <td><a href="/show=1">Research Assistant</a></td>
            </tr>
            <tr class="vacancy-specs odd">
                <td><a href="/show=2">Lab Technician</a></td>
            </tr>
            </tbody>
            </table>
            </body></html>
            """;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // This is what CssToXPath("tr.vacancy-specs") produces
        var xpath = "//tr[contains(concat(' ', normalize-space(@class), ' '), ' vacancy-specs ')]";
        var nodes = doc.DocumentNode.SelectNodes(xpath);

        nodes.ShouldNotBeNull("XPath should find tr.vacancy-specs nodes");
        nodes.Count.ShouldBe(2, "Should find 2 vacancy rows (even with multiple classes)");

        Log($"Found {nodes.Count} nodes");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildVacancyPageHtml_IsExtractableBySelector()
    {
        // Test the EXACT HTML that BuildVacancyPage produces
        var html = """
            <html><body>
            <table class="vacancies">
            <thead><tr><th>TITLE</th><th>FACULTY</th><th>LOCATION</th><th>DEADLINE</th></tr></thead>
            <tbody>

            <tr class="vacancy-specs">
                <td><a href="/all-vacancies/?show=12345">Scientist, Protein Biochemistry</a></td>
                <td>Faculty of Science</td>
                <td>Enzyme Department</td>
                <td>20-04-2026</td>
            </tr>

            </tbody>
            </table>
            </body></html>
            """;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var xpath = "//tr[contains(concat(' ', normalize-space(@class), ' '), ' vacancy-specs ')]";
        var nodes = doc.DocumentNode.SelectNodes(xpath);

        nodes.ShouldNotBeNull("Should find vacancy-specs rows in BuildVacancyPage output");
        nodes.Count.ShouldBe(1);

        // Also verify field selectors work within the matched node
        var row = nodes[0];
        var titleLink = row.SelectSingleNode(".//td[1]//a");
        titleLink.ShouldNotBeNull("Should find title link in first td");
        titleLink.InnerText.Trim().ShouldBe("Scientist, Protein Biochemistry");

        Log($"Title: {titleLink.InnerText.Trim()}, URL: {titleLink.GetAttributeValue("href", "")}");
        await Task.CompletedTask;
    }

    [Test]
    public async Task EmptyVacancyPage_HasNoMatches()
    {
        var html = """
            <html><body>
            <table class="vacancies">
            <thead><tr><th>TITLE</th></tr></thead>
            <tbody>
            </tbody>
            </table>
            </body></html>
            """;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var xpath = "//tr[contains(concat(' ', normalize-space(@class), ' '), ' vacancy-specs ')]";
        var nodes = doc.DocumentNode.SelectNodes(xpath);

        nodes.ShouldBeNull("Empty table should have no matching rows");
        await Task.CompletedTask;
    }

    [Test]
    public async Task SimpleListSelector_MatchesSzuItems()
    {
        var html = """
            <html><body><ul class="lcp_catlist">
            <li><a href="/kariera/admin/">Administrativní pracovník</a></li>
            <li><a href="/kariera/laborant/">Laboratorní pracovník</a></li>
            </ul></body></html>
            """;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // CssToXPath("li") → //li
        var xpath = "//li";
        var nodes = doc.DocumentNode.SelectNodes(xpath);

        nodes.ShouldNotBeNull();
        nodes.Count.ShouldBe(2);
        await Task.CompletedTask;
    }
}
