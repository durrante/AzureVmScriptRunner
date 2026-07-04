using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Infrastructure.Azure;
using AzureVmScriptRunner.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

// Validation harness for the execution core before the WPF UI exists.
// Usage:
//   avsr discover [search]
//   avsr run <vmName> <powershell...>          e.g. avsr run vm-01 Get-Service spooler
//   avsr cmd <vmName> <command...>             e.g. avsr cmd vm-01 ipconfig /all
//   avsr psadt <vmName> <packageUrl> [Install|Uninstall|Repair]
//   avsr restart <vmName>
//   avsr history [search]
//   avsr logout

if (args.Length == 0)
{
    Console.WriteLine("Commands: discover [search] | run <vm> <script> | cmd <vm> <command> | " +
        "psadt <vm> <packageUrl> [type] | restart <vm> | history [search] | logout");
    return 1;
}

if (args[0].Equals("logout", StringComparison.OrdinalIgnoreCase))
{
    AzureSession.ClearPersistedAccount();
    Console.WriteLine("Persisted account cleared; next run will prompt for sign-in.");
    return 0;
}

Console.WriteLine("Signing in to Azure...");
var session = await AzureSession.CreateAsync();
Console.WriteLine($"Signed in as {session.UserPrincipalName}");

var services = new ServiceCollection()
    .AddLocalPersistence()
    .AddAzureVmScriptRunner(session)
    .BuildServiceProvider();

if (args[0].Equals("history", StringComparison.OrdinalIgnoreCase))
{
    var history = services.GetRequiredService<IJobHistoryService>();
    var records = await history.QueryAsync(new JobHistoryQuery
    {
        SearchText = args.Length > 1 ? args[1] : null,
        MaxResults = 25
    });

    Console.WriteLine($"\n{records.Count} record(s):\n");
    Console.WriteLine($"{"WHEN (UTC)",-20} {"VM",-20} {"TYPE",-16} {"STATUS",-24} {"EXIT",-6} {"BY",-25} NAME");
    foreach (var r in records)
    {
        Console.WriteLine(
            $"{r.StartedAt:yyyy-MM-dd HH:mm:ss,-20} {r.VmName,-20} {r.ExecutionType,-16} " +
            $"{r.Status,-24} {r.ExitCode?.ToString() ?? "-",-6} {r.RequestedBy,-25} {r.DisplayName}");
    }

    return 0;
}

var discovery = services.GetRequiredService<IVmDiscoveryService>();

switch (args[0].ToLowerInvariant())
{
    case "discover":
    {
        var filter = args.Length > 1 ? new VmDiscoveryFilter { SearchText = args[1] } : null;
        var vms = await discovery.DiscoverAsync(filter);
        Console.WriteLine($"\n{vms.Count} Windows VM(s):\n");
        Console.WriteLine($"{"NAME",-30} {"RESOURCE GROUP",-30} {"REGION",-15} {"POWER",-12}");
        foreach (var vm in vms)
        {
            Console.WriteLine($"{vm.Name,-30} {vm.ResourceGroup,-30} {vm.Region,-15} {vm.PowerState,-12}");
        }

        return 0;
    }

    case "run" or "cmd" or "psadt" or "restart" when args.Length >= 2:
    {
        var vms = await discovery.DiscoverAsync(new VmDiscoveryFilter { SearchText = args[1] });
        var target = vms.FirstOrDefault(v => v.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            Console.Error.WriteLine($"VM '{args[1]}' not found.");
            return 2;
        }

        ExecutionPayload payload = args[0].ToLowerInvariant() switch
        {
            "run" => new PowerShellPayload(string.Join(' ', args.Skip(2))),
            "cmd" => new CmdPayload(string.Join(' ', args.Skip(2))),
            "psadt" => new PsadtPayload
            {
                PackageUrl = new Uri(args[2]),
                DeploymentType = args.Length > 3
                    ? Enum.Parse<PsadtDeploymentType>(args[3], ignoreCase: true)
                    : PsadtDeploymentType.Install
            },
            _ => new PowerOperationPayload(PowerOperation.Restart)
        };

        var request = new ExecutionRequest
        {
            DisplayName = $"CLI {args[0]} on {target.Name}",
            Payload = payload,
            Targets = new[] { target },
            RequestedBy = session.UserPrincipalName
        };

        Console.WriteLine($"\nExecuting on {target.Name} ({target.PowerState})... Ctrl+C to cancel.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var orchestrator = services.GetRequiredService<ExecutionOrchestrator>();
        var report = await orchestrator.ExecuteAsync(request, cancellationToken: cts.Token);
        var result = report.Results[0];

        Console.WriteLine($"\nStatus:    {result.Status}");
        Console.WriteLine($"Exit code: {result.ExitCode?.ToString() ?? "n/a"}" +
            (result.ExitCode is { } c ? $" ({ExitCodeClassifier.Describe(c)})" : string.Empty));
        Console.WriteLine($"Duration:  {result.Duration?.TotalSeconds:F1}s");
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            Console.WriteLine($"\n--- Output ---\n{result.Output}");
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Console.WriteLine($"\n--- Error ---\n{result.Error}");
        }

        return result.IsSuccess ? 0 : 3;
    }

    default:
        Console.Error.WriteLine("Unknown command or missing arguments.");
        return 1;
}
