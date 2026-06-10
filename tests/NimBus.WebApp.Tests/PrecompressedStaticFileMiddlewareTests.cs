#pragma warning disable CA1707, CA2007

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Middleware;

namespace NimBus.WebApp.Tests
{
    /// <summary>
    /// Tests for <see cref="PrecompressedStaticFileMiddleware"/> — the middleware that
    /// serves the `.br`/`.gz` siblings emitted by the Vite build (vite-plugin-compression2)
    /// instead of re-compressing static assets per request.
    ///
    /// The fixture mirrors the production pipeline shape in `src/NimBus.WebApp/Startup.cs`:
    /// the middleware runs before the static-file middleware, over the same physical file
    /// provider, and the static-file middleware uses <see cref="PrecompressedContentTypeProvider"/>
    /// so rewritten `.js.br` responses keep their real Content-Type. The sibling files on
    /// disk carry distinct marker bodies so each assertion can tell exactly which variant
    /// was streamed.
    /// </summary>
    [TestClass]
    public class PrecompressedStaticFileMiddlewareTests
    {
        private const string PlainBody = "plain-javascript-body";
        private const string BrotliBody = "brotli-sibling-body";
        private const string GzipBody = "gzip-sibling-body";

        private static string _webRoot = null!;

        [ClassInitialize]
        public static void ClassInitialize(TestContext _)
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "nimbus-precompressed-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));

            // app.js has both siblings; styles.css has only .gz; logo.svg has none.
            File.WriteAllText(Path.Combine(_webRoot, "assets", "app.js"), PlainBody);
            File.WriteAllText(Path.Combine(_webRoot, "assets", "app.js.br"), BrotliBody);
            File.WriteAllText(Path.Combine(_webRoot, "assets", "app.js.gz"), GzipBody);
            File.WriteAllText(Path.Combine(_webRoot, "assets", "styles.css"), PlainBody);
            File.WriteAllText(Path.Combine(_webRoot, "assets", "styles.css.gz"), GzipBody);
            File.WriteAllText(Path.Combine(_webRoot, "assets", "logo.svg"), PlainBody);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            try
            {
                Directory.Delete(_webRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        private static IHost BuildTestHost()
        {
            var fileProvider = new PhysicalFileProvider(_webRoot);

            var builder = Host.CreateDefaultBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services => services.AddRouting());
                    web.Configure(app =>
                    {
                        // Mirrors src/NimBus.WebApp/Startup.cs: the precompressed middleware
                        // runs before the (SPA) static-file middleware, which resolves the
                        // rewritten sibling paths back to the underlying Content-Type.
                        app.UseMiddleware<PrecompressedStaticFileMiddleware>((IFileProvider)fileProvider);
                        app.UseStaticFiles(new StaticFileOptions
                        {
                            FileProvider = fileProvider,
                            ContentTypeProvider = new PrecompressedContentTypeProvider(),
                        });
                    });
                });

            return builder.Start();
        }

        private static async Task<HttpResponseMessage> GetAsync(IHost host, string path, string? acceptEncoding)
        {
            var client = host.GetTestServer().CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (acceptEncoding is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
            }

            return await client.SendAsync(request);
        }

        [TestMethod]
        public async Task BrotliAccepted_ServesBrSibling_WithContentEncodingAndVary()
        {
            using var host = BuildTestHost();
            using var response = await GetAsync(host, "/assets/app.js", "br, gzip");

            response.EnsureSuccessStatusCode();
            Assert.AreEqual(BrotliBody, await response.Content.ReadAsStringAsync());
            CollectionAssert.Contains(response.Content.Headers.ContentEncoding.ToArray(), "br");
            CollectionAssert.Contains(response.Headers.Vary.ToArray(), "Accept-Encoding");
        }

        [TestMethod]
        public async Task BrotliSibling_KeepsUnderlyingContentType()
        {
            using var host = BuildTestHost();
            using var response = await GetAsync(host, "/assets/app.js", "br");

            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            Assert.IsTrue(
                contentType == "application/javascript" || contentType == "text/javascript",
                $"Expected a JavaScript Content-Type for the .br sibling, got '{contentType}'.");
        }

