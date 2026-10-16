using GeminiToApi.Gemini;
using static GeminiToApi.Tests.TestJson;

namespace GeminiToApi.Tests;

public class GeminiModelsTests
{
    private static string Fixture(object? statusCode, object?[]? tiers, object?[]? capabilities, params object?[]?[] entries)
    {
        var status = Sized(18);
        status[14] = statusCode;
        status[15] = entries;
        status[16] = tiers;
        status[17] = capabilities;

        return Json(new object?[] { new object?[] { "wrb.fr", "otAQ7b", Json(status), null, null, null, "generic" } });
    }

    private static object?[] Entry(string modelId, string? category, string? display, object? number)
    {
        var entry = Sized(20);
        entry[0]  = modelId;
        entry[1]  = category;
        entry[11] = display;
        entry[17] = number;
        return entry;
    }

    [Fact]
    public void UsesAccountModelIdentity()
    {
        var frame   = Fixture(1000, [8], null, Entry("account-flash-id", "Fast", "Gemini 3.8 Flash", 1), Entry("account-pro-id", "Pro", "Gemini 3.1 Pro", 3));
        var fixture = ")]}'\n\n42\n[[\"wrb.fr\",\"other-rpc\",\"[]\"]]\n" + frame.Length + "\n" + frame + "\n12\n[[\"di\",0]]\n";
        var models  = GeminiModels.Parse(fixture);

        Assert.Equal(2, models.Count);

        foreach (var (request, want) in new[]
        {
            ("", "account-flash-id"), ("gemini-fast", "account-flash-id"), ("gemini-3.8-flash", "account-flash-id"),
            ("gemini-pro", "account-pro-id"), ("gemini-advanced", "account-pro-id"), ("Gemini 3.1 Pro", "account-pro-id"),
            ("gemini-3.1-pro", "account-pro-id"), ("account-pro-id", "account-pro-id"),
        })
        {
            Assert.Equal(want, GeminiModels.Resolve(request, models).ModelId);
        }

        Assert.Equal((2, 12, 3), (models[1].Capacity, models[1].CapacityField, models[1].ModelNumber));
        Assert.Equal("google", models[1].Info.OwnedBy);
        Assert.Equal("gemini", models[1].Info.Provider);
        Assert.NotEqual(0, models[1].Info.Created);
    }

    [Theory]
    [InlineData(null, null, 1, 12)]
    [InlineData(new object[] { 8 }, null, 2, 12)]
    [InlineData(null, new object[] { 19 }, 2, 12)]
    [InlineData(new object[] { 16, 8 }, null, 3, 12)]
    [InlineData(null, new object[] { 106, 19 }, 3, 12)]
    [InlineData(new object[] { 16 }, new object[] { 115, 19 }, 4, 12)]
    [InlineData(new object[] { 21, 22, 16 }, new object[] { 115 }, 1, 13)]
    [InlineData(new object[] { 22, 8 }, new object[] { 115 }, 2, 13)]
    public void ReadsAccountCapacity(object[]? tiers, object[]? capabilities, int capacity, int field)
    {
        var models = GeminiModels.Parse(Fixture(1000, tiers?.Cast<object?>().ToArray(), capabilities?.Cast<object?>().ToArray(), Entry("id", "Pro", "Pro", 3)));
        Assert.Equal((capacity, field), (models[0].Capacity, models[0].CapacityField));
    }

    [Fact]
    public void HandlesAlternateRpcFields()
    {
        var entry = Entry("alternate-id", null, null, null);
        entry[9]  = 3;
        entry[10] = "Pro";
        entry[19] = "Gemini 3 Pro";

        var fixture = Fixture(null, null, null, null, Array.Empty<object?>(), new object?[] { 123 }, entry, entry);
        var models  = GeminiModels.Parse(fixture);

        Assert.Single(models);
        Assert.Equal("gemini-3-pro", models[0].Id);
        Assert.Equal(3, models[0].ModelNumber);

        var pretty = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(fixture).RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Assert.Single(GeminiModels.Parse(pretty));
    }

    [Theory]
    [InlineData(1014)]
    [InlineData(1060)]
    [InlineData(9999)]
    public void RejectsUnavailableSessions(int status)
    {
        Assert.Throws<GeminiException>(() => GeminiModels.Parse(Fixture(status, [8], null, Entry("flash-id", "Flash", "Flash", 1))));
    }

