package ir.marketsignalist.phoenix;
import java.net.URI;
final class TrustedNavigation {
    static boolean trusted(String value) {
        try {URI u=new URI(value);return "https".equalsIgnoreCase(u.getScheme())&&"marketsignalist.ir".equalsIgnoreCase(u.getHost())&&(u.getPort()==-1||u.getPort()==443)&&u.getRawUserInfo()==null;}
        catch(Exception e){return false;}
    }
    static boolean phoenixPage(String value) {
        if(!trusted(value))return false;
        String path=URI.create(value).getPath();
        return "/notifications".equals(path)||"/notifications.html".equals(path)||"/phoenix".equals(path)||"/phoenix.html".equals(path)||"/login".equals(path)||"/login.html".equals(path)||
            "/analysis".equals(path)||"/analysis/signals".equals(path)||"/analysis/coins".equals(path)||
            path.startsWith("/api/analysis/");
    }
    static String route(String value){
        if("/phoenix".equals(value)||"/notifications".equals(value))return value;
        if(value!=null&&value.startsWith("/notifications?eventId="))try{
            String id=java.net.URLDecoder.decode(value.substring("/notifications?eventId=".length()),"UTF-8");
            if(id.matches("[A-Za-z0-9:_-]{1,120}"))return "/notifications?eventId="+java.net.URLEncoder.encode(id,"UTF-8");
        }catch(Exception ignored){}
        return "/";
    }
}
