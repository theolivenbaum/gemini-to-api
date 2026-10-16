using GeminiToApi.Api.Common;
using GeminiToApi.Gemini;

namespace GeminiToApi.Tests;

public class ConversationStoreTests
{
    private static ChatMessage User(string text) => ChatMessage.Text("user", text);

    private static ChatMessage Assistant(string text) => ChatMessage.Text("assistant", text);

    private static GeminiResponse Reply(int turn) => new() { ConversationId = "c_1", ResponseId = "r_" + turn, ChoiceId = "rc_" + turn };

    [Fact]
    public void ContinuesAfterTheLongestAnsweredPrefix()
    {
        var store = new ConversationStore();

        store.Remember("gemini-pro", "sys", [User("a")], Reply(1));
        store.Remember("gemini-pro", "sys", [User("a"), Assistant("x"), User("b")], Reply(2));

        var plan = store.PlanFor("gemini-pro", "sys", [User("a"), Assistant("x"), User("b"), Assistant("y"), User("c"), User("d")]);

        Assert.Equal(new ConversationRef("c_1", "r_2", "rc_2"), plan.Continue);
        Assert.Equal(["c", "d"], plan.NewMessages.Select(m => m.Content));
    }

    [Fact]
    public void EchoedReplyTextDoesNotMatter()
    {
        var store = new ConversationStore();

        store.Remember("m", "", [User("a")], Reply(1));

        Assert.NotNull(store.PlanFor("m", "", [User("a"), Assistant("anything at all"), User("b")]).Continue);
    }

    [Fact]
    public void DifferentModelSystemOrHistoryStartsFresh()
    {
        var store = new ConversationStore();

        store.Remember("m", "sys", [User("a")], Reply(1));

        Assert.Null(store.PlanFor("other", "sys", [User("a"), Assistant("x"), User("b")]).Continue);
        Assert.Null(store.PlanFor("m", "changed", [User("a"), Assistant("x"), User("b")]).Continue);
        Assert.Null(store.PlanFor("m", "sys", [User("A"), Assistant("x"), User("b")]).Continue);
        Assert.Null(store.PlanFor("m", "sys", [User("a")]).Continue);                    // regenerate
        Assert.Null(store.PlanFor("m", "sys", [User("a"), User("b")]).Continue);         // no reply in between
        Assert.Null(store.PlanFor("m", "sys", [User("a"), Assistant("x")]).Continue);    // nothing new to send
    }

    [Fact]
    public void AttachmentsArePartOfTheHistory()
    {
        var store = new ConversationStore();

        store.Remember("m", "", [new ChatMessage("user", "look", [new Attachment("a.png", "image/png", "AAAA")])], Reply(1));

        Assert.NotNull(store.PlanFor("m", "", [new ChatMessage("user", "look", [new Attachment("other-name.png", "image/png", "AAAA")]), Assistant("x"), User("b")]).Continue);
        Assert.Null(store.PlanFor("m", "", [new ChatMessage("user", "look", [new Attachment("a.png", "image/png", "BBBB")]), Assistant("x"), User("b")]).Continue);
    }

    [Fact]
    public void RepliesWithoutIdsAreNotRemembered()
    {
        var store = new ConversationStore();

        store.Remember("m", "", [User("a")], new GeminiResponse());

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void EvictsOldestBeyondCapacity()
    {
        var store = new ConversationStore(capacity: 2);

        for (var i = 0; i < 5; i++) store.Remember("m", "", [User("q" + i)], Reply(i));

        Assert.True(store.Count <= 2);
        Assert.NotNull(store.PlanFor("m", "", [User("q4"), Assistant("x"), User("next")]).Continue);
    }

    [Fact]
    public void FormatsASingleUserMessageAsTyped()
    {
        Assert.Equal("Hello", GeminiChat.FormatPrompt([User(" Hello ")], ""));
        Assert.Equal("System: S\n\nUser: Hello", GeminiChat.FormatPrompt([User("Hello")], "S"));
        Assert.Equal("User: a\nUser: b", GeminiChat.FormatPrompt([User("a"), User("b")], ""));
    }

    [Fact]
    public void ApplyConversationFillsTheRequestMetadata()
    {
        var inner = GeminiProtocol.BuildGenerateInner("x", [], 1, "en", "r", false);

        GeminiProtocol.ApplyConversation(inner, new ConversationRef("c_1", "r_1", "rc_1"));

        Assert.Equal("[\"c_1\",\"r_1\",\"rc_1\",null,null,null,null,null,null,\"\"]", Jspb.Serialize(inner[2]));
    }

    [Fact]
    public void ParserReadsResponseId()
    {
        var response = GeminiResponseParser.Parse(FakeGemini.StreamReply("hi", "c_9", 4));

        Assert.Equal(("c_9", "r_4", "rc_4"), (response.ConversationId, response.ResponseId, response.ChoiceId));
    }
}
