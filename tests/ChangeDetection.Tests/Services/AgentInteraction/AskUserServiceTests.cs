using ChangeDetection.Core.Pipeline;
using ChangeDetection.Hubs;
using ChangeDetection.Services.AgentInteraction;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.AgentInteraction;

[Category("Unit")]
public class AskUserServiceTests : TestBase
{
    [Test]
    public async Task AskAsync_PushesQuestionAndCompletesWhenResponseArrives()
    {
        var hubContext = Substitute.For<IHubContext<GroupWatchHub>>();
        var clientProxy = Substitute.For<ISingleClientProxy>();
        hubContext.Clients.Client("conn-1").Returns(clientProxy);

        var interactionContext = new AgentInteractionContext { ConnectionId = "conn-1" };
        var sut = new AskUserService(hubContext, interactionContext, CreateLogger<AskUserService>());
        var sink = (IAgentResponseSink)sut;

        var question = new AgentQuestion
        {
            Message = "Choose a portal",
            Input = new ChoiceInput(false, [new ChoiceOption("a", "A")])
        };

        var pendingTask = sut.AskAsync(question);
        await Task.Yield();

        await clientProxy.Received(1).SendCoreAsync(
            "PushQuestion",
            Arg.Is<object?[]>(args =>
                args.Length == 1 &&
                args[0] != null &&
                typeof(AgentQuestion).IsInstanceOfType(args[0]) &&
                ((AgentQuestion)args[0]!).Id == question.Id),
            Arg.Any<CancellationToken>());

        sink.TrySubmit(new UserResponse
        {
            QuestionId = question.Id,
            SelectedValues = ["a"]
        }).ShouldBeTrue();

        var response = await pendingTask;
        response.SelectedValues.ShouldBe(["a"]);
    }

    [Test]
    public async Task AskOptionalAsync_ReturnsNullAfterTimeout()
    {
        var hubContext = Substitute.For<IHubContext<GroupWatchHub>>();
        var clientProxy = Substitute.For<ISingleClientProxy>();
        hubContext.Clients.Client("conn-2").Returns(clientProxy);

        var interactionContext = new AgentInteractionContext { ConnectionId = "conn-2" };
        var sut = new AskUserService(hubContext, interactionContext, CreateLogger<AskUserService>());

        var response = await sut.AskOptionalAsync(new AgentQuestion
        {
            Message = "Optional question",
            Input = new TextInput()
        }, TimeSpan.FromMilliseconds(50));

        response.ShouldBeNull();
        await clientProxy.Received(1).SendCoreAsync(
            "PushQuestion",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AskAsync_ExpiredQuestion_ReturnsTimedOutResponse_AndCleansUpPendingQuestion()
    {
        var hubContext = Substitute.For<IHubContext<GroupWatchHub>>();
        var clientProxy = Substitute.For<ISingleClientProxy>();
        hubContext.Clients.Client("conn-ttl").Returns(clientProxy);

        var interactionContext = new AgentInteractionContext { ConnectionId = "conn-ttl" };
        var sut = new AskUserService(
            hubContext,
            interactionContext,
            CreateLogger<AskUserService>(),
            TimeSpan.FromMilliseconds(50));
        var sink = (IAgentResponseSink)sut;

        var response = await sut.AskAsync(new AgentQuestion
        {
            Message = "This will expire",
            Input = new TextInput()
        });

        response.Skipped.ShouldBeTrue();
        response.TextValue.ShouldBe("timed out");
        sink.TrySubmit(new UserResponse
        {
            QuestionId = response.QuestionId,
            TextValue = "late"
        }).ShouldBeFalse();
    }
}
