namespace Phoenix.Web;

internal static class RiskFreeExit
{
    // Classify by the actual filled exchange order, never by the current protection
    // stage: the first limit can fill while the second stage is being installed.
    internal static void Record(ServerSignal signal,string filledOrderId)
    {
        signal.RiskFreeCloseOrderId=filledOrderId;
        signal.RiskFreeCloseKind=filledOrderId==signal.EarlyStopLimitOrderId ? "FirstStepLimit"
            : filledOrderId==signal.StopLoss2OrderId ? "SecondStepLimit"
            : filledOrderId==signal.RiskFreeStopMarketOrderId ? "SafetyStopMarket" : null;
    }

    internal static string Title(ServerSignal signal)=>signal.RiskFreeCloseKind switch
    {
        "FirstStepLimit"=>"پلهٔ اول ریسک‌فری",
        "SecondStepLimit"=>"پلهٔ دوم ریسک‌فری",
        "SafetyStopMarket"=>"محافظ ریسک‌فری",
        _=>"ریسک‌فری"
    };

    internal static string Detail(ServerSignal signal)=>signal.RiskFreeCloseKind switch
    {
        "FirstStepLimit"=>"سفارش خروج: استاپ‌لیمیت پلهٔ اول — سطح ۲۰٪ مسیر ورود تا تارگت",
        "SecondStepLimit"=>"سفارش خروج: استاپ‌لیمیت پلهٔ دوم — سطح ۵۰٪ مسیر ورود تا تارگت",
        "SafetyStopMarket"=>"سفارش خروج: استاپ‌مارکت محافظ — سطح ۲۰٪ مسیر ورود تا تارگت",
        _=>"نوع سفارش خروج در سابقهٔ این معامله ثبت نشده است."
    };
}
