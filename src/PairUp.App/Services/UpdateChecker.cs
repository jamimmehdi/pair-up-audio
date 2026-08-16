using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PairUp.App.Services;

public sealed record UpdateCheckResult(
    bool UpdateAvailable, string LatestVersion, string ReleaseUrl,
    string? InstallerAssetName, string? InstallerDownloadUrl);

/// <summary>
/// Checks GitHub Releases for a newer PairUp version than what's currently running, and can
/// download + launch the release's installer asset for a one-click in-app update.
/// </summary>
public static class UpdateChecker
{
    private const string RepoOwner = "jamimmehdi";
    private const string RepoName = "pair-up-audio";

    private static HttpClient CreateClient(string currentVersion)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PairUp", currentVersion));
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion)
    {
        using var client = CreateClient(currentVersion);

        var response = await client.GetAsync(
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode}.");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp)
            ? urlProp.GetString() ?? ""
            : $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

        var latestVersion = tag.TrimStart('v', 'V');
        var isNewer = CompareVersions(latestVersion, currentVersion) > 0;

        string? assetName = null;
        string? assetUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            // Prefer a Setup/Installer-named .exe if there is one, otherwise the first .exe asset.
            var candidates = assets.EnumerateArray()
                .Select(a => (
                    Name: a.GetProperty("name").GetString() ?? "",
                    Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var best = candidates.FirstOrDefault(a =>
                a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains("install", StringComparison.OrdinalIgnoreCase));
            if (best == default) best = candidates.FirstOrDefault();

            if (best != default)
            {
                assetName = best.Name;
                assetUrl = best.Url;
            }
        }

        return new UpdateCheckResult(isNewer, latestVersion, releaseUrl, assetName, assetUrl);
    }

    /// <summary>Downloads the installer to a temp file and returns its path; caller launches it.</summary>
    public static async Task<string> DownloadInstallerAsync(
        string downloadUrl, string fileName, string currentVersion, IProgress<double>? progress = null)
    {
        using var client = CreateClient(currentVersion);
        client.Timeout = TimeSpan.FromMinutes(5);

        var tempDir = Path.Combine(Path.GetTempPath(), "PairUpUpdate");
        Directory.CreateDirectory(tempDir);
        var destPath = Path.Combine(tempDir, fileName);

        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var httpStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await httpStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (totalBytes > 0)
                progress?.Report((double)readTotal / totalBytes * 100);
        }

        return destPath;
    }

    /// <summary>Simple dotted-numeric version compare; returns >0 if <paramref name="a"/> is newer.</summary>
    private static int CompareVersions(string a, string b)
    {
        var partsA = ParseParts(a);
        var partsB = ParseParts(b);
        var length = Math.Max(partsA.Length, partsB.Length);

        for (var i = 0; i < length; i++)
        {
            var va = i < partsA.Length ? partsA[i] : 0;
            var vb = i < partsB.Length ? partsB[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }

        return 0;
    }

    private static int[] ParseParts(string version) =>
        version.Split('.', '-')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();
}
