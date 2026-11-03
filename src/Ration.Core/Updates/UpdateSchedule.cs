namespace Ration.Core.Updates;

/// <summary>When the automatic update check is made.</summary>
public static class UpdateSchedule
{
    /// <summary>After a successful check, until the next one.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>After a failed check; so we do not wait for days after an offline startup.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(2);

    public static bool IsDue(UpdateCheckResult? last, DateTimeOffset now)
    {
        if (last is null) return true;

        var elapsed = now - last.CheckedAt;
        // If the clock was set back (negative duration) the record is unreliable; check again.
        if (elapsed < TimeSpan.Zero) return true;
        return elapsed >= (last.Succeeded ? Interval : RetryInterval);
    }
}
