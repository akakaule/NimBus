using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using NimBus;
using NimBus.WebApp.Hubs;
using NimBus.WebApp.Services.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using NSwag.AspNetCore;
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.ServiceBus;
using NimBus.WebApp.Services;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Controllers;
using System.Linq;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Middleware;
using System.Text.Json.Serialization;
using NimBus.Core.Extensions;
using NimBus.Management.ServiceBus;
using NimBus.MessageStore.SqlServer;
using NimBus.ServiceBus.Transport;
using NimBus.Transport.RabbitMQ.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.ServiceBus.HealthChecks;
using NimBus.MessageStore.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace NimBus.WebApp
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment webEnv)
        {
            Configuration = configuration;
            Env = webEnv;
        }

        public IWebHostEnvironment Env { get; }
        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Security: Fail fast if dangerous development-only settings are active in non-Development environments
            if (!Env.IsDevelopment())
            {
                if (Configuration.GetValue<bool>("BypassEndpointAuthorization", false))
                    throw new InvalidOperationException("SECURITY: BypassEndpointAuthorization must not be enabled outside Development environment. Remove this setting from production configuration.");

                if (Configuration.GetValue<bool>("EnableLocalDevAuthentication", false))
                    throw new InvalidOperationException("SECURITY: EnableLocalDevAuthentication must not be enabled outside Development environment. Remove this setting from production configuration.");
            }

            // Bypass authentication for local development (requires explicit opt-in via config)
            var enableLocalDevAuth = Configuration.GetValue<bool>("EnableLocalDevAuthentication", false);
            var hasNimBusIdentity = services.Any(s => s.ServiceType.FullName == "NimBus.Extensions.Identity.INimBusIdentityMarker");
            var hasEntraId = Configuration.GetSection("AzureAd").GetValue<string>("ClientId") is { Length: > 0 };

            if (Env.IsDevelopment() && enableLocalDevAuth)
            {
                System.Console.WriteLine("WARNING: Local development authentication bypass is ENABLED. This should NEVER be used in production!");

                services.AddAuthentication("LocalDev")
                    .AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>("LocalDev", null);

                services.AddControllersWithViews().AddMicrosoftIdentityUI();
            }
            else if (hasNimBusIdentity && !hasEntraId)
            {
                // Identity-only mode: ASP.NET Core Identity cookies (no Azure AD)
                services.AddControllersWithViews(options =>
                {
                    var policy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .Build();
                    options.Filters.Add(new AuthorizeFilter(policy));
                });
            }
            else if (hasNimBusIdentity && hasEntraId)
            {
                // Dual mode: Identity cookies + Azure AD
                services
                .AddAuthentication("Az")
                .AddPolicyScheme("Az", "Authorize AzureAD, AzureADBearer, or Identity", options =>
                {
                    options.ForwardDefaultSelector = context =>
                    {
                        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                        if (authHeader?.StartsWith("Bearer", StringComparison.Ordinal) == true)
                        {
                            return JwtBearerDefaults.AuthenticationScheme;
                        }

                        // If user has Identity cookie, use Identity scheme
                        if (context.Request.Cookies.ContainsKey("NimBus.Identity"))
                        {
                            return Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme;
                        }

                        return OpenIdConnectDefaults.AuthenticationScheme;
                    };
                })
                .AddMicrosoftIdentityWebApi(Configuration.GetSection("AzureAd"))
                .EnableTokenAcquisitionToCallDownstreamApi()
                .AddInMemoryTokenCaches();

                services.AddMicrosoftIdentityWebAppAuthentication(Configuration, "AzureAd");

                services.AddControllersWithViews(options =>
                {
                    var policy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .Build();
                    options.Filters.Add(new AuthorizeFilter(policy));
                }).AddMicrosoftIdentityUI();
            }
            else
            {
                // Entra ID only (original behavior)
                services
                .AddAuthentication("Az")
                .AddPolicyScheme("Az", "Authorize AzureAD or AzureADBearer", options =>
                {
                    options.ForwardDefaultSelector = context =>
                    {
                        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                        if (authHeader?.StartsWith("Bearer", StringComparison.Ordinal) == true)
                        {
                            return JwtBearerDefaults.AuthenticationScheme;
                        }
                        return OpenIdConnectDefaults.AuthenticationScheme;
                    };
                })
                .AddMicrosoftIdentityWebApi(Configuration.GetSection("AzureAd"))
                .EnableTokenAcquisitionToCallDownstreamApi()
                .AddInMemoryTokenCaches();

                services.AddMicrosoftIdentityWebAppAuthentication(Configuration, "AzureAd");

                services.AddControllersWithViews(options =>
                {
                    var policy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .Build();
                    options.Filters.Add(new AuthorizeFilter(policy));
                }).AddMicrosoftIdentityUI();
            }

            services.AddControllers(options =>
            {
                options.ModelBinderProviders.Insert(0, new EnumMemberModelBinderProvider());
            }).AddJsonOptions(opts =>
            {
                opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

            services.AddMvc().AddRazorRuntimeCompilation();

            services.AddSwaggerDocument(s =>
            {
                s.Title = "DIS Management API";
                s.Description = "Enterprise Integration Platform API - For data displayed in the management-webapp.";
            });

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            services.AddSpaStaticFiles(configuration =>
            {
                configuration.RootPath = "ClientApp/build/public";
            });

            services.AddSignalR();

            // IPlatform catalog selection.
            // By default the WebApp shows the bundled PlatformConfiguration (Storefront/Billing/Warehouse).
            // Samples that define their own platform (e.g. CrmErpDemo) inject the catalog via config:
            //   NimBus:PlatformType     = "CrmErpDemo.Contracts.CrmErpPlatformConfiguration"
            //   NimBus:PlatformAssembly = absolute path to CrmErpDemo.Contracts.dll (optional; required
            //                             when the assembly isn't already loaded in the WebApp process).
            services.AddSingleton<IPlatform>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var typeName = cfg["NimBus:PlatformType"];
                if (string.IsNullOrWhiteSpace(typeName))
                    return new PlatformConfiguration();

                var assemblyPath = cfg["NimBus:PlatformAssembly"];
                System.Reflection.Assembly? assembly = null;
                if (!string.IsNullOrWhiteSpace(assemblyPath) && System.IO.File.Exists(assemblyPath))
                    assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);

                var type = assembly?.GetType(typeName, throwOnError: false)
                           ?? Type.GetType(typeName, throwOnError: false);
                if (type == null)
                    throw new InvalidOperationException(
                        $"NimBus:PlatformType '{typeName}' could not be resolved. " +
                        (assemblyPath is null
                            ? "Set NimBus:PlatformAssembly to the DLL path, or reference the assembly from NimBus.WebApp."
                            : $"Checked assembly at '{assemblyPath}'."));

                if (!typeof(IPlatform).IsAssignableFrom(type))
                    throw new InvalidOperationException($"Type '{typeName}' does not implement IPlatform.");

                return (IPlatform)Activator.CreateInstance(type)!;
            });

            // ServiceBusClient + ServiceBusAdministrationClient + IServiceBusManagement
            // are now registered by AddServiceBusTransport() inside the AddNimBus
            // configuration callback below (Phase 6.1, task #4 / issue #19). The
            // probe order (AzureWebJobsServiceBus__fullyQualifiedNamespace →
            // ConnectionStrings:servicebus → AzureWebJobsServiceBus) is retained
            // by ServiceBusTransportOptions.
            // Provider selection is configuration-driven (NimBus__StorageProvider /
            // StorageProvider env-var or appsetting, default 'cosmos'). SQL Server
            // is selected when explicitly configured OR when no Cosmos config is
            // present but a SQL connection string is.
            var storageProvider = Configuration.GetValue<string>("NimBus:StorageProvider")
                ?? Configuration.GetValue<string>("StorageProvider");
            if (string.IsNullOrWhiteSpace(storageProvider))
            {
                var hasSqlConfig = !string.IsNullOrWhiteSpace(Configuration.GetValue<string>("SqlConnection"))
                    || !string.IsNullOrWhiteSpace(Configuration.GetConnectionString("sqlserver"))
                    || !string.IsNullOrWhiteSpace(Configuration.GetValue<string>("SqlServerConnection"));
                var hasCosmosConfig = !string.IsNullOrWhiteSpace(Configuration.GetValue<string>("CosmosAccountEndpoint"))
                    || !string.IsNullOrWhiteSpace(Configuration.GetConnectionString("cosmos"))
                    || !string.IsNullOrWhiteSpace(Configuration.GetValue<string>("CosmosConnection"));
                storageProvider = (hasSqlConfig && !hasCosmosConfig) ? "sqlserver" : "cosmos";
            }

            // Transport selection mirrors the storage block: NimBus:Transport (or
            // Transport) from configuration, falling back to 'servicebus'.
            // Recognised values are 'servicebus' (default), 'rabbitmq', and 'inmemory'.
            // The matching Add{Transport}Transport() extension methods land in
            // follow-up tasks; until those exist, every value falls through to
            // WithoutTransport() so legacy ServiceBus wiring keeps working — but
            // unknown values still error for fast feedback.
            var transportProvider = (
                    Configuration.GetValue<string>("NimBus:Transport")
                    ?? Configuration.GetValue<string>("Transport")
                    ?? "servicebus")
                .ToLowerInvariant();
            if (transportProvider is not ("servicebus" or "rabbitmq" or "inmemory"))
            {
                throw new InvalidOperationException(
                    $"Unknown NimBus:Transport '{transportProvider}'. Use 'servicebus' (default), 'rabbitmq', or 'inmemory'.");
            }

            services.AddNimBus(nimbus =>
            {
                if (string.Equals(storageProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
                {
                    nimbus.AddSqlServerMessageStore();
                }
                else
                {
                    nimbus.AddCosmosDbMessageStore();
                }

                // Phase 6 transition: Add{Transport}Transport() extension methods ship
                // with tasks #18 (ServiceBus) and #24 (RabbitMQ). Until those land,
                // every selection falls through to WithoutTransport() and the legacy
                // ServiceBus wiring in NimBus.SDK keeps working. The switch block is
                // the seam those tasks fill in.
                switch (transportProvider)
                {
                    case "servicebus":
                        nimbus.AddServiceBusTransport();
                        break;
                    case "rabbitmq":
                        nimbus.AddRabbitMqTransport(opt =>
                        {
                            // Aspire bridges the RabbitMQ container as
                            // ConnectionStrings:rabbitmq (an AMQP URI). Discrete
                            // RabbitMq:* settings are honoured as a fallback for
                            // non-Aspire deployments.
                            var rabbitUri = Configuration.GetConnectionString("rabbitmq");
                            if (!string.IsNullOrWhiteSpace(rabbitUri))
                            {
                                opt.Uri = rabbitUri;
                                return;
                            }

                            var rabbitSection = Configuration.GetSection("RabbitMq");
                            if (rabbitSection.Exists())
                            {
                                opt.HostName = rabbitSection["HostName"] ?? opt.HostName;
                                if (int.TryParse(rabbitSection["Port"], out var rabbitPort)) opt.Port = rabbitPort;
                                opt.VirtualHost = rabbitSection["VirtualHost"] ?? opt.VirtualHost;
                                opt.UserName = rabbitSection["UserName"] ?? opt.UserName;
                                opt.Password = rabbitSection["Password"] ?? opt.Password;
                            }
                        });
                        break;
                    case "inmemory":
                        // TODO(#22 follow-up): replace with nimbus.AddInMemoryTransport();
                        nimbus.WithoutTransport();
                        break;
                }
            });

            services.AddSingleton<IManagerClient, ManagerClient>();

            services.AddSingleton<ICodeRepoService>(sp => new CodeRepoService(Configuration["RepositoryUrl"]));

            // IServiceBusManagement is now registered by AddServiceBusTransport above (#4 / #21).

            services.AddSingleton<IApplicationInsightsService>(services =>
                new ApplicationInsightsService(Configuration.GetValue<string>("AppInsights:ApplicationId"), Configuration.GetValue<string>("AppInsights:ApiKey"))
            );

            services.AddApplicationInsightsTelemetry();

            services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddMeter("NimBus.ServiceBus");
                })
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddSource("Azure.Cosmos.Operation")
                        .AddSource("Azure.Messaging.ServiceBus")
                        .AddSource("NimBus");
                });

            var useOtlpExporter = !string.IsNullOrWhiteSpace(Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
            if (useOtlpExporter)
            {
                services.AddOpenTelemetry().UseOtlpExporter();
            }

            var healthChecks = services.AddHealthChecks()
                .AddServiceBusHealthCheck();
            if (string.Equals(storageProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
            {
                healthChecks.AddCheck<SqlServerMessageStoreHealthCheck>(
                    "sqlserver-messagestore",
                    failureStatus: HealthStatus.Unhealthy,
                    tags: new[] { "ready" });
            }
            else
            {
                healthChecks
                    .AddCosmosDbHealthCheck()
                    .AddResolverLagCheck();
            }
            services.AddScoped<IEndpointAuthorizationService, EndpointAuthorizationService>();
            services.AddTransient<IEndpointApiController, EndpointImplementation>();
            services.AddTransient<IStorageHookApiController, StorageHookImplementation>();
            services.AddTransient<IEventApiController, EventImplementation>();
            services.AddTransient<IEventTypeApiController, EventTypeImplementation>();
            services.AddTransient<IApplicationApiController, ApplicationImplementation>();
            services.AddTransient<IMessageApiController, MessageImplementation>();
            services.AddScoped<IAdminService, AdminService>();
            services.AddTransient<IAdminApiController, AdminImplementation>();
            services.AddTransient<IMetricsApiController, MetricsImplementation>();
            services.AddTransient<IAuditApiController, AuditImplementation>();
            services.AddTransient<IDevApiController, DevImplementation>();
            services.AddScoped<SeedDataService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            app.UseCors(o =>
            {
                o.AllowCredentials().AllowAnyHeader().AllowAnyMethod().WithOrigins("login.microsoftonline.com").Build();
            });

            app.UseHttpsRedirection();

            // Add security headers to all responses
            app.UseMiddleware<SecurityHeadersMiddleware>();

            app.UseRouting();

            app.UseSpaStaticFiles();

            app.UseOpenApi();
            app.UseSwaggerUi();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHealthChecks("/health");
                endpoints.MapHealthChecks("/alive", new HealthCheckOptions
                {
                    Predicate = r => r.Tags.Contains("live")
                });
                endpoints.MapHealthChecks("/ready", new HealthCheckOptions
                {
                    Predicate = r => r.Tags.Contains("ready")
                });
                endpoints.MapHub<GridEventsHub>(Constants.AppEndpoints.GridEventHub);
                endpoints.MapControllers();
                var loginPath = app.ApplicationServices.GetService(
                    Type.GetType("NimBus.Extensions.Identity.INimBusIdentityMarker, NimBus.Extensions.Identity")
                    ?? typeof(object)) != null ? "/account/login" : "/login";
                endpoints.MapFallbackToFile("index.html", new StaticFileOptions
                {
                    OnPrepareResponse = ctx =>
                    {
                        if (!ctx.Context.User.Identity.IsAuthenticated)
                        {
                            ctx.Context.Response.Redirect(loginPath);
                        }
                    }
                });
            });
        }
    }
}
