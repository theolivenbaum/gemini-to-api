using System.Buffers.Binary;
using System.Text;
using GeminiToApi.Gemini;
using Microsoft.Extensions.Logging.Abstractions;
using static GeminiToApi.Tests.TestJson;

namespace GeminiToApi.Tests;

public class MediaTests
{
    private const string DOWNLOAD_URL = "https://contribution.usercontent.google.com/download?c=test&filename=video.mp4";
    private const string THUMB_URL    = "https://lh3.googleusercontent.com/gg/test-thumbnail";
    private const string AUDIO_URL    = "https://contribution.usercontent.google.com/download?c=test&filename=track.mp3";

    internal static object?[] VideoCandidate(string text, bool withVideo, string downloadUrl = DOWNLOAD_URL)
    {
        var candidate = Sized(13);
        candidate[0] = "rc_test";
        candidate[1] = new object?[] { text };
        candidate[8] = new object?[] { 2.0 };
        candidate[9] = "en";

        if (withVideo)
        {
            object?[] item =
            [
                null, 2.0, "video.mp4", null, null, "$token", null,
                new object?[] { THUMB_URL, downloadUrl, THUMB_URL + "=mm,22,18" },
                2.0, new object?[] { 1790299368.0, 667185666.0 }, null, "video/mp4", null, null, null, null, null,
                new object?[] { new object?[] { 9.0, 999999000.0 }, 1280.0, 720.0 }, null, null, null, null, null, null, 4657944.0,
            ];

            object?[] prompt = ["A cat", null, new object?[] { null, null, "models/omni" }];
            object?[] card   = [new object?[] { new object?[] { new object?[] { item }, prompt } }];

            candidate[12] = new object?[] { new Dictionary<string, object?> { ["8"] = Array.Empty<object?>(), ["60"] = card } };
        }
        else
        {
            candidate[12] = new object?[] { new Dictionary<string, object?> { ["8"] = Array.Empty<object?>() } };
        }

        return candidate;
    }

    internal static string ConversationBody(string text, bool withVideo)
    {
        object?[] turn = [new object?[] { "c_test", "r_test" }, null, new object?[] { new object?[] { "A cat" }, 2.0 }, new object?[] { new object?[] { VideoCandidate(text, withVideo) } }];

        var payload = Json(new object?[] { new object?[] { turn } });
        var record  = Json(new object?[] { new object?[] { "wrb.fr", "hNvQHb", payload, null, null, null, "generic" } });

        return ")]}'\n\n123\n" + record + "\n";
    }

    [Fact]
    public void ExtractsVideoFromSparseCard()
    {
        var video = Assert.Single(Video.ExtractGeneratedVideos(Parsed(VideoCandidate("Your video is ready!", true))));

        Assert.Equal(DOWNLOAD_URL, video.Url);
        Assert.Equal(THUMB_URL, video.ThumbnailUrl);
        Assert.Equal(("video/mp4", 1280, 720, "video.mp4", 4657944L), (video.MimeType, video.Width, video.Height, video.FileName, video.SizeBytes));
        Assert.InRange(video.Duration, 9.99, 10);
    }

    [Fact]
    public void IgnoresUntrustedAndTextLinks()
    {
        Assert.Empty(Video.ExtractGeneratedVideos(Parsed(VideoCandidate("x", true, "https://evil.example/video.mp4"))));
        Assert.Empty(Video.ExtractGeneratedVideos(Parsed(VideoCandidate("https://lh3.googleusercontent.com/x.mp4", false))));
    }

    [Fact]
    public void ParsesConversationReadBack()
    {
        var (candidates, text) = Video.ParseConversationCandidates(ConversationBody("Your video is ready!\n\nhttp://googleusercontent.com/generated_video_content/1", true));

        Assert.Single(candidates.SelectMany(Video.ExtractGeneratedVideos));
        Assert.StartsWith("Your video is ready!", text);
        Assert.Equal("Your video is ready!", Video.StripPlaceholders(text));
    }

    [Fact]
    public void ExtractsGeneratedAudio()
    {
        var candidate = Sized(13);
        candidate[0]  = "rc";
        candidate[1]  = new object?[] { "Here is your track " + AUDIO_URL };
        candidate[12] = new object?[] { new object?[] { null, "track.mp3", new object?[] { AUDIO_URL }, "audio/mpeg" } };

        var audio = Assert.Single(Music.ExtractGeneratedAudio(Parsed(candidate)));
        Assert.Equal((AUDIO_URL, "audio/mpeg", "track.mp3"), (audio.Url, audio.MimeType, audio.FileName));

        candidate[12] = null;
        Assert.Empty(Music.ExtractGeneratedAudio(Parsed(candidate)));
    }

    [Theory]
    [InlineData("While I am currently unable to synthesize and output an audio file directly, here are lo-fi sources", MediaGenerationException.REFUSED)]
    [InlineData("I seem to be encountering an error. Can I try something else for you?", MediaGenerationException.UPSTREAM)]
    [InlineData("I can create more videos as soon as your limit resets.", MediaGenerationException.QUOTA)]
    public void ClassifiesMusicFailures(string text, string code) => Assert.Equal(code, Music.ClassifyReply(text)?.Code);

