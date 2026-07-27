#pragma warning disable CA1707, CA2007
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public class EntraAdminClaimsTransformationTests
{
    private const string AdminGroupId = "11111111-2222-3333-4444-555555555555";
    private const string UserOid = "102ce428-e204-4048-9f22-9be33f2867ac";

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values)
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value));
        return builder.Build();
    }

    private static ClaimsPrincipal BuildPrincipal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "AuthenticationTypes.Federation");
        return new ClaimsPrincipal(identity);
    }

    [TestMethod]
    public async Task GroupObjectIdMatch_AddsAdminGroupClaim()
    {
        var configuration = BuildConfiguration(("Authorization:AdminGroupObjectIds", AdminGroupId));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(
            new Claim("oid", UserOid),
            new Claim("groups", AdminGroupId));

        var result = await sut.TransformAsync(principal);

        Assert.IsTrue(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }

    [TestMethod]
    public async Task UserObjectIdMatch_AddsAdminGroupClaim()
    {
        var configuration = BuildConfiguration(("Authorization:AdminUserObjectIds", UserOid));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(new Claim("oid", UserOid));

        var result = await sut.TransformAsync(principal);

        Assert.IsTrue(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }

    [TestMethod]
    public async Task LongObjectIdClaimType_IsAlsoMatched()
    {
        // Depending on claim mapping, the object id can surface under the full
        // schema URI instead of the short "oid" name.
        var configuration = BuildConfiguration(("Authorization:AdminUserObjectIds", UserOid));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", UserOid));

        var result = await sut.TransformAsync(principal);

        Assert.IsTrue(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }

    [TestMethod]
    public async Task CommaSeparatedList_MatchesAnyEntry()
    {
        var configuration = BuildConfiguration(
            ("Authorization:AdminGroupObjectIds", $"99999999-0000-0000-0000-000000000000, {AdminGroupId}"));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(new Claim("groups", AdminGroupId));

        var result = await sut.TransformAsync(principal);

        Assert.IsTrue(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }

    [TestMethod]
    public async Task ArrayConfigShape_MatchesAnyEntry()
    {
        var configuration = BuildConfiguration(
            ("Authorization:AdminGroupObjectIds:0", "99999999-0000-0000-0000-000000000000"),
            ("Authorization:AdminGroupObjectIds:1", AdminGroupId));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(new Claim("groups", AdminGroupId));

        var result = await sut.TransformAsync(principal);

        Assert.IsTrue(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }

    [TestMethod]
    public async Task NoMatch_AddsNoClaim()
    {
        var configuration = BuildConfiguration(
            ("Authorization:AdminGroupObjectIds", AdminGroupId),
            ("Authorization:AdminUserObjectIds", "99999999-0000-0000-0000-000000000000"));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(
            new Claim("oid", UserOid),
            new Claim("groups", "66666666-7777-8888-9999-000000000000"));

        var result = await sut.TransformAsync(principal);

        Assert.IsFalse(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }

    [TestMethod]
    public async Task NoConfiguration_AddsNoClaim()
    {
        var configuration = BuildConfiguration();
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(
            new Claim("oid", UserOid),
            new Claim("groups", AdminGroupId));

        var result = await sut.TransformAsync(principal);

        Assert.IsFalse(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }

    [TestMethod]
    public async Task AlreadyAdmin_DoesNotDuplicateClaim()
    {
        var configuration = BuildConfiguration(("Authorization:AdminGroupObjectIds", AdminGroupId));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(
            new Claim("groups", "EIP_Management"),
            new Claim("groups", AdminGroupId));

        var result = await sut.TransformAsync(principal);

        Assert.AreEqual(1, result.FindAll(c => c.Type == "groups" && c.Value == "EIP_Management").Count());
    }

    [TestMethod]
    public async Task UnauthenticatedPrincipal_IsLeftUntouched()
    {
        var configuration = BuildConfiguration(("Authorization:AdminGroupObjectIds", AdminGroupId));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var identity = new ClaimsIdentity();
        var principal = new ClaimsPrincipal(identity);

        var result = await sut.TransformAsync(principal);

        Assert.IsFalse(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }

    [TestMethod]
    public async Task GroupIdComparison_IsCaseInsensitive()
    {
        var configuration = BuildConfiguration(
            ("Authorization:AdminGroupObjectIds", AdminGroupId.ToUpperInvariant()));
        var sut = new EntraAdminClaimsTransformation(configuration);
        var principal = BuildPrincipal(new Claim("groups", AdminGroupId));

        var result = await sut.TransformAsync(principal);

        Assert.IsTrue(result.HasClaim(c => c.Type == "groups" && c.Value == "EIP_Management"));
    }
}
