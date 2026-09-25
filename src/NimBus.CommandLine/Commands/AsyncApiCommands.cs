using McMaster.Extensions.CommandLineUtils;
using NimBus.Core.Events;
using NimBus.Core;
using NimBus.MessageStore;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using static NimBus.CommandLine.CliHelpers;

namespace NimBus.CommandLine.Commands;

/// <summary>Registers the <c>nb asyncapi</c> command group.</summary>
internal static class AsyncApiCommands
{
    internal static void Register(CommandLineApplication app)
    {
        app.Command("asyncapi", asyncApiCommand =>
        {
            asyncApiCommand.Description = "Generate, validate, and diff AsyncAPI 3.0 documents for CI/CD governance.";

            asyncApiCommand.OnExecute(() =>
            {
                AnsiConsole.MarkupLine("[yellow]Specify a subcommand: export, validate, or diff[/]");
                asyncApiCommand.ShowHelp();
                return 1;
            });

            asyncApiCommand.Command("export", exportCommand =>
            {
                exportCommand.Description = "Export platform topology as an AsyncAPI 3.0 specification (YAML or JSON)";

                var outputOption = exportCommand.Option("-o|--output <PATH>",
                    "Output file path (defaults to ./asyncapi.yaml, or ./asyncapi.json for --format json)",
                    CommandOptionType.SingleValue);

                var formatOption = exportCommand.Option("-f|--format <FORMAT>",
                    "Output format: yaml (default) or json",
                    CommandOptionType.SingleValue);

                var assemblyOption = exportCommand.Option("-a|--assembly <PATH>",
                    "Host assembly exposing an IAsyncApiDocumentProvider; use to include fluent Publish<T>(o => o.AsyncApi…) enrichment. When omitted, the built-in platform (attribute enrichment) is exported.",
                    CommandOptionType.SingleValue);

                var providerOption = exportCommand.Option("-p|--provider <TYPE>",
                    "IAsyncApiDocumentProvider type name to use when --assembly exposes more than one",
                    CommandOptionType.SingleValue);

                exportCommand.OnExecute(() =>
                {
                    if (!TryParseFormat(formatOption, out var explicitFormat))
                    {
                        return 1;
                    }

                    return AsyncApiCli.RunExport(
                        outputOption.Value(),
                        explicitFormat,
                        Console.Out,
                        assemblyOption.Value(),
                        providerOption.Value());
                });
            });

            asyncApiCommand.Command("validate", validateCommand =>
            {
                validateCommand.Description = "Structurally validate an AsyncAPI 3.0 document (exit 0 valid, non-zero invalid)";

                var fileArgument = validateCommand.Argument("file", "Path to the AsyncAPI document to validate").IsRequired();

                validateCommand.OnExecute(() => AsyncApiCli.RunValidate(fileArgument.Value!, Console.Out));
            });

            asyncApiCommand.Command("diff", diffCommand =>
            {
                diffCommand.Description = "Diff two AsyncAPI documents (exit 0 additive-only, non-zero on breaking changes)";

                var oldArgument = diffCommand.Argument("old-file", "Path to the previous AsyncAPI document").IsRequired();
                var newArgument = diffCommand.Argument("new-file", "Path to the new AsyncAPI document").IsRequired();

                diffCommand.OnExecute(() => AsyncApiCli.RunDiff(oldArgument.Value!, newArgument.Value!, Console.Out));
            });
        });
    }
}
