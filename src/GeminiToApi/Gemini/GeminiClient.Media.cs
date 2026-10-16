using Microsoft.Extensions.Logging;

namespace GeminiToApi.Gemini;

public sealed partial class GeminiClient
{
    private const int MAX_GENERATED_VIDEO_BYTES = 100 << 20;
    private const int MAX_GENERATED_AUDIO_BYTES = 50 << 20;

    private static readonly TimeSpan MediaDownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MusicJobTimeout      = TimeSpan.FromMinutes(5);

    // ---------------------------------------------------------------- video

    /// <summary>
    /// Submits one video request with the native "Videos" tool, polls the same conversation until the
    /// video card appears and downloads it. The prompt is never resubmitted, so one call consumes at
    /// most one quota unit.
    /// </summary>
    public async Task<VideoResult> GenerateVideoAsync(string prompt, string? model, VideoAspectRatio aspect, CancellationToken cancellationToken)
    {
        GeminiResponse response;

        try
        {
            response = await GenerateContentAsync(prompt, new GenerateOptions { Model = model, Video = aspect }, cancellationToken);
        }
        catch (ModelSelectionException)
        {
            throw;
        }
        catch (GeminiException e)
        {
            throw new MediaGenerationException(MediaGenerationException.UPSTREAM, "Gemini could not start video generation: " + e.Message);
        }

        var result = new VideoResult { ConversationId = response.ConversationId, Model = response.Model, Text = Video.StripPlaceholders(response.Text) };

        _log.LogInformation("Video generation submitted (conversation {Conversation}, {Videos} video(s)): {Reply}", result.ConversationId, response.Videos.Count, Video.SafeProviderMessage(response.Text));

        var videos = response.Videos;

        if (videos.Count == 0)
        {
            if (Video.ClassifyReply(response.Text) is { } refusal) throw refusal;

            if (result.ConversationId.Length == 0)
                throw new MediaGenerationException(MediaGenerationException.NO_VIDEO, "Gemini returned neither a video nor a conversation to poll", Video.SafeProviderMessage(response.Text));

            videos = await PollAsync(
                result.ConversationId,
                candidates => candidates.SelectMany(Video.ExtractGeneratedVideos).ToList(),
                text => { if (text.Length > 0) result.Text = Video.StripPlaceholders(text); },
                Video.ClassifyReply,
                "Video was not ready before the deadline; it may still appear in Gemini Web conversation " + result.ConversationId,
                cancellationToken);
        }

        result.Video = videos[0];

        byte[] data;

        try
        {
            var (bytes, contentType) = await DownloadMediaAsync(MediaUrls.WithAuthUser(result.Video.Url, AuthUser), _session?.CookieHeader ?? "", MAX_GENERATED_VIDEO_BYTES, MediaDownloadTimeout, cancellationToken);

            if (contentType.StartsWith("text/", StringComparison.Ordinal) || contentType.Contains("json", StringComparison.Ordinal))
                throw new GeminiException($"download returned {contentType} instead of a video");

            if (!Video.IsMp4(bytes)) throw new GeminiException($"downloaded data is not an MP4 video ({contentType})");

            data = bytes;
        }
        catch (Exception e) when (e is GeminiException or HttpRequestException)
        {
            throw new MediaGenerationException(MediaGenerationException.DOWNLOAD,
                "Generated video could not be downloaded: " + e.Message + "; it is still available in Gemini Web conversation " + result.ConversationId);
        }

        result.Data = data;

        // The card in a StreamGenerate reply may omit the duration; read it from the file.
        if (result.Video.Duration == 0) result.Video.Duration = Video.Mp4Duration(data);

        return result;
    }

    private async Task<List<T>> PollAsync<T>(
        string                                                     conversationId,
        Func<List<System.Text.Json.Nodes.JsonArray>, List<T>>      extract,
        Action<string>                                             onText,
        Func<string, MediaGenerationException?>                    classify,
        string                                                     timeoutMessage,
        CancellationToken                                          cancellationToken)
    {
        using var timer = new PeriodicTimer(MediaPollInterval);

        while (true)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new MediaGenerationException(MediaGenerationException.TIMEOUT, timeoutMessage);
            }

            List<System.Text.Json.Nodes.JsonArray> candidates;
            string text;

