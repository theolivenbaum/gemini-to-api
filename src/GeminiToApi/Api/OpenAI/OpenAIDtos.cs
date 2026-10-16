using System.Text.Json.Serialization;
using GeminiToApi.Gemini;

namespace GeminiToApi.Api.OpenAI;

public sealed class ModelData
{
    [JsonPropertyName("id")]           public string  Id          { get; set; } = "";
    [JsonPropertyName("object")]       public string  Object      { get; set; } = "model";
    [JsonPropertyName("created")]      public long    Created     { get; set; }
    [JsonPropertyName("owned_by")]     public string  OwnedBy     { get; set; } = "google";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }

    public static ModelData From(ModelInfo m) => new() { Id = m.Id, Created = m.Created, OwnedBy = m.OwnedBy, DisplayName = m.DisplayName.Length > 0 ? m.DisplayName : null };
}

public sealed class ModelList
{
    [JsonPropertyName("object")] public string          Object { get; set; } = "list";
    [JsonPropertyName("data")]   public List<ModelData> Data   { get; set; } = new();
}

public sealed class ToolCallFunction
{
    [JsonPropertyName("name")]      public string? Name      { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

public sealed class ToolCall
{
    [JsonPropertyName("index")]    public int?             Index    { get; set; }
    [JsonPropertyName("id")]       public string           Id       { get; set; } = "";
    [JsonPropertyName("type")]     public string           Type     { get; set; } = "function";
    [JsonPropertyName("function")] public ToolCallFunction Function { get; set; } = new();
}

public sealed class ResponseMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "assistant";

    [JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_content")] public string?           ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")]        public List<ToolCall>?   ToolCalls        { get; set; }

    /// <summary>Files written by Gemini's Canvas tool ("-canvas" models).</summary>
    [JsonPropertyName("canvases")] public List<CanvasFile>? Canvases { get; set; }
}

public sealed class Choice
{
    [JsonPropertyName("index")]         public int             Index        { get; set; }
    [JsonPropertyName("message")]       public ResponseMessage Message      { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string          FinishReason { get; set; } = "stop";
}

public sealed class Usage
{
    [JsonPropertyName("prompt_tokens")]     public int PromptTokens     { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")]      public int TotalTokens      { get; set; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")]              public string       Id             { get; set; } = "";
    [JsonPropertyName("object")]          public string       Object         { get; set; } = "chat.completion";
    [JsonPropertyName("created")]         public long         Created        { get; set; }
    [JsonPropertyName("model")]           public string       Model          { get; set; } = "";
    [JsonPropertyName("requested_model")] public string?      RequestedModel { get; set; }
    [JsonPropertyName("choices")]         public List<Choice> Choices        { get; set; } = new();
    [JsonPropertyName("usage")]           public Usage        Usage          { get; set; } = new();
}

public sealed class ChunkDelta
{
    [JsonPropertyName("role")]              public string?           Role             { get; set; }
    [JsonPropertyName("content")]           public string?           Content          { get; set; }
    [JsonPropertyName("reasoning_content")] public string?           ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")]        public List<ToolCall>?   ToolCalls        { get; set; }
    [JsonPropertyName("canvases")]          public List<CanvasFile>? Canvases         { get; set; }
}

public sealed class ChunkChoice
{
    [JsonPropertyName("index")] public int        Index { get; set; }
    [JsonPropertyName("delta")] public ChunkDelta Delta { get; set; } = new();

    [JsonPropertyName("finish_reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FinishReason { get; set; }
}

public sealed class ChatCompletionChunk
{
    [JsonPropertyName("id")]      public string            Id      { get; set; } = "";
    [JsonPropertyName("object")]  public string            Object  { get; set; } = "chat.completion.chunk";
    [JsonPropertyName("created")] public long              Created { get; set; }
    [JsonPropertyName("model")]   public string            Model   { get; set; } = "";
    [JsonPropertyName("choices")] public List<ChunkChoice> Choices { get; set; } = new();
}

public sealed class ImageData
{
    [JsonPropertyName("url")]            public string? Url           { get; set; }
    [JsonPropertyName("b64_json")]       public string? B64Json       { get; set; }
    [JsonPropertyName("revised_prompt")] public string? RevisedPrompt { get; set; }
}

public sealed class ImageGenerationResponse
{
    [JsonPropertyName("created")] public long            Created { get; set; }
    [JsonPropertyName("data")]    public List<ImageData> Data    { get; set; } = new();
}

public sealed class VideoJobError
{
    [JsonPropertyName("code")]    public string Code    { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

/// <summary>The OpenAI Videos API object, plus Gemini-specific fields.</summary>
public sealed class VideoObject
{
    [JsonPropertyName("id")]         public string Id        { get; set; } = "";
    [JsonPropertyName("object")]     public string Object    { get; set; } = "video";
    [JsonPropertyName("model")]      public string Model     { get; set; } = "";
    [JsonPropertyName("status")]     public string Status    { get; set; } = "";
    [JsonPropertyName("progress")]   public int    Progress  { get; set; }
    [JsonPropertyName("created_at")] public long   CreatedAt { get; set; }

    [JsonPropertyName("completed_at"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long?          CompletedAt { get; set; }
    [JsonPropertyName("expires_at"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]   public long?          ExpiresAt   { get; set; }
    [JsonPropertyName("prompt")]                                                          public string         Prompt      { get; set; } = "";
    [JsonPropertyName("seconds")]                                                         public string         Seconds     { get; set; } = "8";
    [JsonPropertyName("size")]                                                            public string         Size        { get; set; } = "";
    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]        public VideoJobError? Error       { get; set; }

    [JsonPropertyName("conversation_id")] public string? ConversationId { get; set; }
    [JsonPropertyName("content_url")]     public string? ContentUrl     { get; set; }
    [JsonPropertyName("message")]         public string? Message        { get; set; }
}

public sealed class VideoList
{
    [JsonPropertyName("object")]                                                       public string            Object  { get; set; } = "list";
    [JsonPropertyName("data")]                                                         public List<VideoObject> Data    { get; set; } = new();
    [JsonPropertyName("first_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]  public string?           FirstId { get; set; }
    [JsonPropertyName("last_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]   public string?           LastId  { get; set; }
    [JsonPropertyName("has_more")]                                                     public bool              HasMore { get; set; }
}

public sealed class VideoDeleted
{
    [JsonPropertyName("id")]      public string Id      { get; set; } = "";
    [JsonPropertyName("object")]  public string Object  { get; set; } = "video.deleted";
    [JsonPropertyName("deleted")] public bool   Deleted { get; set; } = true;
}
