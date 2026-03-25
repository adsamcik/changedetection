namespace ChangeDetection.Core.Pipeline;

public record AgentQuestion
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string Message { get; init; }
    public required QuestionInput Input { get; init; }
    public QuestionPriority Priority { get; init; } = QuestionPriority.Optional;
    public string? Context { get; init; }
}

public enum QuestionPriority
{
    Blocking,
    Optional
}

public abstract record QuestionInput;

public record ChoiceInput(bool Multiple, List<ChoiceOption> Options) : QuestionInput;

public record TextInput(bool Multiline = false) : QuestionInput;

public record ResourceInput(List<string> AcceptedInputs) : QuestionInput;

public record ChoiceOption(string Value, string Label, bool IsDefault = false);

public record UserResponse
{
    public required string QuestionId { get; init; }
    public bool Skipped { get; init; }
    public string? TextValue { get; init; }
    public List<string>? SelectedValues { get; init; }
    public string? ResourceContent { get; init; }
    public string? ResourceType { get; init; }
}
