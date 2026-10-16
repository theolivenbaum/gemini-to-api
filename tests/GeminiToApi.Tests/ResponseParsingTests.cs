using GeminiToApi.Gemini;
using static GeminiToApi.Tests.TestJson;

namespace GeminiToApi.Tests;

public class ResponseParsingTests
{
    private static string Body(object?[] payload) => Json(new object?[] { new object?[] { "wrb.fr", null, Json(payload) } });

    [Fact]
    public void ExtractsTextConversationAndImages()
    {
        const string imageUrl = "https://lh3.googleusercontent.com/generated-image=w1024-h1024";
        const string iconUrl  = "https://fonts.gstatic.com/s/i/short-term/release/googlesymbols/expand/default/24px.svg";

        var response = GeminiResponseParser.Parse(Body([null, "conversation-id", null, null, new object?[] { new object?[] { "response-id", new object?[] { "done", new object?[] { iconUrl, imageUrl } } } }]));

        Assert.Equal("done", response.Text);
        Assert.Equal("conversation-id", response.ConversationId);
        Assert.Equal(imageUrl, Assert.Single(response.Images).Url);
    }

    [Fact]
    public void UsesGeneratedImageMediaSlot()
    {
        object?[] meta = [null, 1, "generated.png", "https://lh3.googleusercontent.com/generated-image", null, "image/png", new object?[] { 1024, 768, 12345 }];

        var slot = Sized(8);
        slot[7] = new object?[] { new object?[] { new object?[] { new object?[] { null, null, null, meta } } } };

        var candidate = Sized(13);
        candidate[0]  = "response-id";
        candidate[1]  = new object?[] { "", null };
        candidate[12] = slot;

        var images = MediaUrls.ExtractGeneratedImages(Parsed(candidate));
        var image  = Assert.Single(images);

        Assert.True(image.Generated);
        Assert.Equal(("image/png", 1024, 768), (image.MimeType, image.Width, image.Height));

        var response = GeminiResponseParser.Parse(Body([null, "conversation-id", null, null, new object?[] { candidate }]));
        Assert.True(Assert.Single(response.Images).Generated);
    }

    [Fact]
    public void ReportsBardErrorInfo()
    {
        const string body = ")]}'\n\n\t[[\"wrb.fr\",null,null,null,null,[13,null,[[\"type.googleapis.com/assistant.boq.bard.application.BardErrorInfo\",[1152]]]]],[\"di\",2461]]";

        var error = Assert.Throws<GeminiException>(() => GeminiResponseParser.Parse(body));
        Assert.Contains("BardErrorInfo code [13 1152]", error.Message);
    }

    [Fact]
    public void ReadsNumberedFieldReplies()
    {
        var payload = Json(new object?[] { null, new object?[] { "c_abc", "r_def" }, new Dictionary<string, object?> { ["7"] = new object?[] { "I'm generating your video" }, ["44"] = true } });
        var body    = ")]}'\n\n100\n" + Json(new object?[] { new object?[] { "wrb.fr", null, payload } }) + "\n";

        var response = GeminiResponseParser.Parse(body);

        Assert.Equal("I'm generating your video", response.Text);
        Assert.Equal("c_abc", response.ConversationId);
    }

    [Fact]
    public void SeparatesThinking()
    {
        var response = GeminiResponseParser.Parse(Body([null, "c", null, null, new object?[] { new object?[] { "r", new object?[] { "<ctrl94>thought Let me think<ctrl95>The answer is 2" } } }]));

        Assert.Equal("Let me think", response.ReasoningText);
        Assert.Equal("The answer is 2", response.Text);
    }

    [Fact]
    public void FailsOnUnparsableBody() => Assert.Throws<GeminiException>(() => GeminiResponseParser.Parse("<html>nope</html>"));

    [Theory]
    [InlineData("http://googleusercontent.com/image_generation_content/211", "")]
    [InlineData("googleusercontent.com/image_generation_content/211", "")]
    [InlineData("//lh3.googleusercontent.com/generated-image=w1024-h1024", "https://lh3.googleusercontent.com/generated-image=w1024-h1024")]
    [InlineData("https://example.com/photo.png", "https://example.com/photo.png")]
    [InlineData("https://example.com/page.html", "")]
    public void NormalizesImageUrls(string input, string want) => Assert.Equal(want, MediaUrls.NormalizeImageUrl(input));

