using System.Text;
using System.Text.RegularExpressions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Infrastructure.Providers;

namespace AzureVmScriptRunner.Tests;

public class RunCommandScriptBuilderTests
{
    [Fact]
    public void PowerShell_payload_passes_through_unchanged()
    {
        var script = RunCommandExecutionProvider.BuildScript(
            new PowerShellPayload("Get-Service | Where-Object Status -eq 'Running'"));

        Assert.Equal("Get-Service | Where-Object Status -eq 'Running'", script);
    }

    [Theory]
    [InlineData("ipconfig /flushdns")]
    [InlineData("echo \"quoted 'nested' value\" & dir C:\\Program Files\\")]
    [InlineData("reg query \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\" /v ProductName")]
    public void Cmd_payload_survives_base64_round_trip(string commandLine)
    {
        var script = RunCommandExecutionProvider.BuildScript(new CmdPayload(commandLine));

        var base64 = Regex.Match(script, "FromBase64String\\('([^']+)'\\)").Groups[1].Value;
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(base64));

        Assert.Equal(commandLine, decoded);
        Assert.Contains("cmd.exe /d /c", script);
        Assert.Contains("exit $LASTEXITCODE", script);
    }

    [Fact]
    public void Psadt_payload_is_rejected_until_phase3()
    {
        Assert.Throws<NotSupportedException>(() =>
            RunCommandExecutionProvider.BuildScript(new PsadtPayload
            {
                PackageUrl = new Uri("https://storage.blob.core.windows.net/packages/app.zip")
            }));
    }
}
