using Phoenix.Web;
namespace Phoenix.Test;
public static class PersonalStoreTests
{
    public static void Persistence()
    {
        var original = Environment.GetEnvironmentVariable("PERSONAL_DATABASE_PATH");
        var directory = Path.Combine(Path.GetTempPath(), "personal-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH", Path.Combine(directory, "personal.db"));
            var store = new PersonalStore();
            var entry = new PersonalEntry(Guid.NewGuid(), "vehicle", new DateOnly(2026, 10, 14),
                "تعویض روغن", "سرویس", 1200000m, null, "روغن و فیلتر", "خودروی من", 80000, DateTimeOffset.UtcNow);
            store.SaveAsync(entry, default).GetAwaiter().GetResult();
            var reopened = new PersonalStore();
            var read = reopened.ListAsync(default).GetAwaiter().GetResult();
            if (read.Count != 1 || read[0] != entry) throw new Exception("Personal entries must survive reopening without losing fields.");
            if (!reopened.DeleteAsync(entry.Id, default).GetAwaiter().GetResult()) throw new Exception("Existing entry was not deleted.");
            if (store.ListAsync(default).GetAwaiter().GetResult().Count != 0) throw new Exception("Deleted entry remains.");
            if (store.DeleteAsync(entry.Id, default).GetAwaiter().GetResult()) throw new Exception("Missing entry incorrectly reported as deleted.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH", original);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
