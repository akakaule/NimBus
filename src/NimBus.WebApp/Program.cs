using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NimBus.WebApp;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args);

        // Run as a dotnet tool (Akaule.NimBus.WebApp), the WebApp starts in the caller's directory;
        // its SPA and appsettings.json live next to the binaries instead.
        if (ResolveContentRoot(Directory.GetCurrentDirectory(), AppContext.BaseDirectory) is { } contentRoot)
        {
            builder.UseContentRoot(contentRoot);
        }

        return builder
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
            })
            .ConfigureAppConfiguration((hostContext, builder) =>
            {
                builder.AddUserSecrets<Program>();
                // Saved non-secret settings must precede MVC/extension registration.
                var configuration = builder.Build();
                var effective = Services.IntegrationIntelligence.IntelligenceSettingsBootstrap.Load(configuration);
                builder.Sources.Clear();
                builder.AddConfiguration(effective);
            });
    }

    /// <summary>
    /// Returns <paramref name="appBaseDirectory"/> when it holds the built SPA and
    /// <paramref name="currentDirectory"/> does not, so a WebApp launched from elsewhere still finds
    /// its content; otherwise <c>null</c>, keeping the default content root.
    /// </summary>
    internal static string? ResolveContentRoot(string currentDirectory, string appBaseDirectory)
    {
        static bool HasSpa(string directory) =>
            Directory.Exists(Path.Combine(directory, "ClientApp", "build", "public"));

        return !HasSpa(currentDirectory) && HasSpa(appBaseDirectory) ? appBaseDirectory : null;
    }
}
