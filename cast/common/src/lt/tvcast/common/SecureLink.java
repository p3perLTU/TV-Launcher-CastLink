package lt.tvcast.common;

import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.security.AlgorithmParameters;
import java.security.GeneralSecurityException;
import java.security.KeyFactory;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.MessageDigest;
import java.security.PrivateKey;
import java.security.PublicKey;
import java.security.SecureRandom;
import java.security.interfaces.ECPublicKey;
import java.security.spec.ECGenParameterSpec;
import java.security.spec.ECParameterSpec;
import java.security.spec.ECPoint;
import java.security.spec.ECPublicKeySpec;
import java.security.spec.PKCS8EncodedKeySpec;
import java.util.Arrays;

import javax.crypto.Cipher;
import javax.crypto.KeyAgreement;
import javax.crypto.Mac;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * The encrypted link between the device that casts (the PC or a phone, "caster") and the one
 * that shows it (the TV or a phone, "receiver"). Plain Java (JCE only), unit tested; the PC has
 * the same in cast/pc/SecureLink.cs (golden values in both test suites keep them equal).
 *
 * Handshake, in the clear (the only unencrypted packets besides WHO / NAME discovery):
 *   HELLO     type | version | casterNonce 16 | casterEphemeral 65 | casterStatic 65 | UTF-8 name
 *             caster -> receiver, every second until answered
 *   HELLO_OK  type | version | receiverNonce 16 | receiverEphemeral 65 | receiverStatic 65
 *             | casterNonce 16 (echo) | knowsYou byte | UTF-8 name           receiver -> caster
 * Keys are P-256 points, uncompressed (04 | X | Y). Each side keeps one long-term "static" key
 * pair (its identity) and makes a new "ephemeral" one for every handshake.
 *
 * Keys: ee = SHA-256(ECDH(ephemerals)), ss = SHA-256(ECDH(statics)) (what .NET's CNG
 * DeriveKeyMaterial gives with KDF Hash / SHA-256). salt = SHA-256("CastLink v1" | casterNonce |
 * receiverNonce | casterEph | receiverEph | casterStatic | receiverStatic); prk = HMAC(salt, ee |
 * ss); then HKDF-expand(prk, "keys") = caster->receiver key 16 | receiver->caster key 16 |
 * caster salt 4 | receiver salt 4, and HKDF-expand(prk, "code") gives the 6-digit pairing code.
 * The ephemeral part keeps old recordings safe even if a static key leaks later; the static part
 * means only the device whose key was confirmed once can take part (no one in the middle).
 *
 * After it every packet is
 *   SEALED    type | counter 8 | AES-128-GCM(inner packet) | tag 16
 * with nonce = sender's salt 4 | counter 8, the first 9 bytes as associated data, a fresh counter
 * per packet and a 64-packet replay window on the receiving side. Inside are the old packet
 * types (CONFIG, VIDEO, ... see Proto) plus PAIR_SHOW.
 *
 * Pairing ("once, then remember", the user's choice): when either side does not know the other's
 * static key yet, both show the 6-digit code; the user compares them and confirms on the caster.
 * Only then does the caster send CONFIG (with CONFIG_PAIRED), and the receiver keeps the caster's
 * key. Someone in the middle would get a different code on each screen.
 */
public final class SecureLink {
    public static final byte HELLO = 20, HELLO_OK = 21, SEALED = 22;
    /** inner type: please show the pairing code (caster -> receiver while the user compares) */
    public static final byte PAIR_SHOW = 24;
    public static final int VERSION = 1;
    public static final int NONCE = 16, POINT = 65, TAG = 16, OVERHEAD = 1 + 8 + TAG;
    /** CONFIG flag: the user confirmed this receiver on the caster (it may keep the caster's key) */
    public static final int CONFIG_PAIRED = 2;
    static final int HELLO_FIXED = 2 + NONCE + POINT + POINT;
    static final int HELLO_OK_FIXED = 2 + NONCE + POINT + POINT + NONCE + 1;

    private static final SecureRandom RANDOM = new SecureRandom();

    // ---- keys ----------------------------------------------------------------------------

    public static KeyPair newKeyPair() {
        try {
            KeyPairGenerator g = KeyPairGenerator.getInstance("EC");
            g.initialize(new ECGenParameterSpec("secp256r1"), RANDOM);
            return g.generateKeyPair();
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException(e);
        }
    }

    /** a saved key pair: PKCS#8 private key + the 65-byte public point */
    public static KeyPair loadKeyPair(byte[] pkcs8, byte[] point) throws GeneralSecurityException {
        PrivateKey priv = KeyFactory.getInstance("EC").generatePrivate(new PKCS8EncodedKeySpec(pkcs8));
        return new KeyPair(decodePoint(point), priv);
    }

    /** a key pair from a fixed private number (tests: the same keys as tests/cs) */
    static KeyPair fromScalar(byte[] d, byte[] point) throws GeneralSecurityException {
        PrivateKey priv = KeyFactory.getInstance("EC").generatePrivate(
                new java.security.spec.ECPrivateKeySpec(new BigInteger(1, d), p256()));
        return new KeyPair(decodePoint(point), priv);
    }

    public static byte[] encodePoint(PublicKey k) {
        ECPoint w = ((ECPublicKey) k).getW();
        byte[] out = new byte[POINT];
        out[0] = 4;
        put32(out, 1, w.getAffineX());
        put32(out, 33, w.getAffineY());
        return out;
    }

    private static void put32(byte[] out, int o, BigInteger v) {
        byte[] b = v.toByteArray(); // big-endian, maybe with a leading 0 or shorter
        int n = Math.min(b.length, 32);
        System.arraycopy(b, b.length - n, out, o + 32 - n, n);
    }

    private static ECParameterSpec p256;

    private static synchronized ECParameterSpec p256() throws GeneralSecurityException {
        if (p256 == null) {
            AlgorithmParameters a = AlgorithmParameters.getInstance("EC");
            a.init(new ECGenParameterSpec("secp256r1"));
            p256 = a.getParameterSpec(ECParameterSpec.class);
        }
        return p256;
    }

    /** a 65-byte point as a key; GeneralSecurityException if it is not a point of the curve */
    public static PublicKey decodePoint(byte[] p) throws GeneralSecurityException {
        if (p == null || p.length != POINT || p[0] != 4) throw new GeneralSecurityException("bad point");
        BigInteger x = new BigInteger(1, Arrays.copyOfRange(p, 1, 33));
        BigInteger y = new BigInteger(1, Arrays.copyOfRange(p, 33, 65));
        // KeyFactory does not check this: a point off the curve could leak the static key bit by
        // bit ("invalid curve" attack), so y^2 = x^3 + ax + b (mod p) is checked here
        ECParameterSpec c = p256();
        BigInteger q = ((java.security.spec.ECFieldFp) c.getCurve().getField()).getP();
        if (x.compareTo(q) >= 0 || y.compareTo(q) >= 0) throw new GeneralSecurityException("bad point");
        BigInteger right = x.pow(3).add(c.getCurve().getA().multiply(x)).add(c.getCurve().getB()).mod(q);
        if (!y.multiply(y).mod(q).equals(right)) throw new GeneralSecurityException("not on the curve");
        return KeyFactory.getInstance("EC").generatePublic(new ECPublicKeySpec(new ECPoint(x, y), c));
    }

    /** SHA-256 of the ECDH shared x coordinate (the same as CNG's DeriveKeyMaterial with SHA-256) */
    static byte[] agree(PrivateKey mine, byte[] theirPoint) throws GeneralSecurityException {
        KeyAgreement ka = KeyAgreement.getInstance("ECDH");
        ka.init(mine);
        ka.doPhase(decodePoint(theirPoint), true);
        byte[] z = ka.generateSecret();
        byte[] h = MessageDigest.getInstance("SHA-256").digest(z);
        Arrays.fill(z, (byte) 0);
        return h;
    }

    public static byte[] randomNonce() {
        byte[] n = new byte[NONCE];
        RANDOM.nextBytes(n);
        return n;
    }

    /** a short readable form of a static key, for logs and the "known devices" list */
    public static String fingerprint(byte[] point) {
        try {
            byte[] h = MessageDigest.getInstance("SHA-256").digest(point);
            StringBuilder s = new StringBuilder();
            for (int i = 0; i < 6; i++) s.append(String.format("%02x", h[i] & 0xFF));
            return s.toString();
        } catch (GeneralSecurityException e) {
            return "?";
        }
    }

    // ---- handshake messages --------------------------------------------------------------

    public static final class Hello {
        public byte[] nonce, ephemeral, statik, echo;
        public boolean knowsYou;
        public String name = "";
    }

    public static byte[] hello(byte[] nonce, byte[] eph, byte[] stat, String name) {
        byte[] nm = utf8(name, 64);
        byte[] b = new byte[HELLO_FIXED + nm.length];
        b[0] = HELLO;
        b[1] = VERSION;
        System.arraycopy(nonce, 0, b, 2, NONCE);
        System.arraycopy(eph, 0, b, 2 + NONCE, POINT);
        System.arraycopy(stat, 0, b, 2 + NONCE + POINT, POINT);
        System.arraycopy(nm, 0, b, HELLO_FIXED, nm.length);
        return b;
    }

    public static byte[] helloOk(byte[] nonce, byte[] eph, byte[] stat, byte[] echo, boolean knowsYou, String name) {
        byte[] nm = utf8(name, 64);
        byte[] b = new byte[HELLO_OK_FIXED + nm.length];
        b[0] = HELLO_OK;
        b[1] = VERSION;
        System.arraycopy(nonce, 0, b, 2, NONCE);
        System.arraycopy(eph, 0, b, 2 + NONCE, POINT);
        System.arraycopy(stat, 0, b, 2 + NONCE + POINT, POINT);
        System.arraycopy(echo, 0, b, 2 + NONCE + 2 * POINT, NONCE);
        b[HELLO_OK_FIXED - 1] = (byte) (knowsYou ? 1 : 0);
        System.arraycopy(nm, 0, b, HELLO_OK_FIXED, nm.length);
        return b;
    }

    /** a HELLO or HELLO_OK of this version, or null */
    public static Hello parse(byte[] b, int n) {
        if (b == null || n < 2 || b[1] != VERSION) return null;
        boolean ok = b[0] == HELLO_OK;
        if (b[0] != HELLO && !ok) return null;
        int fixed = ok ? HELLO_OK_FIXED : HELLO_FIXED;
        if (n < fixed || n > fixed + 64) return null;
        Hello h = new Hello();
        h.nonce = Arrays.copyOfRange(b, 2, 2 + NONCE);
        h.ephemeral = Arrays.copyOfRange(b, 2 + NONCE, 2 + NONCE + POINT);
        h.statik = Arrays.copyOfRange(b, 2 + NONCE + POINT, 2 + NONCE + 2 * POINT);
        if (ok) {
            h.echo = Arrays.copyOfRange(b, 2 + NONCE + 2 * POINT, 2 + 2 * NONCE + 2 * POINT);
            h.knowsYou = b[fixed - 1] == 1;
        }
        h.name = new String(b, fixed, n - fixed, StandardCharsets.UTF_8);
        return h;
    }

    private static byte[] utf8(String s, int max) {
        byte[] b = (s == null ? "" : s).getBytes(StandardCharsets.UTF_8);
        return b.length <= max ? b : Arrays.copyOf(b, max);
    }

    // ---- the session ---------------------------------------------------------------------

    /**
     * Keys for one cast. {@code caster} says which side this is; nonces and points are given as
     * caster's / receiver's. GeneralSecurityException if a point is not on the curve.
     */
    public static Session derive(boolean caster, KeyPair myStatic, KeyPair myEphemeral,
                                 byte[] casterNonce, byte[] receiverNonce,
                                 byte[] casterEph, byte[] receiverEph,
                                 byte[] casterStatic, byte[] receiverStatic) throws GeneralSecurityException {
        byte[] ee = agree(myEphemeral.getPrivate(), caster ? receiverEph : casterEph);
        byte[] ss = agree(myStatic.getPrivate(), caster ? receiverStatic : casterStatic);
        MessageDigest d = MessageDigest.getInstance("SHA-256");
        d.update("CastLink v1".getBytes(StandardCharsets.US_ASCII));
        d.update(casterNonce);
        d.update(receiverNonce);
        d.update(casterEph);
        d.update(receiverEph);
        d.update(casterStatic);
        d.update(receiverStatic);
        byte[] salt = d.digest();
        byte[] ikm = new byte[64];
        System.arraycopy(ee, 0, ikm, 0, 32);
        System.arraycopy(ss, 0, ikm, 32, 32);
        byte[] prk = hmac(salt, ikm);
        Arrays.fill(ikm, (byte) 0);
        byte[] keys = expand(prk, "keys", 40);
        byte[] code = expand(prk, "code", 4);
        Arrays.fill(prk, (byte) 0);
        int c = (int) ((Proto.getInt(code, 0) & 0xFFFFFFFFL) % 1_000_000L);
        byte[] c2r = Arrays.copyOfRange(keys, 0, 16), r2c = Arrays.copyOfRange(keys, 16, 32);
        byte[] cs = Arrays.copyOfRange(keys, 32, 36), rs = Arrays.copyOfRange(keys, 36, 40);
        Arrays.fill(keys, (byte) 0);
        return caster ? new Session(c2r, cs, r2c, rs, c) : new Session(r2c, rs, c2r, cs, c);
    }

    static byte[] hmac(byte[] key, byte[] data) throws GeneralSecurityException {
        Mac m = Mac.getInstance("HmacSHA256");
        m.init(new SecretKeySpec(key, "HmacSHA256"));
        return m.doFinal(data);
    }

    /** HKDF-Expand (RFC 5869) with an ASCII label as info */
    static byte[] expand(byte[] prk, String info, int len) throws GeneralSecurityException {
        Mac m = Mac.getInstance("HmacSHA256");
        m.init(new SecretKeySpec(prk, "HmacSHA256"));
        byte[] out = new byte[len], t = new byte[0];
        byte[] inf = info.getBytes(StandardCharsets.US_ASCII);
        int done = 0;
        for (int i = 1; done < len; i++) {
            m.update(t);
            m.update(inf);
            m.update((byte) i);
            t = m.doFinal();
            int k = Math.min(t.length, len - done);
            System.arraycopy(t, 0, out, done, k);
            done += k;
        }
        return out;
    }

    public static final class Session {
        private final SecretKeySpec sendKey, recvKey;
        private final byte[] sendSalt, recvSalt;
        private final Cipher enc, dec;
        /** the 6-digit pairing code, the same on both sides */
        public final int code;
        private long sendCounter;
        private long highest = -1, window; // replay window: bit i = highest - i seen

        Session(byte[] sk, byte[] ss, byte[] rk, byte[] rs, int code) throws GeneralSecurityException {
            sendKey = new SecretKeySpec(sk, "AES");
            recvKey = new SecretKeySpec(rk, "AES");
            sendSalt = ss;
            recvSalt = rs;
            this.code = code;
            enc = Cipher.getInstance("AES/GCM/NoPadding");
            dec = Cipher.getInstance("AES/GCM/NoPadding");
        }

        /** "123 456" */
        public String codeText() {
            String s = String.format("%06d", code);
            return s.substring(0, 3) + " " + s.substring(3);
        }

        /** the inner packet in[off..off+len) sealed into out (len + OVERHEAD bytes); returns its length */
        public synchronized int seal(byte[] in, int off, int len, byte[] out) {
            try {
                long ctr = sendCounter++;
                out[0] = SEALED;
                Proto.putLong(out, 1, ctr);
                enc.init(Cipher.ENCRYPT_MODE, sendKey, new GCMParameterSpec(TAG * 8, nonce(sendSalt, ctr)));
                enc.updateAAD(out, 0, 9);
                return 9 + enc.doFinal(in, off, len, out, 9);
            } catch (GeneralSecurityException e) {
                throw new IllegalStateException(e);
            }
        }

        public byte[] seal(byte[] in, int off, int len) {
            byte[] out = new byte[len + OVERHEAD];
            seal(in, off, len, out);
            return out;
        }

        /**
         * A SEALED packet opened into out (needs n - OVERHEAD bytes); returns the inner length, or
         * -1 if it is not ours, was changed on the way, or was seen before.
         */
        public synchronized int open(byte[] b, int n, byte[] out) {
            if (n < OVERHEAD || b[0] != SEALED) return -1;
            long ctr = Proto.getLong(b, 1);
            if (ctr < 0 || seen(ctr)) return -1;
            try {
                dec.init(Cipher.DECRYPT_MODE, recvKey, new GCMParameterSpec(TAG * 8, nonce(recvSalt, ctr)));
                dec.updateAAD(b, 0, 9);
                int len = dec.doFinal(b, 9, n - 9, out, 0);
                mark(ctr);
                return len;
            } catch (GeneralSecurityException e) {
                return -1;
            }
        }

        private boolean seen(long ctr) {
            if (highest < 0 || ctr > highest) return false;
            long back = highest - ctr;
            return back >= 64 || (window & (1L << back)) != 0;
        }

        private void mark(long ctr) {
            if (ctr > highest) {
                long shift = highest < 0 ? 64 : ctr - highest;
                window = shift >= 64 ? 1L : (window << shift) | 1L;
                highest = ctr;
            } else {
                window |= 1L << (highest - ctr);
            }
        }

        private static byte[] nonce(byte[] salt, long ctr) {
            byte[] n = new byte[12];
            System.arraycopy(salt, 0, n, 0, 4);
            Proto.putLong(n, 4, ctr);
            return n;
        }
    }
}
