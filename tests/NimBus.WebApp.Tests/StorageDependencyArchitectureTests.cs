#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Heartbeat;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class StorageDependencyArchitectureTests
{
    private static readonly Type AggregateStoreType = typeof(INimBusMessageStore);

    [TestMethod]
    public void WebApp_controllers_and_services_do_not_inject_aggregate_store()
    {
        var assembly = typeof(MetricsImplementation).Assembly;
        var offenders = assembly
            .GetTypes()
            .Where(IsWebAppControllerOrService)
            .SelectMany(type => type
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters()
                    .Where(parameter => parameter.ParameterType == AggregateStoreType)
                    .Select(parameter => $"{type.FullName}({parameter.Name})")))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.IsEmpty(
            offenders,
            "WebApp production consumers must depend on narrow storage contracts, not " +
            $"{AggregateStoreType.Name}: {string.Join(", ", offenders)}");
    }

    [TestMethod]
    public void Known_storage_consumers_declare_their_narrow_contracts()
    {
        var expected = new Dictionary<Type, Type[]>
        {
            [typeof(MetricsImplementation)] = [typeof(IMetricsStore)],
            [typeof(MessageImplementation)] = [typeof(IMessageTrackingStore)],
            [typeof(AuditImplementation)] = [typeof(IMessageTrackingStore)],
            [typeof(EventImplementation)] = [typeof(IMessageTrackingStore)],
            [typeof(AgentImplementation)] = [typeof(IMessageTrackingStore)],
            [typeof(StorageHookImplementation)] = [typeof(IMessageTrackingStore)],
            [typeof(AuditLogService)] = [typeof(IMessageTrackingStore)],
            [typeof(HandoffSettlementService)] = [typeof(IMessageTrackingStore)],
            [typeof(HeartbeatService)] =
            [
                typeof(IEndpointMetadataStore),
                typeof(IServiceHealthStore),
                typeof(IHeartbeatHistoryStore),
            ],
            [typeof(EndpointImplementation)] =
            [
                typeof(IMessageTrackingStore),
                typeof(ISubscriptionStore),
                typeof(IEndpointMetadataStore),
            ],
            [typeof(SeedDataService)] =
            [
                typeof(IMessageTrackingStore),
                typeof(ISubscriptionStore),
                typeof(IEndpointMetadataStore),
            ],
            [typeof(AdminService)] = [typeof(IMessageTrackingStore)],
        };

        foreach (var (consumer, contracts) in expected)
        {
            var constructorParameterTypes = consumer
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .ToArray();

            CollectionAssert.IsSubsetOf(
                contracts,
                constructorParameterTypes,
                $"{consumer.Name} must declare its narrow storage contracts.");
        }
    }

    private static bool IsWebAppControllerOrService(Type type)
    {
        var ns = type.Namespace ?? string.Empty;
        return ns.StartsWith("NimBus.WebApp.Controllers", StringComparison.Ordinal)
            || ns.StartsWith("NimBus.WebApp.Services", StringComparison.Ordinal);
    }
}
