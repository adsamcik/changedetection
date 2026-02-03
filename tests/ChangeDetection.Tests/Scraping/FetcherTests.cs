using ChangeDetection.Core.Interfaces;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Scraping;

[Category("Unit")]
public class FetchOptionsTests
{
    [Test]
    public async Task FetchOptions_HasCorrectDefaults()
    {
        // Act
        var options = new FetchOptions();

        // Assert
        options.UseJavaScript.ShouldBeFalse();
        options.TimeoutSeconds.ShouldBe(30);
        options.UserAgent.ShouldBeNull();
        options.ProxyUrl.ShouldBeNull();
        options.WaitForSelector.ShouldBeNull();
        options.WaitAfterLoadMs.ShouldBe(0);
        options.CaptureScreenshot.ShouldBeFalse();
        options.ViewportWidth.ShouldBe(1920);
        options.ViewportHeight.ShouldBe(1080);
        options.Headers.ShouldNotBeNull();
        options.Headers.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task FetchOptions_CanSetAllProperties()
    {
        // Arrange & Act
        var options = new FetchOptions
        {
            UseJavaScript = true,
            TimeoutSeconds = 60,
            UserAgent = "TestAgent/1.0",
            ProxyUrl = "http://proxy.example.com",
            WaitForSelector = "#content",
            WaitAfterLoadMs = 1000,
            CaptureScreenshot = true,
            ViewportWidth = 1280,
            ViewportHeight = 720,
            Headers = new Dictionary<string, string> { ["X-Custom"] = "value" }
        };

        // Assert
        options.UseJavaScript.ShouldBeTrue();
        options.TimeoutSeconds.ShouldBe(60);
        options.UserAgent.ShouldBe("TestAgent/1.0");
        options.ProxyUrl.ShouldBe("http://proxy.example.com");
        options.WaitForSelector.ShouldBe("#content");
        options.WaitAfterLoadMs.ShouldBe(1000);
        options.CaptureScreenshot.ShouldBeTrue();
        options.ViewportWidth.ShouldBe(1280);
        options.ViewportHeight.ShouldBe(720);
        options.Headers["X-Custom"].ShouldBe("value");
        await Task.CompletedTask;
    }
}

public class FetchResultTests
{
    [Test]
    public async Task FetchResult_HasCorrectDefaults()
    {
        // Act
        var result = new FetchResult();

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Html.ShouldBeNull();
        result.ErrorMessage.ShouldBeNull();
        result.HttpStatusCode.ShouldBe(0);
        result.DurationMs.ShouldBe(0);
        result.Screenshot.ShouldBeNull();
        result.ResponseHeaders.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task FetchResult_CanSetSuccessResult()
    {
        // Arrange & Act
        var result = new FetchResult
        {
            IsSuccess = true,
            Html = "<html><body>Hello</body></html>",
            HttpStatusCode = 200,
            DurationMs = 150,
            ResponseHeaders = new Dictionary<string, string>
            {
                ["Content-Type"] = "text/html"
            }
        };

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Html.ShouldBe("<html><body>Hello</body></html>");
        result.HttpStatusCode.ShouldBe(200);
        result.DurationMs.ShouldBe(150);
        result.ResponseHeaders["Content-Type"].ShouldBe("text/html");
        await Task.CompletedTask;
    }

    [Test]
    public async Task FetchResult_CanSetErrorResult()
    {
        // Arrange & Act
        var result = new FetchResult
        {
            IsSuccess = false,
            ErrorMessage = "Connection timed out",
            HttpStatusCode = 0,
            DurationMs = 30000
        };

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Connection timed out");
        await Task.CompletedTask;
    }

    [Test]
    public async Task FetchResult_CanIncludeScreenshot()
    {
        // Arrange
        var screenshotData = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header

        // Act
        var result = new FetchResult
        {
            IsSuccess = true,
            Html = "<html></html>",
            Screenshot = screenshotData
        };

        // Assert
        result.Screenshot.ShouldNotBeNull();
        result.Screenshot.Length.ShouldBe(4);
        await Task.CompletedTask;
    }
}
