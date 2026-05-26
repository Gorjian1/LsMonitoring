using System.Net;
using LsMonitoring.Core.Updates;

namespace LsMonitoring.Core.Tests;

public sealed class GitHubReleaseCheckerTests
{
    [Fact]
    public void CompareVersions_HandlesTagsAndSuffixes()
    {
        Assert.True(GitHubReleaseChecker.CompareVersions("v0.3.1", "0.3.0") > 0);
        Assert.Equal(0, GitHubReleaseChecker.CompareVersions("v0.3.0+build.1", "0.3.0"));
        Assert.True(GitHubReleaseChecker.CompareVersions("0.2.9", "v0.3.0") < 0);
    }

    [Fact]
    public async Task CheckAsync_ParsesLatestRelease()
    {
        var handler = new ReleaseHandler(
            """
            {
              "tag_name": "v0.3.0",
              "html_url": "https://github.com/Gorjian1/LsMonitoring/releases/tag/v0.3.0"
            }
            """);
        using var client = new HttpClient(handler);

        var result = await GitHubReleaseChecker.CheckAsync(
            "0.2.0",
            client,
            "https://api.example.test/releases/latest");

        Assert.Null(result.Error);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.3.0", result.LatestVersion);
        Assert.Equal("https://github.com/Gorjian1/LsMonitoring/releases/tag/v0.3.0", result.ReleaseUrl);
    }

    private sealed class ReleaseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
