using System.CommandLine;
using Instella.CLI.Commands;

var root = new RootCommand("Instella CLI - project scaffolding, publisher keys and package upload for CI/CD pipelines. " +
    $"Documentation: {Instella.CLI.Services.InstellaDocs.Url}");
root.Subcommands.Add(InitCommand.Create());
root.Subcommands.Add(KeysCommand.Create());
root.Subcommands.Add(UploadCommand.Create());
root.Subcommands.Add(PublishCommand.Create());
root.Subcommands.Add(CiCommand.Create());
root.Subcommands.Add(ListCommand.Create());
root.Subcommands.Add(DeleteCommand.Create());

return await root.Parse(args).InvokeAsync();
