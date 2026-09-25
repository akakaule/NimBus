namespace NimBus.CommandLine;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var app = CliApplicationFactory.Create();

        try
        {
            return await app.ExecuteAsync(args).ConfigureAwait(false);
        }
        catch (CommandException exception)
        {
            CliOutput.WriteError(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            CliOutput.WriteError($"Command failed with exception ({exception.GetType().Name}): {exception.Message}");
            return 1;
        }
    }
}
