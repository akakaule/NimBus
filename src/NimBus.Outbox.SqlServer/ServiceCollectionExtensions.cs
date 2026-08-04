using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Outbox;
using System;

namespace NimBus.Outbox.SqlServer
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the SQL Server transactional outbox with the DI container.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">The SQL Server connection string.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddNimBusSqlServerOutbox(this IServiceCollection services, string connectionString)
        {
            return services.AddNimBusSqlServerOutbox(options => options.ConnectionString = connectionString);
        }

        /// <summary>
        /// Registers the SQL Server transactional outbox with the DI container.
        /// </summary>
        public static IServiceCollection AddNimBusSqlServerOutbox(this IServiceCollection services, Action<SqlServerOutboxOptions> configure)
        {
            var options = new SqlServerOutboxOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString must be specified.", nameof(configure));

            // Fail fast on degenerate lease windows (spec 025, revisions 5-6) —
            // matching the eager ConnectionString check above. The SqlServerOutbox
            // constructor validates again for direct construction.
            options.ValidateLeaseOptions();

            var outbox = new SqlServerOutbox(options);

            services.TryAddSingleton<IOutbox>(outbox);
            services.TryAddSingleton<IOutboxCleanup>(outbox);
            services.TryAddSingleton<IOutboxMetricsQuery>(outbox);

            // Spec 025 companions: registration is UNCONDITIONAL (the same singleton)
            // so DI composition is independent of configuration timing; the capability
            // signal travels on IOutboxDispatchCoordinator.DueTimeDispatchActive.
            services.TryAddSingleton<IScheduledOutbox>(outbox);
            services.TryAddSingleton<IOutboxDispatchCoordinator>(outbox);

            return services;
        }
    }
}
