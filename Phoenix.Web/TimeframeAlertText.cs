namespace Phoenix.Web;

public static class TimeframeAlertText
{
    public static string Name(string? key) => key switch
    {
        "1"=>"۱ دقیقه", "5"=>"۵ دقیقه", "15"=>"۱۵ دقیقه", "30"=>"۳۰ دقیقه",
        "60"=>"۱ ساعت", "240"=>"۴ ساعت", "D"=>"روزانه", "W"=>"هفتگی",
        "M"=>"ماهانه", "3M"=>"۳ ماهه", _=>key??"نامشخص"
    };
    public static string Blocked(ServerSignal signal) =>
        $"{signal.Symbol} به نقطهٔ ورود رسید، اما به‌دلیل قفل بودن تایم‌فریم {Name(signal.Timeframe)} معامله باز نشد.";
    public static MobileSignalEvent Locked(TimeframeLockNotice notice)
    {
        var names=notice.Stops.Select(s=>s.Symbol).ToList();
        while(names.Count<2)names.Insert(0,"معامله با نام ثبت‌نشده");
        var reason=$"تایم‌فریم {Name(notice.Timeframe)} به‌دلیل دو استاپ پیاپی در {string.Join(" و ",names)} قفل شد.";
        return new($"timeframe:{notice.Id:N}:TimeframeLocked","TimeframeLocked",notice.AtUtc,reason,Timeframe:notice.Timeframe);
    }
}
