namespace AzureVmScriptRunner.Domain.Scheduling;

public enum ScheduleFrequency
{
    Once,
    Hourly,
    Daily,
    Weekly,
    Monthly
}

/// <summary>
/// Maps 1:1 onto an Azure Automation schedule linked to the generic execution runbook.
/// </summary>
public sealed record ScheduleDefinition
{
    public required DateTimeOffset StartTime { get; init; }
    public ScheduleFrequency Frequency { get; init; } = ScheduleFrequency.Once;

    /// <summary>Interval in units of <see cref="Frequency"/> (e.g. every 2 days). Ignored for Once.</summary>
    public int Interval { get; init; } = 1;

    /// <summary>IANA/Windows time zone ID for recurrence evaluation.</summary>
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;

    public DateTimeOffset? ExpiryTime { get; init; }

    /// <summary>For Weekly frequency: which days the schedule fires (empty = day of StartTime).</summary>
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = Array.Empty<DayOfWeek>();

    /// <summary>For Monthly frequency: days of the month (1–31; -1 = last day of month).</summary>
    public IReadOnlyList<int> MonthDays { get; init; } = Array.Empty<int>();

    /// <summary>For Monthly frequency: alternative to MonthDays — e.g. "second Tuesday".</summary>
    public MonthlyOccurrence? MonthlyWeekDay { get; init; }
}

/// <summary>Occurrence 1–4 (first–fourth) or -1 (last), on the given weekday.</summary>
public sealed record MonthlyOccurrence(int Occurrence, DayOfWeek Day);
