#pragma warning disable CA1707, CA2007

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.WebApp.Tests
{
    /// <summary>
    /// Integration tests covering spec 009 (Response Compression).
    ///
    /// Spec FR-050..FR-053 require that:
    ///   - SPA asset (e.g. JS) with `Accept-Encoding: br, gzip` returns `Content-Encoding: br`
    ///     and a body smaller than uncompressed.
    ///   - JSON API endpoint advertising br returns `Content-Encoding: br`.
    ///   - Request with `Accept-Encoding: identity` returns no `Content-Encoding`.
    ///   - `UseResponseCompression` runs before `UseSpaStaticFiles`.
    ///
    /// These tests spin up a minimal in-process host that mirrors the exact response-
    /// compression registration / pipeline placement used in
    /// `src/NimBus.WebApp/Startup.cs`. The production Startup pulls in Azure Service Bus +
    /// Cosmos DB / SQL configuration at boot, which is impractical for a unit-test host —
    /// but the compression behaviour is fully captured by the AddResponseCompression /
    /// UseResponseCompression pair, which we reproduce verbatim here. A separate reflective
    /// test (UseResponseCompression_RunsBeforeUseSpaStaticFiles_InStartup) asserts that the
    /// real Startup.cs invokes the middleware in the required order so this fixture cannot
    /// drift away from production wiring without being caught.
    /// </summary>
    [TestClass]
    public class ResponseCompressionTests
    {
        // A body large enough that compression makes a clearly observable difference.
        // The repeating pattern compresses well under both Brotli and Gzip.
        private static readonly string LargeJsBody =
            "// SPA bundle stand-in. " + string.Concat(Enumerable.Repeat("function foo(){return 'bar';} ", 200));

        private static readonly string LargeJsonBody =
            "[" + string.Join(",", Enumerable.Range(0, 200).Select(i =>
                $"{{\"id\":{i},\"name\":\"endpoint-{i}\",\"status\":\"Healthy\"}}")) + "]";

        // FR-001 / FR-004: the four MIME types the WebApp serves that aren't on the
        // default list. Hoisted to a static field to satisfy CA1861.
        private static readonly string[] AdditionalMimeTypes =
        {
            "application/javascript",
            "application/json",
            "text/css",
            "image/svg+xml",
        };

        private static IHost BuildTestHost()
        {
            var builder = Host.CreateDefaultBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        // === Mirror src/NimBus.WebApp/Startup.cs response-compression registration ===
                        services.AddResponseCompression(options =>
                        {
                            options.EnableForHttps = true;
                            options.Providers.Add<BrotliCompressionProvider>();
                            options.Providers.Add<GzipCompressionProvider>();
                            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(AdditionalMimeTypes);
                        });
                        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
                        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
                        services.AddRouting();
                    });
                    web.Configure(app =>
                    {
                        // Pipeline placement mirrors src/NimBus.WebApp/Startup.cs:
                        // UseResponseCompression() before UseRouting / static-file middleware.
                        app.UseResponseCompression();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            // Stand-in for a SPA asset (Vite-style hashed JS bundle).
                            endpoints.MapGet("/static/app.js", async ctx =>
                            {
                                ctx.Response.ContentType = "application/javascript";
                                await ctx.Response.WriteAsync(LargeJsBody);
                            });
                            // Stand-in for a JSON API surface (e.g. `GET /api/endpoints`).
                            endpoints.MapGet("/api/endpoints", async ctx =>
                            {
                                ctx.Response.ContentType = "application/json";
                                await ctx.Response.WriteAsync(LargeJsonBody);
                            });
                        });
                    });
                });

            return builder.Start();
        }

        /// <summary>FR-050: SPA asset advertising br + gzip returns Content-Encoding: br
        /// and a body smaller than the uncompressed body.</summary>
        [TestMethod]
        public async Task SpaAsset_WithBrotliAdvertised_ReturnsBrotliEncoded_AndShorterBody()
        {
            using var host = BuildTestHost();
            using var client = host.GetTestServer().CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, "/static/app.js");
            // Don't let HttpClient transparently decode the body — we want the raw wire bytes.
            request.Headers.AcceptEncoding.ParseAdd("br, gzip");

            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var contentEncoding = response.Content.Headers.ContentEncoding;
            CollectionAssert.Contains(
                contentEncoding.ToArray(),
                "br",
                $"Expected Content-Encoding 'br', got [{string.Join(",", contentEncoding)}]");

            byte[] compressedBody = await response.Content.ReadAsByteArrayAsync();
            int uncompressedLength = Encoding.UTF8.GetByteCount(LargeJsBody);

            Assert.IsTrue(
                compressedBody.Length < uncompressedLength,
                $"Compressed body ({compressedBody.Length} B) should be smaller than uncompressed ({uncompressedLength} B).");

            // Verify the bytes actually decompress back to the original.
            using var br = new BrotliStream(new MemoryStream(compressedBody), CompressionMode.Decompress);
            using var reader = new StreamReader(br, Encoding.UTF8);
            var decoded = await reader.ReadToEndAsync();
            Assert.AreEqual(LargeJsBody, decoded);
        }

        /// <summary>FR-051: JSON API endpoint with br advertised returns Content-Encoding: br.</summary>
        [TestMethod]
        public async Task JsonApi_WithBrotliAdvertised_ReturnsBrotliEncoded()
        {
            using var host = BuildTestHost();
            using var client = host.GetTestServer().CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/endpoints");
            request.Headers.AcceptEncoding.ParseAdd("br, gzip");

            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            CollectionAssert.Contains(
                response.Content.Headers.ContentEncoding.ToArray(),
                "br",
                $"Expected Content-Encoding 'br' on JSON API response, got [{string.Join(",", response.Content.Headers.ContentEncoding)}]");

            byte[] compressedBody = await response.Content.ReadAsByteArrayAsync();
            int uncompressedLength = Encoding.UTF8.GetByteCount(LargeJsonBody);
            Assert.IsTrue(
                compressedBody.Length < uncompressedLength,
                $"Compressed JSON body ({compressedBody.Length} B) should be smaller than uncompressed ({uncompressedLength} B).");
        }

        /// <summary>FR-052: Request with `Accept-Encoding: identity` returns no Content-Encoding header.</summary>
        [TestMethod]
        public async Task IdentityRequest_ReturnsUncompressed_NoContentEncodingHeader()
        {
            using var host = BuildTestHost();
            using var client = host.GetTestServer().CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, "/static/app.js");
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            Assert.AreEqual(
                0,
                response.Content.Headers.ContentEncoding.Count,
                $"Expected no Content-Encoding header for `Accept-Encoding: identity`, got [{string.Join(",", response.Content.Headers.ContentEncoding)}]");

            byte[] body = await response.Content.ReadAsByteArrayAsync();
            int expectedLength = Encoding.UTF8.GetByteCount(LargeJsBody);
            Assert.AreEqual(
                expectedLength,
                body.Length,
                "Identity request should return the body byte-for-byte uncompressed.");
        }

        /// <summary>
        /// FR-053: `UseResponseCompression` MUST run before `UseSpaStaticFiles` in the real
        /// `NimBus.WebApp/Startup.cs`. We assert this by source-level introspection of the
        /// embedded Startup.Configure body so that future refactors that move middleware
        /// around cannot silently regress the ordering invariant.
        /// </summary>
        [TestMethod]
        public void UseResponseCompression_RunsBeforeUseSpaStaticFiles_InStartup()
        {
            // Load the WebApp assembly and locate Startup.Configure via reflection. The
            // method body is not directly inspectable without IL reading, so we read the
            // sibling source file (the WebApp project is referenced by path) and verify
            // textual ordering of the middleware calls.
            string startupPath = LocateStartupSource();
            string source = File.ReadAllText(startupPath);

            int idxCompression = source.IndexOf("app.UseResponseCompression()", StringComparison.Ordinal);
            int idxSpaStatic = source.IndexOf("app.UseSpaStaticFiles", StringComparison.Ordinal);
            int idxRouting = source.IndexOf("app.UseRouting", StringComparison.Ordinal);

            Assert.IsTrue(idxCompression > 0, "Startup.cs is missing `app.UseResponseCompression()` — spec FR-010 violated.");
            Assert.IsTrue(idxSpaStatic > 0, "Startup.cs is missing `app.UseSpaStaticFiles(...)` (expected to remain).");
            Assert.IsTrue(idxRouting > 0, "Startup.cs is missing `app.UseRouting()` (expected to remain).");

            Assert.IsTrue(
                idxCompression < idxSpaStatic,
                "FR-010: `app.UseResponseCompression()` MUST be called BEFORE `app.UseSpaStaticFiles(...)`. The static-file middleware short-circuits the request; compression must run earlier or the SPA bundle ships uncompressed.");
            Assert.IsTrue(
                idxCompression < idxRouting,
                "FR-010: `app.UseResponseCompression()` MUST be called BEFORE `app.UseRouting()` so JSON API responses also negotiate compression.");
        }

        private static string LocateStartupSource()
        {
            // Walk up from the test binary directory until we find the repo root, then
            // descend into src/NimBus.WebApp/Startup.cs.
            string? dir = Path.GetDirectoryName(typeof(ResponseCompressionTests).Assembly.Location);
            while (!string.IsNullOrEmpty(dir))
            {
                string candidate = Path.Combine(dir, "src", "NimBus.WebApp", "Startup.cs");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }

            throw new FileNotFoundException(
                "Could not locate src/NimBus.WebApp/Startup.cs by walking up from the test assembly directory.");
        }
    }
}
