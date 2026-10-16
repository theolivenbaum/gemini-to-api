namespace GeminiToApi.Gemini;

/// <summary>Public description of a model the signed-in account can select.</summary>
public sealed record ModelInfo(string Id, string DisplayName, long Created, string OwnedBy = "google", string Provider = "gemini");

/// <summary>A model with the routing identity GetUserStatus returned for this account.</summary>
public sealed record GeminiModel(
    ModelInfo                Info,
    string                   ModelId,
    int                      Capacity,
    int                      CapacityField,
    int                      ModelNumber,
    IReadOnlyList<string>    Aliases)
{
    public string Id => Info.Id;
}

public sealed record InputFile(string Name, string MimeType, byte[] Data);

public sealed class GeneratedImage
{
    public string  Url       { get; set; } = "";
    public string? B64Json   { get; set; }
    public bool    Generated { get; set; }
    public string? Title     { get; set; }
    public int     Width     { get; set; }
    public int     Height    { get; set; }
    public string? MimeType  { get; set; }
}

public sealed class GeneratedVideo
{
    public string  Url          { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public string? FileName     { get; set; }
    public string? MimeType     { get; set; }
    public int     Width        { get; set; }
    public int     Height       { get; set; }
    public double  Duration     { get; set; }
    public long    SizeBytes    { get; set; }
}

public sealed class GeneratedAudio
{
    public string  Url      { get; set; } = "";
    public string? FileName { get; set; }
    public string? MimeType { get; set; }
}

public enum VideoAspectRatio
{
    Landscape = 1, // 16:9, 1280x720
    Portrait  = 2, // 9:16, 720x1280
}

public sealed class MusicConfig
{
    public string Length { get; set; } = "";
    public string Vocals { get; set; } = "";
    public string Genre  { get; set; } = "";
}

/// <summary>
/// Identifies a turn of an existing Gemini Web conversation: sending it as the request metadata
/// (inner[2]) continues that conversation instead of starting a new one.
/// </summary>
public sealed record ConversationRef(string ConversationId, string ResponseId, string ChoiceId);

/// <summary>Per-request generation options (the Go <c>GenerateConfig</c>).</summary>
public sealed class GenerateOptions
{
    public string?            Model                   { get; set; }
    public string?            ThinkingLevel           { get; set; }
    public List<InputFile>    InputFiles              { get; set; } = new();
    public bool               DownloadGeneratedImages { get; set; }
    public VideoAspectRatio?  Video                   { get; set; }
    public bool               Canvas                  { get; set; }
    public MusicConfig?       Music                   { get; set; }

    /// <summary>Continue this conversation (after the given reply) instead of starting a new one.</summary>
    public ConversationRef?   Continue                { get; set; }
}

/// <summary>A parsed Gemini Web reply.</summary>
public sealed class GeminiResponse
{
    public string               Model          { get; set; } = "";
    public string               ModelId        { get; set; } = "";
    public string               RequestedModel { get; set; } = "";
    public string               Text           { get; set; } = "";
    public string               ReasoningText  { get; set; } = "";
    public List<GeneratedImage> Images         { get; set; } = new();
    public List<GeneratedVideo> Videos         { get; set; } = new();
    public List<Canvas>         Canvases       { get; set; } = new();
    public List<GeneratedAudio> Audios         { get; set; } = new();
    public string               ConversationId { get; set; } = "";
    public string               ResponseId     { get; set; } = "";
    public string               ChoiceId       { get; set; } = "";
}

/// <summary>Base class for errors reported by the Gemini Web client.</summary>
public class GeminiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A requested model name that the account's registry cannot resolve (a client error).</summary>
public sealed class ModelSelectionException(string message) : GeminiException(message);

/// <summary>An invalid thinking level / reasoning effort (a client error).</summary>
public sealed class ThinkingLevelException(string message) : GeminiException(message);

public sealed class VideoValidationException(string message) : GeminiException(message);

public sealed class MusicValidationException(string message) : GeminiException(message);

public sealed class VideoBusyException() : GeminiException("a video is already being generated; wait for it to finish");

public sealed class VideoJobNotFoundException() : GeminiException("video job not found");

public sealed class VideoNotReadyException() : GeminiException("video is not ready");

/// <summary>Categorized video/music generation failure.</summary>
public sealed class MediaGenerationException(string code, string message, string? providerMessage = null) : GeminiException(message)
{
    public const string QUOTA    = "quota_exceeded";
    public const string REFUSED  = "refused";
    public const string TIMEOUT  = "timeout";
    public const string NO_VIDEO = "no_video";
    public const string UPSTREAM = "upstream_error";
    public const string DOWNLOAD = "download_failed";

    public string  Code            { get; } = code;
    public string? ProviderMessage { get; } = providerMessage;
}
