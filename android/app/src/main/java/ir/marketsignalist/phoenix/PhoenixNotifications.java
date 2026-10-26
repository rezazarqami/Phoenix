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
    static void channels(Context c){NotificationChannel signal=new NotificationChannel(SIGNALS,"اعلان‌های فونیکس",NotificationManager.IMPORTANCE_HIGH);signal.enableVibration(true);signal.setLockscreenVisibility(Notification.VISIBILITY_PRIVATE);c.getSystemService(NotificationManager.class).createNotificationChannel(signal);}
    static boolean allowed(Context c){return (Build.VERSION.SDK_INT<33||c.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)==PackageManager.PERMISSION_GRANTED)&&c.getSystemService(NotificationManager.class).areNotificationsEnabled();}
    static boolean channelAllowed(Context c,String id){NotificationChannel channel=c.getSystemService(NotificationManager.class).getNotificationChannel(id);return allowed(c)&&channel!=null&&channel.getImportance()!=NotificationManager.IMPORTANCE_NONE;}
    static void show(Context c,String channel,String eventId,String route){show(c,channel,eventId,route,null);}
    static void show(Context c,String channel,String eventId,String route,String eventKind){
        channels(c);if(!SIGNALS.equals(channel)||!channelAllowed(c,channel))return;
        Intent open=new Intent(c,MainActivity.class).setAction("phoenix.open").setData(android.net.Uri.parse("phoenix://notification/"+android.net.Uri.encode(eventId))).putExtra("route","/phoenix").addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP|Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent content=PendingIntent.getActivity(c,0,open,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Notification publicVersion=new Notification.Builder(c,channel).setSmallIcon(android.R.drawable.ic_dialog_info).setContentTitle("Phoenix").setContentText("اعلان جدید").build();
        Notification notification=new Notification.Builder(c,channel).setSmallIcon(android.R.drawable.ic_dialog_info).setColor(Color.rgb(8,123,255)).setContentTitle("Phoenix").setContentText(signalText(eventKind)).setContentIntent(content).setOnlyAlertOnce(true).setAutoCancel(true).setVisibility(Notification.VISIBILITY_PRIVATE).setPublicVersion(publicVersion).build();
        c.getSystemService(NotificationManager.class).notify(eventId,1,notification);
    }
    static String signalText(String kind){
        if(kind==null)return "اعلان آزمایشی؛ برای ورود، قفل برنامه را باز کن.";
        switch(kind){
            case "Created":return "سیگنال جدید در فونیکس ثبت شد.";
            case "Opened":return "یک معامله در فونیکس باز شد.";
            case "RiskFreeReached":return "یک سیگنال به محدودهٔ ریسک‌فری رسید.";
            case "Target":return "یک سیگنال به تارگت رسید.";
            case "StopLoss":return "یک سیگنال به استاپ رسید.";
            case "RiskFree":return "یک معامله با ریسک‌فری بسته شد.";
            case "Expired":return "یک سیگنال اکسپایر شد.";
            case "Cancelled":return "یک سیگنال لغو شد.";
            case "ManualClosed":return "یک معامله به‌صورت دستی بسته شد.";
            default:return "یک اعلان جدید داری؛ برنامه را باز کن.";
        }
    }
}
