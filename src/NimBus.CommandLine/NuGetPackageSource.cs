using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
    private string? _packageBaseAddress;

    /// <param name="tokenVariableName">
    /// Environment variable a caller sets to authenticate. Named in the denial message so
    /// the error points at the right one — the artifact feed and the platform feed have
    /// separate variables.
    /// </param>
    public NuGetPackageSource(HttpClient http, string? feed, string? token, string tokenVariableName)
    {
        _http = http;
        _feed = (feed ?? DefaultFeed).TrimEnd('/');
        // A credential is only ever attached to a feed the caller actually configured.
        // With no feed set the default is the public nuget.org, and a token that happens
        // to be exported for a private mirror must not be handed to it.
        _token = string.IsNullOrWhiteSpace(feed) ? null : token;
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

        var baseAddress = await ResolvePackageBaseAddressAsync(cancellationToken).ConfigureAwait(false);
        var id = packageId.ToLowerInvariant();
        var normalized = NormalizeVersion(version);
        var url = $"{baseAddress}/{id}/{normalized}/{id}.{normalized}.nupkg";
        CliOutput.WriteLine($"Downloading {packageId} {version} from {_feed}...");

        using var request = CreateRequest(url);
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

    /// <summary>
    /// Finds the feed's flat-container root. Only nuget.org serves it at
    /// <c>/v3-flatcontainer</c>; Azure Artifacts uses <c>/nuget/v3/flat2</c> and GitHub
    /// Packages <c>/download</c>, so the address is read from the V3 service index rather
    /// than assumed. A feed that serves no index keeps working through the nuget.org
    /// layout, which is what mirrors of it use.
    /// </summary>
    private async Task<string> ResolvePackageBaseAddressAsync(CancellationToken cancellationToken)
    {
        if (_packageBaseAddress is not null) return _packageBaseAddress;

        try
        {
            using var request = CreateRequest($"{_feed}/v3/index.json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("resources", out var resources)
                    && resources.ValueKind == JsonValueKind.Array)
                {
                    foreach (var resource in resources.EnumerateArray())
                    {
                        // The type is versioned ("PackageBaseAddress/3.0.0"); match the family.
                        if (resource.TryGetProperty("@type", out var type)
                            && type.GetString()?.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal) == true
                            && resource.TryGetProperty("@id", out var id)
                            && id.GetString() is { Length: > 0 } address)
                        {
                            return _packageBaseAddress = address.TrimEnd('/');
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException
            && !cancellationToken.IsCancellationRequested)
        {
            // An unreachable or malformed index is not fatal on its own: the download below
            // produces the actionable message, naming the package the user asked for.
        }

        return _packageBaseAddress = $"{_feed}/v3-flatcontainer";
    }

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Basic auth over cleartext would put the PAT on the wire, so a plain-http feed
        // is served anonymously and fails with the denial message instead.
        if (!string.IsNullOrWhiteSpace(_token) && request.RequestUri?.Scheme == Uri.UriSchemeHttps)
        {
            // Azure Artifacts and friends take a PAT as the password half of Basic auth;
            // the username is ignored.
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"nb:{_token}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return request;
    }

    /// <summary>
    /// Applies NuGet's version normalization, which is what the flat container keys on:
    /// lowercase, no build metadata, leading zeros stripped, and a trailing zero fourth
    /// component dropped. Without it <c>1.4.0.0</c> or <c>1.4.0-RC1</c> 404s against a feed
    /// that genuinely hosts the package.
    /// </summary>
    internal static string NormalizeVersion(string version)
    {
        var value = version.Trim();

        var metadata = value.IndexOf('+', StringComparison.Ordinal);
        if (metadata >= 0) value = value[..metadata];

        var prerelease = value.IndexOf('-', StringComparison.Ordinal);
        var suffix = prerelease >= 0 ? value[prerelease..] : string.Empty;
        var numeric = prerelease >= 0 ? value[..prerelease] : value;

        var parts = numeric.Split('.')
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : part)
            .ToList();

        while (parts.Count > 3 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
        while (parts.Count < 3) parts.Add("0");

        return (string.Join('.', parts) + suffix).ToLowerInvariant();
    }
}
