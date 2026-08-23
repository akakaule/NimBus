using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace NimBus.CommandLine;

/// <summary>
/// Fetches a NuGet package and extracts it to a local cache. Used for both packages the
/// CLI resolves at deployment time: the NimBus deployment artifacts and the customer's
/// event-catalog assembly (ADR-015).
/// </summary>
/// <remarks>
/// A <c>.nupkg</c> is a zip served over the V3 flat container by a plain GET, so this
/// needs no NuGet client library — which keeps the CLI free of a heavy dependency and
/// works against any feed that speaks the flat-container protocol, including Azure
/// Artifacts.
/// </remarks>
internal sealed class NuGetPackageSource
{
    internal const string DefaultFeed = "https://api.nuget.org";

    private const string CompletionMarker = ".extracted";

    private readonly HttpClient _http;
    private readonly string _feed;
    private readonly string? _token;
    private readonly string _tokenVariableName;

    /// <param name="tokenVariableName">
    /// Environment variable a caller sets to authenticate. Named in the denial message so
    /// the error points at the right one — the artifact feed and the platform feed have
    /// separate variables.
    /// </param>
    public NuGetPackageSource(HttpClient http, string? feed, string? token, string tokenVariableName)
    {
        _http = http;
        _feed = (feed ?? DefaultFeed).TrimEnd('/');
        _token = token;
        _tokenVariableName = tokenVariableName;
    }

    public string Feed => _feed;

    /// <summary>
    /// Ensures <paramref name="packageId"/> <paramref name="version"/> is extracted under
    /// <paramref name="cacheDirectory"/> and returns that directory. Downloads only on a
    /// cache miss.
    /// </summary>
    /// <param name="describeMissing">
    /// Builds the message for a 404. The caller knows what the package means to the user —
    /// missing deployment artifacts and a missing contracts package need different advice.
    /// </param>
    public async Task<string> EnsureExtractedAsync(
        string packageId,
        string version,
        string cacheDirectory,
        Func<string, string> describeMissing,
        CancellationToken cancellationToken)
    {
        // A marker written last means a run interrupted mid-extraction is retried rather
        // than leaving a half-populated cache that looks complete.
        if (File.Exists(Path.Combine(cacheDirectory, CompletionMarker)))
        {
            return cacheDirectory;
        }

        var url = $"{_feed}/v3-flatcontainer/{packageId}/{version}/{packageId}.{version}.nupkg";
        CliOutput.WriteLine($"Downloading {packageId} {version} from {_feed}...");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_token))
        {
            // Azure Artifacts and friends take a PAT as the password half of Basic auth;
            // the username is ignored.
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"nb:{_token}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CommandException(response.StatusCode switch
            {
                HttpStatusCode.NotFound => describeMissing(_feed),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    $"Access to {_feed} was denied ({(int)response.StatusCode}) while fetching {packageId} {version}. Set {_tokenVariableName} to a token that can read the feed.",
                _ => $"Failed to download {packageId} {version} ({(int)response.StatusCode}) from {url}.",
            });
        }

        Directory.CreateDirectory(cacheDirectory);

        // Buffer to a file first: ZipArchive needs to seek, and a failed download must not
        // leave a partial package in the cache.
        var packagePath = Path.Combine(cacheDirectory, $"{packageId}.{version}.nupkg.tmp");
        try
        {
            await using (var target = File.Create(packagePath))
            {
                await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destination = Path.Combine(cacheDirectory, relative);

                    // Guard against a package whose entries escape the cache directory.
                    var fullDestination = Path.GetFullPath(destination);
                    if (!fullDestination.StartsWith(Path.GetFullPath(cacheDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        throw new CommandException($"{packageId} {version} contains an entry that would extract outside the cache directory: '{entry.FullName}'.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
                    entry.ExtractToFile(fullDestination, overwrite: true);
                }
            }

            File.WriteAllText(Path.Combine(cacheDirectory, CompletionMarker), version);
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
        }

        return cacheDirectory;
    }
}
