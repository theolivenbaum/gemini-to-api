using Microsoft.Extensions.Logging;

namespace GeminiToApi.Gemini;

public static class VideoJobStatus
{
    public const string IN_PROGRESS = "in_progress";
    public const string COMPLETED   = "completed";
    public const string FAILED      = "failed";
}

/// <summary>Snapshot of a background video generation.</summary>
public sealed record VideoJob
{
    public string                    Id             { get; init; } = "";
    public string                    Status         { get; init; } = VideoJobStatus.IN_PROGRESS;
    public string                    Model          { get; init; } = "";
    public string                    Prompt         { get; init; } = "";
    public VideoAspectRatio          Aspect         { get; init; } = VideoAspectRatio.Landscape;
    public DateTimeOffset            CreatedAt      { get; init; }
    public DateTimeOffset?           CompletedAt    { get; init; }
    public string                    ConversationId { get; init; } = "";
    public GeneratedVideo            Video          { get; init; } = new();
    public int                       Bytes          { get; init; }
    public string                    Message        { get; init; } = "";
    public MediaGenerationException? Error          { get; init; }

    /// <summary>"WIDTHxHEIGHT", using the generated video when known.</summary>
    public string Size => Video.Width > 0 && Video.Height > 0
        ? $"{Video.Width}x{Video.Height}"
        : Aspect == VideoAspectRatio.Portrait ? "720x1280" : "1280x720";

    /// <summary>When a finished job and its video are discarded.</summary>
    public DateTimeOffset? ExpiresAt => CompletedAt + VideoJobStore.Retention;
}

/// <summary>
/// In-memory video jobs shared by the OpenAI and Gemini APIs. Gemini Web allows few videos per day,
/// so only one job runs at a time.
/// </summary>
public sealed class VideoJobStore : IDisposable
{
    public delegate Task<VideoResult> Generator(string prompt, string model, VideoAspectRatio aspect, CancellationToken cancellationToken);

    public const int MAX_PROMPT_BYTES = 8000;
    public const int MAX_RETAINED     = 20;

    public static readonly TimeSpan Retention  = TimeSpan.FromHours(1);
    public static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public VideoJob Job  = new();
        public byte[]?  Data;
    }

    private readonly Generator                 _generate;
    private readonly ILogger                   _log;
    private readonly Lock                      _lock = new();
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource   _root = new();
    private readonly List<Task>                _running = new();
    private int                                _active;

    internal TimeSpan Timeout { get; set; } = JobTimeout;

    public VideoJobStore(Generator generate, ILogger log)
    {
        _generate = generate;
        _log      = log;
    }

    public VideoJob Start(string prompt, string model, VideoAspectRatio aspect)
    {
        lock (_lock)
        {
            Prune(DateTimeOffset.UtcNow);

            if (_root.IsCancellationRequested) throw new GeminiException("server is shutting down");

            if (_active > 0) throw new VideoBusyException();

            // Lowercase hex only: google-genai extracts file names with [a-z0-9]+.
            var id    = Guid.NewGuid().ToString("N");
            var entry = new Entry { Job = new VideoJob { Id = id, Model = model, Prompt = prompt, Aspect = aspect, CreatedAt = DateTimeOffset.UtcNow } };

            _jobs[id] = entry;
            _active++;
            _running.Add(Task.Run(() => RunAsync(id, prompt, model, aspect)));

            return entry.Job;
        }
    }

    private async Task RunAsync(string id, string prompt, string model, VideoAspectRatio aspect)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_root.Token);
        timeout.CancelAfter(Timeout);

        VideoResult? result = null;
        Exception?   error  = null;

        try
        {
            result = await _generate(prompt, model, aspect, timeout.Token);
        }
        catch (Exception e)
        {
            error = e;
        }

        lock (_lock)
        {
            _active--;

            if (!_jobs.TryGetValue(id, out var entry)) return;

            if (error is not null || result is null)
            {
                var mediaError = error as MediaGenerationException
                    ?? new MediaGenerationException(MediaGenerationException.UPSTREAM, error?.Message ?? "video generation failed");

                _log.LogWarning("Video job {Id} failed: {Error}", id, mediaError.Message);

                entry.Job = entry.Job with
                {
                    Status      = VideoJobStatus.FAILED,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error       = mediaError,
                    Message     = mediaError.ProviderMessage ?? "",
                };

                return;
            }

            entry.Data = result.Data;
            entry.Job  = entry.Job with
            {
                Status         = VideoJobStatus.COMPLETED,
                CompletedAt    = DateTimeOffset.UtcNow,
                ConversationId = result.ConversationId,
                Video          = result.Video,
                Bytes          = result.Data.Length,
                Message        = result.Text,
                Model          = result.Model.Length > 0 ? result.Model : entry.Job.Model,
            };
        }
    }

    public VideoJob Get(string id)
    {
        lock (_lock)
        {
            Prune(DateTimeOffset.UtcNow);
            return _jobs.TryGetValue(id, out var entry) ? entry.Job : throw new VideoJobNotFoundException();
        }
    }

    public (VideoJob Job, byte[] Data) Content(string id)
    {
        lock (_lock)
        {
            Prune(DateTimeOffset.UtcNow);

            if (!_jobs.TryGetValue(id, out var entry)) throw new VideoJobNotFoundException();

            if (entry.Job.Status != VideoJobStatus.COMPLETED || entry.Data is null) throw new VideoNotReadyException();

            return (entry.Job, entry.Data);
        }
    }

    /// <summary>Retained jobs, newest first.</summary>
    public List<VideoJob> List()
    {
        lock (_lock)
        {
            Prune(DateTimeOffset.UtcNow);
            return _jobs.Values.Select(e => e.Job).OrderByDescending(j => j.CreatedAt).ToList();
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var entry)) throw new VideoJobNotFoundException();

            if (entry.Job.Status == VideoJobStatus.IN_PROGRESS) throw new VideoNotReadyException();

            _jobs.Remove(id);
        }
    }

    /// <summary>Drops expired jobs and, beyond the cap, the oldest finished ones.</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var (id, entry) in _jobs.ToList())
        {
            if (entry.Job.ExpiresAt is { } expires && now > expires) _jobs.Remove(id);
        }

        while (_jobs.Count > MAX_RETAINED)
        {
            var oldest = _jobs.Where(kv => kv.Value.Job.CompletedAt is not null).OrderBy(kv => kv.Value.Job.CompletedAt).FirstOrDefault();

            if (oldest.Key is null) return;

            _jobs.Remove(oldest.Key);
        }
    }

    public void Dispose()
    {
        _root.Cancel();

        Task[] running;

        lock (_lock) running = _running.ToArray();

        Task.WaitAll(running, TimeSpan.FromSeconds(5));
        _root.Dispose();
    }
}
