using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NimBus.WebApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
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
}
