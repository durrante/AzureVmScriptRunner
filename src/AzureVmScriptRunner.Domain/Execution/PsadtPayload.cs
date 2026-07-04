namespace AzureVmScriptRunner.Domain.Execution;

public enum PsadtDeploymentType
{
    Install,
    Uninstall,
    Repair
}

public enum PsadtDeployMode
{
    /// <summary>
    /// Default. Run Command executes as SYSTEM in session 0, so silent is the only
    /// mode guaranteed to behave; Interactive requires the package itself to handle
    /// relaunching into the user session.
    /// </summary>
    Silent,
    Interactive,
    NonInteractive,

    /// <summary>
    /// PSADT v4 only: the toolkit chooses Interactive when a user is logged on,
    /// Silent otherwise. Rejected by v3 packages.
    /// </summary>
    Auto
}

public sealed record PsadtPayload : ExecutionPayload
{
    public override ExecutionType Type => ExecutionType.PsadtDeployment;

    /// <summary>Blob URL of the zipped PSADT package (without any SAS token).</summary>
    public required Uri PackageUrl { get; init; }

    public PsadtDeploymentType DeploymentType { get; init; } = PsadtDeploymentType.Install;

    public PsadtDeployMode DeployMode { get; init; } = PsadtDeployMode.Silent;

    /// <summary>Extra arguments appended to the PSADT entry point invocation.</summary>
    public string? AdditionalArguments { get; init; }

    /// <summary>Delete the extracted package and downloaded zip after execution.</summary>
    public bool CleanupTemporaryFiles { get; init; } = true;
}
