using McMaster.Extensions.CommandLineUtils;
using McMaster.Extensions.CommandLineUtils.HelpText;
using Spectre.Console;

namespace NimBus.CommandLine;

class ColoredHelpTextGenerator : IHelpTextGenerator
{
    public void Generate(CommandLineApplication application, TextWriter output)
    {
        var fullCommandPath = GetFullCommandPath(application);

        // Usage line
        var usage = $"[blue bold]Usage:[/] [green]{fullCommandPath.EscapeMarkup()}[/]";

        foreach (var arg in application.Arguments)
            usage += $" [yellow]<{arg.Name.EscapeMarkup()}>[/]";

        var visibleCommands = application.Commands.Where(c => c.ShowInHelpText).ToList();
        if (visibleCommands.Count > 0)
            usage += " [yellow][[command]][/]";

        var visibleOptions = application.Options.Where(o => o.ShowInHelpText).ToList();
        if (visibleOptions.Count > 0)
            usage += " [yellow][[options]][/]";

        AnsiConsole.MarkupLine(usage);

        // Description
        if (!string.IsNullOrEmpty(application.Description))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(application.Description.EscapeMarkup());
        }

        // Arguments
        if (application.Arguments.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue bold]Arguments:[/]");
            var maxArgLen = application.Arguments.Max(a => a.Name?.Length ?? 0);
            foreach (var arg in application.Arguments)
            {
                var name = arg.Name ?? "";
                var padding = new string(' ', Math.Max(maxArgLen - name.Length + 2, 2));
                var desc = arg.Description?.EscapeMarkup() ?? "";
                AnsiConsole.MarkupLine($"  [yellow]{name.EscapeMarkup()}[/]{padding}{desc}");
            }
        }

        // Options
        if (visibleOptions.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue bold]Options:[/]");
            var optionLabels = visibleOptions.Select(FormatOptionLabel).ToList();
            var maxOptLen = optionLabels.Max(l => l.Length);
            for (int i = 0; i < visibleOptions.Count; i++)
            {
                var label = optionLabels[i];
                var padding = new string(' ', Math.Max(maxOptLen - label.Length + 2, 2));
                var desc = visibleOptions[i].Description?.EscapeMarkup() ?? "";
                AnsiConsole.MarkupLine($"  [yellow]{label.EscapeMarkup()}[/]{padding}{desc}");
            }
        }

        // Commands
        if (visibleCommands.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue bold]Commands:[/]");
            var maxCmdLen = visibleCommands.Max(c => c.Name?.Length ?? 0);
            foreach (var subcmd in visibleCommands)
            {
                var name = subcmd.Name ?? "";
                var padding = new string(' ', Math.Max(maxCmdLen - name.Length + 2, 2));
                var desc = subcmd.Description?.EscapeMarkup() ?? "";
                AnsiConsole.MarkupLine($"  [green]{name.EscapeMarkup()}[/]{padding}{desc}");
            }
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]Run '{fullCommandPath.EscapeMarkup()} [[command]] --help' for more information about a command.[/]");
        }

        AnsiConsole.WriteLine();
    }

    static string GetFullCommandPath(CommandLineApplication application)
    {
        var names = new List<string>();
        for (var cmd = application; cmd != null; cmd = cmd.Parent)
            names.Insert(0, cmd.Name ?? "");
        return string.Join(" ", names);
    }

    static string FormatOptionLabel(CommandOption option)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(option.ShortName))
            parts.Add($"-{option.ShortName}");
        if (!string.IsNullOrEmpty(option.LongName))
            parts.Add($"--{option.LongName}");
        if (!string.IsNullOrEmpty(option.SymbolName))
            parts.Add($"-{option.SymbolName}");

        var label = string.Join("|", parts);

        if (option.OptionType == CommandOptionType.SingleValue)
            label += $" <{option.ValueName ?? "VALUE"}>";
        else if (option.OptionType == CommandOptionType.MultipleValue)
            label += $" <{option.ValueName ?? "VALUE"}>...";
        return label;
    }
}
