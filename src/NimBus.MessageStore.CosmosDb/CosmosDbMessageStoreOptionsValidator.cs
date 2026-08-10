using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace NimBus.MessageStore;

// OptionsBuilder.Validate(predicate, message) takes a constant message, but an operator
// needs the offending value in the failure — so validation goes through IValidateOptions.
internal sealed class CosmosDbMessageStoreOptionsValidator : IValidateOptions<CosmosDbMessageStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, CosmosDbMessageStoreOptions options)
        => options.IsValid
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                CosmosDbMessageStoreOptions.DescribeInvalid(options.UnresolvedRetentionDays));
}

// Forces options materialisation during host startup so an invalid configuration value
// fails the host rather than the first request. The store singleton is resolved lazily
// (the WebApp never touches it during startup), so touching IOptions here is the only
// thing that makes a misconfiguration a startup failure.
internal sealed class CosmosDbMessageStoreOptionsStartupValidator : IHostedService
{
    private readonly IOptions<CosmosDbMessageStoreOptions> _options;

    public CosmosDbMessageStoreOptionsStartupValidator(IOptions<CosmosDbMessageStoreOptions> options)
        => _options = options;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _options.Value; // throws OptionsValidationException when invalid
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
