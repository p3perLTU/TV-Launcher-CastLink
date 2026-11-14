package lt.tvcast.common;

import static lt.tvcast.testing.Assert.*;

import java.security.KeyPair;

/** The encrypted link: the same keys on both sides, sealed packets, tampering, replay. */
final class SecureLinkTest {
    // fixed keys, nonces and results: tests/cs/Tests.cs checks the PC's SecureLink.cs against the same
    static final String CS_D = "9f26144dc9b4dfcaa1b200317802e2f2df1489e229a7833ccfbf32a4176c8261";
    static final String CS_P = "0407fad123bbb8703eb1f58fe49ab0fdb034ce8eb7bb04a0ca6c9ed476299c98ceb09584ebd8d64d91f2751cdb45175a2b5e2bdac3d2254738b9754156ac448192";
    static final String CE_D = "d60cb8efff853209590f197e676d557aca0c03bbb5c2b870d4644503912a68b2";
    static final String CE_P = "0412b4821b12940bc8e49a3cf9bc7b172009dc4acf29cc6fa950ac6386e0c1bee603ecc09c51a594796617dbe19d52eec973162e957d54269a0ddaa9f61774995b";
    static final String RS_D = "bb940d30f42070930ca28c30ce22915e088ced671013b5c31eabb2e3e240e243";
    static final String RS_P = "047351cec262be63c1ad19777b7220c3ab8ca0502d935eba90a8705fa952278f829c826fbeeb3f74ee1c65479fd14dab33e983c61e0ff9d401a9078e0288a9774c";
    static final String RE_D = "0507654ec1f2617ea46ebe02b882370f6b5ee3ea83feb1862176cd8ffb533975";
    static final String RE_P = "042ea76c9200d27811b79427e038048c231c1a221baec3a54a19f0bd4d8833b38bcf3d481cf44046e4257c6d0dd5c47c8beb2e52eb29d812e4d5610c7fe2ffeacc";
    static final int CODE = 982123;
    static final String C2R0 = "160000000000000000c508a0bb51cae79c1dbcd6dcfa9b1d386d3f57513a49f3d6";
    static final String R2C0 = "1600000000000000006cf7ce25eb5050ef0243bdac9a8c0e6838646306acf9b213";

    static String full(byte[] b) { StringBuilder s = new StringBuilder(); for (byte x : b) s.append(String.format("%02x", x & 0xFF)); return s.toString(); }

    static KeyPair kp(String d, String p) throws Exception { return SecureLink.fromScalar(unhex(d), unhex(p)); }

    static byte[] casterNonce() { byte[] n = new byte[16]; for (int i = 0; i < 16; i++) n[i] = (byte) (i + 1); return n; }
    static byte[] receiverNonce() { byte[] n = new byte[16]; for (int i = 0; i < 16; i++) n[i] = (byte) (0xA0 + i); return n; }

    static SecureLink.Session side(boolean caster, String casterStaticP) throws Exception {
        return SecureLink.derive(caster, caster ? kp(CS_D, CS_P) : kp(RS_D, RS_P), caster ? kp(CE_D, CE_P) : kp(RE_D, RE_P),
                casterNonce(), receiverNonce(), unhex(CE_P), unhex(RE_P), unhex(casterStaticP), unhex(RS_P));
    }

    static void testBothSidesGetTheSameCodeAndTheGoldenPackets() throws Exception {
        SecureLink.Session c = side(true, CS_P), r = side(false, CS_P);
        assertEquals(CODE, c.code, "caster's code");
        assertEquals(CODE, r.code, "receiver's code");
        assertEquals("982 123", c.codeText(), "as shown");
        byte[] msg = "CastLink".getBytes("US-ASCII");
        assertEquals(C2R0, full(c.seal(msg, 0, msg.length)), "caster -> receiver, packet 0 (same as the PC's)");
        assertEquals(R2C0, full(r.seal(msg, 0, msg.length)), "receiver -> caster, packet 0 (same as the PC's)");
    }

    static void testSealedPacketsOpenOnTheOtherSideOnly() throws Exception {
        SecureLink.Session c = side(true, CS_P), r = side(false, CS_P);
        byte[] msg = { Proto.CONFIG, 1, 2, 3, 4, 5 };
        byte[] s = c.seal(msg, 0, msg.length);
        byte[] out = new byte[64];
        assertEquals(msg.length, r.open(s, s.length, out), "the receiver opens it");
        assertEquals(Proto.CONFIG, out[0], "inner type");
        assertEquals(-1, c.open(s, s.length, out), "the caster's own packet is not one it can open (other key)");
    }

