using ChangeDetection.Services.GroupWatch;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.GroupWatch;

[Category("Unit")]
public class OutreachSignalDetectorTests
{
    private readonly OutreachSignalDetector _sut = new();

    // --- Positive detection tests ---

    [Test]
    public void Analyze_PageWithGeneralApplication_DetectsSignal()
    {
        var html = """
            <html><body>
            <h1>Careers at BioTech Inc</h1>
            <p>Don't see a role that fits? Submit a <a href="/apply">General Application</a> and we'll keep you in mind.</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldContain(s => s.Type == "GeneralApplication");
        result.OverallScore.ShouldBeGreaterThan(0f);
    }

    [Test]
    public void Analyze_PageWithSendCV_DetectsSignal()
    {
        var html = """
            <html><body>
            <h1>Join Our Team</h1>
            <p>Interested in working with us? Send us your CV at careers@biotech.com</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldContain(s => s.Type == "SendCV");
        result.OverallScore.ShouldBeGreaterThan(0f);
    }

    [Test]
    public void Analyze_PageWithTalentCommunity_DetectsSignal()
    {
        var html = """
            <html><body>
            <h1>Work With Us</h1>
            <p>No open positions right now? Join our talent community to hear about future openings.</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldContain(s => s.Type == "TalentCommunity");
    }

    [Test]
    public void Analyze_PageWithAlwaysHiring_DetectsSignal()
    {
        var html = """
            <html><body>
            <p>We're always looking for talented scientists to join our growing team.</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldContain(s => s.Type == "AlwaysHiring");
    }

    [Test]
    public void Analyze_PageWithNamedRecruiter_DetectsSignal()
    {
        var html = """
            <html><body>
            <p>Questions about careers? Please email our HR team at hr@company.com</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldContain(s => s.Type == "NamedRecruiter");
    }

    [Test]
    public void Analyze_GermanSpontanbewerbung_DetectsSignal()
    {
        var html = """
            <html><body>
            <h1>Karriere</h1>
            <p>Wir freuen uns auf Ihre Spontanbewerbung!</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldContain(s => s.Type == "GeneralApplication");
    }

    // --- Hard negative tests ---

    [Test]
    public void Analyze_PageWithHardNegative_NotOutreachFriendly()
    {
        var html = """
            <html><body>
            <h1>Careers</h1>
            <p>Please apply only to posted roles. We do not accept unsolicited applications.</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeFalse();
        result.Signals.ShouldBeEmpty();
        result.OverallScore.ShouldBe(0f);
    }

    [Test]
    public void Analyze_PageWithPositiveAndNegative_NegativeWins()
    {
        var html = """
            <html><body>
            <h1>Join Us</h1>
            <p>Join our talent community!</p>
            <p>We're always looking for great people.</p>
            <p>However, please apply only to posted roles. Do not send unsolicited applications.</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeFalse();
        result.Signals.ShouldBeEmpty();
        result.OverallScore.ShouldBe(0f);
    }

    // --- Empty/null content tests ---

    [Test]
    public void Analyze_NullContent_ReturnsNotOutreachFriendly()
    {
        var result = _sut.Analyze(null);

        result.IsOutreachFriendly.ShouldBeFalse();
        result.Signals.ShouldBeEmpty();
        result.OverallScore.ShouldBe(0f);
    }

    [Test]
    public void Analyze_EmptyContent_ReturnsNotOutreachFriendly()
    {
        var result = _sut.Analyze("");

        result.IsOutreachFriendly.ShouldBeFalse();
        result.Signals.ShouldBeEmpty();
        result.OverallScore.ShouldBe(0f);
    }

    [Test]
    public void Analyze_WhitespaceContent_ReturnsNotOutreachFriendly()
    {
        var result = _sut.Analyze("   \n\t  ");

        result.IsOutreachFriendly.ShouldBeFalse();
        result.Signals.ShouldBeEmpty();
    }

    // --- No signals test ---

    [Test]
    public void Analyze_PageWithNoSignals_ReturnsNotOutreachFriendly()
    {
        var html = """
            <html><body>
            <h1>Careers</h1>
            <ul>
                <li>Senior Scientist - Apply Now</li>
                <li>Lab Technician - Full Time</li>
            </ul>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeFalse();
        result.Signals.ShouldBeEmpty();
        result.OverallScore.ShouldBe(0f);
    }

    // --- Multiple signals test ---

    [Test]
    public void Analyze_PageWithMultiplePositives_ReturnsHigherScore()
    {
        var html = """
            <html><body>
            <h1>Join Our Team</h1>
            <p>Submit a <a href="/apply">general application</a>.</p>
            <p>Or join our <a href="/talent">talent community</a>.</p>
            <p>We're always looking for talented people.</p>
            </body></html>
            """;

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.Count.ShouldBeGreaterThanOrEqualTo(3);
        result.OverallScore.ShouldBeGreaterThan(3f);
    }

    // --- Evidence extraction test ---

    [Test]
    public void Analyze_DetectedSignal_HasNonEmptyEvidence()
    {
        var html = "<p>We welcome speculative applications from talented researchers.</p>";

        var result = _sut.Analyze(html);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.Evidence));
    }

    // --- Page title inclusion test ---

    [Test]
    public void Analyze_SignalInPageTitle_AlsoDetected()
    {
        var html = "<p>Here are our current openings.</p>";
        var title = "General Application - BioTech Inc";

        var result = _sut.Analyze(html, pageTitle: title);

        result.IsOutreachFriendly.ShouldBeTrue();
        result.Signals.ShouldContain(s => s.Type == "GeneralApplication");
    }

    // --- Serialization roundtrip test ---

    [Test]
    public void SerializeDeserialize_Roundtrips()
    {
        var assessment = new OutreachAssessment(
            true,
            [new OutreachSignal("GeneralApplication", "Found 'General Application' link", 0.95f)],
            8.5f);

        var json = OutreachSignalDetector.Serialize(assessment);
        var deserialized = OutreachSignalDetector.Deserialize(json);

        deserialized.ShouldNotBeNull();
        deserialized.IsOutreachFriendly.ShouldBeTrue();
        deserialized.Signals.Count.ShouldBe(1);
        deserialized.Signals[0].Type.ShouldBe("GeneralApplication");
        deserialized.OverallScore.ShouldBe(8.5f);
    }

    [Test]
    public void Deserialize_NullJson_ReturnsNull()
    {
        OutreachSignalDetector.Deserialize(null).ShouldBeNull();
    }

    [Test]
    public void Deserialize_InvalidJson_ReturnsNull()
    {
        OutreachSignalDetector.Deserialize("not json").ShouldBeNull();
    }
}
