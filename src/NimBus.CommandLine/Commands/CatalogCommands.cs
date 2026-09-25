using McMaster.Extensions.CommandLineUtils;
using NimBus.Core.Events;
using NimBus.Core;
using NimBus.MessageStore;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using static NimBus.CommandLine.CliHelpers;

namespace NimBus.CommandLine.Commands;

/// <summary>Registers the <c>nb catalog</c> command group.</summary>
internal static class CatalogCommands
{
    internal static void Register(CommandLineApplication app)
    {
        app.Command("catalog", catalogCommand =>
        {
            catalogCommand.Description = "Generate architecture catalog from platform configuration.";

            catalogCommand.OnExecute(() =>
            {
                AnsiConsole.MarkupLine("[yellow]Specify a subcommand[/]");
                catalogCommand.ShowHelp();
                return 1;
            });

            catalogCommand.Command("export", exportCommand =>
            {
                exportCommand.Description = "Export the platform as a runnable EventCatalog (native MDX + per-service AsyncAPI)";

                var outputOption = exportCommand.Option("-o|--output <PATH>",
                    "Catalog directory (defaults to ./eventcatalog in current directory)",
                    CommandOptionType.SingleValue);

                var assemblyOption = exportCommand.Option("-a|--assembly <PATH>",
                    "Host assembly exposing a public parameterless IPlatform; default is the built-in platform",
                    CommandOptionType.SingleValue);

                var platformOption = exportCommand.Option("--platform <TYPE>",
                    "IPlatform type name when the assembly exposes more than one",
                    CommandOptionType.SingleValue);

                var titleOption = exportCommand.Option("-t|--title <TITLE>",
                    "Catalog title/organization, used only when scaffolding a missing eventcatalog.config.js (default: NimBus)",
                    CommandOptionType.SingleValue);

                exportCommand.OnExecute(() => EventCatalogCli.RunExport(
                    outputOption.Value(),
                    Console.Out,
                    assemblyOption.Value(),
                    platformOption.Value(),
                    titleOption.Value()));
            });

            catalogCommand.Command("asyncapi", asyncApiCommand =>
            {
                asyncApiCommand.Description = "Export platform topology as an AsyncAPI 3.0 specification (YAML or JSON)";

                var outputOption = asyncApiCommand.Option("-o|--output <PATH>",
                    "Output file path (defaults to ./asyncapi.yaml, or ./asyncapi.json for --format json)",
                    CommandOptionType.SingleValue);

                var formatOption = asyncApiCommand.Option("-f|--format <FORMAT>",
                    "Output format: yaml (default) or json",
                    CommandOptionType.SingleValue);

                asyncApiCommand.OnExecute(() =>
                {
                    if (!TryParseFormat(formatOption, out var explicitFormat))
                    {
                        return 1;
                    }

                    // Back-compat alias: identical output to `nb asyncapi export`, same code path.
                    return AsyncApiCli.RunExport(outputOption.Value(), explicitFormat, Console.Out);
                });
            });
        });
    }
}
