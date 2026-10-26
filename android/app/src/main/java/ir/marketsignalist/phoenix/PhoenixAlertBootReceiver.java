package ir.marketsignalist.phoenix;
import android.content.*;
public final class PhoenixAlertBootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context,Intent intent){
        String action=intent.getAction();if(!Intent.ACTION_BOOT_COMPLETED.equals(action)&&!Intent.ACTION_MY_PACKAGE_REPLACED.equals(action))return;
        if(PhoenixAlertService.prefs(context).getBoolean("enabled",false))try{PhoenixAlertService.start(context);}catch(Exception e){PhoenixAlertService.prefs(context).edit().putBoolean("running",false).putString("state","برای ادامهٔ اعلان زنده فونیکس را باز کن").apply();}
    }
}
