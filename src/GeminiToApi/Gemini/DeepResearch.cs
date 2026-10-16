using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GeminiToApi.Gemini;

public sealed class DeepResearchOptions
{
    public string?         Model      { get; set; }
    public string          Language   { get; set; } = "English";
    public int             MaxSources { get; set; } = 10;
    public List<InputFile> InputFiles { get; set; } = new();
}

public sealed record SourceInfo(string Title, string Url, string Snippet, string Domain);

public sealed record StepInfo(int StepNumber, string Type, string Description, string Query = "", string Result = "");

public sealed class DeepResearchResult
{
    public string           Id          { get; set; } = "";
    public string           Query       { get; set; } = "";
    public string           Summary     { get; set; } = "";
    public List<SourceInfo> Sources     { get; set; } = new();
    public List<StepInfo>   Steps       { get; set; } = new();
    public string           Model       { get; set; } = "";
    public long             CreatedAt   { get; set; }
    public long             CompletedAt { get; set; }
    public long             DurationMs  { get; set; }
}

public static class ResearchEventType
{
    public const string PROGRESS = "progress";
    public const string SOURCE   = "source";
    public const string STEP     = "step";
    public const string RESULT   = "result";
    public const string ERROR    = "error";
    public const string DONE     = "done";
}

public sealed record DeepResearchEvent(
    string              Event,
    string              Message  = "",
    int                 Progress = 0,
    StepInfo?           Step     = null,
    SourceInfo?         Source   = null,
    DeepResearchResult? Result   = null,
    string              Error    = "");

/// <summary>
/// Multi-step research built from ordinary prompts: plan sub-questions, research each, then synthesize
/// a report. The prompt texts are ported verbatim from the Go server.
/// </summary>
public sealed partial class DeepResearch(GeminiClient client, ILogger<DeepResearch> log)
{
    /// <summary>Return false from the callback to stop.</summary>
    public delegate Task<bool> EventCallback(DeepResearchEvent researchEvent);

    private sealed record Plan(List<string> SubQuestions, List<string> Outline);

