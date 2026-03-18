using System.Net;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Advanced;

/// <summary>
/// Validates extracted job/application links by following them and checking HTTP status,
/// redirect behavior, and page body death signals.
/// </summary>
public class LinkValidateBlock : IPipelineBlock
{
    private const string NoRedirectClientName = "LinkValidate-NoRedirect";
    private const int MaxBodyBytes = 64 * 1024;

    public string BlockType => "LinkValidate";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return BlockResult.Failed("LinkValidate block requires a 'data' input.");

        var (urlFields, language, followRedirects) = ReadConfig(context);
        if (urlFields.Count == 0)
            return BlockResult.Failed("LinkValidate block requires 'urlFields' in config.");

        var urlValidator = context.Services.GetRequiredService<IUrlValidator>();
        var httpClientFactory = context.Services.GetRequiredService<IHttpClientFactory>();

        try
        {
            if (dataElement.ValueKind == JsonValueKind.Array)
            {
                var validatedItems = new List<JsonElement>();
                var anyDeadLinks = false;

                foreach (var item in dataElement.EnumerateArray())
                {
                    var validatedItem = await ValidateObjectAsync(
                        item,
                        urlFields,
                        language,
                        followRedirects,
                        urlValidator,
                        httpClientFactory,
                        context);

                    if (validatedItem.TryGetProperty("hasDeadLinks", out var hasDeadLinksElement) &&
                        hasDeadLinksElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                        hasDeadLinksElement.GetBoolean())
                    {
                        anyDeadLinks = true;
                    }

                    validatedItems.Add(validatedItem);
                }

                return BlockResult.Succeeded(JsonSerializer.SerializeToElement(new
                {
                    items = validatedItems,
                    hasDeadLinks = anyDeadLinks
                }));
            }

            if (dataElement.ValueKind != JsonValueKind.Object)
                return BlockResult.Failed($"LinkValidate expects array or object, got {dataElement.ValueKind}.");

            var validatedObject = await ValidateObjectAsync(
                dataElement,
                urlFields,
                language,
                followRedirects,
                urlValidator,
                httpClientFactory,
                context);

            return BlockResult.Succeeded(validatedObject);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BlockResult.Failed($"LinkValidate failed: {ex.Message}");
        }
    }

    private static async Task<JsonElement> ValidateObjectAsync(
        JsonElement source,
        IReadOnlyList<string> urlFields,
        string? language,
        bool followRedirects,
        IUrlValidator urlValidator,
        IHttpClientFactory httpClientFactory,
        BlockContext context)
    {
        var output = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.EnumerateObject())
            output[property.Name] = property.Value.Clone();

        var anyDeadLinks = false;

        foreach (var field in urlFields)
        {
            if (!TryGetUrl(source, field, out var url))
                continue;

            var validation = await ValidateUrlAsync(
                field,
                url!,
                language,
                followRedirects,
                urlValidator,
                httpClientFactory,
                context);

            output[$"{field}_valid"] = JsonSerializer.SerializeToElement(validation.IsValid);
            output[$"{field}_status"] = JsonSerializer.SerializeToElement(validation.Status);

            if (!validation.IsValid)
                anyDeadLinks = true;
        }

        output["hasDeadLinks"] = JsonSerializer.SerializeToElement(anyDeadLinks);
        return JsonSerializer.SerializeToElement(output);
    }

    private static async Task<LinkValidationResult> ValidateUrlAsync(
        string field,
        string url,
        string? language,
        bool followRedirects,
        IUrlValidator urlValidator,
        IHttpClientFactory httpClientFactory,
        BlockContext context)
    {
        var validationError = urlValidator.Validate(url);
        if (validationError is not null)
        {
            context.Logger.LogWarning("LinkValidate blocked {Field} URL {Url}: {Reason}", field, url, validationError);
            return new LinkValidationResult(false, "dead_link");
        }

        try
        {
            var client = CreateClient(httpClientFactory, followRedirects);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                context.CancellationToken);

            var responseBody = await ReadBodyPreviewAsync(response.Content, context.CancellationToken);
            var originalUri = new Uri(url, UriKind.Absolute);
            var finalUri = GetFinalUri(response, originalUri);

            var status = DetermineStatus(response, responseBody, originalUri, finalUri, language);
            context.Logger.LogInformation(
                "LinkValidate validated {Field} URL {Url} -> {Status} (HTTP {StatusCode}, final: {FinalUrl})",
                field,
                url,
                status,
                (int)response.StatusCode,
                finalUri);

            return new LinkValidationResult(status == "live", status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "LinkValidate request failed for {Field} URL {Url}", field, url);
            return new LinkValidationResult(false, "dead_link");
        }
    }

    private static HttpClient CreateClient(IHttpClientFactory httpClientFactory, bool followRedirects) =>
        httpClientFactory.CreateClient(followRedirects ? string.Empty : NoRedirectClientName);

    private static async Task<string> ReadBodyPreviewAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream(capacity: MaxBodyBytes);
        var buffer = new byte[4096];
        var remaining = MaxBodyBytes;

        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
                break;

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        var encoding = TryGetEncoding(content.Headers.ContentType?.CharSet) ?? Encoding.UTF8;
        return encoding.GetString(memory.ToArray());
    }

    private static Encoding? TryGetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return null;

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string DetermineStatus(
        HttpResponseMessage response,
        string responseBody,
        Uri originalUri,
        Uri finalUri,
        string? language)
    {
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone ||
            (int)response.StatusCode >= 500)
        {
            return "dead_link";
        }

        if (DeathSignalLibrary.ContainsDeathSignal(responseBody, language))
            return "dead_listing";

        if (IsSuspiciousRedirect(originalUri, finalUri))
            return "redirect";

        return "live";
    }

    private static Uri GetFinalUri(HttpResponseMessage response, Uri originalUri)
    {
        if (response.RequestMessage?.RequestUri is Uri requestUri)
            return requestUri;

        if (response.Headers.Location is Uri locationUri)
            return locationUri.IsAbsoluteUri ? locationUri : new Uri(originalUri, locationUri);

        return originalUri;
    }

    private static bool IsSuspiciousRedirect(Uri originalUri, Uri finalUri)
    {
        if (string.Equals(originalUri.Host, finalUri.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedPath = finalUri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return normalizedPath is "" or "/careers" or "/jobs" or "/login" or "/signin" or "/auth" or "/vacancies";
    }

    private static bool TryGetUrl(JsonElement source, string field, out string? url)
    {
        url = null;

        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(field, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = element.GetString();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out _))
            return false;

        url = candidate;
        return true;
    }

    private static (IReadOnlyList<string> urlFields, string? language, bool followRedirects) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return ([], null, true);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return ([], null, true);

        List<string> urlFields = [];
        string? language = null;
        var followRedirects = true;

        if (config.TryGetProperty("urlFields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var fieldElement in fieldsElement.EnumerateArray())
            {
                if (fieldElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(fieldElement.GetString()))
                {
                    urlFields.Add(fieldElement.GetString()!);
                }
            }
        }

        if (config.TryGetProperty("language", out var languageElement) && languageElement.ValueKind == JsonValueKind.String)
            language = languageElement.GetString();

        if (config.TryGetProperty("followRedirects", out var followRedirectsElement) &&
            followRedirectsElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            followRedirects = followRedirectsElement.GetBoolean();
        }

        return (urlFields, language, followRedirects);
    }

    private sealed record LinkValidationResult(bool IsValid, string Status);
}
