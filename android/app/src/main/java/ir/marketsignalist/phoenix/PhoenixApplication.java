package ir.marketsignalist.phoenix;
import android.app.Application;
public final class PhoenixApplication extends Application {
    @Override public void onCreate(){super.onCreate();PhoenixNotifications.channels(this);PhoenixFirebase.initialize(this);}
}
