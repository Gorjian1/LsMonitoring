using System.Net.Http.Headers;
using System.Text.Json;

namespace LsMonitoring.Core.Updates;

public sealed record ReleaseUpdateResult
{
    public required string CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? Error { get; init; }
    public bool IsUpdateAvailable { get; init; }
}

public static class GitHubReleaseChecker
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/Gorjian1/LsMonitoring/releases/latest";
    public const string RepositoryReleasesUrl = "https://github.com/Gorjian1/LsMonitoring/releases";

    public static async Task<ReleaseUpdateResult> CheckAsync(
        string currentVersion,
        HttpClient? httpClient = null,
        string latestReleaseUrl = LatestReleaseUrl,
        CancellationToken cancellationToken = default)
    {
        var current = NormalizeVersion(currentVersion);
        if (string.IsNullOrWhiteSpace(current))
        {
            return new ReleaseUpdateResult
            {
                CurrentVersion = currentVersion,
                Error = "Не удалось определить текущую версию."
            };
        }

        var ownsClient = httpClient is null;
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("LS-Monitoring-Desktop");

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ReleaseUpdateResult
                {
                    CurrentVersion = current,
                    Error = $"GitHub ответил HTTP {(int)response.StatusCode} {response.StatusCode}."
                };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var latest = root.TryGetProperty("tag_name", out var tag)
                ? NormalizeVersion(tag.GetString() ?? "")
                : "";
            var releaseUrl = root.TryGetProperty("html_url", out var htmlUrl)
                ? htmlUrl.GetString()
                : RepositoryReleasesUrl;

            if (string.IsNullOrWhiteSpace(latest))
            {
                return new ReleaseUpdateResult
                {
                    CurrentVersion = current,
                    ReleaseUrl = releaseUrl,
                    Error = "В latest release нет tag_name."
                };
            }

            return new ReleaseUpdateResult
            {
                CurrentVersion = current,
                LatestVersion = latest,
                ReleaseUrl = string.IsNullOrWhiteSpace(releaseUrl) ? RepositoryReleasesUrl : releaseUrl,
                IsUpdateAvailable = CompareVersions(latest, current) > 0
            };
        }
        catch (Exception ex)
        {
            return new ReleaseUpdateResult
            {
                CurrentVersion = current,
                Error = $"Не удалось проверить обновление: {ex.Message}"
            };
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    public static string NormalizeVersion(string value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        if (normalized.StartsWith('v'))
        {
            normalized = normalized[1..];
        }

        var suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            normalized = normalized[..suffix];
        }

        return normalized;
    }

    public static int CompareVersions(string left, string right)
    {
        var leftParts = NormalizeVersion(left).Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = NormalizeVersion(right).Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Max(leftParts.Length, rightParts.Length);

        for (var i = 0; i < length; i++)
        {
            var leftValue = i < leftParts.Length ? ParseVersionPart(leftParts[i]) : 0;
            var rightValue = i < rightParts.Length ? ParseVersionPart(rightParts[i]) : 0;
            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }

    private static int ParseVersionPart(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 0;
    }
}
