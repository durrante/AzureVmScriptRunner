using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Infrastructure.Psadt;

namespace AzureVmScriptRunner.Tests;

public class PsadtScriptBuilderTests
{
    private static readonly PsadtPayload Payload = new()
    {
        PackageUrl = new Uri("https://store.blob.core.windows.net/packages/7zip.zip"),
        DeploymentType = PsadtDeploymentType.Install,
        DeployMode = PsadtDeployMode.Silent
    };

    [Fact]
    public void Tries_managed_identity_before_sas()
    {
        var script = PsadtScriptBuilder.Build(Payload, fallbackSasUri: null);

        Assert.Contains("169.254.169.254/metadata/identity", script);
        Assert.Contains("https://store.blob.core.windows.net/packages/7zip.zip", script);
        Assert.Contains("$sasUrl = $null", script);
        Assert.Contains("grant the Automation account identity", script);
    }

    [Fact]
    public void Embeds_fallback_sas_when_provided()
    {
        var sas = new Uri("https://store.blob.core.windows.net/packages/7zip.zip?sv=2022&sig=abc");
        var script = PsadtScriptBuilder.Build(Payload, sas);

        Assert.Contains($"$sasUrl = '{sas}'", script);
    }

    [Fact]
    public void Detects_v4_entry_point_before_v3()
    {
        var script = PsadtScriptBuilder.Build(Payload, null);

        var v4Index = script.IndexOf("Invoke-AppDeployToolkit.exe", StringComparison.Ordinal);
        var v3Index = script.IndexOf("Deploy-Application.exe", StringComparison.Ordinal);
        Assert.True(v4Index >= 0 && v3Index > v4Index, "v4 entry point must be probed first");
    }

    [Fact]
    public void Passes_deployment_type_mode_and_extra_arguments()
    {
        var payload = Payload with
        {
            DeploymentType = PsadtDeploymentType.Uninstall,
            DeployMode = PsadtDeployMode.NonInteractive,
            AdditionalArguments = "-AllowRebootPassThru"
        };

        var script = PsadtScriptBuilder.Build(payload, null);

        Assert.Contains("-DeploymentType Uninstall -DeployMode NonInteractive -AllowRebootPassThru", script);
    }

    [Theory]
    [InlineData(true, "$cleanup = $true")]
    [InlineData(false, "$cleanup = $false")]
    public void Honours_cleanup_flag(bool cleanup, string expected)
    {
        var script = PsadtScriptBuilder.Build(Payload with { CleanupTemporaryFiles = cleanup }, null);
        Assert.Contains(expected, script);
    }

    [Fact]
    public void Failure_can_never_exit_zero()
    {
        var script = PsadtScriptBuilder.Build(Payload, null);

        Assert.Contains("$exitCode = 1", script);   // failure default before try
        Assert.Contains("exit $exitCode", script);
    }
}
