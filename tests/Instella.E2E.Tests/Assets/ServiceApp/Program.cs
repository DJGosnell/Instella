// The Windows service ServiceSelfUpdateE2ETests installs. Not part of the solution: the test copies
// this file into a generated project that references Instella.Sdk and the Windows-service host.
// args[0] is the folder it writes its markers to.
using Instella.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var markers = args[0];
var builder = Host.CreateApplicationBuilder();
builder.Services.AddWindowsService();
builder.Services.AddSingleton(new Markers(markers));
builder.Services.AddHostedService<UpdateWorker>();
await builder.Build().RunAsync();

internal sealed record Markers(string Folder);

/// <summary>Writes the running version, then checks for an update once and starts it as a service update.</summary>
internal sealed class UpdateWorker(Markers markers) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var info = InstellaClient.GetCurrentInfo();
        File.WriteAllText(Path.Combine(markers.Folder, $"running-{info.Version}.txt"),
            $"IsRunningAsService={await InstellaClient.IsRunningAsServiceAsync(stoppingToken)}");

        var check = await InstellaClient.CheckForUpdateAsync(stoppingToken);
        if (check.Update is not { } update)
        {
            File.WriteAllText(Path.Combine(markers.Folder, $"check-{info.Version}.txt"), $"{check.Status};{check.Error}");
            return;
        }

        var start = await InstellaClient.StartServiceUpdateAsync(update, stoppingToken);
        File.WriteAllText(Path.Combine(markers.Folder, $"start-{info.Version}.txt"),
            $"IsStarted={start.IsStarted};ProcessId={start.ProcessId};Refusal={start.RefusalReason}");
    }
}