        [TestMethod]
        public async Task GzipOnlyAccepted_ServesGzSibling()
        {
            using var host = BuildTestHost();
            using var response = await GetAsync(host, "/assets/app.js", "gzip");

            response.EnsureSuccessStatusCode();
            Assert.AreEqual(GzipBody, await response.Content.ReadAsStringAsync());
            CollectionAssert.Contains(response.Content.Headers.ContentEncoding.ToArray(), "gzip");
        }

        [TestMethod]
        public async Task BrotliAcceptedButOnlyGzSiblingExists_FallsBackToGz()
        {
            using var host = BuildTestHost();
            using var response = await GetAsync(host, "/assets/styles.css", "br, gzip");

            response.EnsureSuccessStatusCode();
            Assert.AreEqual(GzipBody, await response.Content.ReadAsStringAsync());
            CollectionAssert.Contains(response.Content.Headers.ContentEncoding.ToArray(), "gzip");
            Assert.AreEqual("text/css", response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task QualityZero_IsAnExplicitRefusal()
        {
            using var host = BuildTestHost();
            // br refused via q=0, gzip accepted — must skip .br and serve .gz (RFC 9110).
            using var response = await GetAsync(host, "/assets/app.js", "br;q=0, gzip;q=0.8");

            response.EnsureSuccessStatusCode();
            Assert.AreEqual(GzipBody, await response.Content.ReadAsStringAsync());
            CollectionAssert.Contains(response.Content.Headers.ContentEncoding.ToArray(), "gzip");
        }

        [TestMethod]
        public async Task NoAcceptEncoding_ServesPlainAsset_NoContentEncoding()
        {
            using var host = BuildTestHost();
            using var response = await GetAsync(host, "/assets/app.js", acceptEncoding: null);

            response.EnsureSuccessStatusCode();
            Assert.AreEqual(PlainBody, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
        }

        [TestMethod]
        public async Task NoSiblingOnDisk_FallsThroughToPlainAsset()
        {
            using var host = BuildTestHost();
            using var response = await GetAsync(host, "/assets/logo.svg", "br, gzip");

            response.EnsureSuccessStatusCode();
            Assert.AreEqual(PlainBody, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
        }

        [TestMethod]
        public async Task PostRequest_IsNeverRewritten()
        {
            using var host = BuildTestHost();
            var client = host.GetTestServer().CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/assets/app.js");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");
            using var response = await client.SendAsync(request);

            // Static files middleware does not serve POST; the point here is that no
            // Content-Encoding rewrite leaked onto a non-GET response.
            Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
        }

        /// <summary>
        /// Guards the production wiring: the precompressed middleware MUST be registered in
        /// `Startup.Configure` before `UseSpaStaticFiles`, and the SPA static files options
        /// MUST use <see cref="PrecompressedContentTypeProvider"/>. Mirrors the source-level
        /// ordering assertion in <see cref="ResponseCompressionTests"/>.
        /// </summary>
        [TestMethod]
        public void PrecompressedMiddleware_RunsBeforeUseSpaStaticFiles_InStartup()
        {
            string startupPath = LocateStartupSource();
            string source = File.ReadAllText(startupPath);

            int idxMiddleware = source.IndexOf("UseMiddleware<PrecompressedStaticFileMiddleware>", StringComparison.Ordinal);
            int idxSpaStatic = source.IndexOf("app.UseSpaStaticFiles", StringComparison.Ordinal);
            int idxContentTypeProvider = source.IndexOf("new PrecompressedContentTypeProvider()", StringComparison.Ordinal);

            Assert.IsTrue(idxMiddleware > 0, "Startup.cs is missing `UseMiddleware<PrecompressedStaticFileMiddleware>`.");
            Assert.IsTrue(idxSpaStatic > 0, "Startup.cs is missing `app.UseSpaStaticFiles(...)` (expected to remain).");
            Assert.IsTrue(
                idxMiddleware < idxSpaStatic,
                "`UseMiddleware<PrecompressedStaticFileMiddleware>` MUST be called BEFORE `app.UseSpaStaticFiles(...)` — the rewrite must happen before the static-file middleware resolves the path.");
            Assert.IsTrue(
                idxContentTypeProvider > 0,
                "Startup.cs must hand UseSpaStaticFiles a PrecompressedContentTypeProvider so rewritten `.br`/`.gz` paths keep the underlying Content-Type.");
        }

        private static string LocateStartupSource()
        {
            string? dir = Path.GetDirectoryName(typeof(PrecompressedStaticFileMiddlewareTests).Assembly.Location);
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
