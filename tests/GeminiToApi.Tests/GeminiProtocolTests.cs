using System.Text.Json.Nodes;
using GeminiToApi.Gemini;
using static GeminiToApi.Tests.TestJson;

namespace GeminiToApi.Tests;

public class GeminiProtocolTests
{
    [Fact]
    public void BuildGenerateInnerPreservesUploadedFiles()
    {
        var inner = GeminiProtocol.BuildGenerateInner("Describe", [new GeminiProtocol.UploadedFile("uploaded-id", "image.png")], 3, "en", "request-id", false);

        Assert.Equal(81, inner.Count);
        Assert.Equal("[\"Describe\",0,null,[[[\"uploaded-id\"],\"image.png\"]],null,null,0]", Jspb.Serialize(inner[0]));
        Assert.Equal(3, inner[79]!.GetValue<int>());
        Assert.Null(inner[45]);
    }

    [Fact]
    public void BuildGenerateInnerMarksTemporaryChats()
    {
        var inner = GeminiProtocol.BuildGenerateInner("Hi", [], 1, "en", "r", true);
        Assert.Equal("[\"Hi\"]", Jspb.Serialize(inner[0]));
        Assert.Equal(1, inner[45]!.GetValue<int>());
    }

    [Fact]
    public void ModelHeaderSupportsAlternateCapacityField()
    {
        var model   = TestModels()[1] with { CapacityField = 13 };
        var headers = GeminiProtocol.BuildModelHeaders(model, "request-id", "session-id", 1);

        Assert.Equal("[1,null,null,null,\"2222222222222222\",null,null,0,[4,5,6,8],null,null,null,2,null,null,3,1,\"session-id\"]", headers[GeminiProtocol.MODEL_HEADER_KEY]);
        Assert.Equal("[\"request-id\",1]", headers["x-goog-ext-525005358-jspb"]);
    }

    [Fact]
    public void ModelHeaderAdvertisesWebTools()
    {
        var headers = GeminiProtocol.BuildModelHeaders(TestModels()[1], "r", "g", 4, webTool: true);
        var header  = JsonNode.Parse(headers[GeminiProtocol.MODEL_HEADER_KEY])!.AsArray();

        Assert.Equal("[4,5,6,8,16]", header[8]!.ToJsonString());
        Assert.Equal(4, header[15]!.GetValue<int>());
    }

    [Fact]
    public void ModelHeaderRejectsIncompleteRouting()
    {
        Assert.Throws<GeminiException>(() => GeminiProtocol.BuildModelHeaders(TestModels()[0] with { ModelId = "" }, "r", "g", 1));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("low", 1)]
    [InlineData(" Medium ", 4)]
    [InlineData("HIGH", 2)]
    public void ThinkingLevels(string level, int want) => Assert.Equal(want, GeminiProtocol.ParseThinkingLevel(level));

    [Fact]
    public void RejectsUnknownThinkingLevel() => Assert.Throws<ThinkingLevelException>(() => GeminiProtocol.ParseThinkingLevel("extreme"));

    [Theory]
    [InlineData(VideoAspectRatio.Landscape, "1", "[[16]]")]
    [InlineData(VideoAspectRatio.Portrait, "2", "[[17]]")]
    public void VideoInnerMatchesWebClient(VideoAspectRatio aspect, string wantAspect, string wantTool)
    {
        var inner = GeminiProtocol.BuildGenerateInner("A cat", [], 3, "en", "req", true);
        GeminiProtocol.ApplyVideoInner(inner, aspect);

        var decoded = JsonNode.Parse(Jspb.Serialize(inner))!.AsArray();

        Assert.Equal(GeminiProtocol.WEB_CLIENT_INNER_LENGTH, decoded.Count);
        Assert.Equal("A cat", decoded[0]![0]!.GetValue<string>());
        Assert.Equal("[null,null,null,null,null,null,[[null,null,null," + wantAspect + "]]]", decoded[0]![9]!.ToJsonString());
        Assert.Equal(11, decoded[49]!.GetValue<int>());
        Assert.Equal(wantTool, decoded[55]!.ToJsonString());
        Assert.Null(decoded[45]);
    }

    [Theory]
    [InlineData("", "", "", "", "")]
    [InlineData("short", "instrumental", "lo-fi", "[null,null,null,null,null,null,[null,null,[4,1]]]", "[[42,26,51]]")]
    [InlineData("standard", "", "", "[null,null,null,null,null,null,[null,null,[5]]]", "[[43]]")]
    [InlineData("", "vocals", "", "[null,null,null,null,null,null,[null,null,[null,2]]]", "[[27]]")]
    [InlineData("", "", "pop", "[null,null,null,null,null,null,[]]", "[[44]]")]
    public void MusicInnerMatchesWebClient(string length, string vocals, string genre, string wantOptions, string wantChips)
    {
        var inner = GeminiProtocol.BuildGenerateInner("A song", [], 3, "en", "req", true);
        GeminiProtocol.ApplyMusicInner(inner, new MusicConfig { Length = length, Vocals = vocals, Genre = genre });

        var decoded = JsonNode.Parse(Jspb.Serialize(inner))!.AsArray();
        var message = decoded[0]!.AsArray();

        Assert.Equal(GeminiProtocol.WEB_CLIENT_INNER_LENGTH, decoded.Count);
        Assert.Equal(21, decoded[49]!.GetValue<int>());
        Assert.Null(decoded[45]);
        Assert.Equal(wantOptions, message.Count > 9 ? message[9]!.ToJsonString() : "");
        Assert.Equal(wantChips, decoded[55]?.ToJsonString() ?? "");
    }

    [Fact]
    public void MusicConfigValidates()
    {
        var config = new MusicConfig { Length = " Short ", Vocals = "with vocals", Genre = "Lo-Fi" };
        Music.Validate(config);

        Assert.Equal(("short", "vocals", "lo-fi"), (config.Length, config.Vocals, config.Genre));

        Assert.Throws<MusicValidationException>(() => Music.Validate(new MusicConfig { Length = "long" }));
        Assert.Throws<MusicValidationException>(() => Music.Validate(new MusicConfig { Vocals = "choir" }));
        Assert.Throws<MusicValidationException>(() => Music.Validate(new MusicConfig { Genre = "polka" }));
    }

    [Fact]
    public void CanvasInnerSelectsTool()
    {
        var inner = GeminiProtocol.BuildGenerateInner("x", [], 1, "en", "r", false);
        GeminiProtocol.ApplyCanvasInner(inner);

        Assert.Equal(2, inner[49]!.GetValue<int>());
        Assert.Equal("[4,16]", inner[30]!.ToJsonString());
        Assert.Equal(1, inner[98]!.GetValue<int>());
    }

    [Fact]
    public void FormEncodeSortsAndEscapes()
    {
        Assert.Equal("at=a%2Bb&f.req=%5B%22x+y%22%5D", GeminiProtocol.FormEncode([new("f.req", "[\"x y\"]"), new("at", "a+b")]));
    }
}
