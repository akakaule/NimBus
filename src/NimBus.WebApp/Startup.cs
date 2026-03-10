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
using NimBus.Core.Logging;
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
using Microsoft.Azure.Cosmos;
using Serilog;
using NimBus.SDK.Logging;
using NimBus.Management.ServiceBus;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBusLoggerProvider = NimBus.Core.Logging.ILoggerProvider;

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
            // Bypass authentication for local development (requires explicit opt-in via config)
            var enableLocalDevAuth = Configuration.GetValue<bool>("EnableLocalDevAuthentication", false);
            if (Env.IsDevelopment() && enableLocalDevAuth)
            {
                System.Console.WriteLine("WARNING: Local development authentication bypass is ENABLED. This should NEVER be used in production!");

                services.AddAuthentication("LocalDev")
                    .AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>("LocalDev", null);

                services.AddControllersWithViews().AddMicrosoftIdentityUI();
            }
            else
            {
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

            services.AddControllers().AddJsonOptions(opts =>
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

            services.AddSingleton<IPlatform, PlatformConfiguration>();

            string serviceBusFqns = Configuration.GetValue<string>("AzureWebJobsServiceBus__fullyQualifiedNamespace");
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
            services.AddSingleton<ISender>(sp => new SenderManager(sp.GetRequiredService<ServiceBusClient>().CreateSender(NimBus.Core.Messages.Constants.ManagerId)));

            string globalTraceLogInstrKey = Configuration.GetValue<string>("APPINSIGHTS_INSTRUMENTATIONKEY");
            services.AddSingleton<NimBusLoggerProvider>(sp => {
                Serilog.ILogger baseLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .WriteTo.ApplicationInsights(globalTraceLogInstrKey, TelemetryConverter.Traces)
                .CreateLogger();

                return new LoggerProvider(baseLogger);
            });

            string cosmosEndpoint = Configuration.GetValue<string>("CosmosAccountEndpoint");
            string cosmosConnection = Configuration.GetConnectionString("cosmos")
                ?? Configuration.GetValue<string>("CosmosConnection");
            services.AddSingleton<ICosmosDbClient>(sp =>
            {
                CosmosClient cosmosClient;
                if (!string.IsNullOrEmpty(cosmosEndpoint) && !cosmosEndpoint.Contains("AccountKey="))
                    cosmosClient = new CosmosClient(cosmosEndpoint, new DefaultAzureCredential());
                else
                    cosmosClient = new CosmosClient(cosmosConnection);
                return new CosmosDbClient(cosmosClient);
            });

            services.AddSingleton<IManagerClient, ManagerClient>();

            services.AddSingleton<ICodeRepoService>(sp => new CodeRepoService(Configuration["RepositoryUrl"]));

            services.AddSingleton<IServiceBusManagement>(sp => new ServiceBusManagement(sp.GetRequiredService<ServiceBusAdministrationClient>()));

            services.AddSingleton<IApplicationInsightsService>(services =>
                new ApplicationInsightsService(Configuration.GetValue<string>("AppInsights:ApplicationId"), Configuration.GetValue<string>("AppInsights:ApiKey"))
            );

            services.AddApplicationInsightsTelemetry();
            services.AddHealthChecks();
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
                endpoints.MapHealthChecks("/alive");
                endpoints.MapHub<GridEventsHub>(Constants.AppEndpoints.GridEventHub);
                endpoints.MapControllers();
                endpoints.MapFallbackToFile("index.html", new StaticFileOptions
                {
                    OnPrepareResponse = ctx =>
                    {
                        if (!ctx.Context.User.Identity.IsAuthenticated)
                        {
                            // Can redirect to any URL where you prefer.
                            ctx.Context.Response.Redirect("/login");
                        }
                    }
                });
            });
        }
    }
}
