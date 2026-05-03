namespace NimBus.CommandLine;

internal sealed record CommandContext(string RepositoryRoot)
{
    public static CommandContext Create(string? repoRoot) =>
        new(RepositoryLocator.Resolve(repoRoot));

    public string DeployDirectory => Path.Combine(RepositoryRoot, "deploy");
    public string SourceDirectory => Path.Combine(RepositoryRoot, "src");
    public string CoreBicepPath => Path.Combine(DeployDirectory, "bicep", "deploy.core.bicep");
    public string WebAppBicepPath => Path.Combine(DeployDirectory, "bicep", "deploy.webapp.bicep");
    public string ResolverProjectPath => Path.Combine(SourceDirectory, "NimBus.Resolver", "NimBus.Resolver.csproj");
    public string WebAppProjectPath => Path.Combine(SourceDirectory, "NimBus.WebApp", "NimBus.WebApp.csproj");
}

internal static class RepositoryLocator
{
    public static string Resolve(string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            return Validate(Path.GetFullPath(repoRoot));
        }

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (LooksLikeRepositoryRoot(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new CommandException("Could not locate the NimBus repository root. Run the command from the repository or provide --repo-root.");
    }

    private static string Validate(string path)
    {
        if (!LooksLikeRepositoryRoot(path))
        {
            throw new CommandException($"'{path}' does not look like the NimBus repository root. Expected deploy/ and src/ directories.");
        }

        return path;
    }

    private static bool LooksLikeRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "README.md")) &&
        Directory.Exists(Path.Combine(path, "deploy")) &&
        Directory.Exists(Path.Combine(path, "src"));
}

internal static class NamingConventions
{
    public static string NormalizePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandException("Azure naming inputs cannot be empty.");
        }

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CommandException($"'{value}' does not contain any alpha-numeric characters after normalization.");
        }

        return normalized;
    }

    public static DeploymentNames Build(string solutionId, string environment)
    {
        var normalizedSolutionId = NormalizePart(solutionId);
        var normalizedEnvironment = NormalizePart(environment);

        return new DeploymentNames(
            normalizedSolutionId,
            normalizedEnvironment,
            $"sb-{normalizedSolutionId}-{normalizedEnvironment}",
            $"ai-{normalizedSolutionId}-{normalizedEnvironment}-global-tracelog",
            $"cosmos-{normalizedSolutionId}-{normalizedEnvironment}",
            $"func-{normalizedSolutionId}-{normalizedEnvironment}-resolver",
            $"webapp-{normalizedSolutionId}-{normalizedEnvironment}-management");
    }
}

internal sealed record DeploymentNames(
    string SolutionId,
    string Environment,
    string ServiceBusNamespace,
    string AppInsightsName,
    string CosmosAccountName,
    string ResolverFunctionAppName,
    string WebAppName);

internal sealed record InfrastructureOptions(
    string SolutionId,
    string Environment,
    string ResourceGroupName,
    string? ResourceNamePostFix,
    string? Location,
    string WebAppVersion,
    StorageProviderChoice StorageProvider = StorageProviderChoice.Cosmos,
    SqlProvisioningMode SqlMode = SqlProvisioningMode.Provision,
    string? SqlConnectionString = null,
    string? SqlAdminLogin = null,
    string? SqlAdminPassword = null,
    ResolverPlanChoice ResolverPlan = ResolverPlanChoice.ElasticPremium);

internal enum StorageProviderChoice
{
    Cosmos,
    SqlServer,
}

internal enum SqlProvisioningMode
{
    Provision,
    External,
}

internal enum ResolverPlanChoice
{
    ElasticPremium,
    FlexConsumption,
}

internal sealed record TopologyOptions(
    string SolutionId,
    string Environment,
    string ResourceGroupName);

internal sealed record AppDeploymentOptions(
    string SolutionId,
    string Environment,
    string ResourceGroupName,
    string Configuration);

internal sealed class CommandException : Exception
{
    public CommandException(string message)
        : base(message)
    {
    }
}
