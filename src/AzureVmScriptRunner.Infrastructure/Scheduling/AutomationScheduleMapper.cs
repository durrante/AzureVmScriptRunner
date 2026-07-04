using AzureVmScriptRunner.Domain.Scheduling;

namespace AzureVmScriptRunner.Infrastructure.Scheduling;

/// <summary>Pure mapping from the domain schedule to the Automation schedule API shape.</summary>
public static class AutomationScheduleMapper
{
    public static string ToFrequency(ScheduleFrequency frequency) => frequency switch
    {
        ScheduleFrequency.Once => "OneTime",
        ScheduleFrequency.Hourly => "Hour",
        ScheduleFrequency.Daily => "Day",
        ScheduleFrequency.Weekly => "Week",
        ScheduleFrequency.Monthly => "Month",
        _ => throw new ArgumentOutOfRangeException(nameof(frequency))
    };

    /// <summary>Automation accepts IANA time zone IDs; Windows IDs are converted.</summary>
    public static string ToTimeZone(string timeZoneId)
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var iana))
        {
            return iana;
        }

        return string.IsNullOrWhiteSpace(timeZoneId) ? "Etc/UTC" : timeZoneId;
    }

    public static object ToScheduleProperties(ScheduleDefinition schedule)
    {
        var frequency = ToFrequency(schedule.Frequency);
        return new
        {
            startTime = EnsureFutureStart(schedule.StartTime),
            frequency,
            interval = frequency == "OneTime" ? (int?)null : Math.Max(1, schedule.Interval),
            timeZone = ToTimeZone(schedule.TimeZoneId),
            expiryTime = schedule.ExpiryTime,
            advancedSchedule = ToAdvancedSchedule(schedule)
        };
    }

    /// <summary>Weekly/monthly recurrence details map to Automation's advancedSchedule.</summary>
    public static object? ToAdvancedSchedule(ScheduleDefinition schedule)
    {
        switch (schedule.Frequency)
        {
            case ScheduleFrequency.Weekly when schedule.DaysOfWeek.Count > 0:
                return new
                {
                    weekDays = schedule.DaysOfWeek.Distinct().Select(d => d.ToString()).ToArray()
                };

            case ScheduleFrequency.Monthly when schedule.MonthDays.Count > 0:
                // -1 = last day of month, per the Automation API contract.
                return new
                {
                    monthDays = schedule.MonthDays
                        .Where(d => d == -1 || d is >= 1 and <= 31)
                        .Distinct()
                        .ToArray()
                };

            case ScheduleFrequency.Monthly when schedule.MonthlyWeekDay is { } occurrence:
                return new
                {
                    monthlyOccurrences = new[]
                    {
                        new { occurrence = occurrence.Occurrence, day = occurrence.Day.ToString() }
                    }
                };

            default:
                return null;
        }
    }

    /// <summary>Automation rejects start times less than ~5 minutes ahead.</summary>
    public static DateTimeOffset EnsureFutureStart(DateTimeOffset requested)
    {
        var minimum = DateTimeOffset.UtcNow.AddMinutes(6);
        return requested < minimum ? minimum : requested;
    }
}
