namespace AzureVmScriptRunner.Domain.Execution;

/// <summary>
/// Maps process exit codes to execution statuses with PSADT/MSI awareness, so job
/// history shows "succeeded, reboot required" rather than a bare 3010.
/// </summary>
public static class ExitCodeClassifier
{
    public static VmExecutionStatus Classify(int exitCode) => exitCode switch
    {
        0 => VmExecutionStatus.Succeeded,
        1707 => VmExecutionStatus.Succeeded,                 // MSI: install completed
        3010 => VmExecutionStatus.SucceededRebootRequired,   // MSI: soft reboot required
        1641 => VmExecutionStatus.SucceededRebootRequired,   // MSI: reboot initiated
        _ => VmExecutionStatus.Failed
    };

    public static string Describe(int exitCode) => exitCode switch
    {
        0 => "Success",
        1602 => "User cancelled the installation",
        1618 => "Another installation is already in progress",
        1641 => "Success – reboot initiated by the installation",
        1707 => "Success",
        3010 => "Success – reboot required",
        60001 => "PSADT: general deployment error",
        60002 => "PSADT: Execute-Process failed",
        60008 => "PSADT: failed to load the toolkit",
        60012 => "PSADT: installation deferred by the user",
        60013 => "PSADT: deployment blocked (process running / disk space / prerequisites)",
        >= 60000 and <= 69999 => $"PSADT error {exitCode}",
        _ => $"Exit code {exitCode}"
    };
}
