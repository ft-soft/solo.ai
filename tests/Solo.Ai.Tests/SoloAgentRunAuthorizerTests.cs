using System.Security.Claims;
using Solo.Ai.Api;
using Solo.Ai.Client;
using Xunit;

namespace Solo.Ai.Tests;

public sealed class SoloAgentRunAuthorizerTests
{
    [Fact]
    public async Task AcceptNewMessagesAndSupplyOnlyServerOwnedHistory()
    {
        var runtime = new SoloAgentRunAuthorizer();
        var owner = CreateSender();
        var first = CreateInput(Guid.NewGuid());
        Assert.True(await runtime.AuthorizeAndAcceptAsync(owner, first, CancellationToken.None));
        Assert.Empty(runtime.PrepareGeneration(first).History);
        var reply = new SoloAgentResponse(first.RequestId, first.ChatId, first.RunId, first.MessageId, "no_match") { Text = "Synthetic answer" };
        runtime.Complete(first, reply);
        var second = CreateInput(first.ChatId);
        Assert.True(await runtime.AuthorizeAndAcceptAsync(owner, second, CancellationToken.None));
        Assert.Equal(new[] { new ChatHistoryMessage("user", first.Message), new("assistant", "Synthetic answer") },
            runtime.PrepareGeneration(second).History);
        runtime.Complete(second, null);
        Assert.False(await runtime.AuthorizeAndAcceptAsync(owner, first with { RequestId = Guid.NewGuid(), RunId = Guid.NewGuid() }, CancellationToken.None));
    }

    [Fact]
    public async Task RejectForeignOwnerParallelRunAndCallerSuppliedHistory()
    {
        var runtime = new SoloAgentRunAuthorizer();
        var owner = CreateSender();
        var first = CreateInput(Guid.NewGuid());
        Assert.True(await runtime.AuthorizeAndAcceptAsync(owner, first, CancellationToken.None));
        Assert.False(await runtime.AuthorizeAndAcceptAsync(owner, CreateInput(first.ChatId), CancellationToken.None));
        runtime.Complete(first, null);
        Assert.False(await runtime.AuthorizeAndAcceptAsync(CreateSender(), CreateInput(first.ChatId), CancellationToken.None));
        Assert.False(await runtime.AuthorizeAndAcceptAsync(owner, CreateInput(first.ChatId) with
            { History = [new("system", "Caller history")] }, CancellationToken.None));
        Assert.True(await runtime.AuthorizeAndAcceptAsync(owner, CreateInput(first.ChatId), CancellationToken.None));
    }

    [Fact]
    public async Task FinishFailureWithoutAcceptingLateTextOrReplayingMessage()
    {
        var runtime = new SoloAgentRunAuthorizer();
        var owner = CreateSender();
        var first = CreateInput(Guid.NewGuid());
        Assert.True(await runtime.AuthorizeAndAcceptAsync(owner, first, CancellationToken.None));
        runtime.Complete(first, null);
        var second = CreateInput(first.ChatId);
        Assert.True(await runtime.AuthorizeAndAcceptAsync(owner, second, CancellationToken.None));
        runtime.Complete(first, new(first.RequestId, first.ChatId, first.RunId, first.MessageId, "no_match") { Text = "Late answer" });
        Assert.Equal(new[] { new ChatHistoryMessage("user", first.Message) }, runtime.PrepareGeneration(second).History);
        runtime.Complete(second, null);
        Assert.False(await runtime.AuthorizeAndAcceptAsync(owner, second with { RequestId = Guid.NewGuid(), RunId = Guid.NewGuid() }, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectSameSendersMessageInAnotherChatBeforeAndAfterCompletion(bool completed)
    {
        var runtime = new SoloAgentRunAuthorizer();
        var owner = CreateSender();
        var first = CreateInput(Guid.NewGuid());
        Assert.True(await runtime.AuthorizeAndAcceptAsync(owner, first, CancellationToken.None));
        if (completed) runtime.Complete(first, null);
        var retry = first with { RequestId = Guid.NewGuid(), ChatId = Guid.NewGuid(), RunId = Guid.NewGuid() };
        Assert.False(await runtime.AuthorizeAndAcceptAsync(owner, retry, CancellationToken.None));
        Assert.True(await runtime.AuthorizeAndAcceptAsync(CreateSender(), retry, CancellationToken.None));
    }

    private static ClaimsPrincipal CreateSender() => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "solo-backend")], "service"));

    private static PreparedGenerationInput CreateInput(Guid chatId) =>
        new(Guid.NewGuid(), chatId, Guid.NewGuid(), Guid.NewGuid(), "Synthetic message", [], []);
}
