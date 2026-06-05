using System.IO.Compression;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.ResponseCompression;
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
using Microsoft.Net.Http.Headers;
using NSwag.AspNetCore;
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
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
using NimBus.Extensions.Identity;
using NimBus.Management.ServiceBus;
using NimBus.MessageStore.SqlServer;
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

            // Opt into ASP.NET Core Identity-backed username/password sign-in when the
            // deployment supplies NimBusIdentity:ConnectionString. The reflection check below
            // then routes the auth-branch ladder to the Identity-only path; downstream config
            // (RequireEmailConfirmation, Bootstrap, Smtp) is bound from the same section.
            var identityConnection = Configuration["NimBusIdentity:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(identityConnection))
            {
                services.AddNimBusIdentity(opts =>
                {
                    opts.ConnectionString = identityConnection;
                    var schema = Configuration["NimBusIdentity:Schema"];
                    if (!string.IsNullOrWhiteSpace(schema)) opts.Schema = schema;
                    opts.RequireEmailConfirmation = Configuration.GetValue("NimBusIdentity:RequireEmailConfirmation", true);
                    opts.EnableEntraIdLogin = Configuration.GetValue("NimBusIdentity:EnableEntraIdLogin", false);

                    opts.Bootstrap.Email = Configuration["NimBusIdentity:Bootstrap:Email"] ?? string.Empty;
                    opts.Bootstrap.Password = Configuration["NimBusIdentity:Bootstrap:Password"] ?? string.Empty;
                    opts.Bootstrap.DisplayName = Configuration["NimBusIdentity:Bootstrap:DisplayName"] ?? string.Empty;

                    Configuration.GetSection("NimBusIdentity:Smtp").Bind(opts.Smtp);
                });
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

            // The NSwag-generated controllers cannot carry per-action attributes,
            // so the stats endpoint's anonymous exemption is applied via an
            // application-model convention instead of a class-level
            // [AllowAnonymous] (which would silently exempt every action on the
            // controller, e.g. /api/me).
            services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
                options.Conventions.Add(new AllowAnonymousActionsConvention()));

            // Entra/OIDC parity with the Identity cookie's clean-401 behaviour
            // (spec 010 FR-011/FR-012). When an anonymous, non-bearer request to
            // the SignalR hub or /api/* is challenged, the policy scheme forwards
            // to OpenIdConnect, whose default OnRedirectToIdentityProvider issues a
            // 302 to the IdP — which a SignalR negotiate or SPA fetch cannot follow.
            // Suppress the redirect for those surfaces and return a literal 401 so
            // the client surfaces the standard "session expired" affordance.
            // Browser navigations to non-API paths still redirect to the IdP.
            if (hasEntraId)
            {
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    var previous = options.Events.OnRedirectToIdentityProvider;
                    options.Events.OnRedirectToIdentityProvider = async ctx =>
                    {
                        if (NimBusCookieAuthenticationEvents.IsApiOrHubPath(ctx.Request.Path))
                        {
                            ctx.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized;
                            ctx.HandleResponse();
                            return;
                        }

                        if (previous is not null)
                        {
                            await previous(ctx).ConfigureAwait(false);
                        }
                    };
                });
            }

            services.AddControllers(options =>
            {
                options.ModelBinderProviders.Insert(0, new EnumMemberModelBinderProvider());
            }).AddJsonOptions(opts =>
            {
                opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                // Normalise every outbound DateTime to UTC with a `Z` suffix so the
                // SPA's moment.js parses it as UTC (then renders in browser-local).
                // Without this, Unspecified-Kind DateTimes from Cosmos / SQL get
                // serialised without offset and moment treats them as local time —
                // displaying UTC wall-clocks as if they were local. See
                // Services/UtcDateTimeJsonConverter.cs for the rationale.
                opts.JsonSerializerOptions.Converters.Add(new Services.UtcDateTimeJsonConverter());
                opts.JsonSerializerOptions.Converters.Add(new Services.NullableUtcDateTimeJsonConverter());
            });

            services.AddMvc().AddRazorRuntimeCompilation();

            services.AddSwaggerDocument(s =>
            {
                s.Title = "DIS Management API";
                s.Description = "Enterprise Integration Platform API - For data displayed in the management-webapp.";
            });

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // Response compression — Brotli first, Gzip second.
            // App Service's default IIS compression doesn't cover `application/javascript`
            // reliably and never serves precompressed `.br` files emitted by the Vite build;
            // the in-process middleware fills both gaps for Aspire / reverse-proxy-less
            // deployments. When an outer layer (App Service, Application Gateway) already
            // compresses, ASP.NET Core's middleware honours the existing `Content-Encoding`
            // header and does not double-encode.
            services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
                {
                    // SPA bundle assets — Vite emits these and they dominate first-load bytes.
                    "application/javascript",
                    // JSON API responses (endpoints/audits/messages lists run 100-500 KB).
                    "application/json",
                    // SPA stylesheet bundle (Tailwind compresses ~80 % under Brotli).
                    "text/css",
                    // Inline icon assets shipped from ClientApp/public.
                    "image/svg+xml",
                });
            });
            services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
            services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);

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

            // Env-var providers replace `__` with `:`, so the canonical config key
            // for `AzureWebJobsServiceBus__fullyQualifiedNamespace` is read with a colon.
            // We also accept the literal-double-underscore form (for any appsettings.json
            // that uses it as a flat key) and a bare `ServiceBusNamespace` (sb-nimbus-dev)
            // which we expand to its FQDN.
            string serviceBusFqns = Configuration["AzureWebJobsServiceBus:fullyQualifiedNamespace"]
                ?? Configuration["AzureWebJobsServiceBus__fullyQualifiedNamespace"];
            if (string.IsNullOrWhiteSpace(serviceBusFqns))
            {
                var ns = Configuration["ServiceBusNamespace"];
                if (!string.IsNullOrWhiteSpace(ns))
                {
                    serviceBusFqns = ns.Contains('.', StringComparison.Ordinal)
                        ? ns
                        : $"{ns}.servicebus.windows.net";
                }
            }
            string serviceBusConnection = Configuration.GetConnectionString("servicebus")
                ?? Configuration.GetValue<string>("AzureWebJobsServiceBus");
            if (!string.IsNullOrEmpty(serviceBusFqns) && !serviceBusFqns.Contains("SharedAccessKey="))
            {
                var credential = new DefaultAzureCredential();
                services.AddSingleton(new ServiceBusAdministrationClient(serviceBusFqns, credential));
                services.AddSingleton(new ServiceBusClient(serviceBusFqns, credential));
            }
            else
            {
                services.AddSingleton(new ServiceBusAdministrationClient(serviceBusConnection));
                services.AddSingleton(new ServiceBusClient(serviceBusConnection));
            }
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
            });

            services.AddSingleton<IManagerClient, ManagerClient>();

            services.AddSingleton<ICodeRepoService>(sp => new CodeRepoService(Configuration["RepositoryUrl"]));

            // FakeEventPayloadGenerator is registered as a singleton — it
            // holds no shared mutable state (each call uses a per-call Random
            // instance), so the JIT can keep the heuristic lookup hot.
            services.AddSingleton<FakeEventPayloadGenerator>();

            services.AddSingleton<IServiceBusManagement>(sp => new ServiceBusManagement(sp.GetRequiredService<ServiceBusAdministrationClient>()));

            // Typed HttpClient via IHttpClientFactory — pools the underlying
            // SocketsHttpHandler across calls, hooks into AddHttpClientInstrumentation
            // for OpenTelemetry, and leaves room for a future Polly retry policy.
            services.AddHttpClient<IApplicationInsightsService, ApplicationInsightsService>((sp, http) =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var appId = cfg.GetValue<string>("AppInsights:ApplicationId");
                var apiKey = cfg.GetValue<string>("AppInsights:ApiKey");
                if (!string.IsNullOrWhiteSpace(appId))
                {
                    http.BaseAddress = new Uri($"https://api.applicationinsights.io/v1/apps/{appId}/");
                }
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    http.DefaultRequestHeaders.Add("x-api-key", apiKey);
                }
            });

            services.AddApplicationInsightsTelemetry();

            services.AddNimBusInstrumentation();
            services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddNimBusInstrumentation();
                })
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddSource("Azure.Cosmos.Operation")
                        .AddSource("Azure.Messaging.ServiceBus")
                        .AddNimBusInstrumentation();
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
                healthChecks.AddCosmosDbHealthCheck();
            }
            services.AddScoped<IEndpointAuthorizationService, EndpointAuthorizationService>();
            // Spec 008: centralized audit-write contract. Scoped so the captured
            // INimBusMessageStore lifetime matches the request, consistent with
            // every other message-store consumer in this file.
            services.AddScoped<IAuditLogService, AuditLogService>();
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
            services.AddTransient<IAgentApiController, AgentImplementation>();
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

            // Response compression MUST run before UseSpaStaticFiles, UseStaticFiles, and
            // UseRouting. The static-file middleware short-circuits the request and writes
            // the response body inline; if compression sits *after* it, the SPA bundle
            // ships uncompressed because the encoding is selected too late to apply to the
            // already-written stream. Routing/endpoint middleware has the same hazard for
            // JSON API responses. Do not move this call below either of them.
            app.UseResponseCompression();

            // Add security headers to all responses
            app.UseMiddleware<SecurityHeadersMiddleware>();

            app.UseRouting();

            // Serve the Brotli/gzip siblings the Vite build emits next to each
            // asset, before the SPA static-file middleware. When the client
            // accepts the encoding and the sibling exists on disk the request
            // is rewritten to it and Content-Encoding/Vary are set; otherwise
            // the request falls through and the plain asset is served (with
            // dynamic response compression above as the fallback).
            var spaAssetRoot = System.IO.Path.Combine(env.ContentRootPath, "ClientApp", "build", "public");
            if (System.IO.Directory.Exists(spaAssetRoot))
            {
                app.UseMiddleware<PrecompressedStaticFileMiddleware>(
                    (Microsoft.Extensions.FileProviders.IFileProvider)new Microsoft.Extensions.FileProviders.PhysicalFileProvider(spaAssetRoot));
            }

            app.UseSpaStaticFiles(new StaticFileOptions
            {
                // Resolve the rewritten `.js.br` / `.css.gz` paths back to the
                // underlying asset's Content-Type so precompressed responses
                // keep their real type instead of application/octet-stream.
                ContentTypeProvider = new PrecompressedContentTypeProvider(),
                OnPrepareResponse = ctx =>
                {
                    var path = ctx.Context.Request.Path.Value ?? string.Empty;
                    var headers = ctx.Context.Response.GetTypedHeaders();
                    if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                    {
                        // Vite emits content-hashed filenames under /assets/ — the bytes
                        // for a given URL never change, so cache them aggressively and skip
                        // revalidation entirely. A new deploy ships new hashes, new URLs.
                        headers.CacheControl = new CacheControlHeaderValue
                        {
                            Public = true,
                            MaxAge = TimeSpan.FromDays(365),
                            Extensions = { new NameValueHeaderValue("immutable") },
                        };
                    }
                    else
                    {
                        // Unhashed root assets (favicon, etc.) must revalidate so a deploy
                        // is picked up without serving stale bytes.
                        headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                    }
                },
            });

            // OpenAPI / Swagger UI publishes the management API surface, so keep
            // it gated to Development. Production hosts should not expose the
            // schema (or the "try it out" UI) anonymously.
            if (env.IsDevelopment())
            {
                app.UseOpenApi();
                app.UseSwaggerUi();
            }

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
                        // index.html references content-hashed bundles; it MUST NOT be
                        // cached, or a browser holding a stale copy will request asset
                        // hashes that no longer exist after a deploy (blank page / 404s).
                        ctx.Context.Response.GetTypedHeaders().CacheControl =
                            new CacheControlHeaderValue { NoCache = true, NoStore = true, MustRevalidate = true };

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
