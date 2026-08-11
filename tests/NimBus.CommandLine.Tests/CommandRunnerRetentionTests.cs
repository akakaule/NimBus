#pragma warning disable CA1707, CA2007
using McMaster.Extensions.CommandLineUtils;
using NimBus.MessageStore;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// `nb container resubmit` rewrites the whole tracking document, so the CLI must stamp
/// the same retention the hosts are configured with — otherwise resubmission silently
/// disables expiry on every row it touches.
/// </summary>
/// <remarks>
/// These cases mutate a process-wide environment variable, so the class opts out of
/// xUnit's cross-class parallelism.
/// </remarks>
[Collection("Retention environment")]
public sealed class CommandRunnerRetentionTests
{
    [Fact]
    public void No_flag_and_no_environment_means_unlimited()
    {
        using var env = RetentionEnvironment.Set(null);

        Assert.Equal(-1, CommandRunner.ResolveStoreOptions(null).UnresolvedRetentionDays);
    }

    [Fact]
    public void The_environment_variable_is_the_fallback()
    {
        using var env = RetentionEnvironment.Set("180");

        Assert.Equal(180, CommandRunner.ResolveStoreOptions(null).UnresolvedRetentionDays);
    }

    [Fact]
    public void The_flag_wins_over_the_environment_variable()
    {
        using var env = RetentionEnvironment.Set("180");

        Assert.Equal(365, CommandRunner.ResolveStoreOptions(OptionWithValue("365")).UnresolvedRetentionDays);
    }

    [Fact]
    public void The_flag_can_ask_for_unlimited()
    {
        using var env = RetentionEnvironment.Set("180");

        Assert.Equal(-1, CommandRunner.ResolveStoreOptions(OptionWithValue("-1")).UnresolvedRetentionDays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_in_the_environment_variable_is_treated_as_unset(string raw)
    {
        using var env = RetentionEnvironment.Set(raw);

        Assert.Equal(-1, CommandRunner.ResolveStoreOptions(null).UnresolvedRetentionDays);
    }

    [Fact]
    public void A_non_numeric_environment_value_is_rejected()
    {
        using var env = RetentionEnvironment.Set("abc");

        var ex = Assert.Throws<InvalidOperationException>(() => CommandRunner.ResolveStoreOptions(null));

        Assert.Contains("--unresolved-retention-days", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CommandRunner.UnresolvedRetentionEnvName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("abc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_flag_value_is_rejected()
    {
        using var env = RetentionEnvironment.Set(null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => CommandRunner.ResolveStoreOptions(OptionWithValue("abc")));

        Assert.Contains("abc", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    public void An_out_of_range_flag_value_is_rejected(string raw)
    {
        using var env = RetentionEnvironment.Set(null);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandRunner.ResolveStoreOptions(OptionWithValue(raw)));

        Assert.Contains(nameof(CosmosDbMessageStoreOptions.UnresolvedRetentionDays), ex.Message, StringComparison.Ordinal);
        Assert.Contains(raw, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_range_environment_value_is_rejected()
    {
        using var env = RetentionEnvironment.Set("400");

        Assert.Throws<ArgumentOutOfRangeException>(() => CommandRunner.ResolveStoreOptions(null));
    }

    /// <summary>
    /// McMaster's <c>TryParse</c> takes "the value that follows after the flag" — the parser
    /// has already stripped <c>--unresolved-retention-days=</c>. The guard assertions make a
    /// wrong construction fail here rather than silently falling through to the environment
    /// fallback and passing for the wrong reason.
    /// </summary>
    private static CommandOption OptionWithValue(string value)
    {
        var option = new CommandOption("--unresolved-retention-days", CommandOptionType.SingleValue);
        Assert.True(option.TryParse(value));
        Assert.True(option.HasValue());
        Assert.Equal(value, option.Value());
        return option;
    }

    private sealed class RetentionEnvironment : IDisposable
    {
        private readonly string? _original;

        private RetentionEnvironment(string? original) => _original = original;

        public static RetentionEnvironment Set(string? value)
        {
            var original = Environment.GetEnvironmentVariable(CommandRunner.UnresolvedRetentionEnvName);
            Environment.SetEnvironmentVariable(CommandRunner.UnresolvedRetentionEnvName, value);
            return new RetentionEnvironment(original);
        }

        public void Dispose() =>
            Environment.SetEnvironmentVariable(CommandRunner.UnresolvedRetentionEnvName, _original);
    }
}

[CollectionDefinition("Retention environment", DisableParallelization = true)]
public sealed class RetentionEnvironmentTestGroup
{
}
