using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.BlockExecution;

public enum ExtractionAnomaly
{
    None,
    ZeroResults,
    SignificantDrop,
    FieldDegradation,
    ContentAnomaly
}

public enum WatchHealthState
{
    Healthy,
    Degraded,
    Failing,
    Broken
}

public record ExtractionMetrics(
    int ItemCount,
    int FieldsFilled,
    int TotalFields,
    double FillRate,
    DateTime Timestamp);

public record ExtractionBaseline(
    double MeanItemCount,
    double StdDevItemCount,
    double MeanFillRate,
    int SampleCount,
    DateTime LastUpdated);

public record WatchHealthStatus(
    WatchHealthState State,
    int ConsecutiveFailures,
    int ConsecutiveAnomalies,
    DateTime LastStateChange,
    string? LastError);

public record SupervisedRunStatus(
    int CompletedRuns,
    int RequiredRuns,
    bool IsSupervised,
    bool UserApproved,
    DateTime? ApprovedAt);

public sealed class PipelineReliabilityService(ILogger<PipelineReliabilityService> logger)
{
    private const int MetricsWindowSize = 10;
    private const int DefaultRequiredRuns = 3;
    private const double EmaAlpha = 0.3d;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static readonly string[] ContentAnomalyPatterns =
    [
        "captcha",
        "challenge",
        "verify you are human",
        "maintenance",
        "temporarily unavailable",
        "under construction",
        "sign in",
        "log in",
        "authentication required",
        "403 forbidden",
        "access denied",
        "rate limit"
    ];

    public async Task<ExtractionAnomaly> CheckExtractionAsync(
        Guid watchId,
        int itemCount,
        int fieldsFilled,
        int totalFields,
        IBlockStateStore stateStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        var metrics = CreateMetrics(itemCount, fieldsFilled, totalFields);
        var baseline = await LoadStateAsync<ExtractionBaseline>(watchId, GetBaselineKey(watchId), stateStore, ct);
        var anomaly = EvaluateExtractionAnomaly(metrics, baseline);

        if (anomaly != ExtractionAnomaly.None)
        {
            logger.LogWarning(
                "Extraction anomaly detected for watch {WatchId}: {Anomaly} (items={ItemCount}, fillRate={FillRate:F2})",
                watchId,
                anomaly,
                metrics.ItemCount,
                metrics.FillRate);
        }

        await UpdateBaselineAsync(watchId, metrics, stateStore, ct);
        return anomaly;
    }

    public ExtractionAnomaly CheckContentForAnomalies(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return ExtractionAnomaly.None;
        }

        foreach (var pattern in ContentAnomalyPatterns)
        {
            if (rawContent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return ExtractionAnomaly.ContentAnomaly;
            }
        }