    private sealed record SubResult(string Question, string Answer, List<SourceInfo> Sources);

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)] private static partial Regex JsonObjectRegex();
    [GeneratedRegex(@"[^a-zA-Z0-9]")]                   private static partial Regex SanitizeRegex();

    private static string Quote(string value) => JsonSerializer.Serialize(value, Jspb.WriteOptions);

    private async Task<string> AskAsync(string prompt, string model, CancellationToken ct, List<InputFile>? files = null)
    {
        var response = await client.GenerateContentAsync(prompt, new GenerateOptions { Model = model, InputFiles = files ?? new() }, ct);
        return response.Text;
    }

    private async Task<Plan> GeneratePlanAsync(string query, string language, string model, CancellationToken ct)
    {
        var prompt = $$"""
            You are a deep research planner. Given a research topic, produce a structured JSON research plan.

            RESEARCH TOPIC: {{Quote(query)}}

            OUTPUT: valid JSON only (no markdown fences), with this exact structure:
            {
              "sub_questions": [
                "Specific question 1 to investigate",
                "Specific question 2 to investigate",
                "Specific question 3 to investigate",
                "Specific question 4 to investigate",
                "Specific question 5 to investigate"
              ],
              "outline": [
                "Section 1 title",
                "Section 2 title",
                "Section 3 title",
                "Section 4 title"
              ]
            }

            Generate 4-6 targeted sub-questions that together provide comprehensive coverage.
            Answer in {{language}}.
            """;

        var text = await AskAsync(prompt, model, ct);

        try
        {
            using var doc  = JsonDocument.Parse(ExtractJson(text));
            var root       = doc.RootElement;
            var questions  = root.TryGetProperty("sub_questions", out var q) && q.ValueKind == JsonValueKind.Array ? q.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList() : new List<string>();
            var outline    = root.TryGetProperty("outline", out var o) && o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : new List<string>();

            return new Plan(questions, outline);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // Not JSON: treat each line as a question.
            var questions = text.Trim().Split('\n')
                .Select(l => l.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ').Trim())
                .Where(l => l.Length > 0)
                .ToList();

            if (questions.Count == 0) questions.Add(query);

            return new Plan(questions, new List<string>());
        }
    }

    private async Task<SubResult> ResearchSubQuestionAsync(string question, string language, string model, CancellationToken ct)
    {
        var prompt = $$"""
            You are a research specialist. Thoroughly research the following question and provide a detailed, factual answer with cited sources.

            QUESTION: {{Quote(question)}}

            Respond with valid JSON only (no markdown fences):
            {
              "answer": "Comprehensive, detailed answer in {{language}} (minimum 300 words). Use facts and specific data.",
              "sources": [
                {"title": "Source title", "url": "https://example.com/page", "snippet": "Relevant excerpt", "domain": "example.com"},
                {"title": "Source title 2", "url": "https://example2.com/page", "snippet": "Relevant excerpt", "domain": "example2.com"}
              ]
            }

            Provide 2-4 realistic sources that would typically be consulted for this topic.
            """;

        var text = await AskAsync(prompt, model, ct);

        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(text));
            var root      = doc.RootElement;
            var sources   = new List<SourceInfo>();

            if (root.TryGetProperty("sources", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in list.EnumerateArray())
                {
                    string Get(string name) => s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

                    var url    = Get("url");
                    var domain = Get("domain");

                    if (domain.Length == 0 && Uri.TryCreate(url, UriKind.Absolute, out var uri)) domain = uri.Authority;

                    sources.Add(new SourceInfo(Get("title"), url, Get("snippet"), domain));
                }
            }

            var answer = root.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "";

            return new SubResult(question, answer, sources);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return new SubResult(question, text, new List<SourceInfo>());
        }
    }

    private async Task<string> AnalyzeInputFilesAsync(string query, string language, string model, List<InputFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return "";

        var prompt = $"""
            Analyze the attached image(s) as research context for the following topic.

            RESEARCH TOPIC: {Quote(query)}

            Return a detailed textual description in {language} that captures:
            - visible objects, text, people, UI elements, charts, diagrams, or documents
            - any facts or clues relevant to the research topic
            - uncertainties or details that need external verification

            This description will be used as context for a deep research workflow.
            """;

        return (await AskAsync(prompt, model, ct, files)).Trim();
    }

    private async Task<string> SynthesizeAsync(string query, string language, string model, Plan plan, List<SubResult> results, CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.Append("RESEARCH TOPIC: ").Append(Quote(query)).Append("\n\n");
        sb.Append("REPORT OUTLINE: ").Append(string.Join(" | ", plan.Outline)).Append("\n\n");
        sb.Append("RESEARCH FINDINGS:\n");

        for (var i = 0; i < results.Count; i++)
        {
            sb.Append($"\n### Finding {i + 1}: {Quote(results[i].Question)}\n{Quote(results[i].Answer)}\n");
        }

        var prompt = $"""
            You are a research writer. Using the research findings below, write a comprehensive, well-structured research report.

            {sb}

            Write the final report in {language}. Requirements:
            - Use markdown with clear headings (##, ###)
            - Minimum 800 words
            - Include an Executive Summary
            - Cover all findings in the outline structure
            - End with a clear Conclusion and Recommendations
            - Cite specific facts and data points from the findings

            Output the report text directly (no JSON wrapper).
            """;

        return await AskAsync(prompt, model, ct);
    }

    /// <summary>Runs the research. Events are optional; the final result is also returned.</summary>
    public async Task<DeepResearchResult?> RunAsync(string query, DeepResearchOptions options, EventCallback? onEvent, CancellationToken ct)
    {
        var emit = onEvent ?? (_ => Task.FromResult(true));

        var started   = DateTimeOffset.UtcNow;
        var model     = string.IsNullOrWhiteSpace(options.Model) ? client.ListModels().FirstOrDefault()?.Id ?? "" : options.Model;
        var original  = query;
        var steps     = new List<StepInfo>();
        var sources   = new List<SourceInfo>();
        var stepNum   = 0;

        log.LogInformation("Deep research starting: {Query} (model {Model})", query, model);

        try
        {
            if (options.InputFiles.Count > 0)
            {
                if (!await emit(new DeepResearchEvent(ResearchEventType.PROGRESS, "Analyzing attached image context...", 3))) return null;

                var context = await AnalyzeInputFilesAsync(query, options.Language, model, options.InputFiles, ct);

                if (context.Length > 0) query = $"{query}\n\nAttached image context:\n{context}";

                var step = new StepInfo(++stepNum, "image_analysis", $"Analyzed {options.InputFiles.Count} attached image(s) as research context", original, $"Image context: {context.Length} chars");
                steps.Add(step);

                if (!await emit(new DeepResearchEvent(ResearchEventType.STEP, step.Description, 5, step))) return null;
            }

            if (!await emit(new DeepResearchEvent(ResearchEventType.PROGRESS, "Planning research strategy...", 5))) return null;

            var plan     = await GeneratePlanAsync(query, options.Language, model, ct);
            var planStep = new StepInfo(++stepNum, "plan", $"Research plan: {plan.SubQuestions.Count} sub-questions identified", "", string.Join(" | ", plan.SubQuestions));
            steps.Add(planStep);

            if (!await emit(new DeepResearchEvent(ResearchEventType.STEP, planStep.Description, 10, planStep))) return null;

            var results = new List<SubResult>();
            var total   = plan.SubQuestions.Count;

            for (var i = 0; i < total; i++)
            {
                var question = plan.SubQuestions[i];
                var pct      = 10 + (int)((double)i / total * 70);

                if (!await emit(new DeepResearchEvent(ResearchEventType.PROGRESS, $"Researching [{i + 1}/{total}]: {question}", pct))) return null;

                SubResult sub;

                try
                {
                    sub = await ResearchSubQuestionAsync(question, options.Language, model, ct);
                }
                catch (GeminiException e)
                {
                    log.LogWarning("Sub-question research failed, skipping: {Error}", e.Message);
                    continue;
                }

                results.Add(sub);
                sources.AddRange(sub.Sources);

                var step = new StepInfo(++stepNum, "search", $"Researched: {question}", question, $"Found {sub.Sources.Count} sources, {sub.Answer.Length} chars");
                steps.Add(step);

                if (!await emit(new DeepResearchEvent(ResearchEventType.STEP, step.Description, pct + 5, step))) return null;

                foreach (var source in sub.Sources) await emit(new DeepResearchEvent(ResearchEventType.SOURCE, source.Title, pct + 5, Source: source));
            }

            if (results.Count == 0) throw new GeminiException("all sub-question research failed");

            if (!await emit(new DeepResearchEvent(ResearchEventType.PROGRESS, "Synthesizing findings into final report...", 85))) return null;

            var summary   = await SynthesizeAsync(query, options.Language, model, plan, results, ct);
            var synthStep = new StepInfo(++stepNum, "synthesize", "Synthesized all findings into comprehensive report", "", $"Report: {summary.Length} chars");
            steps.Add(synthStep);

            await emit(new DeepResearchEvent(ResearchEventType.STEP, synthStep.Description, 95, synthStep));

            var deduplicated = sources.GroupBy(s => s.Url.Length > 0 ? s.Url : s.Title).Select(g => g.First()).ToList();

            if (options.MaxSources > 0 && deduplicated.Count > options.MaxSources) deduplicated = deduplicated.Take(options.MaxSources).ToList();

            var result = new DeepResearchResult
            {
                Id          = GenerateId(original, started.ToUnixTimeSeconds()),
                Query       = original,
                Summary     = summary,
                Sources     = deduplicated,
                Steps       = steps,
                Model       = model,
                CreatedAt   = started.ToUnixTimeSeconds(),
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                DurationMs  = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            };

            log.LogInformation("Deep research completed: {Steps} steps, {Sources} sources, {Duration} ms", steps.Count, deduplicated.Count, result.DurationMs);

            await emit(new DeepResearchEvent(ResearchEventType.RESULT, "Research complete", 100, Result: result));
            await emit(new DeepResearchEvent(ResearchEventType.DONE, "done", 100));

            return result;
        }
        catch (GeminiException e)
        {
            await emit(new DeepResearchEvent(ResearchEventType.ERROR, Error: e.Message));
            throw;
        }
    }

    internal static string ExtractJson(string text)
    {
        var obj = TextUtils.ExtractFirstJsonObject(text);

        if (obj.Length > 0) return obj;

        var s = text.Trim();

        if (s.StartsWith("```json", StringComparison.Ordinal)) s = s[7..];
        else if (s.StartsWith("```", StringComparison.Ordinal)) s = s[3..];

        if (s.EndsWith("```", StringComparison.Ordinal)) s = s[..^3];

        s = s.Trim();

        var match = JsonObjectRegex().Match(s);
        return match.Success ? match.Value : s;
    }

    internal static string GenerateId(string query, long timestamp)
    {
        var part = query.Length > 8 ? query[..8] : query;
        return "research-" + SanitizeRegex().Replace(part, "").ToLowerInvariant() + "-" + timestamp.ToString("x");
    }
}