    [Fact]
    public void SuccessReplyIsNotAFailure() => Assert.Null(Music.ClassifyReply("Here is your lo-fi track!"));

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    [Fact]
    public void ReadsMp4Duration()
    {
        var mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), 10000);

        var file = Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\u0002\0")).Concat(Box("moov", Box("mvhd", mvhd))).ToArray();

        Assert.True(Video.IsMp4(file));
        Assert.Equal(10, Video.Mp4Duration(file));
        Assert.Equal(0, Video.Mp4Duration(file.AsSpan(0, 20)));
    }

    [Theory]
    [InlineData("", "", VideoAspectRatio.Landscape)]
    [InlineData("720x1280", "", VideoAspectRatio.Portrait)]
    [InlineData("", "9:16", VideoAspectRatio.Portrait)]
    [InlineData("1792x1024", "16:9", VideoAspectRatio.Landscape)]
    public void ParsesAspect(string size, string ratio, VideoAspectRatio want) => Assert.Equal(want, Video.ParseAspect(size, ratio));

    [Theory]
    [InlineData("640x480", "")]
    [InlineData("", "4:3")]
    [InlineData("1280x720", "9:16")]
    public void RejectsBadAspect(string size, string ratio) => Assert.Throws<VideoValidationException>(() => Video.ParseAspect(size, ratio));

    private static GeminiClient ClientWithModels()
    {
        var client = new GeminiClient(new Configuration.GeminiOptions(), NullLogger<GeminiClient>.Instance, new FakeGemini());
        client.SetSessionForTesting(new GeminiClient.SessionState("token", "cookie=1", "push", "", "", "en", TestModels()));
        return client;
    }

    private static async Task<VideoJob> WaitFor(GeminiClient client, string id, string status)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = client.VideoJobs.Get(id);

            if (job.Status == status) return job;

            await Task.Delay(10);
        }

        throw new TimeoutException("job did not reach " + status);
    }

    [Fact]
    public async Task VideoJobLifecycle()
    {
        using var client  = ClientWithModels();
        var       release = new TaskCompletionSource();
        VideoAspectRatio? seenAspect = null;

        client.SetVideoGeneratorForTesting(async (prompt, model, aspect, ct) =>
        {
            seenAspect = aspect;
            await release.Task.WaitAsync(ct);
            return new VideoResult { ConversationId = "c_test", Data = "mp4"u8.ToArray(), Model = model };
        });

        var job = client.StartVideoJob(" A cat ", "veo-3.0-generate-001", VideoAspectRatio.Portrait);

        Assert.Equal((VideoJobStatus.IN_PROGRESS, "A cat", "gemini-flash", "720x1280"), (job.Status, job.Prompt, job.Model, job.Size));
        Assert.Throws<VideoBusyException>(() => client.StartVideoJob("Another", "", VideoAspectRatio.Landscape));
        Assert.Throws<VideoNotReadyException>(() => client.VideoJobs.Content(job.Id));
        Assert.Throws<VideoNotReadyException>(() => client.VideoJobs.Delete(job.Id));

        release.SetResult();

        var done = await WaitFor(client, job.Id, VideoJobStatus.COMPLETED);

        Assert.Equal(VideoAspectRatio.Portrait, seenAspect);
        Assert.Equal(("c_test", 3), (done.ConversationId, done.Bytes));
        Assert.NotNull(done.ExpiresAt);
        Assert.Equal("mp4", Encoding.ASCII.GetString(client.VideoJobs.Content(job.Id).Data));
        Assert.Equal(job.Id, Assert.Single(client.VideoJobs.List()).Id);

        client.VideoJobs.Delete(job.Id);
        Assert.Throws<VideoJobNotFoundException>(() => client.VideoJobs.Get(job.Id));
    }

    [Fact]
    public async Task VideoJobFailureKeepsProviderError()
    {
        using var client = ClientWithModels();

        client.SetVideoGeneratorForTesting((_, _, _, _) => throw new MediaGenerationException(MediaGenerationException.QUOTA, "limit", "limit resets"));

        var job    = client.StartVideoJob("A cat", "gemini-pro", VideoAspectRatio.Landscape);
        var failed = await WaitFor(client, job.Id, VideoJobStatus.FAILED);

        Assert.Equal((MediaGenerationException.QUOTA, "limit resets"), (failed.Error?.Code, failed.Message));
    }

    [Fact]
    public void VideoJobValidation()
    {
        using var client = ClientWithModels();

        Assert.Throws<VideoValidationException>(() => client.StartVideoJob("  ", "", VideoAspectRatio.Landscape));
        Assert.Throws<ModelSelectionException>(() => client.StartVideoJob("A cat", "unknown-model", VideoAspectRatio.Landscape));
    }
}
