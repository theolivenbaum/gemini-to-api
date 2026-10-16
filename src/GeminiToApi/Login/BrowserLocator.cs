using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GeminiToApi.Login;

public enum BrowserKind
{
    Chrome,
    Edge,
    Firefox,
}

public sealed record BrowserInstallation(BrowserKind Kind, string ExecutablePath)
{
    /// <summary>Chrome and Edge are attached over the Chrome DevTools Protocol; Firefox is not.</summary>
    public bool IsChromium => Kind is BrowserKind.Chrome or BrowserKind.Edge;
}

/// <summary>Finds an installed browser. Auto-detection is Windows-only for now.</summary>
public static class BrowserLocator
{
    public static BrowserKind ParseKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "chrome" or "google-chrome" => BrowserKind.Chrome,
        "edge" or "msedge"          => BrowserKind.Edge,
        "firefox"                   => BrowserKind.Firefox,
        _                           => throw new ArgumentException($"unknown browser \"{value}\": use chrome, edge or firefox"),
    };

    /// <summary>Guesses the kind from an executable name (chrome.exe, msedge, firefox, chromium…).</summary>
    public static BrowserKind KindFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

        if (name.Contains("edge")) return BrowserKind.Edge;
        if (name.Contains("firefox")) return BrowserKind.Firefox;

        return BrowserKind.Chrome;
    }

    /// <summary>All installed browsers in preference order: Chrome, Edge, Firefox.</summary>
    public static List<BrowserInstallation> Detect()
    {
        var found = new List<BrowserInstallation>();

        if (!OperatingSystem.IsWindows()) return found;

        foreach (var kind in new[] { BrowserKind.Chrome, BrowserKind.Edge, BrowserKind.Firefox })
        {
            var path = FindWindows(kind);

            if (path is not null) found.Add(new BrowserInstallation(kind, path));
        }

        return found;
    }

    public static BrowserInstallation? Find(BrowserKind kind) => Detect().FirstOrDefault(b => b.Kind == kind);

    [SupportedOSPlatform("windows")]
    private static string? FindWindows(BrowserKind kind)
    {
        var (exe, relativePaths) = kind switch
        {
            BrowserKind.Chrome  => ("chrome.exe", new[] { @"Google\Chrome\Application\chrome.exe" }),
            BrowserKind.Edge    => ("msedge.exe", new[] { @"Microsoft\Edge\Application\msedge.exe" }),
            _                   => ("firefox.exe", new[] { @"Mozilla Firefox\firefox.exe" }),
        };

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var root in roots.Where(r => r.Length > 0))
        {
            foreach (var relative in relativePaths)
            {
                var candidate = Path.Combine(root, relative);

                if (File.Exists(candidate)) return candidate;
            }
        }

        // Installers register their executable under "App Paths".
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);

            if (key?.GetValue(null) is string registered && File.Exists(registered.Trim('"'))) return registered.Trim('"');
        }

        return null;
    }
}