    [Fact]
    public void RejectsMissingOrMalformedDiscovery()
    {
        foreach (var fixture in new[]
        {
            "",
            "<html>gemini-pro gemini-flash</html>",
            "[[\"wrb.fr\",\"another-rpc\",\"[]\"]]",
            "[[\"wrb.fr\",\"otAQ7b\",null,null,null,[7]]]",
            "[[\"wrb.fr\",\"otAQ7b\",null]]",
            "[[\"wrb.fr\",\"otAQ7b\",\"not json\"]]",
            Fixture(1000, null, null),
            Fixture("1000", null, null, Entry("id", "Pro", "Pro", 3)),
        })
        {
            Assert.ThrowsAny<GeminiException>(() => GeminiModels.Parse(fixture));
        }
    }

    [Fact]
    public void RejectsInvalidModelNumbers()
    {
        foreach (var number in new object?[] { 0, -1, "3", 3.5, false })
        {
            Assert.ThrowsAny<GeminiException>(() => GeminiModels.Parse(Fixture(1000, null, null, Entry("pro-id", "Pro", "Pro", number))));
        }

        var models = GeminiModels.Parse(Fixture(1000, null, null, Entry("flash-id", "Flash", "Flash", null)));
        Assert.Equal(1, models.Single().ModelNumber);
    }

    [Fact]
    public void NeverFallsBackFromPro()
    {
        var models = GeminiModels.Parse(Fixture(1000, null, null, Entry("flash-id", "Fast", "Gemini 3.8 Flash", 1)));

        foreach (var request in new[] { "gemini-advanced", "gemini-pro", "gemini-3-pro-image-preview", "gemini-unknown", "gemini-99-flash" })
        {
            var error = Assert.Throws<ModelSelectionException>(() => GeminiModels.Resolve(request, models));
            Assert.Contains("gemini-3.8-flash", error.Message);
        }

        Assert.Throws<ModelSelectionException>(() => GeminiModels.Resolve("", []));
    }

    [Fact]
    public void RejectsAmbiguousAliases()
    {
        GeminiModel[] models =
        [
            new(new ModelInfo("gemini-pro-a", "", 0), "first", 1, 12, 1, ["gemini-pro"]),
            new(new ModelInfo("gemini-pro-b", "", 0), "second", 1, 12, 1, ["gemini-pro"]),
        ];

        foreach (var request in new[] { "gemini-pro", "gemini-advanced" })
        {
            Assert.Contains("ambiguous", Assert.Throws<ModelSelectionException>(() => GeminiModels.Resolve(request, models)).Message);
        }

        Assert.Equal("first", GeminiModels.Resolve("first", models).ModelId);
    }

    [Fact]
    public void PrefersCanonicalNamesOverDisplayAliases()
    {
        GeminiModel[] models =
        [
            new(new ModelInfo("gemini-pro", "", 0), "pro-id", 1, 12, 1, []),
            new(new ModelInfo("gemini-thinking", "", 0), "thinking-id", 1, 12, 1, ["gemini-pro", "pro-id"]),
        ];

        foreach (var request in new[] { "gemini-pro", "gemini-advanced", "pro-id" })
        {
            Assert.Equal("pro-id", GeminiModels.Resolve(request, models).ModelId);
        }

        var discovered = GeminiModels.Parse(Fixture(1000, null, null, Entry("thinking-id", "Thinking", "Gemini 3 Pro", 2)));
        Assert.Throws<ModelSelectionException>(() => GeminiModels.Resolve("gemini-advanced", discovered));
    }

    [Fact]
    public void AccountRouting()
    {
        Assert.Equal(GeminiProtocol.ENDPOINT_BATCH_EXEC, GeminiProtocol.AccountUrl(GeminiProtocol.ENDPOINT_BATCH_EXEC, ""));
        Assert.Equal("https://gemini.google.com/u/2/_/BardChatUi/data/batchexecute", GeminiProtocol.AccountUrl(GeminiProtocol.ENDPOINT_BATCH_EXEC, "2"));
        Assert.Equal("/app", GeminiProtocol.SourcePath(""));
        Assert.Equal("/u/2/app", GeminiProtocol.SourcePath("2"));
    }
}
