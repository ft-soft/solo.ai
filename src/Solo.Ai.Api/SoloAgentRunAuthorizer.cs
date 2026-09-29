using System.Security.Claims;
using Solo.Ai.Client;
using Solo.Ai.Visograph;

namespace Solo.Ai.Api;

public sealed class SoloAgentRunAuthorizer : ISoloAgentRunAuthorizer
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Chat> _chats = [];
    private readonly HashSet<Guid> _requests = [];
    private readonly HashSet<Guid> _runs = [];
    private readonly HashSet<(Guid Owner, Guid MessageId)> _messages = [];

    public Task<bool> AuthorizeAndAcceptAsync(ClaimsPrincipal sender, PreparedGenerationInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sender.Identity?.IsAuthenticated != true || !sender.IsInRole("solo-backend") ||
            !Guid.TryParse(sender.FindFirstValue(ClaimTypes.NameIdentifier), out var owner) || owner == Guid.Empty ||
            input.History.Count != 0)
            return Task.FromResult(false);
        lock (_gate)
        {
            // ponytail: process-local chats and replay memory, capped at 1024 attempts; durable storage belongs to DOC-5748.
            if (_requests.Count >= 1024)
                throw new ModelExchangeException("limit_exceeded", "runtime");
            if (_requests.Contains(input.RequestId) || _runs.Contains(input.RunId) || _messages.Contains((owner, input.MessageId)))
                return Task.FromResult(false);
            if (!_chats.TryGetValue(input.ChatId, out var chat))
                _chats.Add(input.ChatId, chat = new Chat(owner));
            if (chat.Owner != owner || chat.ActiveRun is not null)
                return Task.FromResult(false);
            _messages.Add((owner, input.MessageId));
            chat.ActiveRun = input.RunId;
            _requests.Add(input.RequestId);
            _runs.Add(input.RunId);
            return Task.FromResult(true);
        }
    }

    public PreparedGenerationInput PrepareGeneration(PreparedGenerationInput input)
    {
        lock (_gate)
        {
            var chat = _chats[input.ChatId];
            if (chat.ActiveRun != input.RunId)
                throw new ModelExchangeException("authorization_failed", "runtime");
            return input with { History = chat.History.ToArray() };
        }
    }

    public void Complete(PreparedGenerationInput input, SoloAgentResponse? result)
    {
        lock (_gate)
        {
            if (!_chats.TryGetValue(input.ChatId, out var chat) || chat.ActiveRun != input.RunId)
                return;
            chat.History.Add(new("user", input.Message));
            if (result?.Text is not null && result.Outcome is "recommendation" or "clarification" or "no_match" or "empty_catalog")
            {
                chat.History.Add(new("assistant", result.Text));
            }
            chat.ActiveRun = null;
        }
    }

    private sealed class Chat(Guid owner)
    {
        public Guid Owner { get; } = owner;
        public Guid? ActiveRun { get; set; }
        public List<ChatHistoryMessage> History { get; } = [];
    }
}
