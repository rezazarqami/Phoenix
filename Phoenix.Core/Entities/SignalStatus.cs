namespace Phoenix.Core.Entities;

public enum SignalStatus
{
    WaitingEntry,
    PositionOpen,
    ProtectedExit,
    TakeProfit,
    Stopped,
    Cancelled,
    Expired,
    EmergencyClosed
}