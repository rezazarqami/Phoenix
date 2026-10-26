package ir.marketsignalist.phoenix;
import com.google.firebase.messaging.*;
import java.util.*;
public final class PhoenixMessagingService extends FirebaseMessagingService {
    @Override public void onNewToken(String token){
        getSharedPreferences("orbit-device",MODE_PRIVATE).edit().putString("push-token",token).apply();
        if(getSharedPreferences("orbit-device",MODE_PRIVATE).getBoolean("push-enabled",false))PushRegistrationWorker.enqueue(this);
    }
    @Override public void onMessageReceived(RemoteMessage message){
        if(!getSharedPreferences("orbit-device",MODE_PRIVATE).getBoolean("push-enabled",false))return;
        String kind=message.getData().get("kind");if(!"signal".equals(kind)&&!"test".equals(kind))return;
        String id=message.getData().get("eventId");if(id==null||!id.matches("[A-Za-z0-9:_-]{1,120}"))return;
        if(!PhoenixNotifications.channelAllowed(this,PhoenixNotifications.SIGNALS))return;
        android.content.SharedPreferences seen=getSharedPreferences("orbit-push-seen",MODE_PRIVATE);
        // A stable server event id suppresses retries even after the user dismisses the original.
        synchronized(PhoenixMessagingService.class){
            if(seen.contains(id))return;
            android.content.SharedPreferences.Editor editor=seen.edit();long now=System.currentTimeMillis();
            java.util.List<java.util.Map.Entry<String,?>> entries=new ArrayList<>(seen.getAll().entrySet());
            entries.sort(java.util.Comparator.comparingLong(entry->entry.getValue() instanceof Long?(Long)entry.getValue():0));
            for(int i=0;i<entries.size();i++)if(i<entries.size()-255||!(entries.get(i).getValue() instanceof Long)||(Long)entries.get(i).getValue()<now-86400000L)editor.remove(entries.get(i).getKey());
            PhoenixNotifications.show(this,PhoenixNotifications.SIGNALS,id,"/phoenix",message.getData().get("eventKind"));
            editor.putLong(id,now).commit();
        }
    }
}
