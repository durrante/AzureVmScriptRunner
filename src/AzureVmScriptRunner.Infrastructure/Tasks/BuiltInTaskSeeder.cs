using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Tasks;

namespace AzureVmScriptRunner.Infrastructure.Tasks;

/// <summary>
/// Seeds the starter task library on first run. Tasks are ordinary editable saved
/// tasks — IsBuiltIn only marks their origin; administrators can modify or delete them.
/// </summary>
public static class BuiltInTaskSeeder
{
    public static async Task EnsureSeededAsync(
        ISavedTaskRepository repository, CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetAllAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return;
        }

        foreach (var task in CreateDefaults())
        {
            await repository.SaveAsync(task, cancellationToken);
        }
    }

    private static IEnumerable<SavedTask> CreateDefaults()
    {
        yield return Cmd("Group Policy Update", "Maintenance",
            "Forces a group policy refresh.",
            "echo n | gpupdate /force");

        yield return Cmd("Flush DNS Cache", "Network",
            "Clears the DNS resolver cache.",
            "ipconfig /flushdns");

        yield return Ps("Restart Print Spooler", "Services",
            "Restarts the Print Spooler service and reports its state.",
            """
            Restart-Service -Name Spooler -Force
            Get-Service -Name Spooler | Select-Object Name, Status | Format-List
            """);

        yield return Ps("Restart Intune Management Extension", "Services",
            "Restarts the Intune Management Extension agent (retriggers Win32 app processing).",
            """
            Restart-Service -Name IntuneManagementExtension -Force
            Get-Service -Name IntuneManagementExtension | Select-Object Name, Status | Format-List
            """);

        yield return Ps("Restart Azure VM Guest Agent", "Services",
            "Restarts the Azure guest agent services (fixes 'agent not ready').",
            """
            'RdAgent', 'WindowsAzureGuestAgent' | ForEach-Object {
                if (Get-Service -Name $_ -ErrorAction SilentlyContinue) {
                    Restart-Service -Name $_ -Force
                    Get-Service -Name $_ | Select-Object Name, Status | Format-List
                }
            }
            """);

        yield return Cmd("Windows Update Scan", "Updates",
            "Triggers a Windows Update detection scan.",
            "UsoClient StartScan & echo Scan triggered — check Settings or WindowsUpdate.log on the VM.");

        yield return Ps("Pending Reboot Check", "Diagnostics",
            "Reports whether the VM has a pending reboot.",
            """
            $pending = @(
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
            ) | Where-Object { Test-Path $_ }
            if ($pending) { Write-Output 'REBOOT PENDING'; $pending } else { Write-Output 'No reboot pending.' }
            """);

        yield return Ps("Disk Space Report", "Diagnostics",
            "Free space on all fixed drives.",
            """
            Get-Volume | Where-Object DriveType -eq 'Fixed' |
                Select-Object DriveLetter, FileSystemLabel,
                    @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}},
                    @{n='FreeGB';e={[math]::Round($_.SizeRemaining/1GB,1)}} |
                Format-Table -AutoSize
            """);
    }

    private static SavedTask Ps(string name, string category, string description, string script) =>
        new()
        {
            Name = name,
            Category = category,
            Description = description,
            Payload = new PowerShellPayload(script),
            IsBuiltIn = true
        };

    private static SavedTask Cmd(string name, string category, string description, string commandLine) =>
        new()
        {
            Name = name,
            Category = category,
            Description = description,
            Payload = new CmdPayload(commandLine),
            IsBuiltIn = true
        };
}
