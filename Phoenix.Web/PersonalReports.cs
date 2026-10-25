namespace Phoenix.Web;

public static class PersonalReports
{
    public static object Build(IReadOnlyList<PersonalEntry> entries, DateOnly from, DateOnly to)
    {
        entries = entries.Where(e => !(e.Kind == "finance" && e.Details?.SourceId != null && e.Details.Status is "waiting" or "cancelled")).ToArray();
        var selected = entries.Where(e => e.Date >= from && e.Date <= to).ToArray();
        var length = to.DayNumber - from.DayNumber + 1;
        var previousFrom = from.AddDays(-length); var previousTo = from.AddDays(-1);
        var previous = entries.Where(e => e.Date >= previousFrom && e.Date <= previousTo).ToArray();
        decimal Expenses(IEnumerable<PersonalEntry> list) => list.Where(e => e.Kind == "vehicle" || e.Kind == "finance" && e.Flow == "expense").Sum(e => e.Amount ?? 0);
        decimal Income(IEnumerable<PersonalEntry> list) => list.Where(e => e.Kind == "finance" && e.Flow == "income").Sum(e => e.Amount ?? 0);
        decimal Minutes(IEnumerable<PersonalEntry> list) => list.Where(e => e.Kind == "time").Sum(e => e.Details?.DurationMinutes ?? 0);
        var checkins = selected.Where(e => e.Kind == "checkin").GroupBy(e => e.Date).Select(g => g.First()).ToArray();
        var chart = Enumerable.Range(0, length).Select(n =>
        {
            var date = from.AddDays(n); var day = selected.Where(e => e.Date == date).ToArray(); var checkin = checkins.FirstOrDefault(e => e.Date == date);
            return new { date, expense = Expenses(day), income = Income(day), minutes = Minutes(day), energy = checkin?.Details?.Energy, focus = checkin?.Details?.Focus, mood = checkin?.Details?.Mood, sleepHours = checkin?.Details?.SleepHours };
        }).ToArray();
        var categories = selected.Where(e => e.Kind == "vehicle" || e.Kind == "finance" && e.Flow == "expense")
            .GroupBy(e => e.Category.Length > 0 ? e.Category : e.Kind == "vehicle" ? "خودرو" : "بدون دسته")
            .Select(g => new { name = g.Key, amount = g.Sum(e => e.Amount ?? 0), count = g.Count(), recordIds = g.Select(e => e.Id).ToArray() }).OrderByDescending(e => e.amount).ToArray();
        var projects = entries.Where(e => e.Kind is "goal" or "project").Select(parent =>
        {
            var relatedProjects = entries.Where(e => e.Kind == "project" && e.Details?.GoalId == parent.Id).Select(e => e.Id).ToHashSet();
            var linked = selected.Where(e => e.Details?.GoalId == parent.Id || e.Details?.ProjectId == parent.Id || e.Details?.ProjectId is { } id && relatedProjects.Contains(id)).ToArray();
            return new { parent.Id, parent.Title, parent.Kind, status = parent.Details?.Status, minutes = Minutes(linked), completedTasks = linked.Count(e => e.Kind == "task" && e.Details?.Status == "done"), recordCount = linked.Length, recordIds = linked.Select(e => e.Id).ToArray() };
        }).ToArray();
        var activities = checkins.SelectMany(e => e.Details?.Activities ?? []).Distinct().Select(activity =>
        {
            var with = checkins.Where(e => e.Details?.Activities.Contains(activity) == true && e.Details.Focus.HasValue).ToArray();
            var without = checkins.Where(e => e.Details?.Activities.Contains(activity) != true && e.Details?.Focus.HasValue == true).ToArray();
            decimal? Average(PersonalEntry[] records) => records.Length == 0 ? null : decimal.Round(records.Average(e => (decimal)e.Details!.Focus!.Value), 2);
            return new { activity, withCount = with.Length, withoutCount = without.Length, withFocus = Average(with), withoutFocus = Average(without), enoughData = with.Length >= 5 && without.Length >= 5 };
        }).ToArray();
        return new
        {
            from, to, previousFrom, previousTo,
            totals = new { income = Income(selected), expenses = Expenses(selected), vehicleExpenses = selected.Where(e => e.Kind == "vehicle").Sum(e => e.Amount ?? 0), minutes = Minutes(selected), completedTasks = entries.Count(e => e.Kind == "task" && e.Details?.Status == "done" && (e.Details.CompletedDate ?? e.Date) >= from && (e.Details.CompletedDate ?? e.Date) <= to), journalCount = selected.Count(e => e.Kind == "journal"), checkinDays = checkins.Length, calendarDays = length, records = selected.Length },
            previous = new { income = Income(previous), expenses = Expenses(previous), minutes = Minutes(previous), checkinDays = previous.Count(e => e.Kind == "checkin") },
            chart, categories, projects, activities,
            decisions = selected.Where(e => e.Kind == "decision").ToArray(),
            reviewsDue = entries.Where(e => e.Kind == "decision" && e.Details?.ReviewDate <= to && e.Details.ReviewedDate is null).ToArray(),
            overdueTasks = entries.Where(e => e.Kind == "task" && e.Date <= to && e.Details?.Status is "todo" or "waiting" or "active").ToArray(),
            craft = selected.Where(e => e.Kind == "craft").ToArray(),
            recordIds = selected.Select(e => e.Id).ToArray()
        };
    }
    public static object Experiment(PersonalEntry experiment, IReadOnlyList<PersonalEntry> entries)
    {
        var details = experiment.Details!;
        decimal? Measure(PersonalEntry e) => details.Metric switch { "energy" => e.Details?.Energy, "mood" => e.Details?.Mood, "sleepHours" => e.Details?.SleepHours, _ => e.Details?.Focus };
        var baseline = entries.Where(e => e.Kind == "checkin" && e.Date >= details.BaselineFrom && e.Date <= details.BaselineTo && Measure(e).HasValue).GroupBy(e => e.Date).Select(g => g.First()).ToArray();
        var trial = entries.Where(e => e.Kind == "checkin" && e.Date >= details.TrialFrom && e.Date <= details.TrialTo && Measure(e).HasValue).GroupBy(e => e.Date).Select(g => g.First()).ToArray();
        decimal? Average(PersonalEntry[] records) => records.Length == 0 ? null : decimal.Round(records.Average(e => Measure(e)!.Value), 2);
        var before = Average(baseline); var after = Average(trial);
        return new { experiment.Id, experiment.Title, details.Metric, before, after, difference = before.HasValue && after.HasValue ? after - before : null, baselineCount = baseline.Length, trialCount = trial.Length,
            baselineDays = details.BaselineTo!.Value.DayNumber - details.BaselineFrom!.Value.DayNumber + 1,
            trialDays = details.TrialTo!.Value.DayNumber - details.TrialFrom!.Value.DayNumber + 1,
            enoughData = baseline.Length >= 5 && trial.Length >= 5, recordIds = baseline.Concat(trial).Select(e => e.Id).ToArray(),
            note = "این مقایسه توصیفی است؛ تفاوت شرایط و روزهای بدون ثبت می‌توانند نتیجه را تغییر دهند. رابطهٔ علت و معلولی اثبات نمی‌شود." };
    }
}
