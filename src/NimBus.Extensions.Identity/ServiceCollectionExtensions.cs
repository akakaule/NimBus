using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Extensions.Identity.Data;
using NimBus.Extensions.Identity.Services;

namespace NimBus.Extensions.Identity;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers NimBus Identity with the DI container using a SQL Server connection string.
    /// </summary>
    public static IServiceCollection AddNimBusIdentity(this IServiceCollection services, string connectionString)
    {
        return services.AddNimBusIdentity(options => options.ConnectionString = connectionString);
    }

    /// <summary>
    /// Registers NimBus Identity with the DI container.
    /// </summary>
    public static IServiceCollection AddNimBusIdentity(this IServiceCollection services, Action<NimBusIdentityOptions> configure)
    {
        var options = new NimBusIdentityOptions();
        configure(options);

        if (string.IsNullOrEmpty(options.ConnectionString))
            throw new ArgumentException("ConnectionString must be specified.", nameof(configure));

        services.AddSingleton(options);
        services.TryAddSingleton<INimBusIdentityMarker, NimBusIdentityMarker>();

        services.AddDbContext<NimBusIdentityDbContext>(db =>
            db.UseSqlServer(options.ConnectionString));

        services.AddIdentity<NimBusUser, IdentityRole>(identity =>
            {
                identity.SignIn.RequireConfirmedEmail = options.RequireEmailConfirmation;
                identity.Password.RequireDigit = true;
                identity.Password.RequireLowercase = true;
                identity.Password.RequireUppercase = true;
                identity.Password.RequireNonAlphanumeric = false;
                identity.Password.RequiredLength = 8;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                identity.Lockout.MaxFailedAccessAttempts = 5;
                identity.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<NimBusIdentityDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.LoginPath = "/account/login";
            cookie.LogoutPath = "/account/logout";
            cookie.AccessDeniedPath = "/account/login";
            cookie.Cookie.Name = "NimBus.Identity";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
            cookie.SlidingExpiration = true;
        });

        services.AddTransient<IEmailSender, SmtpEmailSender>();
        services.AddTransient<IClaimsTransformation, NimBusClaimsTransformation>();

        return services;
    }
}