        return ExtractionAnomaly.None;
    }

    public async Task<WatchHealthStatus> UpdateHealthAsync(
        Guid watchId,
        bool executionSuccess,
        ExtractionAnomaly anomaly,
        IBlockStateStore stateStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        var current = await LoadHealthStatusAsync(watchId, stateStore, ct);
        var hasFailure = !executionSuccess;
        var hasAnomaly = anomaly != ExtractionAnomaly.None;
        var lastError = hasFailure
            ? "Execution failed"
            : hasAnomaly
                ? $"Extraction anomaly: {anomaly}"
                : null;

        PersistedWatchHealthStatus updated;
        if (current.State == WatchHealthState.Broken)
        {
            updated = current with
            {
                ConsecutiveFailures = hasFailure ? current.ConsecutiveFailures + 1 : current.ConsecutiveFailures,
                ConsecutiveAnomalies = hasAnomaly ? current.ConsecutiveAnomalies + 1 : current.ConsecutiveAnomalies,
                ConsecutiveIssueRuns = hasFailure || hasAnomaly ? current.ConsecutiveIssueRuns + 1 : current.ConsecutiveIssueRuns,
                ConsecutiveCleanRuns = hasFailure || hasAnomaly ? 0 : current.ConsecutiveCleanRuns + 1,
                LastError = lastError ?? current.LastError
            };
        }
        else
        {
            var consecutiveFailures = hasFailure ? current.ConsecutiveFailures + 1 : 0;
            var consecutiveAnomalies = hasAnomaly ? current.ConsecutiveAnomalies + 1 : 0;
            var consecutiveIssueRuns = hasFailure || hasAnomaly ? current.ConsecutiveIssueRuns + 1 : 0;
            var consecutiveCleanRuns = hasFailure || hasAnomaly ? 0 : current.ConsecutiveCleanRuns + 1;

            var nextState = current.State;
            if (consecutiveCleanRuns >= 2)
            {
                nextState = WatchHealthState.Healthy;
            }
            else if (consecutiveIssueRuns >= 3)
            {
                nextState = WatchHealthState.Broken;
            }
            else if (consecutiveIssueRuns >= 2)
            {
                nextState = WatchHealthState.Failing;
            }
            else if (consecutiveIssueRuns >= 1)
            {
                nextState = WatchHealthState.Degraded;
            }

            updated = new PersistedWatchHealthStatus(
                nextState,
                consecutiveFailures,
                consecutiveAnomalies,
                consecutiveIssueRuns,
                consecutiveCleanRuns,
                nextState == current.State ? current.LastStateChange : DateTime.UtcNow,
                lastError);
        }

        await SaveStateAsync(watchId, GetHealthKey(watchId), updated, stateStore, ct);

        if (updated.State != current.State)
        {
            logger.LogWarning(
                "Watch {WatchId} health changed from {PreviousState} to {NextState}",
                watchId,
                current.State,
                updated.State);
        }

        return updated.ToPublicStatus();
    }

    public async Task<WatchHealthState> GetHealthStateAsync(
        Guid watchId,
        IBlockStateStore stateStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        var status = await LoadHealthStatusAsync(watchId, stateStore, ct);
        return status.State;
    }

    public async Task<SupervisedRunStatus> GetSupervisionStatusAsync(
        Guid watchId,
        IBlockStateStore stateStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        return await LoadSupervisionStatusAsync(watchId, stateStore, ct);
    }

    public async Task RecordRunAsync(
        Guid watchId,
        IBlockStateStore stateStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        var current = await LoadSupervisionStatusAsync(watchId, stateStore, ct);
        var nextCompletedRuns = Math.Min(current.CompletedRuns + 1, current.RequiredRuns);
        var isSupervised = !current.UserApproved && nextCompletedRuns < current.RequiredRuns;

        var updated = current with
        {
            CompletedRuns = nextCompletedRuns,
            IsSupervised = isSupervised
        };

        await SaveStateAsync(watchId, GetSupervisionKey(watchId), updated, stateStore, ct);
        logger.LogDebug(
            "Recorded supervised run for watch {WatchId}: {CompletedRuns}/{RequiredRuns}",
            watchId,
            updated.CompletedRuns,
            updated.RequiredRuns);
    }

    public async Task ApproveAsync(
        Guid watchId,
        IBlockStateStore stateStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        var current = await LoadSupervisionStatusAsync(watchId, stateStore, ct);
        var updated = current with
        {
            UserApproved = true,
            ApprovedAt = current.ApprovedAt ?? DateTime.UtcNow,
            IsSupervised = false
        };

        await SaveStateAsync(watchId, GetSupervisionKey(watchId), updated, stateStore, ct);
        logger.LogInformation("User approved supervised runs for watch {WatchId}", watchId);
    }

    private async Task UpdateBaselineAsync(
        Guid watchId,
        ExtractionMetrics metrics,
        IBlockStateStore stateStore,
        CancellationToken ct)
    {
        var metricsKey = GetMetricsKey(watchId);
        var baselineKey = GetBaselineKey(watchId);

        var history = await LoadStateAsync<List<ExtractionMetrics>>(watchId, metricsKey, stateStore, ct) ?? [];
        history.Add(metrics);

        if (history.Count > MetricsWindowSize)
        {
            history = history
                .OrderByDescending(x => x.Timestamp)
                .Take(MetricsWindowSize)
                .OrderBy(x => x.Timestamp)
                .ToList();
        }

        var existingBaseline = await LoadStateAsync<ExtractionBaseline>(watchId, baselineKey, stateStore, ct);
        var meanItemCount = existingBaseline is null
            ? metrics.ItemCount
            : ApplyEma(existingBaseline.MeanItemCount, metrics.ItemCount);
        var meanFillRate = existingBaseline is null
            ? metrics.FillRate
            : ApplyEma(existingBaseline.MeanFillRate, metrics.FillRate);
        var stdDevItemCount = CalculateStdDev(history.Select(x => (double)x.ItemCount));

        var updatedBaseline = new ExtractionBaseline(
            meanItemCount,
            stdDevItemCount,
            meanFillRate,
            history.Count,
            metrics.Timestamp);

        await SaveStateAsync(watchId, metricsKey, history, stateStore, ct);
        await SaveStateAsync(watchId, baselineKey, updatedBaseline, stateStore, ct);
    }

    private static ExtractionMetrics CreateMetrics(int itemCount, int fieldsFilled, int totalFields)
    {
        var sanitizedItemCount = Math.Max(0, itemCount);
        var sanitizedTotalFields = Math.Max(0, totalFields);
        var sanitizedFieldsFilled = sanitizedTotalFields == 0
            ? 0
            : Math.Clamp(fieldsFilled, 0, sanitizedTotalFields);
        var fillRate = sanitizedTotalFields == 0
            ? 0d
            : (double)sanitizedFieldsFilled / sanitizedTotalFields;

        return new ExtractionMetrics(
            sanitizedItemCount,
            sanitizedFieldsFilled,
            sanitizedTotalFields,
            fillRate,
            DateTime.UtcNow);
    }

    private static ExtractionAnomaly EvaluateExtractionAnomaly(
        ExtractionMetrics metrics,
        ExtractionBaseline? baseline)
    {
        if (baseline is null || baseline.SampleCount == 0)
        {
            return ExtractionAnomaly.None;
        }

        if (metrics.ItemCount == 0 && baseline.MeanItemCount > 5)
        {
            return ExtractionAnomaly.ZeroResults;
        }

        if (baseline.StdDevItemCount > 0 &&
            baseline.MeanItemCount > 10 &&
            metrics.ItemCount < baseline.MeanItemCount - (2 * baseline.StdDevItemCount))
        {
            return ExtractionAnomaly.SignificantDrop;
        }

        if (baseline.MeanFillRate > 0.7d && metrics.FillRate < 0.3d)
        {
            return ExtractionAnomaly.FieldDegradation;
        }

        return ExtractionAnomaly.None;
    }

    private async Task<PersistedWatchHealthStatus> LoadHealthStatusAsync(
        Guid watchId,
        IBlockStateStore stateStore,
        CancellationToken ct)
    {
        var key = GetHealthKey(watchId);
        var persisted = await LoadStateAsync<PersistedWatchHealthStatus>(watchId, key, stateStore, ct);
        if (persisted is not null)
        {
            return persisted;
        }

        var legacy = await LoadStateAsync<WatchHealthStatus>(watchId, key, stateStore, ct);
        if (legacy is not null)
        {
            return PersistedWatchHealthStatus.FromPublic(legacy);
        }

        return PersistedWatchHealthStatus.Default;
    }

    private async Task<SupervisedRunStatus> LoadSupervisionStatusAsync(
        Guid watchId,
        IBlockStateStore stateStore,
        CancellationToken ct)
    {
        var key = GetSupervisionKey(watchId);
        var stored = await LoadStateAsync<SupervisedRunStatus>(watchId, key, stateStore, ct);
        if (stored is not null)
        {
            var requiredRuns = stored.RequiredRuns <= 0 ? DefaultRequiredRuns : stored.RequiredRuns;
            var completedRuns = Math.Clamp(stored.CompletedRuns, 0, requiredRuns);
            var isSupervised = !stored.UserApproved && completedRuns < requiredRuns;

            return stored with
            {
                RequiredRuns = requiredRuns,
                CompletedRuns = completedRuns,
                IsSupervised = isSupervised
            };
        }

        return new SupervisedRunStatus(
            0,
            DefaultRequiredRuns,
            true,
            false,
            null);
    }

    private static async Task<T?> LoadStateAsync<T>(
        Guid watchId,
        string key,
        IBlockStateStore stateStore,
        CancellationToken ct)
    {
        if (TryGetSingleKeyMethods(
                stateStore,
                out var getMethod,
                out _,
                out _))
        {
            ArgumentNullException.ThrowIfNull(getMethod);
            var parameters = BuildGetParameters(getMethod, key, ct);
            var invocation = getMethod.Invoke(stateStore, parameters);
            var rawValue = await AwaitGetInvocationAsync(invocation);

            return rawValue switch
            {
                null => default,
                JsonElement element => DeserializeElement(element),
                string json when string.IsNullOrWhiteSpace(json) => default,
                string json => JsonSerializer.Deserialize<T>(json, SerializerOptions),
                _ => rawValue is T value ? value : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(rawValue, SerializerOptions), SerializerOptions)
            };
        }

        var stored = await stateStore.GetPreviousOutputAsync(watchId.ToString("D"), key, ct);
        return stored is null ? default : DeserializeElement(stored.Value);

        static T? DeserializeElement(JsonElement element)
            => JsonSerializer.Deserialize<T>(element.GetRawText(), SerializerOptions);
    }

    private static async Task SaveStateAsync<T>(
        Guid watchId,
        string key,
        T value,
        IBlockStateStore stateStore,
        CancellationToken ct)
    {
        if (TryGetSingleKeyMethods(
                stateStore,
                out _,
                out var setMethod,
                out var usesJsonPayload))
        {
            ArgumentNullException.ThrowIfNull(setMethod);
            var parameters = BuildSetParameters(setMethod, key, value, usesJsonPayload, ct);
            var invocation = setMethod.Invoke(stateStore, parameters);
            if (invocation is Task task)
            {
                await task;
            }

            return;
        }

        var payload = JsonSerializer.SerializeToElement(value, SerializerOptions);
        await stateStore.SaveOutputAsync(watchId.ToString("D"), key, payload, ct: ct);
    }

    private static object?[] BuildGetParameters(MethodInfo getMethod, string key, CancellationToken ct)
    {
        var parameters = getMethod.GetParameters();
        return parameters.Length switch
        {
            1 => [key],
            2 when parameters[1].ParameterType == typeof(CancellationToken) => [key, ct],
            _ => throw new InvalidOperationException($"Unsupported GetAsync signature on {getMethod.DeclaringType?.FullName}.")
        };
    }

    private static object?[] BuildSetParameters<T>(
        MethodInfo setMethod,
        string key,
        T value,
        bool usesJsonPayload,
        CancellationToken ct)
    {
        object payload = usesJsonPayload
            ? JsonSerializer.SerializeToElement(value, SerializerOptions)
            : JsonSerializer.Serialize(value, SerializerOptions);

        var parameters = setMethod.GetParameters();
        return parameters.Length switch
        {
            2 => [key, payload],
            3 when parameters[2].ParameterType == typeof(CancellationToken) => [key, payload, ct],
            _ => throw new InvalidOperationException($"Unsupported SetAsync signature on {setMethod.DeclaringType?.FullName}.")
        };
    }

    private static async Task<object?> AwaitGetInvocationAsync(object? invocation)
    {
        if (invocation is null)
        {
            return null;
        }

        if (invocation is Task task)
        {
            await task.ConfigureAwait(false);

            var taskType = task.GetType();
            if (!taskType.IsGenericType)
            {
                return null;
            }

            return taskType.GetProperty("Result")?.GetValue(task);
        }

        return invocation;
    }

    private static bool TryGetSingleKeyMethods(
        IBlockStateStore stateStore,
        out MethodInfo? getMethod,
        out MethodInfo? setMethod,
        out bool usesJsonPayload)
    {
        getMethod = stateStore.GetType().GetMethod("GetAsync", [typeof(string), typeof(CancellationToken)])
            ?? stateStore.GetType().GetMethod("GetAsync", [typeof(string)]);

        var jsonSetMethod = stateStore.GetType().GetMethod("SetAsync", [typeof(string), typeof(JsonElement), typeof(CancellationToken)])
            ?? stateStore.GetType().GetMethod("SetAsync", [typeof(string), typeof(JsonElement)]);
        var stringSetMethod = stateStore.GetType().GetMethod("SetAsync", [typeof(string), typeof(string), typeof(CancellationToken)])
            ?? stateStore.GetType().GetMethod("SetAsync", [typeof(string), typeof(string)]);

        if (getMethod is null || (jsonSetMethod is null && stringSetMethod is null))
        {
            usesJsonPayload = false;
            setMethod = null;
            return false;
        }

        setMethod = jsonSetMethod ?? stringSetMethod;
        usesJsonPayload = jsonSetMethod is not null;
        return true;
    }

    private static double ApplyEma(double currentMean, double currentValue)
        => (EmaAlpha * currentValue) + ((1 - EmaAlpha) * currentMean);

    private static double CalculateStdDev(IEnumerable<double> values)
    {
        var samples = values.ToArray();
        if (samples.Length <= 1)
        {
            return 0d;
        }

        var mean = samples.Average();
        var variance = samples.Select(x => Math.Pow(x - mean, 2)).Average();
        return Math.Sqrt(variance);
    }

    private static string GetBaselineKey(Guid watchId) => $"reliability:baseline:{watchId:D}";

    private static string GetHealthKey(Guid watchId) => $"reliability:health:{watchId:D}";

    private static string GetSupervisionKey(Guid watchId) => $"reliability:supervised:{watchId:D}";

    private static string GetMetricsKey(Guid watchId) => $"reliability:metrics:{watchId:D}";

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record PersistedWatchHealthStatus(
        WatchHealthState State,
        int ConsecutiveFailures,
        int ConsecutiveAnomalies,
        int ConsecutiveIssueRuns,
        int ConsecutiveCleanRuns,
        DateTime LastStateChange,
        string? LastError)
    {
        public static PersistedWatchHealthStatus Default { get; } = new(
            WatchHealthState.Healthy,
            0,
            0,
            0,
            0,
            DateTime.UtcNow,
            null);

        public static PersistedWatchHealthStatus FromPublic(WatchHealthStatus status) => new(
            status.State,
            status.ConsecutiveFailures,
            status.ConsecutiveAnomalies,
            Math.Max(status.ConsecutiveFailures, status.ConsecutiveAnomalies),
            0,
            status.LastStateChange,
            status.LastError);

        public WatchHealthStatus ToPublicStatus() => new(
            State,
            ConsecutiveFailures,
            ConsecutiveAnomalies,
            LastStateChange,
            LastError);
    }
}
