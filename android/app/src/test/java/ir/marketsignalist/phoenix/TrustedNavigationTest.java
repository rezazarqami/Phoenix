package ir.marketsignalist.phoenix;
import org.junit.Test;
import static org.junit.Assert.*;
public class TrustedNavigationTest {
    @Test public void restrictBridgeToTheExactHttpsOrigin(){
        assertTrue(TrustedNavigation.trusted("https://marketsignalist.ir/"));
        assertTrue(TrustedNavigation.trusted("https://marketsignalist.ir:443/phoenix"));
        for(String bad:new String[]{"http://marketsignalist.ir/","https://marketsignalist.ir.evil.example/","https://evil.example@marketsignalist.ir/","https://marketsignalist.ir:444/","file:///data/local/file","javascript:alert(1)","https://marketsignalist.ir%2eevil.example/","https://evil.example/"})assertFalse(bad,TrustedNavigation.trusted(bad));
    }
    @Test public void notificationRoutesCannotOpenArbitraryWebPages(){assertEquals("/phoenix",TrustedNavigation.route("/phoenix"));assertEquals("/",TrustedNavigation.route("https://evil.example"));assertEquals("/",TrustedNavigation.route("//evil.example"));assertEquals("/",TrustedNavigation.route(null));}
}
