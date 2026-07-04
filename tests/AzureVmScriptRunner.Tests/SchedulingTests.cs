using System.Text;
using System.Text.Json;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Scheduling;
using AzureVmScriptRunner.Infrastructure.Scheduling;

namespace AzureVmScriptRunner.Tests;

public class SchedulingTests
{
    [Theory]
    [InlineData(ScheduleFrequency.Once, "OneTime")]
    [InlineData(ScheduleFrequency.Hourly, "Hour")]
    [InlineData(ScheduleFrequency.Daily, "Day")]
    [InlineData(ScheduleFrequency.Weekly, "Week")]
    [InlineData(ScheduleFrequency.Monthly, "Month")]
    public void Maps_frequencies_to_automation_values(ScheduleFrequency frequency, string expected) =>
        Assert.Equal(expected, AutomationScheduleMapper.ToFrequency(frequency));

    [Fact]
    public void Start_times_in_the_past_are_pushed_forward()
    {
        var adjusted = AutomationScheduleMapper.EnsureFutureStart(DateTimeOffset.UtcNow.AddMinutes(-30));
        Assert.True(adjusted > DateTimeOffset.UtcNow.AddMinutes(5));

        var future = DateTimeOffset.UtcNow.AddDays(2);
        Assert.Equal(future, AutomationScheduleMapper.EnsureFutureStart(future));
    }

    [Fact]
    public void Windows_time_zone_ids_convert_to_iana()
    {
        Assert.Equal("Europe/London", AutomationScheduleMapper.ToTimeZone("GMT Standard Time"));
        Assert.Equal("Europe/London", AutomationScheduleMapper.ToTimeZone("Europe/London"));
    }

