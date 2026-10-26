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
        PhoenixAlertDelivery.deliver(this,id,message.getData().get("eventKind"),message.getData().get("symbol"),message.getData().get("exitKind"),0);
    }
}