    [Theory]
    [InlineData("google.com", true)]
    [InlineData("lh3.googleusercontent.com", true)]
    [InlineData("contribution.usercontent.google.com.", true)]
    [InlineData("evilgoogle.com", false)]
    [InlineData("google.com.evil.com", false)]
    public void TrustedHosts(string host, bool trusted) => Assert.Equal(trusted, MediaUrls.IsTrustedGoogleHost(host));

    [Fact]
    public void AppendsImageSize()
    {
        Assert.Equal("https://lh3.googleusercontent.com/abc=s2048?x=1", MediaUrls.AppendImageSize("https://lh3.googleusercontent.com/abc?x=1", 2048));
        Assert.Equal("https://lh3.googleusercontent.com/abc=w100", MediaUrls.AppendImageSize("https://lh3.googleusercontent.com/abc=w100", 2048));
    }

    [Fact]
    public void ExtractsCanvases()
    {
        const string html = "```html\n<!DOCTYPE html>\n<html><body>Todo</body></html>\n```";

        var code = Sized(18);
        code[0] = "c_test_index.html"; code[2] = "Simple To-Do List"; code[4] = html; code[9] = "index.html"; code[10] = 2.0;
        code[14] = new object?[] { html, "html", null, false, true };

        var doc = Sized(18);
        doc[0] = "c_test_coffee_essay.md"; doc[2] = "The Magic of Coffee"; doc[4] = "The Magic of Coffee\n\nCoffee is great."; doc[9] = "coffee_essay.md"; doc[10] = 1.0;
        doc[14] = new object?[] { "The Magic of Coffee\n\nCoffee is great.", null, Array.Empty<object?>(), null, "markdown" };

        var candidate = Sized(38);
        candidate[0]  = "rc";
        candidate[1]  = new object?[] { "x" };
        candidate[30] = new object?[] { code, doc };

        var canvases = Canvas.Extract(Parsed(candidate));

        Assert.Equal(2, canvases.Count);
        Assert.Equal((Canvas.CODE, "html", "index.html", "Simple To-Do List"), (canvases[0].Type, canvases[0].Language, canvases[0].FileName, canvases[0].Title));
        Assert.Equal((Canvas.DOCUMENT, "markdown", "coffee_essay.md"), (canvases[1].Type, canvases[1].Language, canvases[1].FileName));

        var file = canvases[0].ToFile();
        Assert.Equal("text/html", file.MimeType);
        Assert.Equal("<!DOCTYPE html>\n<html><body>Todo</body></html>", file.Content);
    }

    [Fact]
    public void RendersCanvasPlaceholders()
    {
        List<Canvas> canvases = [new() { Type = Canvas.CODE, Language = "go", Content = "package main" }, new() { Type = Canvas.DOCUMENT, Content = "# Essay" }];

        const string text = "Intro\n\nhttp://googleusercontent.com/immersive_entry_chip/0\n\nhttp://googleusercontent.com/immersive_entry_chip/1\n\nhttp://googleusercontent.com/immersive_entry_chip/7\nDone";

        Assert.Equal("Intro\n\n```go\npackage main\n```\n\n# Essay\n\n\nDone", Canvas.RenderPlaceholders(text, canvases));
    }

    [Theory]
    [InlineData("gemini-pro-canvas", "gemini-pro", true)]
    [InlineData("Gemini-Pro-CANVAS", "Gemini-Pro", true)]
    [InlineData("gemini-pro", "gemini-pro", false)]
    [InlineData("-canvas", "-canvas", false)]
    public void SplitsCanvasSuffix(string input, string model, bool canvas) => Assert.Equal((model, canvas), Canvas.SplitModel(input));

    [Fact]
    public void ServedModelNeedsUnambiguousEvidence()
    {
        var models = TestModels();

        Assert.Equal("gemini-flash", GeminiClient.ServedModel("[[\"model\",\"1111111111111111\"]]", models)?.Id);
        Assert.Null(GeminiClient.ServedModel("1111111111111111 2222222222222222", models));
        Assert.Null(GeminiClient.ServedModel("no model metadata", models));
    }
}
