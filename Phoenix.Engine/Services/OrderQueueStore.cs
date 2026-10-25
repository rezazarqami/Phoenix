using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Core.Entities;

namespace Phoenix.Engine.Services;

public sealed class OrderQueueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public OrderQueueStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Phoenix",
            "queued-orders.json");
    }

    public string FilePath { get; }

    public IReadOnlyList<QueuedOrder> Load()
    {
        if (!File.Exists(FilePath))
            return [];

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<QueuedOrder>>(json, JsonOptions) ?? [];
    }

    public void Save(IEnumerable<QueuedOrder> orders)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The queue file needs a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(orders, JsonOptions));
        File.Move(temporaryPath, FilePath, true);
    }
}
