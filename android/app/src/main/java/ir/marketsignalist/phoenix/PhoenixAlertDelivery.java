package ir.marketsignalist.phoenix;
import android.content.*;
import java.util.*;

final class PhoenixAlertDelivery {
    static synchronized boolean deliver(Context context,String id,String kind,String symbol,String exitKind,long at){return deliver(context,id,kind,symbol,exitKind,at,null);}
    static synchronized boolean deliver(Context context,String id,String kind,String symbol,String exitKind,long at,String reason){
        if(Arrays.asList("Created","Cancelled").contains(kind))return true;
        if(!Arrays.asList("NearEntry","Opened","FirstProtection","RiskFreeReached","Target","StopLoss","RiskFree","Expired","EntryError","ManualClosed","TimeframeLocked","EntryBlocked","Test").contains(kind))return false;
        if(id==null||!id.matches("[A-Za-z0-9:_-]{1,120}"))return false;
        if(!PhoenixNotifications.channelAllowed(context,PhoenixNotifications.SIGNALS))return false;
        SharedPreferences seen=context.getSharedPreferences("orbit-push-seen",Context.MODE_PRIVATE);
        if(seen.contains(id))return true;
        String text=Arrays.asList("EntryError","TimeframeLocked","EntryBlocked").contains(kind)&&reason!=null&&!reason.isEmpty()?reason:PhoenixNotifications.signalText(kind);
        if(symbol!=null&&symbol.matches("[A-Za-z0-9-]{2,40}")&&!text.contains(symbol))text=symbol+" — "+text;
        if("RiskFree".equals(kind)){
            if("FirstStepLimit".equals(exitKind))text+=" پلهٔ اول ریسک‌فری (استاپ‌لیمیت).";
            else if("SecondStepLimit".equals(exitKind))text+=" پلهٔ دوم ریسک‌فری (استاپ‌لیمیت).";
            else if("SafetyStopMarket".equals(exitKind))text+=" محافظ ریسک‌فری (استاپ‌مارکت).";
        }
        long now=System.currentTimeMillis();
        if(at>0&&now-at>120000)text+=" (رویداد قبلی، با تأخیر پس از اتصال)";
        PhoenixNotifications.showText(context,id,text);
        SharedPreferences.Editor edit=seen.edit();
        List<Map.Entry<String,?>> entries=new ArrayList<>(seen.getAll().entrySet());
        entries.sort(Comparator.comparingLong(e->e.getValue() instanceof Long?(Long)e.getValue():0));
        for(int i=0;i<entries.size();i++)if(i<entries.size()-1023||!(entries.get(i).getValue() instanceof Long)||(Long)entries.get(i).getValue()<now-604800000L)edit.remove(entries.get(i).getKey());
        return edit.putLong(id,now).commit();
    }
}
