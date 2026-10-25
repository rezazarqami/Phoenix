package ir.marketsignalist.phoenix;
import android.content.*;
public final class ReminderReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context c,Intent intent) {try{Reminders.deliver(c,intent.getStringExtra("id"),intent.getBooleanExtra("snooze",false));}catch(Exception ignored){/* Fail closed; never expose a private reminder in an error. */}}
}
