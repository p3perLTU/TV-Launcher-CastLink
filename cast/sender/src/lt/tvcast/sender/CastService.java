package lt.tvcast.sender;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.graphics.PixelFormat;
import android.graphics.drawable.Icon;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.Process;
import android.os.SystemClock;
import android.provider.Settings;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.Display;
import android.view.Gravity;
import android.view.Surface;
import android.view.View;
import android.view.WindowManager;

import lt.tvcast.common.Proto;
import lt.tvcast.common.SecureLink;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;

/**
 * Mirrors the phone screen to the TV: MediaProjection -> VirtualDisplay -> H.264 encoder
 * surface -> UDP, plus the sound of other apps (AudioPlaybackCapture) as raw 5 ms PCM packets.
 * The TV asks for a keyframe (KEYREQ) whenever it lost a packet; the encoder never waits.
 */
public class CastService extends Service {
    private static final String TAG = "phonecast";
    static final String EXTRA_CODE = "code";
    static final String EXTRA_DATA = "data";
    static final String EXTRA_HOST = "host";
    static final String EXTRA_FPS = "fps";
    static final String ACTION_STOP = "lt.tvcast.sender.STOP";

    private static final String MIME = MediaFormat.MIMETYPE_VIDEO_AVC;
    /** The TV shows 60 Hz; 120 only makes the frame it picks at each refresh ~4 ms fresher. */
    private static final int MAX_W = 1920, MAX_H = 1080;
    /** per-frame bits shrink at 120 fps, so not quite double */
    private static final int BITRATE_60 = 16_000_000, BITRATE_120 = 28_000_000;
    private int fps = 60;
    private static final long KEY_EVERY_MS = 150;

    /** read by MainActivity */
    static volatile boolean running;
    /** the pairing code while the user should compare it with the TV's screen; the answer:
     *  0 none yet, 1 the codes match, -1 they do not (the cast stops) */
    static volatile String pairCode, pairPeer = "";
    static volatile int pairAnswer;
    /** an old TV app that ignores the encrypted handshake */
    static volatile boolean oldReceiver;
    static volatile String host;
    static volatile boolean noAudio;
    static volatile int runningFps;

