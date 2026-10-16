using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiToApi.Gemini;

/// <summary>URL rules for images/videos/audio found in Gemini replies.</summary>
internal static partial class MediaUrls
{
    [GeneratedRegex(@"(?i)(?:https?:)?//[^\s""'<>\\]+|(?:[a-z0-9.-]+\.)?googleusercontent\.com/[^\s""'<>\\]+")]
    public static partial Regex ImageUrlRegex();

    public static bool IsTrustedGoogleHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();

        return host == "google.com" || host.EndsWith(".google.com", StringComparison.Ordinal)
            || host == "googleusercontent.com" || host.EndsWith(".googleusercontent.com", StringComparison.Ordinal);
    }

    /// <summary>An https URL on a trusted Google media host, without user info.</summary>
    public static bool IsTrustedMediaUrl(string? raw)
        => Uri.TryCreate(raw, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.IsNullOrEmpty(uri.UserInfo)
           && IsTrustedGoogleHost(uri.Host);

    public static string NormalizeImageUrl(string raw)
    {
        var cleaned = WebUtility.HtmlDecode(raw.Trim()).TrimEnd('.', ',', ')', ';', ']');

        if (cleaned.StartsWith("//", StringComparison.Ordinal)) cleaned = "https:" + cleaned;

        var lower     = cleaned.ToLowerInvariant();
        var firstPart = lower.Split('/', 2)[0];

        if (lower.StartsWith("googleusercontent.com/", StringComparison.Ordinal) || firstPart.EndsWith(".googleusercontent.com", StringComparison.Ordinal))
            cleaned = "https://" + cleaned;

        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "";

        if (!LooksLikeImage(uri)) return "";

        if (uri.Host.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase) && uri.Scheme != Uri.UriSchemeHttps)
        {
            cleaned = "https" + cleaned[uri.Scheme.Length..];
        }

        return cleaned;
    }

    private static bool LooksLikeImage(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (path.EndsWith(".svg", StringComparison.Ordinal) || host.Contains("fonts.gstatic.com", StringComparison.Ordinal)) return false;
        if (host == "googleusercontent.com") return false;
        if (host.EndsWith(".googleusercontent.com", StringComparison.Ordinal)) return true;

        return path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".webp") || path.EndsWith(".gif");
    }

    public static string? MimeTypeFromImageUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;

        var path = uri.AbsolutePath.ToLowerInvariant();

        if (path.EndsWith(".png")) return "image/png";
        if (path.EndsWith(".jpg") || path.EndsWith(".jpeg")) return "image/jpeg";
        if (path.EndsWith(".webp")) return "image/webp";
        if (path.EndsWith(".gif")) return "image/gif";

        return null;
    }

    /// <summary>Appends <c>=s{size}</c> to a Google image URL unless it already carries a size.</summary>
    public static string AppendImageSize(string raw, int size)
    {
        if (size <= 0 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

        var lastSegment = uri.AbsolutePath.TrimEnd('/');
        lastSegment = lastSegment[(lastSegment.LastIndexOf('/') + 1)..];

        if (lastSegment.Contains("=s", StringComparison.Ordinal) || lastSegment.Contains("=w", StringComparison.Ordinal)) return raw;

        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "=s" + size };
        return builder.Uri.ToString();
    }

    /// <summary>Selects the Google multi-login account that owns a media file (otherwise 403 for slot 0).</summary>
    public static string WithAuthUser(string raw, string authUser)
    {
        if (authUser.Length == 0 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["authuser"] = authUser;

        return new UriBuilder(uri) { Query = query.ToString() }.Uri.ToString();
    }

    public static string ExtensionForMimeType(string? mimeType) => (mimeType ?? "").Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png"                 => ".png",
        "image/webp"                => ".webp",
        "image/gif"                 => ".gif",
        _                           => ".bin",
    };

    /// <summary>Walks every string in a payload and records anything that looks like an image URL.</summary>
    public static void CollectImages(JsonNode? value, Dictionary<string, GeneratedImage> images, List<string> order)
    {
        switch (value)
        {
            case JsonArray array:
                foreach (var item in array) CollectImages(item, images, order);
                break;
            case JsonObject obj:
                foreach (var (_, item) in obj) CollectImages(item, images, order);
                break;
            case JsonValue:
                var text = value.Str();

                if (text is null) break;

                foreach (Match match in ImageUrlRegex().Matches(text))
                {
                    var url = NormalizeImageUrl(match.Value);

                    if (url.Length == 0 || images.ContainsKey(url)) continue;

                    images[url] = new GeneratedImage { Url = url, MimeType = MimeTypeFromImageUrl(url) };
                    order.Add(url);
                }

                break;
        }
    }

    /// <summary>
    /// Reads the dedicated generated-media slot: candidate[12][7][0], each item carrying metadata at
    /// item[0][3] (filename, URL, MIME type, dimensions).
    /// </summary>
    public static List<GeneratedImage> ExtractGeneratedImages(JsonArray candidate)
    {
        var images = new List<GeneratedImage>();

        if (candidate.At(12) is not JsonArray media || media.At(7) is not JsonArray groups || groups.At(0) is not JsonArray generated) return images;

        foreach (var raw in generated)
        {
            if (raw.At(0) is not JsonArray wrapper || wrapper.At(3) is not JsonArray metadata || metadata.Count <= 3) continue;

            var url = metadata.At(3).Str();

            if (url is null || NormalizeImageUrl(url).Length == 0) continue;

            var image = new GeneratedImage { Url = NormalizeImageUrl(url), MimeType = "image/png", Generated = true, Title = metadata.At(2).Str() };

            foreach (var field in metadata)
            {
                if (field.Str() is { } s && s.StartsWith("image/", StringComparison.Ordinal))
                {
                    image.MimeType = s;
                }
                else if (field is JsonArray dims && dims.Count >= 2)
                {
                    if (dims.At(0).TryInt(out var width)) image.Width = width;
                    if (dims.At(1).TryInt(out var height)) image.Height = height;
                }
            }

            images.Add(image);
        }

        return images;
    }
}
