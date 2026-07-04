using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Application.Preflight;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;
using AzureVmScriptRunner.Domain.Tasks;

namespace AzureVmScriptRunner.Tests;

public class ExecutionOrchestratorTests
{
    private static ExecutionOrchestrator Orchestrator(
        FakeProvider provider,
        FakeHistory? history = null,
        FakeSavedTasks? savedTasks = null,
        IEnumerable<IPreflightCheck>? checks = null) =>
        new(
            new ProviderSelector(new[] { provider }),
            savedTasks ?? new FakeSavedTasks(),
            history ?? new FakeHistory(),
            checks ?? new IPreflightCheck[] { new PowerStatePreflightCheck(), new AgentHealthPreflightCheck() });

    [Fact]
    public async Task Executes_against_all_targets_and_records_history()
    {
        var provider = new FakeProvider();
        var history = new FakeHistory();
        var targets = Enumerable.Range(1, 5).Select(i => TestData.Vm($"vm-{i:00}")).ToArray();

        var report = await Orchestrator(provider, history)
            .ExecuteAsync(TestData.Request(targets: targets));

        Assert.Equal(5, report.Results.Count);
        Assert.True(report.AllSucceeded);
        Assert.Equal(5, history.Records.Count);
        Assert.All(history.Records, r => Assert.Equal("admin@contoso.com", r.RequestedBy));
        Assert.All(history.Records, r => Assert.Equal("Get-Service", r.ScriptContent));
    }

    [Fact]
    public async Task Respects_max_parallelism()
    {
        var provider = new FakeProvider { ExecutionDelay = TimeSpan.FromMilliseconds(100) };
        var targets = Enumerable.Range(1, 12).Select(i => TestData.Vm($"vm-{i:00}")).ToArray();
        var options = ExecutionOptions.Default with { MaxParallelism = 3 };

        await Orchestrator(provider)
            .ExecuteAsync(TestData.Request(targets: targets, options: options));

        Assert.Equal(12, provider.ExecutedTargets.Count);
        Assert.InRange(provider.MaxObservedConcurrency, 1, 3);
    }

    [Fact]
    public async Task Preflight_failure_skips_execution_for_that_vm_only()
    {
        var provider = new FakeProvider();
        var targets = new[]
        {
            TestData.Vm("vm-ok"),
            TestData.Vm("vm-off", power: VmPowerState.Deallocated),
            TestData.Vm("vm-sick", agent: VmAgentStatus.NotReady)
        };

        var report = await Orchestrator(provider).ExecuteAsync(TestData.Request(targets: targets));

        Assert.Single(provider.ExecutedTargets);
        Assert.Equal("vm-ok", provider.ExecutedTargets.Single().Name);

        var off = report.Results.Single(r => r.Target.Name == "vm-off");
        Assert.Equal(VmExecutionStatus.PreflightFailed, off.Status);
        Assert.Contains("Deallocated", off.Error);

        var sick = report.Results.Single(r => r.Target.Name == "vm-sick");
        Assert.Equal(VmExecutionStatus.PreflightFailed, sick.Status);
        Assert.Contains("guest agent", sick.Error);
    }

