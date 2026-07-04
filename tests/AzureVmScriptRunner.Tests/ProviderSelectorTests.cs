using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Scheduling;

namespace AzureVmScriptRunner.Tests;

public class ProviderSelectorTests
{
    private static readonly FakeProvider RunCommand = new()
    {
        Kind = ExecutionProviderKind.RunCommand,
        CanExecutePredicate = r => !r.IsScheduled && r.Payload.Type != ExecutionType.PowerOperation
    };

    private static readonly FakeProvider Automation = new()
    {
        Kind = ExecutionProviderKind.AutomationRunbook,
        CanExecutePredicate = r => r.IsScheduled
    };

    private static readonly FakeProvider ControlPlane = new()
    {
        Kind = ExecutionProviderKind.ControlPlane,
        CanExecutePredicate = r => r.Payload.Type == ExecutionType.PowerOperation
    };

    private static ProviderSelector Selector() =>
        new(new[] { RunCommand, Automation, ControlPlane });

    [Fact]
    public void RunNow_selects_RunCommand()
    {
        var provider = Selector().Select(TestData.Request());
        Assert.Equal(ExecutionProviderKind.RunCommand, provider.Kind);
    }

    [Fact]
    public void Scheduled_selects_Automation()
    {
        var request = TestData.Request() with
        {
            Schedule = new ScheduleDefinition { StartTime = DateTimeOffset.UtcNow.AddHours(1) }
        };

        var provider = Selector().Select(request);
        Assert.Equal(ExecutionProviderKind.AutomationRunbook, provider.Kind);
    }

    [Fact]
    public void Power_operation_selects_ControlPlane()
    {
        var request = TestData.Request(payload: new PowerOperationPayload(PowerOperation.Restart));

        var provider = Selector().Select(request);
        Assert.Equal(ExecutionProviderKind.ControlPlane, provider.Kind);
    }

    [Fact]
    public void No_capable_provider_throws_with_context()
    {
        var selector = new ProviderSelector(new[] { Automation });

        var ex = Assert.Throws<InvalidOperationException>(() => selector.Select(TestData.Request()));
        Assert.Contains("PowerShell", ex.Message);
    }
}