    [Fact]
    public void Runbook_parameter_round_trips_and_contains_rendered_script()
    {
        var request = TestData.Request(
            payload: new CmdPayload("ipconfig /flushdns"),
            targets: new[] { TestData.Vm("vm-01"), TestData.Vm("vm-02") });

        var parameter = ScheduledRequestSerializer.ToRunbookParameter(request);
        using var json = JsonDocument.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(parameter)));

        var root = json.RootElement;
        Assert.Equal("Test execution", root.GetProperty("displayName").GetString());
        Assert.Equal("admin@contoso.com", root.GetProperty("requestedBy").GetString());
        Assert.Equal(2, root.GetProperty("targets").GetArrayLength());
        Assert.Contains("cmd.exe /d /c", root.GetProperty("script").GetString());
    }

    [Fact]
    public void Scheduled_psadt_uses_scheduler_token_not_sas()
    {
        var request = TestData.Request(payload: new PsadtPayload
        {
            PackageUrl = new Uri("https://store.blob.core.windows.net/packages/app.zip")
        });

        var parameter = ScheduledRequestSerializer.ToRunbookParameter(request);
        using var json = JsonDocument.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(parameter)));
        var script = json.RootElement.GetProperty("script").GetString()!;

        Assert.True(json.RootElement.GetProperty("needsStorageToken").GetBoolean());
        Assert.Contains("169.254.169.254/metadata/identity", script);           // VM MI tried first
        Assert.Contains(Infrastructure.Psadt.PsadtScriptBuilder.StorageTokenPlaceholder, script);
        Assert.Contains("$sasUrl = $null", script);   // no SAS baked into a future-dated script
        Assert.Contains("Replace('__AVSR_STORAGE_TOKEN__'", RunbookContent.Script);
    }

    [Fact]
    public void Immediate_runs_do_not_carry_the_token_placeholder()
    {
        var request = TestData.Request(payload: new CmdPayload("hostname"));
        var parameter = ScheduledRequestSerializer.ToRunbookParameter(request);
        using var json = JsonDocument.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(parameter)));

        Assert.False(json.RootElement.GetProperty("needsStorageToken").GetBoolean());

        var immediateScript = Infrastructure.Psadt.PsadtScriptBuilder.Build(
            new PsadtPayload { PackageUrl = new Uri("https://x.blob.core.windows.net/c/p.zip") },
            fallbackSasUri: new Uri("https://x.blob.core.windows.net/c/p.zip?sig=abc"));
        Assert.DoesNotContain(
            Infrastructure.Psadt.PsadtScriptBuilder.StorageTokenPlaceholder, immediateScript);
    }

    [Fact]
    public void Weekly_day_selection_maps_to_advanced_schedule()
    {
        var schedule = new ScheduleDefinition
        {
            StartTime = DateTimeOffset.UtcNow.AddDays(1),
            Frequency = ScheduleFrequency.Weekly,
            DaysOfWeek = new[] { DayOfWeek.Monday, DayOfWeek.Friday, DayOfWeek.Monday }
        };

        var advanced = AutomationScheduleMapper.ToAdvancedSchedule(schedule);
        Assert.NotNull(advanced);
        var weekDays = (string[])advanced!.GetType().GetProperty("weekDays")!.GetValue(advanced)!;
        Assert.Equal(new[] { "Monday", "Friday" }, weekDays);

        // No days, or non-weekly frequency → no advanced schedule.
        Assert.Null(AutomationScheduleMapper.ToAdvancedSchedule(schedule with { DaysOfWeek = Array.Empty<DayOfWeek>() }));
        Assert.Null(AutomationScheduleMapper.ToAdvancedSchedule(schedule with { Frequency = ScheduleFrequency.Daily }));
    }

    [Fact]
    public void Monthly_days_map_to_advanced_schedule_including_last_day()
    {
        var schedule = new ScheduleDefinition
        {
            StartTime = DateTimeOffset.UtcNow.AddDays(1),
            Frequency = ScheduleFrequency.Monthly,
            MonthDays = new[] { 1, 15, 15, -1, 40 } // dupes and invalid values dropped
        };

        var advanced = AutomationScheduleMapper.ToAdvancedSchedule(schedule);
        var monthDays = (int[])advanced!.GetType().GetProperty("monthDays")!.GetValue(advanced)!;
        Assert.Equal(new[] { 1, 15, -1 }, monthDays);
    }

    [Fact]
    public void Monthly_weekday_occurrence_maps_to_advanced_schedule()
    {
        var schedule = new ScheduleDefinition
        {
            StartTime = DateTimeOffset.UtcNow.AddDays(1),
            Frequency = ScheduleFrequency.Monthly,
            MonthlyWeekDay = new MonthlyOccurrence(2, DayOfWeek.Tuesday)
        };

        var advanced = AutomationScheduleMapper.ToAdvancedSchedule(schedule);
        var occurrences = (object[])advanced!.GetType().GetProperty("monthlyOccurrences")!.GetValue(advanced)!;
        var first = occurrences.Single();
        Assert.Equal(2, (int)first.GetType().GetProperty("occurrence")!.GetValue(first)!);
        Assert.Equal("Tuesday", (string)first.GetType().GetProperty("day")!.GetValue(first)!);
    }

    [Fact]
    public void Runbook_parameter_carries_identity_client_id_when_user_assigned()
    {
        var parameter = ScheduledRequestSerializer.ToRunbookParameter(
            TestData.Request(), identityClientId: "11111111-2222-3333-4444-555555555555");
        using var json = JsonDocument.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(parameter)));

        Assert.Equal("11111111-2222-3333-4444-555555555555",
            json.RootElement.GetProperty("identityClientId").GetString());
        Assert.Contains("client_id", RunbookContent.Script);
    }

    [Fact]
    public void Runbook_is_module_free_and_parameterised()
    {
        Assert.Contains("param(", RunbookContent.Script);
        Assert.Contains("$RequestB64", RunbookContent.Script);
        Assert.Contains("IDENTITY_ENDPOINT", RunbookContent.Script);
        Assert.DoesNotContain("Connect-AzAccount", RunbookContent.Script);
        Assert.DoesNotContain("Import-Module", RunbookContent.Script);
    }
}
