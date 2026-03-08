namespace EET.DIS.CommandLine
{
    using McMaster.Extensions.CommandLineUtils;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var app = new CommandLineApplication
            {
                Name = "dis-cli"
            };

            var sbConnectionString = new CommandOption("-sbc|--sb-connection-string", CommandOptionType.SingleValue)
            {
                Description = $"Overrides environment variable '{CommandRunner.SbConnectionStringEnvName}'"
            };

            var dbConnectionString = new CommandOption("-dbc|--db-connection-string", CommandOptionType.SingleValue)
            {
                Description = $"Overrides environment variable '{CommandRunner.DbConnectionStringEnvName}'"
            };

            app.HelpOption(inherited: true);

            app.Command("endpoint", endpointCommand =>
            {
                endpointCommand.OnExecute(() =>
                {
                    Console.WriteLine("Specify a subcommand");
                    endpointCommand.ShowHelp();
                    return 1;
                });

                endpointCommand.Command("session", sessionCommand =>
                {
                    sessionCommand.OnExecute(() =>
                    {
                        Console.WriteLine("Specify a subcommand");
                        sessionCommand.ShowHelp();
                        return 1;
                    });

                    sessionCommand.Command("delete", deleteSessionCommand =>
                    {
                        deleteSessionCommand.Description = "Deletes messages on a session and its session state";

                        var endpointName = deleteSessionCommand.Argument("endpoint-name", "Name of endpoint (requried)").IsRequired();
                        var sessionId = deleteSessionCommand.Argument("session", "Session id (required)").IsRequired();

                        deleteSessionCommand.AddOption(sbConnectionString);
                        deleteSessionCommand.AddOption(dbConnectionString);

                        deleteSessionCommand.OnExecuteAsync(async ct =>
                        {
                            await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient, sbAdmin) => Endpoint.DeleteSession(sbClient, dbClient, endpointName, sessionId));

                            Console.WriteLine($"Endpoint '{endpointName.Value}' is ready.");
                        });
                    });
                });
                
                endpointCommand.Command("topics", sessionCommand =>
                {
                    sessionCommand.OnExecute(() =>
                    {
                        Console.WriteLine("Specify a subcommand");
                        sessionCommand.ShowHelp();
                        return 1;
                    });
                    
                    sessionCommand.Command("removeDeprecated", removeDeprecatedCommand =>
                    {
                        removeDeprecatedCommand.Description = "Deletes deprecated topics and the underlying subscriptions and rules from the service bus";
                        
                        var endpointName = removeDeprecatedCommand.Argument("endpoint-name", "Name of endpoint (requried)").IsRequired();
                        
                        removeDeprecatedCommand.AddOption(sbConnectionString);

                        removeDeprecatedCommand.OnExecuteAsync(async ct =>
                        {
                            await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient, sbAdmin) => Endpoint.RemoveDeprecated(sbAdmin, endpointName));
                        });
                    });
                });
            });

            app.Command("config-export", configExportCommand =>
            {
                configExportCommand.Description = "Exports platform configuration to JSON for PowerShell deployment scripts";

                var outputPath = new CommandOption("-o|--output", CommandOptionType.SingleValue)
                {
                    Description = "Output file path (defaults to platform-config.json)"
                };
                configExportCommand.AddOption(outputPath);

                configExportCommand.OnExecuteAsync(async ct =>
                {
                    await ConfigExport.Export(outputPath);
                });
            });

            app.Command("container", endpointCommand =>
            {
                endpointCommand.OnExecute(() =>
                {
                    Console.WriteLine("Specify a subcommand");
                    endpointCommand.ShowHelp();
                    return 1;
                });

                endpointCommand.Command("event", messageCommand =>
                {
                    messageCommand.OnExecute(() =>
                    {
                        Console.WriteLine("Specify a subcommand");
                        messageCommand.ShowHelp();
                        return 1;
                    });

                    messageCommand.Command("delete", deleteMessageCommand =>
                    {
                        deleteMessageCommand.Description = "Deletes a message";

                        deleteMessageCommand.AddOption(dbConnectionString);

                        var endpointName = deleteMessageCommand.Argument("endpoint-name", "Name of endpoint (requried)").IsRequired();
                        var eventId = deleteMessageCommand.Argument("event-id", "Id of event (requried)").IsRequired();

                        deleteMessageCommand.OnExecuteAsync(async ct =>
                        {
                            await CommandRunner.Run(dbConnectionString, (dbClient) => Container.DeleteDocument(dbClient, endpointName, eventId));
                        });
                    });
                });

                endpointCommand.Command("delete", deleteCommand =>
                {
                    deleteCommand.Description = "Deletes messages in cosmos db";

                    var endpointName = deleteCommand.Argument("endpoint-name", "Name of endpoint (requried)").IsRequired();
                    deleteCommand.AddOption(dbConnectionString);

                    deleteCommand.OnExecuteAsync(async ct =>
                    {
                        await CommandRunner.Run(dbConnectionString, (dbClient) => Container.DeleteDocuments(dbClient, endpointName));
                    });
                });

                endpointCommand.Command("resubmit", deleteCommand =>
                {
                    deleteCommand.Description = "Updates messages in cosmos db and resubmit messages";

                    var endpointName = deleteCommand.Argument("endpoint-name", "Name of endpoint (requried)").IsRequired();
                    deleteCommand.AddOption(sbConnectionString);
                    deleteCommand.AddOption(dbConnectionString);

                    deleteCommand.OnExecuteAsync(async ct =>
                    {
                        await CommandRunner.Run(sbConnectionString, dbConnectionString, (sbClient, dbClient) => Container.ResubmitMessages(sbClient, dbClient, endpointName));
                    });
                });
            });

            app.OnExecute(() =>
            {
                Console.WriteLine("Specify a subcommand");
                app.ShowHelp();
                return 1;
            });

            try
            {
                return app.Execute(args);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Command failed with exception ({exception.GetType().Name}): {exception.Message}");
                return 1;
            }
        }
    }
}