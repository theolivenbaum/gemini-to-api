using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GeminiToApi.Gemini;

namespace GeminiToApi.Api.Common;

/// <summary>
/// Remembers which Gemini Web conversation answered which message history, so a follow-up request
/// (the same history plus the assistant's reply and new messages) continues that conversation and
/// sends only the new messages. The OpenAI, Claude and Gemini APIs are stateless and resend the
/// whole history every time; without this every request would start a new Gemini chat.
/// </summary>
/// <remarks>
/// A history is identified by a hash of the model, the system prompt and every message (role, text,
/// attachments). The assistant reply a client echoes back is deliberately not part of the key: clients
/// reformat it (tool calls, image links, thinking blocks), so a request continues a conversation when
/// everything <em>before</em> the echoed reply matches a request that was answered before.
/// </remarks>
public sealed class ConversationStore(int capacity = 4096, TimeSpan? lifetime = null)
{
    private sealed record Entry(ConversationRef Conversation, DateTimeOffset StoredAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan                             _lifetime = lifetime ?? TimeSpan.FromHours(12);

    public int Count => _entries.Count;

    /// <summary>What to send for a request: the messages Gemini has not seen, and the conversation to continue.</summary>
    public sealed record Plan(IReadOnlyList<ChatMessage> NewMessages, ConversationRef? Continue);

    public Plan PlanFor(string model, string system, IReadOnlyList<ChatMessage> messages)
    {
        var prefixes = PrefixHashes(model, system, messages);
        var now      = DateTimeOffset.UtcNow;

        // Longest answered prefix first: messages[..k] was a request, messages[k] is the reply the
        // client echoes back, and messages[(k + 1)..] are new.
        for (var k = messages.Count - 2; k >= 1; k--)
        {
            if (!IsAssistant(messages[k])) continue;

            if (_entries.TryGetValue(prefixes[k], out var entry) && now - entry.StoredAt < _lifetime)
            {
                return new Plan(messages.Skip(k + 1).ToList(), entry.Conversation);
            }
        }

        return new Plan(messages, null);
    }

    /// <summary>Records the Gemini turn that answered <paramref name="messages"/>.</summary>
    public void Remember(string model, string system, IReadOnlyList<ChatMessage> messages, GeminiResponse response)
    {
        if (response.ConversationId.Length == 0 || response.ChoiceId.Length == 0) return;

        var key = PrefixHashes(model, system, messages)[messages.Count];

        _entries[key] = new Entry(new ConversationRef(response.ConversationId, response.ResponseId, response.ChoiceId), DateTimeOffset.UtcNow);

        if (_entries.Count > capacity) Trim();
    }

    private void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow - _lifetime;

        foreach (var (key, entry) in _entries)
        {
            if (entry.StoredAt < cutoff) _entries.TryRemove(key, out _);
        }

        foreach (var (key, _) in _entries.OrderBy(kv => kv.Value.StoredAt).Take(Math.Max(0, _entries.Count - capacity)))
        {
            _entries.TryRemove(key, out _);
        }
    }

    internal static bool IsAssistant(ChatMessage message) => message.Role.ToLowerInvariant() is "assistant" or "model";

    private static string NormalizedRole(string role) => role.ToLowerInvariant() switch
    {
        "assistant" or "model"  => "model",
        "system" or "developer" => "system",
        _                       => "user",
    };

    /// <summary>hashes[k] identifies (model, system, messages[..k]); computed in one pass.</summary>
    private static string[] PrefixHashes(string model, string system, IReadOnlyList<ChatMessage> messages)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void Add(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }

        var result = new string[messages.Count + 1];

        Add(model.ToLowerInvariant());
        Add(system);
        result[0] = Convert.ToHexString(hash.GetCurrentHash());

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            Add(NormalizedRole(message.Role));
            Add(message.Content);

            foreach (var attachment in message.Attachments)
            {
                Add(attachment.MimeType);
                Add(attachment.Data);
            }

            Add("\u001e"); // end of message
            result[i + 1] = Convert.ToHexString(hash.GetCurrentHash());
        }

        return result;
    }
}

/// <summary>Sends chat turns to Gemini, continuing the Gemini conversation a request belongs to.</summary>
public sealed class GeminiChat(GeminiClient client, ConversationStore store, Configuration.AppConfig config, ILogger<GeminiChat> log)
{
    /// <param name="Messages">The full history the client sent.</param>
    /// <param name="System">System prompt (sent only when a new conversation starts).</param>
    /// <param name="Wrap">Applied to the prompt before sending (the tool bridge); identity when null.</param>
    public sealed record Request(string Model, string System, IReadOnlyList<ChatMessage> Messages, Func<string, string>? Wrap, GenerateOptions Options);

    /// <param name="Prompt">What was actually sent to Gemini (for token estimates).</param>
    public sealed record Result(GeminiResponse Response, string Prompt, bool Continued);

    public async Task<Result> SendAsync(Request request, CancellationToken ct)
    {
        var model = client.ResolveModel(request.Model).Id;
        var wrap  = request.Wrap ?? (p => p);

        if (config.Gemini.ContinueConversations)
        {
            var plan = store.PlanFor(model, request.System, request.Messages);

            if (plan.Continue is { } conversation && plan.NewMessages.Count > 0)
            {
                var prompt = wrap(FormatPrompt(plan.NewMessages, ""));

                request.Options.Continue   = conversation;
                request.Options.InputFiles = ChatMessages.ToInputFiles(plan.NewMessages.SelectMany(m => m.Attachments));

                try
                {
                    var response = await client.GenerateContentAsync(prompt, request.Options, ct);

                    store.Remember(model, request.System, request.Messages, response);
                    log.LogDebug("Continued Gemini conversation {Conversation} with {Count} new message(s)", conversation.ConversationId, plan.NewMessages.Count);

                    return new Result(response, prompt, true);
                }
                catch (GeminiException e) when (e is not (ModelSelectionException or ThinkingLevelException))
                {
                    // The conversation may be gone (deleted in Gemini, temporary chat expired):
                    // start a new one with the whole history instead of failing the request.
                    log.LogWarning("Could not continue Gemini conversation {Conversation} ({Error}); starting a new one", conversation.ConversationId, e.Message);
                }
            }
        }

        var fullPrompt = FormatPrompt(request.Messages, request.System);

        if (fullPrompt.Length == 0) throw new ApiException(400, "no valid content in messages");

        fullPrompt = wrap(fullPrompt);

        request.Options.Continue   = null;
        request.Options.InputFiles = ChatMessages.ToInputFiles(request.Messages.SelectMany(m => m.Attachments));

        var fresh = await client.GenerateContentAsync(fullPrompt, request.Options, ct);

        if (config.Gemini.ContinueConversations) store.Remember(model, request.System, request.Messages, fresh);

        return new Result(fresh, fullPrompt, false);
    }

    /// <summary>
    /// A single user message without a system prompt is sent as is, the way a person types it into
    /// Gemini; anything else is flattened into "System: / User: / Model:" lines.
    /// </summary>
    public static string FormatPrompt(IReadOnlyList<ChatMessage> messages, string system)
    {
        if (system.Length == 0 && messages.Count == 1 && !ConversationStore.IsAssistant(messages[0]) && messages[0].Role.ToLowerInvariant() != "system")
        {
            var only = messages[0];

            if (only.Content.Trim().Length > 0) return only.Content.Trim();
        }

        return ChatMessages.BuildPrompt(messages, system);
    }
}
