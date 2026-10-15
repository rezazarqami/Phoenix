package ir.marketsignalist.phoenix;
import android.app.Application;
import com.google.firebase.*;
public final class OrbitApplication extends Application {
    @Override public void onCreate(){super.onCreate();OrbitNotifications.channels(this);
        if(!BuildConfig.FIREBASE_APP_ID.isEmpty()&&!BuildConfig.FIREBASE_API_KEY.isEmpty()&&!BuildConfig.FIREBASE_PROJECT_ID.isEmpty()&&!BuildConfig.FIREBASE_SENDER_ID.isEmpty()){
            FirebaseOptions options=new FirebaseOptions.Builder().setApplicationId(BuildConfig.FIREBASE_APP_ID).setApiKey(BuildConfig.FIREBASE_API_KEY).setProjectId(BuildConfig.FIREBASE_PROJECT_ID).setGcmSenderId(BuildConfig.FIREBASE_SENDER_ID).build();
            FirebaseApp.initializeApp(this,options);
        }
    }
}
