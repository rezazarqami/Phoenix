namespace Phoenix.Web;

public sealed record CryptoStatisticsItem(string Symbol, int Total, int Open, int Targets,
    int Stops, int RiskFree, int OtherClosed);
