namespace Phoenix.Web;

/// <summary>Source-backed tendencies only. None of these observations can invalidate a count.</summary>
public static class ElliottGuidelines
{
    public static ElliottScenario Apply(ElliottScenario s)
    {
        var rules = s.Rules.ToList();
        var p = s.Waves;
        decimal L(int i) => Math.Abs(p[i+1].Price-p[i].Price);
        void Add(string code,string description,bool pass,string source) => rules.Add(
            new ElliottRule(code,description,pass,false) { Source=source });
        bool Near(decimal value,params decimal[] targets) => targets.Any(t=>Math.Abs(value-t)<=t*.12m);
        if (s.Pattern is "Impulse" or "TruncatedImpulse" && p.Count==6)
        {
            var r2=L(1)/L(0); var r4=L(3)/L(2);
            Add("depth-alternation","تناوب عمق ۲ و ۴ راهنماست، نه شرط ابطال.",
                r2>=.618m && r4<=.382m || r2<=.382m && (Near(r4,.236m,.5m)),"ب:۳۸؛ ف:۴۱");
            Add("wave5-projection","طول ۵ با حرکت خالص مبدأ۱ تا انتهای۳ یا طول۱ مقایسه شد.",
                Near(L(4)/Math.Abs(p[3].Price-p[0].Price),.382m,.618m) || Near(L(4)/L(0),1m,1.618m),"ب:۳۷؛ ف:۳۸");
            var thirdExtended=L(2)>=1.618m*L(0);
            if(thirdExtended) Add("extension-equality","هنگام کشیدگی ۳، برابری تقریبی ۱ و ۵ بررسی شد.",Near(L(4)/L(0),1m),"ب:۳۷؛ ف:۳۹");
            Add("wave2-depth","اصلاح ۵۰ تا ۶۱٫۸٪ یا عمیق‌تر برای ۲ متداول است.",r2>=.5m,"ب:۴۰");
            Add("wave4-depth","اصلاح ۳۸٫۲٪ برای ۴ یک گرایش رایج است.",Near(r4,.382m),"ب:۴۰");
            if(r2<.5m) Add("shallow-second","۲ کم‌عمق می‌تواند از گسترش ۳ حمایت کند.",true,"ب:۳۸");
            // Orthodox endpoints, not the largest visible wick or the later B.
            var channel=p[3].Price+(p[4].Price-p[2].Price)*(p[5].Time-p[3].Time)/(p[4].Time-p[2].Time);
            Add("fifth-channel","انتهای ۵ با خط موازی کانال ۲–۴ از انتهای۳ مقایسه شد.",
                Math.Abs(p[5].Price-channel)<=Math.Abs(p[3].Price-p[0].Price)*.12m,"ب:۴۰–۴۱");
        }
        if(p.Count==4 && s.Pattern is "Zigzag" or "Flat" or "ExpandedFlat" or "RunningFlat")
        {
            Add("c-a-ratio","طول C نسبت به A فقط راهنمای رتبه‌بندی است.",Near(L(2)/L(0),.618m,1m,1.382m,1.618m,2.618m),"ب:۲۶–۲۷،۴۱–۴۴");
            if(s.Pattern=="Zigzag") Add("zigzag-b-depth","عمق B با نسبت‌های رایج زیگزاگ مقایسه شد؛ سقف عددی مبهم قانون سخت نیست.",Near(L(1)/L(0),.382m,.5m,.618m),"ب:۴۴؛ ف:۱۹–۲۰");
            if(s.Pattern is "ExpandedFlat" or "RunningFlat") Add("expanded-b-ratio","B گسترش‌یافته با نسبت‌های رایج مقایسه شد.",Near(L(1)/L(0),1.382m,1.618m),"ب:۴۱–۴۲");
        }
        var added=rules.Skip(s.Rules.Count).ToArray();
        return s with { Rules=rules, Score=Math.Round(Math.Clamp(s.Score+added.Count(r=>r.Passed),0,100),1) };
    }
}