    [Fact]
    public async Task SkipPreflight_executes_even_on_deallocated_vm()
    {
        var provider = new FakeProvider();
        var targets = new[] { TestData.Vm("vm-off", power: VmPowerState.Deallocated) };
        var options = ExecutionOptions.Default with { SkipPreflight = true };

        var report = await Orchestrator(provider)
            .ExecuteAsync(TestData.Request(targets: targets, options: options));

        Assert.True(report.AllSucceeded);
    }

    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        var attempts = 0;
        var provider = new FakeProvider
        {
            OnExecute = (req, target, ct) =>
            {
                if (Interlocked.Increment(ref attempts) < 3)
                {
                    throw new TransientExecutionException("429 throttled");
                }

                return Task.FromResult(new VmExecutionResult
                {
                    Target = target,
                    Status = VmExecutionStatus.Succeeded,
                    ExitCode = 0,
                    StartedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow
                });
            }
        };
        var options = ExecutionOptions.Default with
        {
            MaxRetries = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1)
        };

        var report = await Orchestrator(provider).ExecuteAsync(TestData.Request(options: options));

        Assert.Equal(3, attempts);
        Assert.True(report.AllSucceeded);
    }

    [Fact]
    public async Task Transient_failures_beyond_retry_budget_fail_the_vm()
    {
        var provider = new FakeProvider
        {
            OnExecute = (_, _, _) => throw new TransientExecutionException("still throttled")
        };
        var options = ExecutionOptions.Default with
        {
            MaxRetries = 1,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1)
        };

        var report = await Orchestrator(provider).ExecuteAsync(TestData.Request(options: options));

        Assert.Equal(VmExecutionStatus.Failed, report.Results.Single().Status);
        Assert.Contains("still throttled", report.Results.Single().Error);
    }

    [Fact]
    public async Task NonTransient_failure_does_not_retry()
    {
        var attempts = 0;
        var provider = new FakeProvider
        {
            OnExecute = (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("VM not found");
            }
        };

        var report = await Orchestrator(provider).ExecuteAsync(TestData.Request());

        Assert.Equal(1, attempts);
        Assert.Equal(VmExecutionStatus.Failed, report.Results.Single().Status);
    }

    [Fact]
    public async Task Timeout_reports_TimedOut_not_Cancelled()
    {
        var provider = new FakeProvider { ExecutionDelay = TimeSpan.FromSeconds(30) };
        var options = ExecutionOptions.Default with { Timeout = TimeSpan.FromMilliseconds(50) };

        var report = await Orchestrator(provider).ExecuteAsync(TestData.Request(options: options));

        Assert.Equal(VmExecutionStatus.TimedOut, report.Results.Single().Status);
    }

    [Fact]
    public async Task User_cancellation_reports_Cancelled_for_running_and_pending_vms()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeProvider { ExecutionDelay = TimeSpan.FromSeconds(30) };
        var targets = Enumerable.Range(1, 4).Select(i => TestData.Vm($"vm-{i:00}")).ToArray();
        var options = ExecutionOptions.Default with { MaxParallelism = 1 };

        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var report = await Orchestrator(provider)
            .ExecuteAsync(TestData.Request(targets: targets, options: options), cancellationToken: cts.Token);

        Assert.Equal(4, report.Results.Count);
        Assert.All(report.Results, r => Assert.Equal(VmExecutionStatus.Cancelled, r.Status));
    }

    [Fact]
    public async Task Saved_task_payload_is_resolved_before_dispatch()
    {
        var provider = new FakeProvider();
        var savedTasks = new FakeSavedTasks();
        var task = new SavedTask { Name = "Flush DNS", Payload = new CmdPayload("ipconfig /flushdns") };
        savedTasks.Add(task);

        var report = await Orchestrator(provider, savedTasks: savedTasks)
            .ExecuteAsync(TestData.Request(payload: new SavedTaskPayload(task.Id)));

        Assert.True(report.AllSucceeded);
        var seen = Assert.IsType<CmdPayload>(provider.SeenPayloads.Single());
        Assert.Equal("ipconfig /flushdns", seen.CommandLine);
    }

    [Fact]
    public async Task Missing_saved_task_throws()
    {
        var provider = new FakeProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Orchestrator(provider).ExecuteAsync(TestData.Request(payload: new SavedTaskPayload(Guid.NewGuid()))));
    }

    [Fact]
    public async Task History_store_failure_does_not_fail_the_execution()
    {
        var provider = new FakeProvider();
        var history = new FakeHistory { ThrowOnRecord = true };

        var report = await Orchestrator(provider, history).ExecuteAsync(TestData.Request());

        Assert.True(report.AllSucceeded);
    }

    [Fact]
    public async Task Progress_reports_completion_for_every_vm()
    {
        var provider = new FakeProvider();
        var targets = Enumerable.Range(1, 3).Select(i => TestData.Vm($"vm-{i:00}")).ToArray();
        var events = new List<ExecutionProgress>();
        var completed = 0;
        var allDone = new TaskCompletionSource();

        // Progress<T> marshals to a sync context asynchronously; a synchronous
        // reporter keeps the assertions deterministic.
        var inline = new InlineProgress<ExecutionProgress>(e =>
        {
            lock (events)
            {
                events.Add(e);
            }

            if (e.Kind == ExecutionProgressKind.VmCompleted && Interlocked.Increment(ref completed) == 3)
            {
                allDone.TrySetResult();
            }
        });

        await Orchestrator(provider).ExecuteAsync(TestData.Request(targets: targets), inline);
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, events.Count(e => e.Kind == ExecutionProgressKind.VmStarted));
        Assert.Equal(3, events.Count(e => e.Kind == ExecutionProgressKind.VmCompleted));
        Assert.Contains(events, e => e is { Kind: ExecutionProgressKind.VmCompleted, CompletedCount: 3, TotalCount: 3 });
    }

    [Fact]
    public async Task Empty_target_list_throws()
    {
        var provider = new FakeProvider();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Orchestrator(provider).ExecuteAsync(TestData.Request(targets: Array.Empty<VmTarget>())));
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public InlineProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
