using AzureVmScriptRunner.Domain.Execution;

namespace AzureVmScriptRunner.Tests;

public class ExitCodeClassifierTests
{
    [Theory]
    [InlineData(0, VmExecutionStatus.Succeeded)]
    [InlineData(1707, VmExecutionStatus.Succeeded)]
    [InlineData(3010, VmExecutionStatus.SucceededRebootRequired)]
    [InlineData(1641, VmExecutionStatus.SucceededRebootRequired)]
    [InlineData(1, VmExecutionStatus.Failed)]
    [InlineData(1602, VmExecutionStatus.Failed)]
    [InlineData(60001, VmExecutionStatus.Failed)]
    public void Classifies_common_exit_codes(int exitCode, VmExecutionStatus expected) =>
        Assert.Equal(expected, ExitCodeClassifier.Classify(exitCode));

    [Theory]
    [InlineData(3010, "reboot required")]
    [InlineData(1602, "cancelled")]
    [InlineData(1618, "already in progress")]
    [InlineData(60012, "deferred")]
    [InlineData(64000, "PSADT error 64000")]
    public void Describes_exit_codes(int exitCode, string expectedFragment) =>
        Assert.Contains(expectedFragment, ExitCodeClassifier.Describe(exitCode), StringComparison.OrdinalIgnoreCase);
}
