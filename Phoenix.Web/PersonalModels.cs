using System.Text.Json;
namespace Phoenix.Web;

public sealed record PersonalDetails
{
    public string? SourceId { get; init; }
    public string Status { get; init; } = "active";
    public bool Important { get; init; }
    public string? DueTime { get; init; }
    public Guid? GoalId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? ParentId { get; init; }
    public DateOnly? DueDate { get; init; }
    public DateOnly? CompletedDate { get; init; }
    public string Cadence { get; init; } = "week";
    public int Target { get; init; } = 3;
    public int? Mood { get; init; }
    public int? Energy { get; init; }
    public int? Focus { get; init; }
    public int? SleepQuality { get; init; }
    public decimal? SleepHours { get; init; }
    public string[] Activities { get; init; } = [];
    public decimal? DurationMinutes { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string Alternatives { get; init; } = "";
    public string Hypothesis { get; init; } = "";
    public string ExpectedOutcome { get; init; } = "";
    public string ActualOutcome { get; init; } = "";
    public DateOnly? ReviewDate { get; init; }
    public DateOnly? ReviewedDate { get; init; }
    public string Materials { get; init; } = "";
    public string Method { get; init; } = "";
    public string Observation { get; init; } = "";
    public string Conclusion { get; init; } = "";
    public DateOnly? BaselineFrom { get; init; }
    public DateOnly? BaselineTo { get; init; }
    public DateOnly? TrialFrom { get; init; }
    public DateOnly? TrialTo { get; init; }
    public string Metric { get; init; } = "focus";
    public Guid[] AttachmentIds { get; init; } = [];
}
public record PersonalEntry(Guid Id, string Kind, DateOnly Date, string Title, string Category,
    decimal? Amount, string? Flow, string Notes, string Vehicle, int? Odometer, DateTimeOffset CreatedAt,
    PersonalDetails? Details = null, int Revision = 1, DateTimeOffset? UpdatedAt = null);
public record PersonalInput(string Kind, DateOnly Date, string Title, string? Category,
    decimal? Amount, string? Flow, string? Notes, string? Vehicle, int? Odometer,
    PersonalDetails? Details = null, Guid? ClientId = null);
public record PersonalUpdate(PersonalInput Entry, int Revision);
public record PersonalRevision(int Revision);
public record PersonalAttachment(Guid Id, string Name, string ContentType, long Size, DateTimeOffset CreatedAt);
public record PersonalBackup(int Version, DateTimeOffset ExportedAt, PersonalEntry[] Entries, PersonalAttachment[] Attachments);
public sealed class PersonalConflict(string message, PersonalEntry? current = null) : Exception(message)
{
    public PersonalEntry? Current { get; } = current;
}
public static class PersonalRules
{
    public static readonly HashSet<string> Kinds = ["category", "finance", "vehicle", "journal", "task", "goal", "project", "habit", "habit-log", "checkin", "decision", "craft", "time", "experiment", "review"];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string? Validate(PersonalInput input)
    {
        if (!Kinds.Contains(input.Kind ?? "") || string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200 || input.Date == default)
            return "نوع ثبت، عنوان و تاریخ معتبر لازم است.";
        if ((input.Notes?.Length ?? 0) > 10000 || (input.Category?.Length ?? 0) > 100 || (input.Vehicle?.Length ?? 0) > 100)
            return "متن یا دسته‌بندی بیش از حد طولانی است.";
        if (input.Amount is < 0 or > 1000000000000000m || input.Odometer < 0)
            return "مبلغ و کیلومتر باید معتبر و نامنفی باشند.";
        if (input.Kind == "finance" && (input.Amount is null or <= 0 || input.Flow is not ("income" or "expense")))
            return "مبلغ مثبت و نوع درآمد یا هزینه را مشخص کنید.";
        if (input.Details is not { } d) return input.Kind is "finance" or "vehicle" or "journal" ? null : "جزئیات این ثبت لازم است.";
        if (d.SourceId is { Length: > 100 }) return "شناسهٔ پیامک معتبر نیست.";
        if (input.Kind == "category" && input.Category is not ("finance" or "task")) return "نوع دسته معتبر نیست.";
        if (d.Status is not ("active" or "todo" or "done" or "waiting" or "cancelled" or "archived")) return "وضعیت معتبر نیست.";
        if (d.DueTime is { Length: > 0 } && !TimeOnly.TryParseExact(d.DueTime, "HH:mm", out _)) return "زمان را به شکل ساعت و دقیقه وارد کنید.";
        if (d.Target is < 1 or > 100 || d.Cadence is not ("day" or "week")) return "تعداد هدف و دورهٔ تکرار معتبر نیست.";
        if (input.Kind == "habit" && (d.Cadence == "day" && d.Target != 1 || d.Cadence == "week" && d.Target > 7)) return "عادت روزانه یک روز و عادت هفتگی حداکثر هفت روز است.";
        if (d.Mood is < 1 or > 5 || d.Energy is < 1 or > 5 || d.Focus is < 1 or > 5 || d.SleepQuality is < 1 or > 5 || d.SleepHours is < 0 or > 24)
            return "امتیازها بین ۱ تا ۵ و خواب بین صفر تا ۲۴ ساعت است.";
        if (d.Activities is null || d.Activities.Length > 20 || d.Activities.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 100)) return "فعالیت‌ها معتبر نیستند.";
        if (d.AttachmentIds is null || d.AttachmentIds.Length > 8 || d.AttachmentIds.Distinct().Count() != d.AttachmentIds.Length) return "حداکثر هشت پیوست یکتا مجاز است.";
        foreach (var value in new[] { d.Alternatives, d.Hypothesis, d.ExpectedOutcome, d.ActualOutcome, d.Materials, d.Method, d.Observation, d.Conclusion })
            if (value is null || value.Length > 10000) return "متن جزئیات بیش از حد طولانی است.";
        if (d.DurationMinutes is < 0 or > 1440) return "زمان فعالیت باید بین صفر و ۱۴۴۰ دقیقه باشد.";
        if (input.Kind == "time")
        {
            if (d.StartedAt is null && d.DurationMinutes is null or <= 0) return "مدت فعالیت یا زمان شروع لازم است.";
            if (d.EndedAt.HasValue && (!d.StartedAt.HasValue || d.EndedAt < d.StartedAt || d.EndedAt - d.StartedAt > TimeSpan.FromHours(24)))
                return "بازهٔ فعالیت باید معتبر و حداکثر ۲۴ ساعت باشد.";
            if (d.StartedAt > DateTimeOffset.UtcNow.AddMinutes(5)) return "زمان شروع در آینده نیست.";
        }
        if (input.Kind == "checkin" && d.Mood is null && d.Energy is null && d.Focus is null && d.SleepHours is null && d.SleepQuality is null && d.Activities.Length == 0 && string.IsNullOrWhiteSpace(input.Notes))
            return "حداقل یک مورد از وضعیت روزانه را ثبت کنید.";
        if (input.Kind == "habit-log" && d.ParentId is null) return "عادت مرتبط لازم است.";
        if (input.Kind == "decision" && (string.IsNullOrWhiteSpace(d.Hypothesis) || string.IsNullOrWhiteSpace(d.ExpectedOutcome) || d.ReviewDate is null))
            return "دلیل تصمیم، انتظار و زمان مرور را مشخص کنید.";
        if (input.Kind == "craft" && string.IsNullOrWhiteSpace(d.Method)) return "روش آزمایش ساز را بنویسید.";
        if (input.Kind == "experiment")
        {
            if (string.IsNullOrWhiteSpace(d.Hypothesis) || d.Metric is not ("energy" or "focus" or "mood" or "sleepHours") || d.BaselineFrom is null || d.BaselineTo is null || d.TrialFrom is null || d.TrialTo is null
                || d.BaselineFrom > d.BaselineTo || d.TrialFrom > d.TrialTo || d.BaselineTo >= d.TrialFrom
                || d.BaselineTo.Value.DayNumber - d.BaselineFrom.Value.DayNumber > 365 || d.TrialTo.Value.DayNumber - d.TrialFrom.Value.DayNumber > 365)
                return "فرضیه و دو بازهٔ معتبر، جدا و حداکثر یک‌ساله لازم است.";
        }
        return null;
    }
    public static PersonalEntry Build(PersonalInput input, Guid id, DateTimeOffset? created = null, int revision = 1)
    {
        var details = input.Details;
        if (input.Kind == "time" && details?.StartedAt is { } start && details.EndedAt is { } end)
            details = details with { DurationMinutes = decimal.Round((decimal)(end - start).TotalMinutes, 2), Status = "done" };
        return new(id, input.Kind, input.Date, input.Title.Trim(), input.Category?.Trim() ?? "", input.Kind is "finance" or "vehicle" ? input.Amount : null,
            input.Kind == "finance" ? input.Flow : null, input.Notes?.Trim() ?? "", input.Kind == "vehicle" ? input.Vehicle?.Trim() ?? "" : "",
            input.Kind == "vehicle" ? input.Odometer : null, created ?? DateTimeOffset.UtcNow, details, revision, revision == 1 ? null : DateTimeOffset.UtcNow);
    }
    public static PersonalInput Input(PersonalEntry e) => new(e.Kind, e.Date, e.Title, e.Category, e.Amount, e.Flow, e.Notes, e.Vehicle, e.Odometer, e.Details, e.Id);
}
