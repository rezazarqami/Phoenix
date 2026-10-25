namespace Phoenix.Engine.Exchanges;

public sealed record PaperOrder(
    DateTime CreatedAt,
    string Type,
    Guid PositionId,
    decimal Price,
    decimal Quantity,
    string Status)
{
    public DateTime LocalCreatedAt => CreatedAt.ToLocalTime();
}
