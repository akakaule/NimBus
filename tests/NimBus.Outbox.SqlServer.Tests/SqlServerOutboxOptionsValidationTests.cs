#pragma warning disable CA1707, CA2007
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Outbox.SqlServer;

namespace NimBus.Outbox.SqlServer.Tests;

/// <summary>
/// Spec 025 (revisions 5-6): lease-option invariants fail fast at startup with
/// ArgumentOutOfRangeException naming the offending property — a degenerate
/// window may never silently cancel every send attempt.
/// </summary>
[TestClass]
public sealed class SqlServerOutboxOptionsValidationTests
{
    private static SqlServerOutboxOptions Options(TimeSpan duration, TimeSpan margin) => new()
    {
        ConnectionString = "Server=unused;Database=unused;Encrypt=false",
        SendLeaseDuration = duration,
        SendLeaseSafetyMargin = margin,
    };

    [TestMethod]
    public void MarginEqualToDuration_IsRejectedNamingProperty()
    {
        var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Options(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)).ValidateLeaseOptions());
        Assert.AreEqual(nameof(SqlServerOutboxOptions.SendLeaseSafetyMargin), ex.ParamName);
    }

    [TestMethod]
    public void MarginGreaterThanDuration_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Options(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(31)).ValidateLeaseOptions());
    }

    [TestMethod]
    public void NegativeMargin_IsRejectedNamingProperty()
    {
        var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Options(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(-1)).ValidateLeaseOptions());
        Assert.AreEqual(nameof(SqlServerOutboxOptions.SendLeaseSafetyMargin), ex.ParamName);
    }

    [TestMethod]
    public void NonPositiveDuration_IsRejectedNamingProperty()
    {
        var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Options(TimeSpan.Zero, TimeSpan.Zero).ValidateLeaseOptions());
        Assert.AreEqual(nameof(SqlServerOutboxOptions.SendLeaseDuration), ex.ParamName);
    }

    [TestMethod]
    public void DurationAbove24Hours_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Options(TimeSpan.FromHours(24) + TimeSpan.FromTicks(1), TimeSpan.Zero).ValidateLeaseOptions());
    }

    [TestMethod]
    public void UsableWindowOneTickBelowFloor_IsRejected()
    {
        var duration = SqlServerOutboxOptions.MinimumUsableSendWindow + TimeSpan.FromSeconds(1);
        var margin = TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Options(duration, margin).ValidateLeaseOptions());
    }

    [TestMethod]
    public void UsableWindowExactlyAtFloor_IsAccepted()
    {
        Options(SqlServerOutboxOptions.MinimumUsableSendWindow + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            .ValidateLeaseOptions();
    }

    [TestMethod]
    public void Defaults_AreValid()
    {
        new SqlServerOutboxOptions { ConnectionString = "x" }.ValidateLeaseOptions();
    }

    [TestMethod]
    public void Constructor_ValidatesEagerly()
    {
        var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SqlServerOutbox(Options(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))));
        Assert.AreEqual(nameof(SqlServerOutboxOptions.SendLeaseSafetyMargin), ex.ParamName);
    }
}
