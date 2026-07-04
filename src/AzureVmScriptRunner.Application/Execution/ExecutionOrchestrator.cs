using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.History;
using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Application.Execution;

/// <summary>
/// The single execution pipeline shared by every execution type:
/// saved-task resolution → provider selection → per-VM preflight → throttled parallel
/// dispatch with retry/timeout/cancellation → history recording → progress reporting.
/// </summary>
public sealed class ExecutionOrchestrator
{
    private readonly IProviderSelector _providerSelector;
    private readonly ISavedTaskRepository _savedTasks;
    private readonly IJobHistoryService _history;
    private readonly IReadOnlyList<IPreflightCheck> _preflightChecks;

    public ExecutionOrchestrator(
        IProviderSelector providerSelector,
        ISavedTaskRepository savedTasks,
        IJobHistoryService history,
        IEnumerable<IPreflightCheck> preflightChecks)
    {
        _providerSelector = providerSelector;
        _savedTasks = savedTasks;
        _history = history;
        _preflightChecks = preflightChecks.ToList();
    }

    public async Task<ExecutionReport> ExecuteAsync(
        ExecutionRequest request,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Targets.Count == 0)
        {
            throw new ArgumentException("Execution request has no target VMs.", nameof(request));
        }

        var effectiveRequest = await ResolveSavedTaskAsync(request, cancellationToken);
        var provider = _providerSelector.Select(effectiveRequest);

        var results = new VmExecutionResult[effectiveRequest.Targets.Count];
        var completed = 0;
        using var throttle = new SemaphoreSlim(Math.Max(1, effectiveRequest.Options.MaxParallelism));

        var vmTasks = effectiveRequest.Targets.Select(async (target, index) =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                progress?.Report(new ExecutionProgress
                {
                    Kind = ExecutionProgressKind.VmStarted,
                    Target = target,
                    CompletedCount = Volatile.Read(ref completed),
                    TotalCount = effectiveRequest.Targets.Count
                });

                var result = await ExecuteSingleAsync(
                    effectiveRequest, provider, target, progress, cancellationToken);
                results[index] = result;

                await RecordAsync(effectiveRequest, provider.Kind, result, cancellationToken);

                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ExecutionProgress
                {
                    Kind = ExecutionProgressKind.VmCompleted,
                    Target = target,
                    CompletedCount = done,
                    TotalCount = effectiveRequest.Targets.Count,
                    Result = result
                });
            }
            finally
            {
                throttle.Release();
            }
        });

        try
        {
            await Task.WhenAll(vmTasks);
        }
        catch (OperationCanceledException)
        {
            // Fill in VMs that never ran so the report accounts for every target.
            for (var i = 0; i < results.Length; i++)
            {
                results[i] ??= new VmExecutionResult
                {
                    Target = effectiveRequest.Targets[i],
                    Status = VmExecutionStatus.Cancelled,
                    StartedAt = DateTimeOffset.UtcNow
                };
            }
        }

        return new ExecutionReport { RequestId = request.Id, Results = results };
    }

    private async Task<VmExecutionResult> ExecuteSingleAsync(
        ExecutionRequest request,
        IExecutionProvider provider,
        VmTarget target,
        IProgress<ExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        if (!request.Options.SkipPreflight)
        {
            foreach (var check in _preflightChecks)
            {
                var verdict = await check.CheckAsync(request, target, cancellationToken);
                if (!verdict.Passed)
                {
                    return new VmExecutionResult
                    {
                        Target = target,
                        Status = VmExecutionStatus.PreflightFailed,
                        Provider = provider.Kind,
                        Error = $"{check.Name}: {verdict.Reason}",
                        StartedAt = startedAt,
                        CompletedAt = DateTimeOffset.UtcNow
                    };
                }
            }
        }

        for (var attempt = 0; ; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.Options.Timeout);

            try
            {
                return await provider.ExecuteAsync(request, target, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Complete(VmExecutionStatus.Cancelled, "Execution cancelled by user.");
            }
            catch (OperationCanceledException)
            {
                return Complete(VmExecutionStatus.TimedOut,
                    $"Execution exceeded the {request.Options.Timeout} timeout.");
            }
            catch (TransientExecutionException ex) when (attempt < request.Options.MaxRetries)
            {
                progress?.Report(new ExecutionProgress
                {
                    Kind = ExecutionProgressKind.VmRetrying,
                    Target = target,
                    CompletedCount = 0,
                    TotalCount = request.Targets.Count,
                    Message = $"Attempt {attempt + 1} failed ({ex.Message}); retrying."
                });
                await Task.Delay(Backoff(request.Options.RetryBaseDelay, attempt), cancellationToken);
            }
            catch (Exception ex)
            {
                return Complete(VmExecutionStatus.Failed, ex.Message);
            }
        }

        VmExecutionResult Complete(VmExecutionStatus status, string error) => new()
        {
            Target = target,
            Status = status,
            Provider = provider.Kind,
            Error = error,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static TimeSpan Backoff(TimeSpan baseDelay, int attempt) =>
        baseDelay * Math.Pow(2, attempt);

    private async Task<ExecutionRequest> ResolveSavedTaskAsync(
        ExecutionRequest request, CancellationToken cancellationToken)
    {
        if (request.Payload is not SavedTaskPayload reference)
        {
            return request;
        }

        var task = await _savedTasks.GetAsync(reference.SavedTaskId, cancellationToken)
            ?? throw new InvalidOperationException($"Saved task {reference.SavedTaskId} was not found.");

        if (task.Payload is SavedTaskPayload)
        {
            throw new InvalidOperationException($"Saved task '{task.Name}' must not reference another saved task.");
        }

        return request with { Payload = task.Payload };
    }

    private async Task RecordAsync(
        ExecutionRequest request,
        ExecutionProviderKind providerKind,
        VmExecutionResult result,
        CancellationToken cancellationToken)
    {
        var record = new JobRecord
        {
            RequestId = request.Id,
            RequestedBy = request.RequestedBy,
            DisplayName = request.DisplayName,
            ExecutionType = request.Payload.Type,
            Provider = providerKind,
            VmName = result.Target.Name,
            SubscriptionId = result.Target.SubscriptionId,
            ResourceGroup = result.Target.ResourceGroup,
            VmResourceId = result.Target.ResourceId,
            ScriptContent = DescribePayload(request.Payload),
            Status = result.Status,
            ExitCode = result.ExitCode,
            ExitCodeDescription = result.ExitCode is { } code ? ExitCodeClassifier.Describe(code) : null,
            Output = result.Output,
            Error = result.Error,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt
        };

        // History must never fail an execution that already succeeded on the VM.
        try
        {
            await _history.RecordAsync(record, cancellationToken);
        }
        catch
        {
        }
    }

    private static string DescribePayload(ExecutionPayload payload) => payload switch
    {
        PowerShellPayload p => p.Script,
        CmdPayload c => c.CommandLine,
        PsadtPayload d =>
            $"PSADT {d.DeploymentType} ({d.DeployMode}) from {d.PackageUrl} {d.AdditionalArguments}".TrimEnd(),
        PowerOperationPayload o => $"Power operation: {o.Operation}",
        var other => other.Type.ToString()
    };
}