    private final Handler main = new Handler(Looper.getMainLooper());
    private DisplayManager dm;
    private MediaProjection projection;
    private VirtualDisplay display;
    private DatagramSocket socket;
    private WifiManager.WifiLock wifiLock;
    /**
     * Android stops the projection when the screen turns off. A screen wake lock does not help
     * (Android 14+ disables it for an app without a visible window), so an invisible 1x1
     * overlay with FLAG_KEEP_SCREEN_ON keeps it on; needs "Display over other apps".
     */
    private View keepAwake;
    private volatile boolean on;
    private volatile Encoder enc;
    private volatile int vw, vh;
    private volatile boolean configDirty;
    private int dpi;
    private long lastKey;
    private Thread audioThread, controlThread;
    /** frame numbering survives encoder restarts (rotation) so the TV sees no gap */
    private int frameId;
    private volatile byte[] csd;
    // the encrypted link (SecureLink): this phone's static key is the one its receiver uses too
    private java.security.KeyPair identity;
    private byte[] identityPoint;
    private volatile SecureLink.Session link;
    private volatile boolean linkReady;
    private java.security.KeyPair eph;
    private byte[] myNonce, helloPkt, peerStatic;
    private long helloSince, lastHello, lastSealedHeard, lastPairShow;

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null || ACTION_STOP.equals(intent.getAction())) {
            stopSelf();
            return START_NOT_STICKY;
        }
        if (on) return START_NOT_STICKY;
        String h = intent.getStringExtra(EXTRA_HOST);
        fps = intent.getIntExtra(EXTRA_FPS, 60) == 120 ? 120 : 60;
        runningFps = fps;
        startForeground(1, notification(h), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);

        MediaProjectionManager mpm = getSystemService(MediaProjectionManager.class);
        Intent data = intent.getParcelableExtra(EXTRA_DATA);
        projection = data != null ? mpm.getMediaProjection(intent.getIntExtra(EXTRA_CODE, 0), data) : null;
        if (projection == null || h == null || h.isEmpty()) {
            stopSelf();
            return START_NOT_STICKY;
        }
        // Android 14: the callback must be registered before the virtual display is created
        projection.registerCallback(new MediaProjection.Callback() {
            @Override
            public void onStop() {
                stopSelf();
            }
        }, main);

        on = true;
        running = true;
        host = h;
        pairCode = null;
        oldReceiver = false;
        loadIdentity();
        noAudio = false;
        dm = getSystemService(DisplayManager.class);
        WifiManager wm = getApplicationContext().getSystemService(WifiManager.class);
        wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, TAG);
        wifiLock.acquire();
        keepScreenOn();

        new Thread(() -> {
            try {
                DatagramSocket s = new DatagramSocket();
                s.setSendBufferSize(2 << 20);
                s.setSoTimeout(250);
                s.connect(InetAddress.getByName(h), Proto.PORT);
                socket = s;
            } catch (IOException e) {
                Log.e(TAG, "socket " + h, e);
                main.post(this::stopSelf);
                return;
            }
            main.post(this::startVideo);
        }, "connect").start();
        return START_NOT_STICKY;
    }

    private void keepScreenOn() {
        if (!Settings.canDrawOverlays(this)) {
            Log.w(TAG, "no overlay permission: the screen may turn off and stop the cast");
            return;
        }
        WindowManager.LayoutParams lp = new WindowManager.LayoutParams(1, 1,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
                        | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON,
                PixelFormat.TRANSLUCENT);
        lp.gravity = Gravity.TOP | Gravity.START;
        keepAwake = new View(this);
        try {
            getSystemService(WindowManager.class).addView(keepAwake, lp);
        } catch (RuntimeException e) {
            Log.w(TAG, "keep-awake overlay", e);
            keepAwake = null;
        }
    }

    private void startVideo() {
        if (!on) return;
        int[] sz = captureSize();
        enc = newEncoder(sz);
        if (enc == null) {
            stopSelf();
            return;
        }
        vw = sz[0];
        vh = sz[1];
        display = projection.createVirtualDisplay("tvcast", vw, vh, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, enc.surface, null, null);
        dm.registerDisplayListener(displayListener, main);

        audioThread = new Thread(this::audioLoop, "audio");
        audioThread.start();
        controlThread = new Thread(this::controlLoop, "control");
        controlThread.start();
    }

    /**
     * The phone screen scaled to fit 1920x1080, rounded to even numbers (2340x1080 -> 1920x886).
     * Rounding to 16 would change the aspect ratio and leave thin bars at the sides.
     */
    @SuppressWarnings("deprecation")
    private int[] captureSize() {
        DisplayMetrics m = new DisplayMetrics();
        dm.getDisplay(Display.DEFAULT_DISPLAY).getRealMetrics(m);
        dpi = m.densityDpi;
        float s = Math.min(1f, Math.min((float) MAX_W / m.widthPixels, (float) MAX_H / m.heightPixels));
        return new int[]{Math.round(m.widthPixels * s) & ~1, Math.round(m.heightPixels * s) & ~1};
    }

    /** an encoder for sz; if it refuses a size that is not a multiple of 16, sz is rounded down to one */
    private Encoder newEncoder(int[] sz) {
        try {
            return new Encoder(sz[0], sz[1]);
        } catch (IOException | RuntimeException e) {
            Log.w(TAG, "encoder " + sz[0] + "x" + sz[1], e);
        }
        if ((sz[0] & 15) == 0 && (sz[1] & 15) == 0) return null;
        sz[0] &= ~15;
        sz[1] &= ~15;
        try {
            return new Encoder(sz[0], sz[1]);
        } catch (IOException | RuntimeException e) {
            Log.e(TAG, "encoder " + sz[0] + "x" + sz[1], e);
            return null;
        }
    }

    /** rotation: a new encoder with the new size; refresh-rate changes are ignored */
    private final DisplayManager.DisplayListener displayListener = new DisplayManager.DisplayListener() {
        @Override
        public void onDisplayChanged(int id) {
            if (id != Display.DEFAULT_DISPLAY || !on || display == null) return;
            int[] sz = captureSize();
            if (sz[0] == vw && sz[1] == vh) return;
            display.setSurface(null);
            Encoder old = enc;
            enc = null;
            if (old != null) old.stop();
            enc = newEncoder(sz);
            if (enc == null) {
                stopSelf();
                return;
            }
            vw = sz[0];
            vh = sz[1];
            display.resize(vw, vh, dpi);
            display.setSurface(enc.surface);
            configDirty = true;
        }

        @Override public void onDisplayAdded(int id) {}
        @Override public void onDisplayRemoved(int id) {}
    };

    @Override
    public void onDestroy() {
        on = false;
        running = false;
        if (dm != null) dm.unregisterDisplayListener(displayListener);
        if (display != null) display.release();
        display = null;
        Encoder e = enc;
        enc = null;
        if (e != null) e.stop();
        MediaProjection p = projection;
        projection = null;
        if (p != null) p.stop();
        if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
        if (keepAwake != null) {
            getSystemService(WindowManager.class).removeView(keepAwake);
            keepAwake = null;
        }
        Thread a = audioThread, c = controlThread;
        DatagramSocket s = socket;
        new Thread(() -> {
            try {
                if (a != null) a.join(500);
                if (c != null) c.join(500);
            } catch (InterruptedException ignored) {}
            if (s == null) return;
            for (int i = 0; i < 3; i++) sendSealed(new byte[]{Proto.BYE}, 0, 1);
            s.close();
        }, "close").start();
        stopForeground(STOP_FOREGROUND_REMOVE);
        super.onDestroy();
    }

    /** a packet of the cast: sealed, and only once the TV is confirmed */
    private void send(DatagramPacket p) {
        if (!linkReady) return;
        sendSealed(p.getData(), p.getOffset(), p.getLength());
    }

    private void sendSealed(byte[] b, int off, int len) {
        SecureLink.Session l = link;
        if (l == null) return;
        sendRaw(l.seal(b, off, len));
    }

    private void sendRaw(byte[] b) {
        try {
            socket.send(new DatagramPacket(b, b.length));
        } catch (IOException e) {
            // PortUnreachable while the TV app is closed: keep going, it may open
        }
    }

    /** the same static key and prefs as this app's receiver (ReceiverActivity.loadIdentity) */
    private void loadIdentity() {
        android.content.SharedPreferences sp = getSharedPreferences("secure", MODE_PRIVATE);
        String priv = sp.getString("idPriv", null), pub = sp.getString("idPub", null);
        try {
            if (priv != null && pub != null) {
                identity = SecureLink.loadKeyPair(android.util.Base64.decode(priv, android.util.Base64.NO_WRAP),
                        android.util.Base64.decode(pub, android.util.Base64.NO_WRAP));
            }
        } catch (java.security.GeneralSecurityException | IllegalArgumentException e) {
            identity = null;
        }
        if (identity == null) {
            identity = SecureLink.newKeyPair();
            sp.edit().putString("idPriv", android.util.Base64.encodeToString(identity.getPrivate().getEncoded(), android.util.Base64.NO_WRAP))
                    .putString("idPub", android.util.Base64.encodeToString(SecureLink.encodePoint(identity.getPublic()), android.util.Base64.NO_WRAP))
                    .apply();
        }
        identityPoint = SecureLink.encodePoint(identity.getPublic());
    }

    private boolean trustedReceiver(byte[] point) {
        return getSharedPreferences("secure", MODE_PRIVATE).getStringSet("trustedReceivers", new java.util.HashSet<>())
                .contains(android.util.Base64.encodeToString(point, android.util.Base64.NO_WRAP));
    }

    private void trustReceiver(byte[] point) {
        android.content.SharedPreferences sp = getSharedPreferences("secure", MODE_PRIVATE);
        java.util.Set<String> set = new java.util.HashSet<>(sp.getStringSet("trustedReceivers", new java.util.HashSet<>()));
        set.add(android.util.Base64.encodeToString(point, android.util.Base64.NO_WRAP));
        sp.edit().putStringSet("trustedReceivers", set).apply();
    }

    /** a new handshake: new ephemeral key and nonce (also after the TV app restarted) */
    private void newHello() {
        link = null;
        linkReady = false;
        eph = SecureLink.newKeyPair();
        myNonce = SecureLink.randomNonce();
        helloPkt = SecureLink.hello(myNonce, SecureLink.encodePoint(eph.getPublic()), identityPoint, Build.MODEL);
        helloSince = SystemClock.uptimeMillis();
        lastHello = 0;
    }

    private void onHelloOk(byte[] b, int n) {
        SecureLink.Hello h = SecureLink.parse(b, n);
        if (h == null || h.echo == null || link != null || !java.util.Arrays.equals(h.echo, myNonce)) return;
        try {
            SecureLink.Session l = SecureLink.derive(true, identity, eph, myNonce, h.nonce,
                    SecureLink.encodePoint(eph.getPublic()), h.ephemeral, identityPoint, h.statik);
            peerStatic = h.statik;
            lastSealedHeard = SystemClock.uptimeMillis();
            oldReceiver = false;
            link = l;
            if (trustedReceiver(h.statik) && h.knowsYou) {
                linkReady = true;
                configDirty = true;
            } else {
                pairAnswer = 0;
                pairPeer = h.name;
                pairCode = l.codeText(); // MainActivity asks the user to compare it with the TV's
            }
        } catch (java.security.GeneralSecurityException e) {
            Log.w(TAG, "hello ok", e); // a point off the curve
        }
    }

    // ---- video -----------------------------------------------------------------------------

    private MediaFormat format(int w, int h, boolean extras) {
        MediaFormat f = MediaFormat.createVideoFormat(MIME, w, h);
        f.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        f.setInteger(MediaFormat.KEY_BIT_RATE, fps == 120 ? BITRATE_120 : BITRATE_60);
        f.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        // a keyframe every 2 s as a safety net; lost packets ask for one at once
        f.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2);
        // a still screen produces no frames; repeat one every 100 ms so the TV can recover
        // (ignored on the S23 FE, see redrawDisplay)
        f.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000);
        // the phone draws at up to 120 Hz
        f.setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER, fps);
        f.setInteger(MediaFormat.KEY_PRIORITY, 0);
        if (extras) {
            f.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR);
            f.setInteger(MediaFormat.KEY_OPERATING_RATE, fps);
            f.setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0);
            f.setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1);
            if (Build.VERSION.SDK_INT >= 30) f.setInteger(MediaFormat.KEY_LATENCY, 1);
            f.setInteger("vendor.qti-ext-enc-low-latency.enable", 1);
        }
        return f;
    }

    private final class Encoder {
        final MediaCodec codec;
        final Surface surface;
        final Thread thread;
        volatile boolean alive = true;
        /** the TV asked for a keyframe (set by the control thread) */
        volatile boolean keyWanted;
        private long lastOutAt, lastNudge;

        Encoder(int w, int h) throws IOException {
            MediaCodec c = MediaCodec.createEncoderByType(MIME);
            try {
                c.configure(format(w, h, true), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            } catch (RuntimeException e) {
                // some encoders refuse CBR or one of the low-latency keys: plain settings then
                Log.w(TAG, "encoder extras refused", e);
                c.release();
                c = MediaCodec.createEncoderByType(MIME);
                c.configure(format(w, h, false), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            }
            codec = c;
            surface = c.createInputSurface();
            csd = null;
            c.start();
            thread = new Thread(this::drain, "encode");
            thread.start();
        }

        void stop() {
            alive = false;
            try {
                thread.join(500);
            } catch (InterruptedException ignored) {}
            try {
                codec.stop();
            } catch (RuntimeException ignored) {}
            codec.release();
            surface.release();
        }

        private void drain() {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY);
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            byte[] frame = new byte[512 << 10];
            byte[] pkt = new byte[Proto.MAX_PACKET];
            DatagramPacket dp = new DatagramPacket(pkt, pkt.length);
            while (alive) {
                int i;
                try {
                    i = codec.dequeueOutputBuffer(info, 20_000);
                } catch (IllegalStateException e) {
                    break;
                }
                if (i < 0) {
                    long now = SystemClock.uptimeMillis();
                    if (keyWanted && now - lastOutAt > 100 && now - lastNudge > 500) {
                        lastNudge = now;
                        main.post(CastService.this::redrawDisplay);
                    }
                    continue;
                }
                ByteBuffer out = codec.getOutputBuffer(i);
                int n = info.size;
                if (out != null && n > 0) {
                    out.position(info.offset);
                    out.limit(info.offset + n);
                    if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                        byte[] c = new byte[n];
                        out.get(c);
                        csd = c;
                    } else {
                        boolean key = (info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;
                        byte[] c = csd;
                        // the TV decoder needs SPS/PPS in front of every keyframe
                        int pre = key && c != null && !startsWithSps(out, info.offset, n) ? c.length : 0;
                        if (frame.length < pre + n) frame = new byte[pre + n];
                        if (pre > 0) System.arraycopy(c, 0, frame, 0, pre);
                        out.get(frame, pre, n);
                        sendFrame(frame, pre + n, key, info.presentationTimeUs, pkt, dp);
                        lastOutAt = SystemClock.uptimeMillis();
                        if (key) keyWanted = false;
                    }
                }
                codec.releaseOutputBuffer(i, false);
            }
        }
    }

    /**
     * A still screen gives the encoder no input (KEY_REPEAT_PREVIOUS_FRAME_AFTER does nothing
     * on the S23 FE), so a keyframe request from the TV would go unanswered and the TV would
     * stay black. Re-attaching the surface makes the system compose the mirror once more;
     * that frame becomes the requested keyframe.
     */
    private void redrawDisplay() {
        VirtualDisplay d = display;
        Encoder e = enc;
        if (!on || d == null || e == null) return;
        d.setSurface(null);
        d.setSurface(e.surface);
    }

    private static boolean startsWithSps(ByteBuffer b, int off, int n) {
        if (n < 5) return false;
        int p = off;
        if (b.get(p) == 0 && b.get(p + 1) == 0 && b.get(p + 2) == 1) p += 3;
        else if (b.get(p) == 0 && b.get(p + 1) == 0 && b.get(p + 2) == 0 && b.get(p + 3) == 1) p += 4;
        return (b.get(p) & 0x1F) == 7;
    }

    private void sendFrame(byte[] frame, int len, boolean key, long pts, byte[] pkt, DatagramPacket dp) {
        int count = Proto.videoChunks(len);
        if (count < 0) return;
        int id = frameId++;
        for (int c = 0; c < count; c++) {
            int o = c * Proto.MAX_PAYLOAD;
            int l = Math.min(Proto.MAX_PAYLOAD, len - o);
            Proto.putVideoHeader(pkt, key, id, c, count, pts);
            System.arraycopy(frame, o, pkt, Proto.VIDEO_HEADER, l);
            dp.setData(pkt, 0, Proto.VIDEO_HEADER + l);
            send(dp);
        }
    }

    private void requestKeyframe() {
        long now = SystemClock.uptimeMillis();
        Encoder e = enc;
        if (e == null || now - lastKey < KEY_EVERY_MS) return;
        lastKey = now;
        e.keyWanted = true;
        Bundle b = new Bundle();
        b.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
        try {
            e.codec.setParameters(b);
        } catch (IllegalStateException ignored) {}
    }

    // ---- control: CONFIG every second, KEYREQ from the TV ------------------------------------

    private void controlLoop() {
        byte[] raw = new byte[512], in = new byte[512];
        DatagramPacket ip = new DatagramPacket(raw, raw.length);
        // the long CONFIG: flags (CONFIG_PAIRED: the user confirmed this TV) and an empty token
        byte[] cfg = new byte[Proto.CONFIG_LONG];
        DatagramPacket cp = new DatagramPacket(cfg, cfg.length);
        byte[] pong = new byte[17];
        DatagramPacket pp = new DatagramPacket(pong, pong.length);
        long lastCfg = 0;
        newHello();
        while (on) {
            long now = SystemClock.uptimeMillis();
            if (link == null && now - lastHello >= 1000) {
                lastHello = now;
                sendRaw(helloPkt);
                if (now - helloSince > 5000) oldReceiver = true; // an old TV app ignores HELLO
            } else if (link != null && !linkReady) {
                if (pairAnswer == 1) {
                    trustReceiver(peerStatic);
                    pairCode = null;
                    linkReady = true;
                    configDirty = true;
                    lastKey = 0;
                    requestKeyframe();
                } else if (pairAnswer == -1) {
                    pairCode = null;
                    main.post(this::stopSelf);
                    break;
                } else if (now - lastPairShow >= 1000) {
                    lastPairShow = now;
                    sendSealed(new byte[]{SecureLink.PAIR_SHOW}, 0, 1);
                }
            } else if (linkReady && now - lastSealedHeard > 4000) {
                newHello(); // the TV app went quiet (restarted?): shake hands again, it knows us
            }
            if (linkReady && (configDirty || now - lastCfg >= 1000)) {
                configDirty = false;
                lastCfg = now;
                ByteBuffer.wrap(cfg).put(Proto.CONFIG).putInt(vw).putInt(vh)
                        .putInt(Proto.SAMPLE_RATE).put((byte) Proto.CHANNELS).put((byte) SecureLink.CONFIG_PAIRED);
                send(cp);
            }
            try {
                ip.setLength(raw.length);
                socket.receive(ip);
                int rn = ip.getLength();
                if (rn > 0 && raw[0] == SecureLink.HELLO_OK) { onHelloOk(raw, rn); continue; }
                SecureLink.Session l = link;
                int m = rn > 0 && raw[0] == SecureLink.SEALED && l != null ? l.open(raw, rn, in) : -1;
                if (m < 1) continue;
                lastSealedHeard = SystemClock.uptimeMillis();
                if (m >= 1 && in[0] == Proto.KEYREQ) requestKeyframe();
                if (m >= 9 && in[0] == Proto.PING) {
                    ByteBuffer.wrap(pong).put(Proto.PONG).putLong(ByteBuffer.wrap(in, 1, 8).getLong())
                            .putLong(System.nanoTime());
                    send(pp);
                }
            } catch (SocketTimeoutException ignored) {
            } catch (IOException e) {
                if (!on) break;
                SystemClock.sleep(50);
            }
        }
    }

    // ---- audio -----------------------------------------------------------------------------

    private void audioLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO);
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            noAudio = true;
            return;
        }
        AudioRecord rec;
        try {
            AudioPlaybackCaptureConfiguration cfg = new AudioPlaybackCaptureConfiguration.Builder(projection)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                    .build();
            int min = AudioRecord.getMinBufferSize(Proto.SAMPLE_RATE, AudioFormat.CHANNEL_IN_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT);
            rec = new AudioRecord.Builder()
                    .setAudioFormat(new AudioFormat.Builder()
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setSampleRate(Proto.SAMPLE_RATE)
                            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                            .build())
                    .setBufferSizeInBytes(Math.max(min, Proto.AUDIO_CHUNK_BYTES * 4))
                    .setAudioPlaybackCaptureConfig(cfg)
                    .build();
        } catch (RuntimeException e) {
            Log.w(TAG, "audio capture", e);
            noAudio = true;
            return;
        }
        rec.startRecording();
        byte[] pkt = new byte[5 + Proto.AUDIO_CHUNK_BYTES];
        ByteBuffer h = ByteBuffer.wrap(pkt);
        DatagramPacket dp = new DatagramPacket(pkt, pkt.length);
        int seq = 0;
        while (on) {
            int n = rec.read(pkt, 5, Proto.AUDIO_CHUNK_BYTES);
            if (n < 0) break;
            if (n == 0) continue;
            h.clear();
            h.put(Proto.AUDIO).putInt(seq++);
            dp.setData(pkt, 0, 5 + n);
            send(dp);
        }
        rec.stop();
        rec.release();
    }

    // ---- notification ----------------------------------------------------------------------

    private Notification notification(String h) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel("cast", getString(R.string.channel),
                NotificationManager.IMPORTANCE_LOW));
        PendingIntent stop = PendingIntent.getService(this, 0,
                new Intent(this, CastService.class).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE);
        PendingIntent open = PendingIntent.getActivity(this, 0,
                new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, "cast")
                .setSmallIcon(R.drawable.ic_cast)
                .setContentTitle(getString(R.string.casting, h))
                .setContentIntent(open)
                .setOngoing(true)
                .addAction(new Notification.Action.Builder(
                        Icon.createWithResource(this, R.drawable.ic_cast), getString(R.string.stop), stop).build())
                .build();
    }
}
