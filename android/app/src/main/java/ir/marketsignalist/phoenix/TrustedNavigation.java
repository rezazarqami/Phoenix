package ir.marketsignalist.phoenix;
import java.net.URI;
final class TrustedNavigation {
    static boolean trusted(String value) {
        try {URI u=new URI(value);return "https".equalsIgnoreCase(u.getScheme())&&("marketsignalist.ir".equalsIgnoreCase(u.getHost())||"appassets.androidplatform.net".equalsIgnoreCase(u.getHost()))&&(u.getPort()==-1||u.getPort()==443)&&u.getRawUserInfo()==null;}
        catch(Exception e){return false;}
    }
    static String route(String value){return "/phoenix".equals(value)?"/phoenix":"/";}
}
