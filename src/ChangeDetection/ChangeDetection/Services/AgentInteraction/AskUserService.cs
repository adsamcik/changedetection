using System.Collections.Concurrent;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Services.AgentInteraction;

public interface IAskUserService
{
    Task<UserResponse> AskAsync(AgentQuestion question, CancellationToken ct = default);
    Task<UserResponse?> AskOptionalAsync(AgentQuestion question, TimeSpan timeout, CancellationToken ct = default);
}

public interface IAgentResponseSink
{
    bool TrySubmit(UserResponse response, string? submitterConnectionId = null);
}

public interface IAgentInteractionContext
{
    string? ConnectionId { get; set; }
}

public sealed class AgentInteractionContext : IAgentInteractionContext
{
    public string? ConnectionId { get; set; }
}

public sealed class AskUserService(
    IHubContext<GroupWatchHub> hubContext,
    IAgentInteractionContext interactionContext,
    ILogger<AskUserService> logger,
    TimeSpan? questionTtl = null) : IAskUserService, IAgentResponseSink
{
    private const string PushQuestionMethodName = "PushQuestion";
    private const string TimedOutMessage = "timed out";
    private static readonly TimeSpan DefaultQuestionTtl = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _questionTtl = questionTtl is null
        ? DefaultQuestionTtl
        : questionTtl.Value > TimeSpan.Zero
            ? questionTtl.Value
            : throw new ArgumentOutOfRangeException(nameof(questionTtl), "Question TTL must be greater than zero.");

    private sealed record PendingQuestion(
        TaskCompletionSource<UserResponse> CompletionSource,
        string ConnectionId,
        DateTime CreatedAt);

    private static readonly ConcurrentDictionary<string, PendingQuestion> PendingQuestions = new(StringComparer.Ordinal);

    internal static int CleanupConnection(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return 0;

        return ExpireQuestions(
            kvp => string.Equals(kvp.Value.ConnectionId, connectionId, StringComparison.Ordinal),
            "connection disconnected");
    }

    public async Task<UserResponse> AskAsync(AgentQuestion question, CancellationToken ct = default)
        => await AskCoreAsync(question, timeout: null, ct)
            ?? throw new InvalidOperationException("Blocking question completed without a response.");

    public Task<UserResponse?> AskOptionalAsync(AgentQuestion question, TimeSpan timeout, CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");

        return AskCoreAsync(question, timeout, ct);
    }

    public bool TrySubmit(UserResponse response, string? submitterConnectionId = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        ExpireStaleQuestions(DateTime.UtcNow);

        if (!PendingQuestions.TryGetValue(response.QuestionId, out var pending))
        {
            logger.LogWarning("Received response for unknown or expired question {QuestionId}", response.QuestionId);
            return false;
        }

        if (submitterConnectionId is not null &&
            !string.Equals(pending.ConnectionId, submitterConnectionId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Connection {SubmitterConnectionId} attempted to answer question {QuestionId} owned by {OwnerConnectionId}",
                submitterConnectionId,
                response.QuestionId,
                pending.ConnectionId);
            return false;
        }

        var submitted = pending.CompletionSource.TrySetResult(response);
        if (submitted)
            PendingQuestions.TryRemove(response.QuestionId, out _);

        return submitted;
    }

    private async Task<UserResponse?> AskCoreAsync(AgentQuestion question, TimeSpan? timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(question.Message);
        ArgumentNullException.ThrowIfNull(question.Input);

        var connectionId = interactionContext.ConnectionId;
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new InvalidOperationException("No active SignalR connection is associated with the current agent interaction scope.");

        ExpireStaleQuestions(DateTime.UtcNow);

        var completionSource = new TaskCompletionSource<UserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingQuestion(completionSource, connectionId, DateTime.UtcNow);
        if (!PendingQuestions.TryAdd(question.Id, pending))
            throw new InvalidOperationException($"A pending question with id '{question.Id}' already exists.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var ttlCts = new CancellationTokenSource(_questionTtl);
        using var ttlRegistration = ttlCts.Token.Register(() => ExpireQuestion(question.Id, TimedOutMessage));
        if (timeout is { } effectiveTimeout)
            linkedCts.CancelAfter(effectiveTimeout);

        try
        {
            logger.LogInformation(
                "Pushing question {QuestionId} to connection {ConnectionId} with priority {Priority}",
                question.Id,
                connectionId,
                question.Priority);

            await hubContext.Clients.Client(connectionId)
                .SendAsync(PushQuestionMethodName, question, linkedCts.Token);

            return await pending.CompletionSource.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout is not null)
        {
            logger.LogInformation(
                "Optional question {QuestionId} timed out after {Timeout}",
                question.Id,
                timeout.Value);

            return null;
        }
        finally
        {
            PendingQuestions.TryRemove(question.Id, out _);
        }
    }

    private static int ExpireQuestions(
        Func<KeyValuePair<string, PendingQuestion>, bool> predicate,
        string reason)
    {
        var expired = 0;

        foreach (var pending in PendingQuestions.Where(predicate).ToList())
        {
            if (!ExpireQuestion(pending.Key, reason))
                continue;

            expired++;
        }

        return expired;
    }

    private void ExpireStaleQuestions(DateTime nowUtc)
    {
        var expired = ExpireQuestions(
            kvp => nowUtc - kvp.Value.CreatedAt >= _questionTtl,
            TimedOutMessage);

        if (expired > 0)
        {
            logger.LogInformation(
                "Expired {ExpiredCount} stale pending question(s) older than {QuestionTtl}",
                expired,
                _questionTtl);
        }
    }

    private static bool ExpireQuestion(string questionId, string reason)
    {
        if (!PendingQuestions.TryRemove(questionId, out var pending))
            return false;

        return pending.CompletionSource.TrySetResult(CreateTimedOutResponse(questionId, reason));
    }

    private static UserResponse CreateTimedOutResponse(string questionId, string reason)
        => new()
        {
            QuestionId = questionId,
            Skipped = true,
            TextValue = reason
        };
}