    static void testChangedPacketIsRefused() throws Exception {
        SecureLink.Session c = side(true, CS_P), r = side(false, CS_P);
        byte[] msg = new byte[100];
        byte[] s = c.seal(msg, 0, msg.length);
        byte[] out = new byte[200];
        s[20] ^= 1;
        assertEquals(-1, r.open(s, s.length, out), "one flipped bit in the picture data");
        s[20] ^= 1;
        s[3] ^= 1;
        assertEquals(-1, r.open(s, s.length, out), "a changed counter (associated data)");
    }

    static void testReplayIsRefusedButReorderingIsFine() throws Exception {
        SecureLink.Session c = side(true, CS_P), r = side(false, CS_P);
        byte[] msg = new byte[10], out = new byte[64];
        byte[][] p = new byte[5][];
        for (int i = 0; i < 5; i++) p[i] = c.seal(msg, 0, msg.length);
        assertEquals(10, r.open(p[2], p[2].length, out), "2 first");
        assertEquals(10, r.open(p[0], p[0].length, out), "0 late is fine");
        assertEquals(-1, r.open(p[2], p[2].length, out), "2 again: replay");
        assertEquals(-1, r.open(p[0], p[0].length, out), "0 again: replay");
        assertEquals(10, r.open(p[4], p[4].length, out), "4");
        assertEquals(10, r.open(p[1], p[1].length, out), "1 late");
    }

    static void testVeryOldPacketIsRefused() throws Exception {
        SecureLink.Session c = side(true, CS_P), r = side(false, CS_P);
        byte[] msg = new byte[4], out = new byte[64];
        byte[] first = c.seal(msg, 0, msg.length);
        byte[] last = null;
        for (int i = 0; i < 70; i++) last = c.seal(msg, 0, msg.length);
        assertEquals(4, r.open(last, last.length, out), "the newest");
        assertEquals(-1, r.open(first, first.length, out), "70 packets back: outside the window");
    }

    static void testSomeoneElsesStaticKeyGivesOtherKeys() throws Exception {
        // a receiver that believes the caster's static key is another one (someone in the middle)
        KeyPair other = SecureLink.newKeyPair();
        SecureLink.Session c = side(true, CS_P);
        SecureLink.Session r = side(false, full(SecureLink.encodePoint(other.getPublic())));
        byte[] msg = new byte[8], out = new byte[64];
        byte[] s = c.seal(msg, 0, msg.length);
        assertEquals(-1, r.open(s, s.length, out), "cannot open");
    }

    static void testHelloRoundTrip() throws Exception {
        byte[] n = casterNonce();
        byte[] b = SecureLink.hello(n, unhex(CE_P), unhex(CS_P), "Mano PC");
        SecureLink.Hello h = SecureLink.parse(b, b.length);
        assertNotNull(h, "parsed");
        assertArrayEquals(n, h.nonce, "nonce");
        assertArrayEquals(unhex(CE_P), h.ephemeral, "ephemeral");
        assertArrayEquals(unhex(CS_P), h.statik, "static");
        assertEquals("Mano PC", h.name, "name");
        byte[] ok = SecureLink.helloOk(receiverNonce(), unhex(RE_P), unhex(RS_P), n, true, "TV");
        SecureLink.Hello o = SecureLink.parse(ok, ok.length);
        assertArrayEquals(n, o.echo, "echo");
        assertTrue(o.knowsYou, "knows you");
        assertEquals("TV", o.name, "name");
    }

    static void testMalformedHelloIsRefused() throws Exception {
        byte[] b = SecureLink.hello(casterNonce(), unhex(CE_P), unhex(CS_P), "");
        assertNull(SecureLink.parse(b, b.length - 1), "cut short");
        b[1] = 9;
        assertNull(SecureLink.parse(b, b.length), "other version");
        assertNull(SecureLink.parse(new byte[] { Proto.CONFIG, 1 }, 2), "not a hello");
        byte[] notOnCurve = unhex(CE_P);
        notOnCurve[64] ^= 1;
        boolean refused = false;
        try { SecureLink.decodePoint(notOnCurve); } catch (java.security.GeneralSecurityException e) { refused = true; }
        assertTrue(refused, "a point that is not on the curve");
    }
}
