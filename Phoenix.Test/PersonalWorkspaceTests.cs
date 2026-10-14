using Phoenix.Web;
using System.Text.Json;
namespace Phoenix.Test;
public static class PersonalWorkspaceTests
{
    private static PersonalInput Input(string kind, string title, PersonalDetails? details = null, Guid? id = null) =>
        new(kind, new DateOnly(2026, 10, 14), title, "نمونه", null, null, "", "", null, details ?? new(), id ?? Guid.NewGuid());
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Conflict(Action action) { try { action(); } catch (PersonalConflict) { return; } throw new Exception("Expected conflict."); }
    public static void Workspace()
    {
        var previous = Environment.GetEnvironmentVariable("PERSONAL_DATABASE_PATH");
        var directory = Path.Combine(Path.GetTempPath(), "workspace-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH", Path.Combine(directory, "personal.db"));
            var store = new PersonalStore();
            PersonalEntry Create(PersonalInput input) { Check(PersonalRules.Validate(input) is null, "Fixture invalid."); return store.CreateAsync(input, default).GetAwaiter().GetResult(); }
            var input = Input("journal", "یک یادداشت"); var entry = Create(input);
            var retried = Create(input);
            Check(retried.Id == entry.Id && retried.CreatedAt == entry.CreatedAt && store.ListAsync(default).GetAwaiter().GetResult().Count == 1, "Retry created a duplicate.");
            Conflict(() => Create(input with { Title = "متن متفاوت" }));
            var updatedInput = PersonalRules.Input(entry) with { Notes = "ویرایش اول" };
            var updated = store.UpdateAsync(entry.Id, updatedInput, entry.Revision, default).GetAwaiter().GetResult()!;
            Check(updated.Revision == 2, "Revision did not advance.");
            Check(store.UpdateAsync(entry.Id, updatedInput, 1, default).GetAwaiter().GetResult()!.Revision == 2, "Retrying an update changed revision.");
            Conflict(() => store.UpdateAsync(entry.Id, updatedInput with { Notes = "ویرایش قدیمی" }, 1, default).GetAwaiter().GetResult());
            Check(store.HistoryAsync(entry.Id, default).GetAwaiter().GetResult().Count == 2, "History incomplete.");
            Check(store.DeleteAsync(entry.Id, default, 2).GetAwaiter().GetResult(), "Delete failed.");
            Check(store.ListAsync(default, true).GetAwaiter().GetResult().Single().Revision == 3, "Soft delete missing.");
            Conflict(() => Create(input));
            Check(store.RestoreAsync(entry.Id, 3, default).GetAwaiter().GetResult(), "Restore failed.");
            for (var i = 0; i < 3; i++) Create(Input("task", "کار مهم " + i, new() { Important = true, Status = "todo" }));
            Conflict(() => Create(Input("task", "چهارمین کار", new() { Important = true, Status = "todo" })));
            var goal = Create(Input("goal", "بهبود کوک"));
            var project = Create(Input("project", "آزمایش کوک", new() { GoalId = goal.Id }));
            Conflict(() => store.DeleteAsync(goal.Id, default).GetAwaiter().GetResult());
            var habit = Create(Input("habit", "تمرین", new() { GoalId = goal.Id, Target = 3 }));
            Create(Input("habit-log", "تمرین", new() { ParentId = habit.Id, Status = "done" }));
            Conflict(() => Create(Input("habit-log", "تمرین", new() { ParentId = habit.Id, Status = "done" })));
            Create(Input("checkin", "وضعیت روز", new() { Focus = 4, Energy = 3, Activities = ["پیاده‌روی"] }));
            Conflict(() => Create(Input("checkin", "وضعیت تکراری", new() { Energy = 4 })));
            var decision = Create(Input("decision", "خرید ابزار", new() { Hypothesis = "دلیل اولیه", Alternatives = "گزینهٔ دیگر", ExpectedOutcome = "انتظار اولیه", ReviewDate = new(2026, 10, 21) }));
            var reviewed = PersonalRules.Input(decision) with { Details = decision.Details! with { ActualOutcome = "نتیجه", ReviewedDate = new(2026, 10, 21) } };
            Check(store.UpdateAsync(decision.Id, reviewed, 1, default).GetAwaiter().GetResult()!.Details!.Hypothesis == "دلیل اولیه", "Original decision lost.");
            Conflict(() => store.UpdateAsync(decision.Id, reviewed with { Details = reviewed.Details! with { Hypothesis = "بازنویسی دلیل" } }, 2, default).GetAwaiter().GetResult());
            var running = Create(Input("time", "تمرکز", new() { StartedAt = DateTimeOffset.UtcNow, ProjectId = project.Id }));
            Conflict(() => Create(Input("time", "تمرکز دوم", new() { StartedAt = DateTimeOffset.UtcNow })));
            var stopped = store.UpdateAsync(running.Id, PersonalRules.Input(running) with { Details = running.Details! with { EndedAt = running.Details.StartedAt!.Value.AddMinutes(25), Status = "done" } }, 1, default).GetAwaiter().GetResult()!;
            Check(stopped.Details!.DurationMinutes == 25, "Timer duration not derived from timestamps.");
            var attachmentId = Guid.NewGuid(); var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
            var metadata = store.AddAttachmentAsync(attachmentId, "قبل.png", "image/png", png, default).GetAwaiter().GetResult();
            Check(store.AddAttachmentAsync(attachmentId, "قبل.png", "image/png", png, default).GetAwaiter().GetResult() == metadata, "Attachment retry not idempotent.");
            Create(Input("craft", "آزمایش صدا", new() { Method = "روش آزمایشی", Materials = "فولاد", ProjectId = project.Id, AttachmentIds = [attachmentId] }));
            var backup = store.BackupAsync(default).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH", Path.Combine(directory, "restored", "personal.db"));
            var restored = new PersonalStore(); var imported = restored.ImportAsync(backup, default).GetAwaiter().GetResult();
            Check(imported.Added == store.ListAsync(default).GetAwaiter().GetResult().Count, "Backup import lost entries.");
            Check(restored.ImportAsync(backup, default).GetAwaiter().GetResult().Added == 0, "Repeated backup duplicated entries.");
            Check(File.ReadAllBytes(Path.Combine(restored.AttachmentDirectory, attachmentId.ToString("N"))).AsSpan().SequenceEqual(png), "Backup lost media.");
            var rejected = PersonalRules.Validate(Input("experiment", "آزمایش", new() { Hypothesis = "فرضیه", BaselineFrom = new(2026,10,1), BaselineTo = new(2026,10,10), TrialFrom = new(2026,10,10), TrialTo = new(2026,10,20) }));
            Check(rejected is not null, "Overlapping experiment windows accepted.");
            Check(PersonalRules.Validate(Input("habit", "عادت", new() { Cadence = "day", Target = 3 })) is not null, "Unattainable daily habit accepted.");
        }
        finally { Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH", previous); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    public static void Reports()
    {
        var date = new DateOnly(2026, 10, 14);
        var car = PersonalRules.Build(new("vehicle", date, "روغن", "خودرو", 100, null, "", "", null), Guid.NewGuid());
        var expense = PersonalRules.Build(new("finance", date, "خرید", "خوراک", 200, "expense", "", "", null), Guid.NewGuid());
        var checkin = PersonalRules.Build(Input("checkin", "ثبت", new() { Focus = 4 }), Guid.NewGuid());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(PersonalReports.Build([car, expense, checkin], date.AddDays(-1), date), PersonalRules.Json));
        Check(json.RootElement.GetProperty("totals").GetProperty("expenses").GetDecimal() == 300, "Car cost double-counted.");
        var chart = json.RootElement.GetProperty("chart");
        Check(chart[0].GetProperty("focus").ValueKind == JsonValueKind.Null, "Missing check-in became zero.");
        Check(chart[1].GetProperty("focus").GetInt32() == 4, "Recorded focus lost.");
        var experiment = PersonalRules.Build(Input("experiment", "تغییر", new() { Hypothesis = "فرضیه", BaselineFrom = date.AddDays(-2), BaselineTo = date.AddDays(-1), TrialFrom = date, TrialTo = date.AddDays(1) }), Guid.NewGuid());
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(PersonalReports.Experiment(experiment, [checkin]), PersonalRules.Json));
        Check(result.RootElement.GetProperty("before").ValueKind == JsonValueKind.Null && !result.RootElement.GetProperty("enoughData").GetBoolean(), "Missing experiment baseline treated as evidence.");
    }
}
