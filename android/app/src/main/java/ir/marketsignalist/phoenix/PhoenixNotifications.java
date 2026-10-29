package ir.marketsignalist.phoenix;
import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.os.Build;

final class PhoenixNotifications {
    // Retain the existing channel ID so notification preferences survive an update.
    static final String SIGNALS="orbit-signals-v1";
    static void channels(Context c){soundChannels(c);NotificationChannel signal=new NotificationChannel(SIGNALS,"اعلان‌های فونیکس",NotificationManager.IMPORTANCE_HIGH);signal.enableVibration(true);signal.setLockscreenVisibility(Notification.VISIBILITY_PRIVATE);c.getSystemService(NotificationManager.class).createNotificationChannel(signal);}
    static String selectedChannel(Context c){return "phoenix-tone-"+c.getSharedPreferences("phoenix-sounds",0).getString("tone","crystal");}
    static void soundChannels(Context c){
        String[] tones={"crystal","pulse","beacon","soft"};
        String[] names={"کریستال","ضربان","فانوس","ملایم"};
        for(int i=0;i<tones.length;i++){
            NotificationChannel channel=new NotificationChannel("phoenix-tone-"+tones[i],"سیگنال فونیکس · "+names[i],NotificationManager.IMPORTANCE_HIGH);
            int resource=c.getResources().getIdentifier("tone_"+tones[i],"raw",c.getPackageName());
            channel.setSound(android.net.Uri.parse("android.resource://"+c.getPackageName()+"/"+resource),new android.media.AudioAttributes.Builder().setUsage(android.media.AudioAttributes.USAGE_NOTIFICATION).build());
            channel.enableVibration(true);c.getSystemService(NotificationManager.class).createNotificationChannel(channel);
        }
    }
    static boolean allowed(Context c){return (Build.VERSION.SDK_INT<33||c.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)==PackageManager.PERMISSION_GRANTED)&&c.getSystemService(NotificationManager.class).areNotificationsEnabled();}
    static boolean channelAllowed(Context c,String id){NotificationChannel channel=c.getSystemService(NotificationManager.class).getNotificationChannel(SIGNALS.equals(id)?selectedChannel(c):id);return allowed(c)&&channel!=null&&channel.getImportance()!=NotificationManager.IMPORTANCE_NONE;}
    static void show(Context c,String channel,String eventId,String route){show(c,channel,eventId,route,null);}
    static void show(Context c,String channel,String eventId,String route,String eventKind){
        if(!SIGNALS.equals(channel)||java.util.Arrays.asList("Created","Cancelled").contains(eventKind))return;
        showText(c,eventId,signalText(eventKind));
    }
    static void showText(Context c,String eventId,String text){
        channels(c);if(!channelAllowed(c,SIGNALS))return;
        String channel=selectedChannel(c);
        Intent open=new Intent(c,MainActivity.class).setAction("phoenix.open").setData(android.net.Uri.parse("phoenix://notification/"+android.net.Uri.encode(eventId))).putExtra("route","/notifications?eventId="+android.net.Uri.encode(eventId)).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP|Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent content=PendingIntent.getActivity(c,0,open,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Notification publicVersion=new Notification.Builder(c,channel).setSmallIcon(R.drawable.ic_notification_p).setContentTitle("Phoenix").setContentText("اعلان جدید").build();
        Notification notification=new Notification.Builder(c,channel).setSmallIcon(R.drawable.ic_notification_p).setColor(Color.rgb(8,123,255)).setContentTitle("Phoenix").setContentText(text).setStyle(new Notification.BigTextStyle().bigText(text)).setContentIntent(content).setOnlyAlertOnce(true).setAutoCancel(true).setVisibility(Notification.VISIBILITY_PRIVATE).setPublicVersion(publicVersion).build();
        c.getSystemService(NotificationManager.class).notify(eventId,1,notification);
    }
    static String signalText(String kind){
        if(kind==null)return "اعلان آزمایشی؛ برای ورود، قفل برنامه را باز کن.";
        switch(kind){
            case "Test":return "اعلان آزمایشی فونیکس؛ اتصال نمایش اعلان گوشی برقرار است.";
            case "Created":return "سیگنال جدید در فونیکس ثبت شد.";
            case "NearEntry":return "قیمت به محدودهٔ ورود سیگنال رسید.";
            case "FirstProtection":return "پلهٔ اول ریسک‌فری فعال شد.";
            case "Opened":return "ورود معامله در بای‌بیت تأیید شد.";
            case "RiskFreeReached":return "یک سیگنال به محدودهٔ ریسک‌فری رسید.";
            case "Target":return "یک سیگنال به تارگت رسید.";
            case "StopLoss":return "یک سیگنال به استاپ رسید.";
            case "RiskFree":return "یک معامله با ریسک‌فری بسته شد.";
            case "EntryError":return "ورود معامله با خطا مواجه شد؛ علت را در نتایج ببین.";
            case "Expired":return "یک سیگنال اکسپایر شد.";
            case "Cancelled":return "یک سیگنال لغو شد.";
            case "ManualClosed":return "یک معامله به‌صورت دستی بسته شد.";
            default:return "یک اعلان جدید داری؛ برنامه را باز کن.";
        }
    }
}
