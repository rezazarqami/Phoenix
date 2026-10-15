package ir.marketsignalist.phoenix;
import android.app.Application;
public final class OrbitApplication extends Application {
    @Override public void onCreate(){super.onCreate();OrbitNotifications.channels(this);OrbitFirebase.initialize(this);}
}