            try
            {
                (candidates, text) = await ReadConversationAsync(conversationId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new MediaGenerationException(MediaGenerationException.TIMEOUT, timeoutMessage);
            }
            catch (Exception e) when (e is GeminiException or HttpRequestException or OperationCanceledException)
            {
                _log.LogWarning("Status poll for conversation {Conversation} failed: {Error}", conversationId, e.Message);
                continue;
            }

            onText(text);

            var found = extract(candidates);

            if (found.Count > 0) return found;

            if (classify(text) is { } refusal) throw refusal;

            _log.LogDebug("Media still generating in conversation {Conversation}", conversationId);
        }
    }

    // ---------------------------------------------------------------- music

    /// <summary>
    /// Submits one request with the native "Create music" tool, waits for the audio (polling the
    /// conversation if the stream ends early) and downloads it. The prompt is never resubmitted.
    /// </summary>
    public async Task<MusicResult> GenerateMusicAsync(string prompt, string? model, MusicConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new MusicValidationException("prompt is required");

        Music.Validate(config);

        model = Music.SplitModel(model).Model;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MusicJobTimeout);

        GeminiResponse response;

        try
        {
            response = await GenerateContentAsync(prompt, new GenerateOptions { Model = model, Music = config }, timeout.Token);
        }
        catch (ModelSelectionException)
        {
            throw;
        }
        catch (GeminiException e)
        {
            throw new MediaGenerationException(MediaGenerationException.UPSTREAM, "Gemini could not start music generation: " + e.Message);
        }

        var result = new MusicResult { ConversationId = response.ConversationId, Model = response.Model, Text = response.Text };

        _log.LogInformation("Music generation submitted (conversation {Conversation}, {Audios} audio(s)): {Reply}", result.ConversationId, response.Audios.Count, Video.SafeProviderMessage(response.Text));

        var audios = response.Audios;

        if (audios.Count == 0)
        {
            if (Music.ClassifyReply(response.Text) is { } refusal) throw refusal;

            if (result.ConversationId.Length == 0)
                throw new MediaGenerationException(MediaGenerationException.NO_VIDEO, "Gemini returned no music", Video.SafeProviderMessage(response.Text));

            audios = await PollAsync(
                result.ConversationId,
                candidates => candidates.SelectMany(Music.ExtractGeneratedAudio).ToList(),
                text => { if (text.Length > 0) result.Text = text; },
                Music.ClassifyReply,
                "Music was not ready before the deadline; it may still appear in Gemini Web conversation " + result.ConversationId,
                timeout.Token);
        }

        result.Audio = audios[0];

        try
        {
            var (data, contentType) = await DownloadMediaAsync(MediaUrls.WithAuthUser(result.Audio.Url, AuthUser), _session?.CookieHeader ?? "", MAX_GENERATED_AUDIO_BYTES, MediaDownloadTimeout, timeout.Token);

            if (contentType.StartsWith("text/", StringComparison.Ordinal) || contentType.Contains("json", StringComparison.Ordinal))
                throw new GeminiException($"download returned {contentType} instead of audio");

            result.Data = data;

            if (string.IsNullOrEmpty(result.Audio.MimeType)) result.Audio.MimeType = contentType;
        }
        catch (Exception e) when (e is GeminiException or HttpRequestException)
        {
            throw new MediaGenerationException(MediaGenerationException.DOWNLOAD,
                "Generated music could not be downloaded: " + e.Message + "; it is still available in Gemini Web conversation " + result.ConversationId);
        }

        return result;
    }

    // ---------------------------------------------------------------- background video jobs

    internal VideoJobStore VideoJobs => _videoJobs ??= new VideoJobStore((prompt, model, aspect, ct) => GenerateVideoAsync(prompt, model, aspect, ct), _log);

    /// <summary>Test hook: replaces the generator behind the video job store.</summary>
    internal void SetVideoGeneratorForTesting(VideoJobStore.Generator generator) => _videoJobs = new VideoJobStore(generator, _log);

    /// <summary>Validates the request and generates the video in the background.</summary>
    public VideoJob StartVideoJob(string? prompt, string? model, VideoAspectRatio aspect)
    {
        prompt = (prompt ?? "").Trim();

        if (prompt.Length == 0) throw new VideoValidationException("prompt is required");

        if (System.Text.Encoding.UTF8.GetByteCount(prompt) > VideoJobStore.MAX_PROMPT_BYTES)
            throw new VideoValidationException($"prompt must be at most {VideoJobStore.MAX_PROMPT_BYTES} bytes");

        // SDK defaults name the official video models; Gemini Web always uses its own video model, so
        // those names go to the account's default model.
        var lower = (model ?? "").Trim().ToLowerInvariant();

        if (lower.StartsWith("models/", StringComparison.Ordinal)) lower = lower[7..];

        if (lower.StartsWith("sora", StringComparison.Ordinal) || lower.StartsWith("veo", StringComparison.Ordinal)) model = "";

        var resolved = ResolveModel(model);

        return VideoJobs.Start(prompt, resolved.Id, aspect);
    }
}
