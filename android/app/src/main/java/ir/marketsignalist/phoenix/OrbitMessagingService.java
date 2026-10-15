package ir.marketsignalist.phoenix;
import com.google.firebase.messaging.*;
public final class OrbitMessagingService extends FirebaseMessagingService {
    @Override public void onNewToken(String token){getSharedPreferences("orbit-device",MODE_PRIVATE).edit().putString("push-token",token).apply();}
    @Override public void onMessageReceived(RemoteMessage message){
        if(!getSharedPreferences("orbit-device",MODE_PRIVATE).getBoolean("push-enabled",false))return;
        String id=message.getData().get("eventId");if(id==null||id.length()>100)id=message.getMessageId();if(id==null)id=java.util.UUID.randomUUID().toString();
        OrbitNotifications.show(this,OrbitNotifications.SIGNALS,id,"signal".equals(message.getData().get("kind"))?"/phoenix":"/");
    }
}
