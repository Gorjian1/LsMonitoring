using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using LsMonitoring.Core.Models;
using LsMonitoring.Core.Parsing;

namespace LsMonitoring.Core.Sources;

public sealed partial class CsvGatewaySource : IDataSource
{
    private static readonly string[] CsvPathCandidates =
    [
        "/dataserver/current/reading/{0}",
        "/dataserver/node/{0}/{0}-readings-current.csv",
        "/dataserver/node/view/{0}/{0}-readings-current.csv"
    ];

    private readonly string _gatewayIp;
    private readonly string _username;
    private readonly string _password;
    private readonly TimeSpan _timeout;
    private HttpClient? _client;

    public CsvGatewaySource(string gatewayIp, string username, string password, TimeSpan timeout)
    {
        _gatewayIp = gatewayIp.Trim();
        _username = username;
        _password = password;
        _timeout = timeout;
    }

    public string BaseUrl
    {
        get
        {
            var host = SchemeRegex().Replace(_gatewayIp, "").TrimEnd('/');
            return $"https://{host}";
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            return Task.CompletedTask;
        }

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = true
        };

        _client = new HttpClient(handler)
        {
            Timeout = _timeout
        };

        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        return Task.CompletedTask;
    }

    public async Task<NodeReadings> FetchReadingsAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        var parsed = await FetchCsvAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return NodeReadings.FromParsedCsv(nodeId, parsed);
    }

    public async Task<ParsedCsv> FetchCsvAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var template in CsvPathCandidates)
        {
            var path = string.Format(template, nodeId);
            try
            {
                using var response = await GetAsync(path, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    response.EnsureSuccessStatusCode();
                }

                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    continue;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    lastError = new InvalidDataException($"Got HTML response for node {nodeId}.");
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return CmtCsvParser.Parse(bytes);
            }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw;
            }
            catch (Exception e)
            {
                lastError = e;
            }
        }

        throw new InvalidOperationException($"Could not fetch CSV for node {nodeId}: {lastError?.Message}");
    }

    public async Task<IReadOnlyList<NodeInfo>> DiscoverNodesAsync(CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var found = new Dictionary<int, NodeInfo>();

        string root;
        try
        {
            using var response = await GetAsync("/dataserver/", cancellationToken).ConfigureAwait(false);
            root = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }

        var networkIds = NetworkLinkRegex().Matches(root).Select(x => x.Groups[1].Value).Distinct();
        foreach (var networkId in networkIds)
        {
            string html;
            try
            {
                using var response = await GetAsync($"/dataserver/network/view/{networkId}", cancellationToken).ConfigureAwait(false);
                html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            foreach (Match match in NodeLinkRegex().Matches(html))
            {
                var nodeId = int.Parse(match.Groups[1].Value);
                if (found.ContainsKey(nodeId))
                {
                    continue;
                }

                var model = TryFindModelHint(html, match.Index);
                found[nodeId] = new NodeInfo(nodeId, model);
            }
        }

        var live = new List<NodeInfo>();
        foreach (var info in found.Values)
        {
            try
            {
                var readings = await FetchReadingsAsync(info.NodeId, cancellationToken).ConfigureAwait(false);
                live.Add(info with { Model = readings.Model ?? info.Model });
            }
            catch
            {
                // Inactive/dead nodes can appear in the admin page. Keep discovery focused on live CSV sources.
            }
        }

        return live.OrderBy(x => x.NodeId).ToList();
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }

    private Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Source is not connected.");
        }

        return _client.GetAsync($"{BaseUrl}{path}", cancellationToken);
    }

    private static string? TryFindModelHint(string html, int index)
    {
        var start = Math.Max(0, index - 300);
        var len = Math.Min(html.Length - start, 600);
        var slice = html.Substring(start, len);
        var match = ModelHintRegex().Match(slice);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex("^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex("/dataserver/network/view/(\\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkLinkRegex();

    [GeneratedRegex("/dataserver/node/(?:view|edit)/(\\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NodeLinkRegex();

    [GeneratedRegex("LS-[A-Z0-9]+-[A-Z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex ModelHintRegex();
}
