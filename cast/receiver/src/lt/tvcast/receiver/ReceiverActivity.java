package lt.tvcast.receiver;

import android.app.Activity;
import android.graphics.Color;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.Process;
import android.os.SystemClock;
import android.provider.Settings;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.PointerIcon;
import android.view.KeyEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.ImageView;

import lt.tvcast.ui.Look;

import lt.tvcast.common.DeskInput;
import lt.tvcast.common.MultiTap;
import lt.tvcast.common.SecureLink;
import lt.tvcast.common.FrameAssembler;
import lt.tvcast.common.Proto;
import lt.tvcast.common.TouchMapper;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.NetworkInterface;
import java.net.SocketAddress;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.util.Collections;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Shows the phone's screen: UDP -> H.264 frames -> hardware decoder rendering straight to a
 * SurfaceView, and raw PCM -> low-latency AudioTrack. Nothing is buffered on purpose: a
 * decoded frame is shown the moment it comes out of the decoder, and a lost frame makes the
 * TV skip everything up to the next keyframe (which it asks the phone for).
 */
public class ReceiverActivity extends Activity implements SurfaceHolder.Callback {
    private static final String TAG = "tvcast";
    private static final String MIME = MediaFormat.MIMETYPE_VIDEO_AVC;
    private static final long IDLE_MS = 3000;
    private static final long KEYREQ_EVERY_MS = 200;
    /** audio queued in the AudioTrack never grows past this (non-blocking writes drop the rest) */
    private static final int AUDIO_BUFFER_MS = 50;
    /** H.264 access unit delimiter NAL (type 9, any slice type) */
    private static final byte[] AUD = {0, 0, 0, 1, 0x09, (byte) 0xF0};

    private static final int FIT_FIT = 0, FIT_FILL = 1, FIT_STRETCH = 2;
    private static final int[] FIT_NAMES = {R.string.fit_fit, R.string.fit_fill, R.string.fit_stretch};

    private final Handler ui = new Handler(Looper.getMainLooper());
    /** remote Left/Right, kept in prefs "fit" */
    private int fitMode;
    private int vidW, vidH;
    private FrameLayout root;
    private SurfaceView video;
    private View waiting;
    private TextView waitingIp, stats, toast;

    private boolean resumed;
    private volatile Surface surface;
    private volatile boolean running;
    private DatagramSocket socket;
    private Thread rxThread, decThread;
    private HandlerThread codecThread;
    private Handler codecHandler;
    private WifiManager.WifiLock wifiLock;
    private NsdManager nsd;
    private NsdManager.RegistrationListener nsdListener;

    // receive thread only
    private SocketAddress sender;
    // the encrypted link (SecureLink): this device's static key, the casters the user confirmed
    // once, and the cast now: its keys, the caster's static key and name, the cached HELLO_OK
    private java.security.KeyPair identity;
    private byte[] identityPoint;
    private java.util.Set<String> trustedCasters;
    private volatile SecureLink.Session link;
    private byte[] linkCasterStatic, linkHelloNonce, helloOkBytes;
    private volatile boolean linkTrusted;
    private volatile String linkCasterName = "";
    private volatile long lastLinkAt, lastHelloAt;
    private volatile String pairCode;      // shown while the user compares it on the caster
    private volatile long oldSenderAt;     // an old (unencrypted) CastLink tried to connect
    private final byte[] inner = new byte[Proto.MAX_PACKET];
    /** the device casting right now; null while waiting */
    private InetAddress session;
    // control of the PC from the phone: allowed by the PC in CONFIG, signed with its token
    private volatile boolean control;
    private volatile byte[] token;
    private TouchMapper touch;
    // a mouse and a keyboard on the TV (or tablet) control the PC the same way
    private DeskInput desk;
    private boolean pointerHidden;
    // "own pointer" (remote Down toggles it, pref "ownPointer"): the TV's mouse moves an arrow
    // drawn here only; a click borrows the PC's pointer and the PC puts it back, so the PC's own
    // mouse keeps working on the main screen at the same time
    private boolean ownPointer;
    private View arrow;
    private volatile float arrowX, arrowY;
    /** the arrow hides after this long without mouse input (it stayed over YouTube videos) */
    private static final long ARROW_IDLE_MS = 3000;
    private final Runnable hideIdleArrow = new Runnable() {
        @Override public void run() { if (arrow != null) arrow.setVisibility(View.GONE); }
    };
    private int ownEvents;
    private HandlerThread inputThread;
    private Handler inputHandler;
    private long lastKeyReq;
    private int cfgW, cfgH;
    // An access unit delimiter after each frame: the MediaTek decoder only starts on a frame
    // once it sees the next start code, which otherwise costs a whole frame (16.7 ms).
    private final FrameAssembler assembler = new FrameAssembler(AUD);
    private AudioTrack track;
    private long lastPing;
    // "smooth picture" (remote Up toggles it, pref "smooth"): frames go out one per screen refresh
    // instead of the moment they are decoded; costs up to one refresh of delay, no frame is dropped
    private volatile boolean smooth;
    private long nextPresentNs, lastSlot;
    private long periodNs = 16_666_667L;
    // shown frames, counted by screen refresh: onFrameRendered callbacks may be dropped by the
    // system (it reported ~42 fps while SurfaceFlinger showed ~51)
    private final AtomicInteger statSlots = new AtomicInteger();
    private long bestRtt;

    /** phone System.nanoTime() minus TV System.nanoTime(), from the PING with the shortest round trip */
    private volatile long clockOffset;
    private volatile boolean clockValid;
    private volatile long rttNs;
    // pts -> System.nanoTime() when the frame went into the decoder
    private final long[] queuedPts = new long[64];
    private final long[] queuedAt = new long[64];
    private int queuedPos;

    // shared
    private volatile boolean streaming;
    private volatile long lastPacketAt;
    private final ArrayBlockingQueue<Frame> frames = new ArrayBlockingQueue<>(6);

    // decoder thread + codec callbacks
    private final LinkedBlockingQueue<Integer> inputs = new LinkedBlockingQueue<>();
    private volatile MediaCodec codec;
    private int decW, decH;
    private volatile String codecName = "-";
    private volatile boolean lowLatency;
    /**
     * Frames given to the decoder and not out yet (diagnostics). Flushing the decoder when it
     * fell behind after a Wi-Fi hiccup was tried (2026-10-25) and made things worse on a
     * congested link: the fresh keyframe is a new burst and the slow state came straight back.
     */
    private final AtomicInteger inFlight = new AtomicInteger();

    private final AtomicInteger statFrames = new AtomicInteger();
    private final AtomicLong statBytes = new AtomicLong();
    private final AtomicLong statDecNs = new AtomicLong();
    private final AtomicInteger statDecN = new AtomicInteger();
    private final AtomicLong statE2eNs = new AtomicLong();
    private final AtomicInteger statE2eN = new AtomicInteger();
    private final AtomicInteger statShown = new AtomicInteger();
    private final AtomicInteger maxQueued = new AtomicInteger(), maxInFlight = new AtomicInteger();
    /** packets dropped: not from the home network, or from a second device during a cast */
    private final AtomicInteger statRejected = new AtomicInteger();
    private final AtomicLong statScreenNs = new AtomicLong();
    private final AtomicInteger statScreenN = new AtomicInteger();
    /** loudest 16-bit sample received since the last stats tick */
    private volatile int statPeak;
    private long statAt;

    /** A whole H.264 access unit, or (data == null) a request to (re)configure the decoder:
     *  w > 0 for a new size, w < 0 to release it. */
    private static final class Frame {
        final byte[] data;
        final long pts;
        final boolean key;
        final int w, h;

        Frame(byte[] data, long pts, boolean key, int w, int h) {
            this.data = data; this.pts = pts; this.key = key; this.w = w; this.h = h;
        }
    }

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        // ALLM: the TV switches to its game picture mode (no motion smoothing etc.) for this window
        getWindow().setPreferMinimalPostProcessing(true);
        // the same screen runs inside the phone app (the phone shows the PC): there it says so,
        // and hides the status and navigation bars
        onPhone = !getPackageManager().hasSystemFeature(android.content.pm.PackageManager.FEATURE_LEANBACK);
        if (onPhone) immersive();
        fitMode = getSharedPreferences("tvcast", MODE_PRIVATE).getInt("fit", FIT_FIT) % FIT_NAMES.length;
        smooth = getSharedPreferences("tvcast", MODE_PRIVATE).getBoolean("smooth", false);
        ownPointer = getSharedPreferences("tvcast", MODE_PRIVATE).getBoolean("ownPointer", false);
        loadIdentity();
        float hz = getDisplay() != null ? getDisplay().getRefreshRate() : 60f;
        if (hz >= 20 && hz <= 240) periodNs = (long) (1_000_000_000L / hz);

        root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);
        video = new SurfaceView(this);
        video.getHolder().addCallback(this);
        root.addView(video, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT, Gravity.CENTER));

        // waiting screen: the CastLink page (glow background, icon, a glass card with the address)
        LinearLayout w = onPhone ? phoneWaiting() : tvWaiting();
        waiting = w;
        root.addView(w, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        stats = Look.text(this, "", 14, Look.INK, false);
        stats.setBackground(glass(16));
        stats.setPadding(dp(16), dp(12), dp(16), dp(12));
        stats.setVisibility(View.GONE);
        FrameLayout.LayoutParams sp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.START);
        sp.leftMargin = sp.topMargin = dp(24);
        root.addView(stats, sp);

        // picture-size messages: our own glass label (a system toast looks out of place here)
        toast = center(Look.text(this, "", 20, Look.INK, true));
        toast.setBackground(glass(18));
        toast.setPadding(dp(28), dp(16), dp(28), dp(16));
        toast.setVisibility(View.GONE);
        FrameLayout.LayoutParams tp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        tp.bottomMargin = dp(64);
        root.addView(toast, tp);
        // a transparent layer over the whole screen that draws the arrow itself at (arrowX, arrowY):
        // moving a small view by translation was not shown on this TV (it was drawn once only)
        arrow = new View(this) {
            final android.graphics.Paint fill = new android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG);
            final android.graphics.Paint edge = new android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG);
            final android.graphics.Path path = new android.graphics.Path();
            {
                fill.setColor(0xFFFFFFFF);
                edge.setColor(0xFF000000);
                edge.setStyle(android.graphics.Paint.Style.STROKE);
                edge.setStrokeWidth(dp(0.6f));
                float u = dp(0.55f); // a bit smaller than the Windows pointer
                path.moveTo(1, 1); path.lineTo(1, 17 * u); path.lineTo(5 * u, 13 * u); path.lineTo(8 * u, 20 * u);
                path.lineTo(11 * u, 19 * u); path.lineTo(8 * u, 12 * u); path.lineTo(13 * u, 12 * u); path.close();
                setWillNotDraw(false);
            }
            @Override protected void onDraw(android.graphics.Canvas c) {
                c.save();
                c.translate(arrowX, arrowY);
                c.drawPath(path, fill);
                c.drawPath(path, edge);
                c.restore();
            }
        };
        arrow.setVisibility(View.GONE);
        root.addView(arrow, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        setContentView(root);

        nsd = getSystemService(NsdManager.class);
        WifiManager wm = getApplicationContext().getSystemService(WifiManager.class);
        wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, TAG);
        wifiLock.setReferenceCounted(false);
    }

    private boolean onPhone;

    @SuppressWarnings("deprecation")
    private void immersive() {
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
    }

    @Override
    public void onWindowFocusChanged(boolean focus) {
        super.onWindowFocusChanged(focus);
        if (focus && onPhone) immersive();
    }

    /**
     * On a phone: while the PC allows control, touches become its mouse (TouchMapper: tap,
     * drag, long press / two-finger tap = right click, two-finger swipe = wheel) and three
     * fingers show or hide the statistics, with or without control (the TV uses OK).
     */
    @Override
    public boolean onTouchEvent(android.view.MotionEvent e) {
        if (e.isFromSource(InputDevice.SOURCE_MOUSE)) {
            if (!deskLive()) return super.onTouchEvent(e);
            int a = e.getActionMasked();
            if (ownPointer) { // not captured (yet / any more): take the mouse back, click nothing
                if (a == android.view.MotionEvent.ACTION_DOWN) Log.i(TAG, "click dropped: mouse not captured (touch)");
                capture();
                return true;
            }
            if (a == android.view.MotionEvent.ACTION_MOVE) desk().move(e.getX(), e.getY());
            else if (a == android.view.MotionEvent.ACTION_CANCEL) desk().release();
            else desk().buttons(e.getButtonState(), e.getX(), e.getY());
            return true;
        }
        if (!onPhone) return super.onTouchEvent(e);
        int a = e.getActionMasked(), n = e.getPointerCount();
        if (!streaming) { // the waiting screen: nothing to show statistics of
            stats.setVisibility(View.GONE);
            return true;
        }
        boolean live = streaming && control && token != null;
        // only three fingers show / hide the statistics (a tap never does, also with control off)
        if (a == android.view.MotionEvent.ACTION_POINTER_DOWN && n == 3) {
            if (touch != null && live) touch.cancel();
            stats.setVisibility(stats.getVisibility() == View.VISIBLE ? View.GONE : View.VISIBLE);
            return true;
        }
        if (!live) return true;
        if (n >= 3) return true;
        if (touch == null) touch = new TouchMapper(getResources().getDisplayMetrics().density, this::sendInput);
        touch.setArea(video.getLeft(), video.getTop(), video.getWidth(), video.getHeight());
        long t = SystemClock.uptimeMillis();
        float cx = n >= 2 ? (e.getX(0) + e.getX(1)) / 2 : e.getX(), cy = n >= 2 ? (e.getY(0) + e.getY(1)) / 2 : e.getY();
        switch (a) {
            case android.view.MotionEvent.ACTION_DOWN:
                touch.start(e.getX(), e.getY(), t);
                ui.postDelayed(longPress, TouchMapper.LONG_PRESS_MS + 10);
                break;
            case android.view.MotionEvent.ACTION_POINTER_DOWN:
                ui.removeCallbacks(longPress);
                touch.two(cx, cy, t);
                break;
            case android.view.MotionEvent.ACTION_MOVE:
                touch.move(n, cx, cy, t);
                break;
            case android.view.MotionEvent.ACTION_POINTER_UP:
                touch.end(n - 1, t);
                break;
            case android.view.MotionEvent.ACTION_UP:
                ui.removeCallbacks(longPress);
                touch.end(0, t);
                break;
            case android.view.MotionEvent.ACTION_CANCEL:
                ui.removeCallbacks(longPress);
                touch.cancel();
                break;
            default: break;
        }
        return true;
    }

    private final Runnable longPress = () -> { if (touch != null) touch.tick(SystemClock.uptimeMillis()); };

    /** the PC allows control and a picture is showing */
    private boolean deskLive() { return streaming && control && token != null; }

    private DeskInput desk() {
        if (desk == null) desk = new DeskInput(this::sendInput);
        desk.setArea(video.getLeft(), video.getTop(), video.getWidth(), video.getHeight());
        // the own pointer stays on the visible screen ("Fill" makes the picture wider than it)
        int l = Math.max(0, video.getLeft()), t = Math.max(0, video.getTop());
        int r = Math.min(root.getWidth(), video.getRight()), b = Math.min(root.getHeight(), video.getBottom());
        if (r > l && b > t) desk.ownArea(l, t, r - l, b - t);
        return desk;
    }

    /** Android's own arrow is hidden while the mouse drives the PC: the PC's pointer is in the picture */
    private void hidePointer(boolean hide) {
        if (hide == pointerHidden) return;
        pointerHidden = hide;
        getWindow().getDecorView().setPointerIcon(hide ? PointerIcon.getSystemIcon(this, PointerIcon.TYPE_NULL) : null);
    }

    private boolean captured;

    /**
     * Pointer capture: the TV's mouse then drives the PC like a mouse plugged into it (relative
     * moves), so the pointer goes across every screen of the PC, also off the one the TV shows.
     * Android hides its own arrow meanwhile; the remote's keys are not affected.
     */
    private View captureView;
    private boolean swallowClick;
    // the waiting screen's state: dot + title (amber waiting, green connected, red a problem)
    private View waitDot;
    private TextView waitTitle;
    private boolean portBusy;
    private volatile boolean waitingUp = true; // hidden at the first shown frame
    private int lastOwnButtons;
    private float idleMove;      // mouse movement since the arrow hid
    private long arrowBackAt;    // when the hidden arrow was shown again
    private static final float ARROW_NUDGE = 8;     // counts; less than this keeps it hidden
    private static final long ARROW_BACK_MS = 400;  // a press this soon after it came back is dropped

    private void capture() {
        if (captured || !hasWindowFocus()) return;
        // captured events go to the FOCUSED view; with nobody taking them Android turns the moves
        // into arrow keys (which here change the picture size). So: one focused view takes them.
        View v = getWindow().getDecorView();
        v.setFocusable(true);
        v.setFocusableInTouchMode(true);
        if (getCurrentFocus() == null) v.requestFocus();
        View f = getCurrentFocus();
        if (f != null) v = f;
        captureView = v;
        v.setOnCapturedPointerListener((view, e) -> {
            if (!deskLive()) { releaseCapture(); return false; }
            DeskInput d = desk();
            if (ownPointer) {
                if (e.getActionMasked() == android.view.MotionEvent.ACTION_SCROLL) {
                    d.ownScroll(e.getAxisValue(android.view.MotionEvent.AXIS_VSCROLL), e.getAxisValue(android.view.MotionEvent.AXIS_HSCROLL));
                } else {
                    boolean hidden = arrow.getVisibility() != View.VISIBLE;
                    long now = SystemClock.uptimeMillis();
                    for (int h = 0; h < e.getHistorySize(); h++) {
                        d.ownMove(e.getHistoricalX(h), e.getHistoricalY(h));
                        idleMove += Math.abs(e.getHistoricalX(h)) + Math.abs(e.getHistoricalY(h));
                    }
                    d.ownMove(e.getX(), e.getY());
                    idleMove += Math.abs(e.getX()) + Math.abs(e.getY());
                    int b = e.getButtonState();
                    // a click while the arrow is hidden, or just after it came back, only brings it
                    // back: the user can't see where it would land (it paused YouTube). The hand
                    // nudges the mouse while pressing, so a small nudge doesn't count as "back".
                    if (b != 0 && !swallowClick && d.ownButtonsUp() && (hidden || now - arrowBackAt < ARROW_BACK_MS)) {
                        swallowClick = true;
                        Log.i(TAG, "click dropped: arrow " + (hidden ? "hidden" : "just came back"));
                    }
                    if (b != lastOwnButtons) Log.i(TAG, "own buttons " + b + (swallowClick ? " dropped" : " sent")
                        + (hidden ? ", arrow hidden" : "") + ", " + (now - arrowBackAt) + " ms since it came back");
                    lastOwnButtons = b;
                    if (swallowClick) { if (b == 0) swallowClick = false; }
                    else d.ownButtons(b);
                    if (hidden && idleMove < ARROW_NUDGE && b == 0) return true; // stays hidden
                    if (hidden) arrowBackAt = now;
                    idleMove = 0;
                }
                showArrow();
                if (++ownEvents % 120 == 1) Log.i(TAG, "own pointer " + Math.round(d.ownX()) + "," + Math.round(d.ownY())
                    + " arrow " + (arrow.getVisibility() == View.VISIBLE ? "shown" : "hidden"));
                return true;
            }
            switch (e.getActionMasked()) {
                case android.view.MotionEvent.ACTION_SCROLL:
                    d.scroll(e.getAxisValue(android.view.MotionEvent.AXIS_VSCROLL), e.getAxisValue(android.view.MotionEvent.AXIS_HSCROLL));
                    break;
                default:
                    // a captured mouse reports the movement since the last event as x / y
                    for (int h = 0; h < e.getHistorySize(); h++) d.moveBy(e.getHistoricalX(h), e.getHistoricalY(h));
                    d.moveBy(e.getX(), e.getY());
                    d.buttonsHere(e.getButtonState());
                    break;
            }
            return true;
        });
        v.requestPointerCapture();
        captured = true;
    }

    /** the own pointer's arrow at its spot on the picture */
    private void showArrow() {
        if (desk == null) return;
        arrowX = desk.ownX();
        arrowY = desk.ownY();
        if (arrow.getVisibility() != View.VISIBLE) arrow.setVisibility(View.VISIBLE);
        arrow.invalidate();
        ui.removeCallbacks(hideIdleArrow);
        ui.postDelayed(hideIdleArrow, ARROW_IDLE_MS);
    }

    private void releaseCapture() {
        ui.removeCallbacks(hideIdleArrow);
        arrow.setVisibility(View.GONE);
        if (!captured) return;
        captured = false;
        if (desk != null) { desk.releaseHere(); desk.ownRelease(); }
        View v = captureView != null ? captureView : getWindow().getDecorView();
        v.releasePointerCapture();
        v.setOnCapturedPointerListener(null);
        captureView = null;
    }

    /** an arrow key the system made up from mouse or trackball motion (not the remote, not a keyboard) */
    private static boolean fromPointer(KeyEvent e) {
        InputDevice d = e.getDevice();
        return d != null && (d.supportsSource(InputDevice.SOURCE_MOUSE) || d.supportsSource(InputDevice.SOURCE_MOUSE_RELATIVE)
            || d.supportsSource(InputDevice.SOURCE_TRACKBALL)) && !d.supportsSource(InputDevice.SOURCE_DPAD);
    }

    @Override
    public void onPointerCaptureChanged(boolean hasCapture) {
        Log.i(TAG, "pointer capture " + hasCapture);
        if (!hasCapture && captured) { captured = false; if (desk != null) desk.releaseHere(); }
        super.onPointerCaptureChanged(hasCapture);
    }

    /** the mouse without a button held: moves, the wheel, button changes */
    @Override
    public boolean onGenericMotionEvent(android.view.MotionEvent e) {
        if (!e.isFromSource(InputDevice.SOURCE_MOUSE)) return super.onGenericMotionEvent(e);
        boolean live = deskLive();
        hidePointer(live);
        if (live) capture();
        if (!live) return super.onGenericMotionEvent(e);
        if (ownPointer) { // not captured yet: the own pointer never moves or clicks the PC's mouse
            if (e.getActionMasked() == android.view.MotionEvent.ACTION_BUTTON_PRESS) Log.i(TAG, "click dropped: mouse not captured (button)");
            return true;
        }
        switch (e.getActionMasked()) {
            case android.view.MotionEvent.ACTION_HOVER_MOVE:
            case android.view.MotionEvent.ACTION_HOVER_ENTER:
                desk().move(e.getX(), e.getY());
                return true;
            case android.view.MotionEvent.ACTION_SCROLL:
                desk().scroll(e.getAxisValue(android.view.MotionEvent.AXIS_VSCROLL), e.getAxisValue(android.view.MotionEvent.AXIS_HSCROLL));
                return true;
            case android.view.MotionEvent.ACTION_BUTTON_PRESS:
            case android.view.MotionEvent.ACTION_BUTTON_RELEASE:
                desk().buttons(e.getButtonState(), e.getX(), e.getY());
                return true;
            default:
                return true;
        }
    }

    /** a real keyboard (letters on it), not the remote: the remote keeps its own keys */
    private static boolean fromKeyboard(KeyEvent e) {
        InputDevice d = e.getDevice();
        return d != null && !d.isVirtual() && d.getKeyboardType() == InputDevice.KEYBOARD_TYPE_ALPHABETIC;
    }

    /** While the PC allows control, every key of a keyboard goes to the PC (typed text, Enter,
     *  arrows, ...); shortcuts with Ctrl or Alt are dropped, like on the iPad. */
    @Override
    public boolean dispatchKeyEvent(KeyEvent e) {
        if (!deskLive() || !fromKeyboard(e)) return super.dispatchKeyEvent(e);
        if (e.getAction() == KeyEvent.ACTION_MULTIPLE && e.getCharacters() != null) {
            desk().text(e.getCharacters());
        } else if (e.getAction() == KeyEvent.ACTION_DOWN) {
            boolean shortcut = e.isCtrlPressed() || e.isAltPressed() || e.isMetaPressed();
            int ch = e.getUnicodeChar(e.getMetaState() & (KeyEvent.META_SHIFT_MASK | KeyEvent.META_CAPS_LOCK_ON));
            desk().key(e.getKeyCode(), ch, shortcut);
        }
        return true; // a keyboard's keys never change the picture size or close the app here
    }

    // ---- the encrypted link ---------------------------------------------------------------

    /** this device's static key (made once, kept in the app's private storage) and the casters
     *  the user confirmed with the pairing code */
    private void loadIdentity() {
        android.content.SharedPreferences sp = getSharedPreferences("secure", MODE_PRIVATE);
        String priv = sp.getString("idPriv", null), pub = sp.getString("idPub", null);
        try {
            if (priv != null && pub != null) {
                identity = SecureLink.loadKeyPair(android.util.Base64.decode(priv, android.util.Base64.NO_WRAP),
                        android.util.Base64.decode(pub, android.util.Base64.NO_WRAP));
            }
        } catch (java.security.GeneralSecurityException | IllegalArgumentException e) {
            Log.w(TAG, "identity", e);
            identity = null;
        }
        if (identity == null) {
            identity = SecureLink.newKeyPair();
            sp.edit().putString("idPriv", android.util.Base64.encodeToString(identity.getPrivate().getEncoded(), android.util.Base64.NO_WRAP))
                    .putString("idPub", android.util.Base64.encodeToString(SecureLink.encodePoint(identity.getPublic()), android.util.Base64.NO_WRAP))
                    .apply();
        }
        identityPoint = SecureLink.encodePoint(identity.getPublic());
        trustedCasters = new java.util.HashSet<>(sp.getStringSet("trustedCasters", new java.util.HashSet<>()));
    }

    private static String b64(byte[] b) { return android.util.Base64.encodeToString(b, android.util.Base64.NO_WRAP); }

    private synchronized void trustCaster(byte[] point) {
        if (!trustedCasters.add(b64(point))) return;
        getSharedPreferences("secure", MODE_PRIVATE).edit().putStringSet("trustedCasters", new java.util.HashSet<>(trustedCasters)).apply();
    }

    /**
     * A caster says hello. Answered when nobody else casts (or it is the same caster again, e.g.
     * restarted); new handshakes at most 4 a second (each costs two ECDH operations).
     */
    private void onHello(DatagramPacket p, int n) {
        SecureLink.Hello h = SecureLink.parse(p.getData(), n);
        if (h == null) return;
        InetAddress from = p.getAddress();
        if (link != null && helloOkBytes != null && java.util.Arrays.equals(h.nonce, linkHelloNonce)) {
            sendRaw(helloOkBytes, p.getSocketAddress()); // our answer was lost: the same again
            return;
        }
        long now = SystemClock.uptimeMillis();
        boolean busy = link != null && now - lastLinkAt < IDLE_MS
                && !java.util.Arrays.equals(h.statik, linkCasterStatic) && !from.equals(session);
        if (busy || now - lastHelloAt < 250) {
            statRejected.incrementAndGet();
            return;
        }
        lastHelloAt = now;
        try {
            java.security.KeyPair eph = SecureLink.newKeyPair();
            byte[] ephPoint = SecureLink.encodePoint(eph.getPublic());
            byte[] rn = SecureLink.randomNonce();
            SecureLink.Session sess = SecureLink.derive(false, identity, eph, h.nonce, rn,
                    h.ephemeral, ephPoint, h.statik, identityPoint);
            boolean known;
            synchronized (this) { known = trustedCasters.contains(b64(h.statik)); }
            String me = nsdName != null ? nsdName : deviceName();
            byte[] reply = SecureLink.helloOk(rn, ephPoint, identityPoint, h.nonce, known, me);
            if (streaming || link != null) goIdleQuiet();
            link = sess;
            linkCasterStatic = h.statik;
            linkHelloNonce = h.nonce;
            linkCasterName = h.name;
            linkTrusted = known;
            helloOkBytes = reply;
            pairCode = null;
            session = from;
            sender = p.getSocketAddress();
            lastLinkAt = now;
            Log.i(TAG, "hello from " + h.name + " key " + SecureLink.fingerprint(h.statik) + (known ? " (known)" : " (new)"));
            sendRaw(reply, sender);
        } catch (java.security.GeneralSecurityException e) {
            statRejected.incrementAndGet(); // a point off the curve
        }
    }

    private void sendRaw(byte[] b, SocketAddress to) {
        DatagramSocket s = socket;
        if (s == null || to == null) return;
        try {
            s.send(new DatagramPacket(b, b.length, to));
        } catch (IOException e) {
            Log.w(TAG, "send", e);
        }
    }

    /** an inner packet to the caster, sealed with this cast's keys (dropped when there is none) */
    private void sendSealed(byte[] pkt, int len, SocketAddress to) {
        SecureLink.Session l = link;
        if (l == null || to == null) return;
        sendRaw(l.seal(pkt, 0, len), to);
    }

    /** the pairing code on the waiting screen until the caster's user confirms it */
    private void showPairCode(String code) {
        if (code.equals(pairCode)) return;
        pairCode = code;
        ui.post(this::refreshWaitState);
    }

    /** an older CastLink that does not encrypt: refused, the waiting screen says what to do */
    private void noteOldSender() {
        long now = SystemClock.uptimeMillis();
        if (now - oldSenderAt < 2000) return;
        oldSenderAt = now;
        ui.post(this::refreshWaitState);
    }

    /** one input message to the PC, off the UI thread (no network on it) */
    private void sendInput(byte[] msg) {
        byte[] t = token;
        SocketAddress to = sender;
        DatagramSocket s = socket;
        if (t == null || to == null || s == null) return;
        if (inputHandler == null) {
            inputThread = new HandlerThread("input");
            inputThread.start();
            inputHandler = new Handler(inputThread.getLooper());
        }
        byte[] pkt = new byte[1 + t.length + msg.length];
        pkt[0] = Proto.INPUT;
        System.arraycopy(t, 0, pkt, 1, t.length);
        System.arraycopy(msg, 0, pkt, 1 + t.length, msg.length);
        inputHandler.post(() -> {
            try {
                SecureLink.Session l = link;
                if (l == null) return;
                byte[] sealed = l.seal(pkt, 0, pkt.length);
                s.send(new DatagramPacket(sealed, sealed.length, to));
            } catch (IOException ex) {
                Log.w(TAG, "input", ex);
            }
        });
    }

    private static TextView center(TextView t) {
        t.setGravity(Gravity.CENTER);
        return t;
    }

    /** dark glass over the picture */
    private android.graphics.drawable.GradientDrawable glass(int radiusDp) {
        android.graphics.drawable.GradientDrawable g = Look.card(this);
        g.setColor(0xD91C1C22);
        g.setCornerRadius(dp(radiusDp));
        return g;
    }

    private final Runnable hideToast = () -> toast.setVisibility(View.GONE);

    private void showToast(String s) {
        toast.setText(s);
        toast.setVisibility(View.VISIBLE);
        ui.removeCallbacks(hideToast);
        ui.postDelayed(hideToast, 1800);
    }

    private int dp(float v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        refreshWaitState();
        ui.removeCallbacks(waitCheck);
        ui.postDelayed(waitCheck, 2000);
        startIfReady();
    }

    @Override
    protected void onPause() {
        resumed = false;
        if (desk != null) desk.release();
        releaseCapture();
        hidePointer(false);
        stopAll();
        super.onPause();
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        surface = holder.getSurface();
        startIfReady();
    }

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {}

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        // the decoder must be gone before the surface is
        stopAll();
        surface = null;
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (fromPointer(event)) return true; // a mouse moving never changes the picture size
        if (!onPhone && deskLive() && remoteTyping(keyCode, event)) return true;
        if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT || keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) {
            if (event.getRepeatCount() == 0) {
                int step = keyCode == KeyEvent.KEYCODE_DPAD_RIGHT ? 1 : FIT_NAMES.length - 1;
                fitMode = (fitMode + step) % FIT_NAMES.length;
                getSharedPreferences("tvcast", MODE_PRIVATE).edit().putInt("fit", fitMode).apply();
                fitVideo(vidW, vidH);
                String name = getString(FIT_NAMES[fitMode]);
                if (vidH > vidW && fitMode != FIT_FIT) name += "\n" + getString(R.string.fit_portrait);
                showToast(name);
            }
            return true;
        }
        if (keyCode == KeyEvent.KEYCODE_DPAD_DOWN) {
            if (event.getRepeatCount() == 0) {
                if (desk != null) { desk.releaseHere(); desk.ownRelease(); }
                ownPointer = !ownPointer;
                getSharedPreferences("tvcast", MODE_PRIVATE).edit().putBoolean("ownPointer", ownPointer).apply();
                if (!ownPointer) arrow.setVisibility(View.GONE); else if (captured) showArrow();
                showToast(getString(ownPointer ? R.string.own_pointer_on : R.string.own_pointer_off));
            }
            return true;
        }
        if (keyCode == KeyEvent.KEYCODE_DPAD_UP) {
            if (event.getRepeatCount() == 0) {
                smooth = !smooth;
                getSharedPreferences("tvcast", MODE_PRIVATE).edit().putBoolean("smooth", smooth).apply();
                showToast(getString(smooth ? R.string.smooth_on : R.string.smooth_off));
            }
            return true;
        }
        if (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER) {
            if (event.getRepeatCount() == 0) {
                stats.setVisibility(stats.getVisibility() == View.VISIBLE ? View.GONE : View.VISIBLE);
            }
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }

    /**
     * The phone's waiting screen (landscape, about 890 x 410 dp), two columns: left the icon,
     * the name and the state card with the address; right the touch gestures, one per row.
     */
    private LinearLayout phoneWaiting() {
        LinearLayout w = new LinearLayout(this);
        w.setOrientation(LinearLayout.HORIZONTAL);
        w.setGravity(Gravity.CENTER);
        w.setBackground(Look.background());

        LinearLayout left = new LinearLayout(this);
        left.setOrientation(LinearLayout.VERTICAL);
        left.setGravity(Gravity.CENTER_HORIZONTAL);
        LinearLayout brand = new LinearLayout(this);
        brand.setOrientation(LinearLayout.HORIZONTAL);
        brand.setGravity(Gravity.CENTER_VERTICAL);
        ImageView logo = new ImageView(this);
        logo.setImageResource(R.drawable.app_icon);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(56), dp(56));
        lp.rightMargin = dp(14);
        brand.addView(logo, lp);
        LinearLayout names = new LinearLayout(this);
        names.setOrientation(LinearLayout.VERTICAL);
        names.addView(Look.text(this, getString(R.string.app_name), 24, Look.INK, true));
        names.addView(Look.text(this, getString(R.string.phone_subtitle), 14, Look.MUTED, false));
        brand.addView(names);
        left.addView(brand);
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setGravity(Gravity.CENTER);
        card.setBackground(Look.card(this));
        card.setPadding(dp(24), dp(18), dp(24), dp(20));
        card.addView(statusTitle(18), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        TextView hint = center(Look.text(this, getString(R.string.wait_hint_phone), 14, Look.MUTED, false));
        hint.setPadding(0, dp(6), 0, dp(14));
        card.addView(hint, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        waitingIp = center(Look.text(this, "", 16, Look.INK, true));
        waitingIp.setBackground(Look.pill(this, 0x1FFFFFFF));
        waitingIp.setPadding(dp(18), dp(8), dp(18), dp(8));
        card.addView(waitingIp, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        cp.topMargin = dp(18);
        left.addView(card, cp);
        w.addView(left, new LinearLayout.LayoutParams(dp(340), ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout touch = helpCard(getString(R.string.help_touch));
        helpRow(touch, getString(R.string.g_tap), getString(R.string.g_tap_does), 92);
        helpRow(touch, getString(R.string.g_drag), getString(R.string.g_drag_does), 92);
        helpRow(touch, getString(R.string.g_hold), getString(R.string.g_hold_does), 92);
        helpRow(touch, getString(R.string.g_two), getString(R.string.g_two_does), 92);
        helpRow(touch, getString(R.string.g_three), getString(R.string.k_ok), 92);
        helpRow(touch, getString(R.string.k_back_key), getString(R.string.k_back), 92);
        TextView note = Look.text(this, getString(R.string.g_note), 12, Look.FAINT, false);
        note.setPadding(0, dp(10), 0, 0);
        touch.addView(note);
        LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(dp(340), ViewGroup.LayoutParams.WRAP_CONTENT);
        rp.leftMargin = dp(28);
        w.addView(touch, rp);
        return w;
    }

    /**
     * The TV's waiting screen, two columns: left the icon, the name and the card with the
     * address; right what the remote does and how to type with it, one key per row.
     */
    private LinearLayout tvWaiting() {
        LinearLayout w = new LinearLayout(this);
        w.setOrientation(LinearLayout.HORIZONTAL);
        w.setGravity(Gravity.CENTER);
        w.setBackground(Look.background());

        LinearLayout left = new LinearLayout(this);
        left.setOrientation(LinearLayout.VERTICAL);
        left.setGravity(Gravity.CENTER_HORIZONTAL);
        ImageView logo = new ImageView(this);
        logo.setImageResource(R.drawable.app_icon);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(96), dp(96));
        lp.bottomMargin = dp(10);
        left.addView(logo, lp);
        left.addView(center(Look.text(this, getString(R.string.app_name), 36, Look.INK, true)));
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setGravity(Gravity.CENTER);
        card.setBackground(Look.card(this));
        card.setPadding(dp(32), dp(26), dp(32), dp(28));
        card.addView(statusTitle(26), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        TextView hint = center(Look.text(this, getString(R.string.wait_hint), 17, Look.MUTED, false));
        hint.setPadding(0, dp(8), 0, dp(18));
        card.addView(hint, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        waitingIp = center(Look.text(this, "", 21, Look.INK, true));
        waitingIp.setBackground(Look.pill(this, 0x1FFFFFFF));
        waitingIp.setPadding(dp(24), dp(10), dp(24), dp(10));
        card.addView(waitingIp, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        cp.topMargin = dp(22);
        left.addView(card, cp);
        TextView mouse = center(Look.text(this, getString(R.string.help_mouse), 14, Look.FAINT, false));
        mouse.setPadding(dp(8), dp(16), dp(8), 0);
        left.addView(mouse, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        w.addView(left, new LinearLayout.LayoutParams(dp(400), ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout right = new LinearLayout(this);
        right.setOrientation(LinearLayout.VERTICAL);
        LinearLayout remote = helpCard(getString(R.string.help_remote));
        helpRow(remote, "OK", getString(R.string.k_ok));
        helpRow(remote, "◀ ▶", getString(R.string.k_lr));
        helpRow(remote, "▲", getString(R.string.k_up));
        helpRow(remote, "▼", getString(R.string.k_down));
        helpRow(remote, getString(R.string.k_back_key), getString(R.string.k_back));
        right.addView(remote, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        LinearLayout typing = helpCard(getString(R.string.help_type));
        helpRow(typing, "2–9", getString(R.string.t_letters));
        helpRow(typing, "1", ". , ? ! ; :");
        helpRow(typing, "0", getString(R.string.t_space));
        LinearLayout colours = new LinearLayout(this);
        colours.setOrientation(LinearLayout.HORIZONTAL);
        colours.setGravity(Gravity.CENTER_VERTICAL);
        colourKey(colours, Look.BAD, getString(R.string.c_red));
        colourKey(colours, Look.GOOD, getString(R.string.c_green));
        colourKey(colours, Look.WAIT, getString(R.string.c_yellow));
        LinearLayout.LayoutParams clp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        clp.topMargin = dp(10);
        typing.addView(colours, clp);
        TextView hold = Look.text(this, getString(R.string.t_hold), 13, Look.FAINT, false);
        hold.setPadding(0, dp(10), 0, 0);
        typing.addView(hold);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        tp.topMargin = dp(14);
        right.addView(typing, tp);
        LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(dp(380), ViewGroup.LayoutParams.WRAP_CONTENT);
        rp.leftMargin = dp(40);
        w.addView(right, rp);
        return w;
    }

    /** a glass card with a small heading, for the waiting screen's help */
    private LinearLayout helpCard(String title) {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        c.setBackground(Look.card(this));
        c.setPadding(dp(22), dp(16), dp(22), dp(18));
        TextView t = Look.text(this, title, 16, Look.INK, true);
        t.setPadding(0, 0, 0, dp(4));
        c.addView(t);
        return c;
    }

    /** one help row: the key as a small pill, then what it does */
    private void helpRow(LinearLayout card, String key, String what) { helpRow(card, key, what, 64); }

    private void helpRow(LinearLayout card, String key, String what, int chipDp) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        TextView k = Look.text(this, key, 13, Look.INK, true);
        k.setGravity(Gravity.CENTER);
        k.setBackground(Look.pill(this, 0x1FFFFFFF));
        k.setPadding(dp(8), dp(3), dp(8), dp(3));
        row.addView(k, new LinearLayout.LayoutParams(dp(chipDp), ViewGroup.LayoutParams.WRAP_CONTENT));
        TextView d = Look.text(this, what, 14, Look.MUTED, false);
        d.setPadding(dp(12), 0, 0, 0);
        row.addView(d, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        p.topMargin = dp(6);
        card.addView(row, p);
    }

    /** a coloured remote key: its dot and what it does */
    private void colourKey(LinearLayout row, int colour, String what) {
        View dot = new View(this);
        dot.setBackground(Look.dot(colour));
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(dp(12), dp(12));
        p.rightMargin = dp(6);
        row.addView(dot, p);
        TextView t = Look.text(this, what, 14, Look.MUTED, false);
        t.setPadding(0, 0, dp(16), 0);
        row.addView(t);
    }

    /** the remote as a keyboard: number keys type letters like an old phone (MultiTap),
     *  red = delete, green = Enter, yellow = capitals; text goes to the PC's active window */
    private MultiTap keypad;

    private boolean remoteTyping(int keyCode, KeyEvent event) {
        if (keypad == null) keypad = new MultiTap(new MultiTap.Out() {
            @Override public void text(String s) { if (deskLive()) desk().text(s); }
            @Override public void backspace() { if (deskLive()) desk().key(KeyEvent.KEYCODE_DEL, 0, false); }
        });
        int repeat = event.getRepeatCount();
        if (keyCode >= KeyEvent.KEYCODE_0 && keyCode <= KeyEvent.KEYCODE_9) {
            int digit = keyCode - KeyEvent.KEYCODE_0;
            if (repeat == 0) keypad.press(digit, SystemClock.uptimeMillis());
            else if (repeat == 1) keypad.hold(digit);
            return true;
        }
        switch (keyCode) {
            case KeyEvent.KEYCODE_PROG_RED: // delete; holding it keeps deleting
                keypad.reset();
                desk().key(KeyEvent.KEYCODE_DEL, 0, false);
                return true;
            case KeyEvent.KEYCODE_PROG_GREEN:
                keypad.reset();
                if (repeat == 0) desk().key(KeyEvent.KEYCODE_ENTER, 0, false);
                return true;
            case KeyEvent.KEYCODE_PROG_YELLOW:
                if (repeat == 0) showToast(getString(keypad.toggleCase() ? R.string.caps_on : R.string.caps_off));
                return true;
            default:
                return false;
        }
    }

    // ---- start / stop ----------------------------------------------------------------------

    private void startIfReady() {
        if (running || !resumed || surface == null) return;
        try {
            DatagramSocket s = new DatagramSocket(null);
            s.setReuseAddress(true);
            s.setReceiveBufferSize(4 << 20);
            s.setSoTimeout(500);
            // Android binds even 0.0.0.0 dual-stack (shows as [::]:47800), so IPv6 (the TV has a
            // public IPv6 address, with no NAT in front of it) is kept out by isLanV4 in receiveLoop
            s.bind(new InetSocketAddress(Proto.PORT));
            socket = s;
            portBusy = false;
        } catch (SocketException e) {
            Log.e(TAG, "bind", e);
            portBusy = true;
            refreshWaitState();
            return;
        }
        running = true;
        resetStream();

        codecThread = new HandlerThread("codec", Process.THREAD_PRIORITY_URGENT_DISPLAY);
        codecThread.start();
        codecHandler = new Handler(codecThread.getLooper());
        decThread = new Thread(this::decodeLoop, "decode");
        decThread.start();
        rxThread = new Thread(this::receiveLoop, "receive");
        rxThread.start();

        wifiLock.acquire();
        registerNsd();
        statAt = SystemClock.uptimeMillis();
        ui.postDelayed(statsTick, 1000);
    }

    private void stopAll() {
        if (!running) return;
        running = false;
        ui.removeCallbacks(statsTick);
        socket.close();
        decThread.interrupt();
        try {
            rxThread.join(1000);
            decThread.join(1000);
        } catch (InterruptedException ignored) {}
        codecThread.quitSafely();
        releaseTrack();
        if (wifiLock.isHeld()) wifiLock.release();
        unregisterNsd();
        frames.clear();
        streaming = false;
        showWaiting(true);
    }

    /** receive-thread state back to "nothing received yet" */
    private void resetStream() {
        cfgW = cfgH = 0;
        assembler.reset();
        sender = null;
        session = null;
        link = null;
        linkTrusted = false;
        pairCode = null;
        token = null;
        control = false;
        clockValid = false;
        bestRtt = Long.MAX_VALUE;
    }

    private void registerNsd() {
        NsdServiceInfo si = new NsdServiceInfo();
        si.setServiceName(deviceName());
        si.setServiceType(Proto.NSD_TYPE);
        si.setPort(Proto.PORT);
        nsdListener = new NsdManager.RegistrationListener() {
            @Override public void onServiceRegistered(NsdServiceInfo info) { nsdName = info.getServiceName(); }
            @Override public void onRegistrationFailed(NsdServiceInfo info, int err) { Log.w(TAG, "nsd register " + err); }
            @Override public void onServiceUnregistered(NsdServiceInfo info) {}
            @Override public void onUnregistrationFailed(NsdServiceInfo info, int err) {}
        };
        nsd.registerService(si, NsdManager.PROTOCOL_DNS_SD, nsdListener);
    }

    private volatile String nsdName; // as registered: NSD adds " (2)" when the name is taken
    private long lastWho;

    /** NAME back to the asker: the same name the PC saw in its mDNS list, so it knows this is the
     *  receiver it casts to over Wi-Fi (at most 5 answers a second) */
    private void answerWho(DatagramPacket p) {
        long now = SystemClock.uptimeMillis();
        if (now - lastWho < 200) return;
        lastWho = now;
        String name = nsdName != null ? nsdName : deviceName();
        byte[] n = name.getBytes(java.nio.charset.StandardCharsets.UTF_8);
        byte[] out = new byte[1 + Math.min(n.length, 255)];
        out[0] = Proto.NAME;
        System.arraycopy(n, 0, out, 1, out.length - 1);
        try {
            socket.send(new DatagramPacket(out, out.length, p.getSocketAddress()));
        } catch (IOException e) {
            Log.w(TAG, "who", e);
        }
    }

    private void unregisterNsd() {
        if (nsdListener == null) return;
        try {
            nsd.unregisterService(nsdListener);
        } catch (IllegalArgumentException ignored) {}
        nsdListener = null;
    }

    private String deviceName() {
        String n = Settings.Global.getString(getContentResolver(), Settings.Global.DEVICE_NAME);
        return n != null && !n.isEmpty() ? n : Build.MODEL;
    }

    private static String localIp() {
        try {
            for (NetworkInterface ni : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!ni.isUp() || ni.isLoopback()) continue;
                for (InetAddress a : Collections.list(ni.getInetAddresses())) {
                    if (a instanceof Inet4Address && a.isSiteLocalAddress()) return a.getHostAddress();
                }
            }
        } catch (SocketException ignored) {}
        return "?";
    }

    // ---- receive thread --------------------------------------------------------------------

    private void receiveLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO);
        byte[] buf = new byte[Proto.MAX_PACKET];
        DatagramPacket p = new DatagramPacket(buf, buf.length);
        while (running) {
            try {
                p.setLength(buf.length);
                socket.receive(p);
            } catch (SocketTimeoutException e) {
                checkIdle();
                continue;
            } catch (IOException e) {
                if (running) Log.w(TAG, "receive", e);
                continue;
            }
            int n = p.getLength();
            if (n < 1) continue;
            // Only devices of this home network, and while one of them casts, only that one:
            // nobody else can slip their picture (or a malformed stream for the decoder) in.
            InetAddress from = p.getAddress();
            if (!isLanV4(from)) {
                statRejected.incrementAndGet();
                continue;
            }
            if (buf[0] == Proto.WHO) { // "who are you?" from a PC on the USB cable (mDNS is not there)
                answerWho(p);
                continue;
            }
            if (buf[0] == SecureLink.HELLO) { // a caster starts the encrypted link
                onHello(p, n);
                continue;
            }
            // everything else must be sealed with this cast's keys: a packet nobody sealed for us
            // (an old unencrypted CastLink, a stranger, a changed or replayed packet) is dropped
            SecureLink.Session l = link;
            int m = buf[0] == SecureLink.SEALED && l != null ? l.open(buf, n, inner) : -1;
            if (m < 1) {
                if (buf[0] == Proto.CONFIG || buf[0] == Proto.VIDEO) noteOldSender();
                statRejected.incrementAndGet();
                continue;
            }
            // only the caster has the keys, so it may move (USB cable <-> Wi-Fi): follow it
            session = from;
            sender = p.getSocketAddress();
            long now = SystemClock.uptimeMillis();
            lastLinkAt = now;
            statBytes.addAndGet(n);
            byte type = inner[0];
            if (type == SecureLink.PAIR_SHOW) {
                if (!linkTrusted) showPairCode(l.codeText());
                continue;
            }
            if (!linkTrusted) {
                // nothing but the pairing until the user confirmed the code on the caster: it then
                // sends CONFIG with CONFIG_PAIRED, and this device keeps the caster's key
                if (type != Proto.CONFIG || m < Proto.CONFIG_LONG || (inner[14] & SecureLink.CONFIG_PAIRED) == 0) continue;
                trustCaster(linkCasterStatic);
                linkTrusted = true;
                pairCode = null;
                Log.i(TAG, "paired with " + linkCasterName);
            }
            lastPacketAt = now;
            ByteBuffer b = ByteBuffer.wrap(inner, 0, m);
            switch (b.get()) {
                case Proto.CONFIG: onConfig(b, m); break;
                case Proto.VIDEO: onVideo(inner, m); break;
                case Proto.AUDIO: onAudio(inner, m); break;
                case Proto.BYE: goIdle(); break;
                case Proto.PONG: onPong(b, m); break;
                default: break;
            }
            if (assembler.wantsKey() && streaming) requestKey();
            if (streaming) ping();
        }
    }

    private static boolean isLanV4(InetAddress a) {
        return a instanceof Inet4Address && Proto.isLanV4(a.getAddress());
    }

    private int pings;

    /** this screen in landscape (width >= height), in real pixels */
    private int[] screenSize() {
        android.graphics.Rect r = getWindowManager().getMaximumWindowMetrics().getBounds();
        int a = r.width(), b = r.height();
        return new int[] { Math.max(a, b), Math.min(a, b) };
    }

    private void ping() {
        long now = SystemClock.uptimeMillis();
        SocketAddress to = sender;
        if (to == null || now - lastPing < 500) return;
        lastPing = now;
        if (pings++ % 4 == 0) { // every 2 s: our screen size, so the PC sends the picture that big
            int[] sz = screenSize();
            byte[] info = new byte[9];
            ByteBuffer.wrap(info).put(Proto.INFO).putInt(sz[0]).putInt(sz[1]);
            sendSealed(info, info.length, to);
        }
        byte[] b = new byte[9];
        ByteBuffer.wrap(b).put(Proto.PING).putLong(System.nanoTime());
        sendSealed(b, b.length, to);
    }

    private void onPong(ByteBuffer b, int n) {
        if (n < 17) return;
        long sent = b.getLong(), phone = b.getLong();
        long rtt = System.nanoTime() - sent;
        if (rtt < 0 || rtt > 1_000_000_000L) return;
        rttNs = rtt;
        // the best sample ages 0.2 ms per ping, so clock drift gets picked up over time
        if (bestRtt != Long.MAX_VALUE) bestRtt += 200_000;
        if (rtt <= bestRtt) {
            bestRtt = rtt;
            clockOffset = phone - (sent + rtt / 2);
            clockValid = true;
        }
    }

    private void onConfig(ByteBuffer b, int n) {
        if (n < 14) return;
        int w = b.getInt(), h = b.getInt(), rate = b.getInt(), ch = b.get();
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return;
        if (!streaming) {
            streaming = true;
            showConnected();
        }
        if (w != cfgW || h != cfgH) {
            cfgW = w;
            cfgH = h;
            assembler.reset();
            frames.clear();
            frames.offer(new Frame(null, 0, false, w, h));
            ui.post(() -> fitVideo(w, h));
        }
        if (track == null && (ch == 1 || ch == 2) && rate >= 8000 && rate <= 48000) track = newTrack(rate, ch);
        if (n >= Proto.CONFIG_LONG) {
            control = (b.get() & Proto.CONFIG_CONTROL) != 0;
            byte[] t = new byte[Proto.TOKEN_BYTES];
            b.get(t);
            token = t;
        } else control = false;
    }

    private void onVideo(byte[] buf, int n) {
        if (cfgW == 0) return;
        FrameAssembler.Frame f = assembler.accept(buf, n);
        if (f == null) return;
        if (!frames.offer(new Frame(f.data, f.ptsUs, f.key, 0, 0))) {
            // decoder can't keep up: drop the backlog and restart from a keyframe
            frames.clear();
            assembler.dropped();
        }
    }

    private void onAudio(byte[] buf, int n) {
        AudioTrack t = track;
        if (t == null || n <= 5) return;
        // never blocks: if the track already holds AUDIO_BUFFER_MS, the rest is dropped
        t.write(buf, 5, n - 5, AudioTrack.WRITE_NON_BLOCKING);
        int peak = 0;
        for (int i = 5; i + 1 < n; i += 2) peak = Math.max(peak, Math.abs((short) ((buf[i] & 0xFF) | (buf[i + 1] << 8))));
        if (peak > statPeak) statPeak = peak;
    }

    private void requestKey() {
        long now = SystemClock.uptimeMillis();
        SocketAddress to = sender;
        if (to == null || now - lastKeyReq < KEYREQ_EVERY_MS) return;
        lastKeyReq = now;
        sendSealed(new byte[]{Proto.KEYREQ}, 1, to);
    }

    private void checkIdle() {
        long now = SystemClock.uptimeMillis();
        if (streaming && now - lastPacketAt > IDLE_MS) goIdle();
        else if (!streaming && link != null && now - lastLinkAt > IDLE_MS) {
            link = null; // a handshake or pairing nobody finished
            pairCode = null;
            ui.post(this::refreshWaitState);
        }
    }

    /** the old cast ends without showing the waiting screen (a new one starts right away) */
    private void goIdleQuiet() {
        streaming = false;
        if (desk != null) desk.release();
        ui.post(() -> { releaseCapture(); hidePointer(false); });
        frames.clear();
        frames.offer(new Frame(null, 0, false, -1, -1));
        releaseTrack();
        resetStream();
    }

    private void goIdle() {
        if (!streaming) return;
        streaming = false;
        if (desk != null) desk.release();
        ui.post(() -> { releaseCapture(); hidePointer(false); });
        frames.clear();
        frames.offer(new Frame(null, 0, false, -1, -1));
        releaseTrack();
        resetStream();
        showWaiting(true);
    }

    private AudioTrack newTrack(int rate, int ch) {
        int mask = ch == 1 ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
        int min = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT);
        int size = Math.max(min, rate * ch * 2 * AUDIO_BUFFER_MS / 1000);
        AudioTrack t = new AudioTrack.Builder()
                .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                        .build())
                .setAudioFormat(new AudioFormat.Builder()
                        .setSampleRate(rate)
                        .setChannelMask(mask)
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .build())
                .setBufferSizeInBytes(size)
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                .build();
        t.play();
        return t;
    }

    private void releaseTrack() {
        AudioTrack t = track;
        track = null;
        if (t != null) t.release();
    }

    // ---- decoder ---------------------------------------------------------------------------

    private void decodeLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY);
        while (running) {
            Frame f;
            try {
                f = frames.take();
            } catch (InterruptedException e) {
                break;
            }
            if (f.data == null) {
                releaseCodec();
                if (f.w > 0) startCodec(f.w, f.h);
                continue;
            }
            MediaCodec c = codec;
            if (c == null) continue;
            try {
                Integer idx = inputs.poll(100, TimeUnit.MILLISECONDS);
                if (idx == null) {
                    assembler.requestKey();
                    continue;
                }
                ByteBuffer in = c.getInputBuffer(idx);
                if (in == null || in.capacity() < f.data.length) {
                    c.queueInputBuffer(idx, 0, 0, 0, 0);
                    assembler.requestKey();
                    continue;
                }
                in.clear();
                in.put(f.data);
                synchronized (queuedPts) {
                    queuedPts[queuedPos] = f.pts;
                    queuedAt[queuedPos] = System.nanoTime();
                    queuedPos = (queuedPos + 1) % queuedPts.length;
                }
                c.queueInputBuffer(idx, 0, f.data.length, f.pts, f.key ? MediaCodec.BUFFER_FLAG_KEY_FRAME : 0);
                int fl = inFlight.incrementAndGet();
                if (fl > maxInFlight.get()) maxInFlight.set(fl);
                int qs = frames.size();
                if (qs > maxQueued.get()) maxQueued.set(qs);
            } catch (InterruptedException e) {
                break;
            } catch (RuntimeException e) {
                Log.w(TAG, "decode", e);
                int w = decW, h = decH;
                releaseCodec();
                startCodec(w, h);
            }
        }
        releaseCodec();
    }

    private void startCodec(int w, int h) {
        Surface s = surface;
        if (s == null) return;
        MediaCodecInfo info = pickDecoder();
        try {
            MediaCodec c = info != null ? MediaCodec.createByCodecName(info.getName()) : MediaCodec.createDecoderByType(MIME);
            boolean ll = info != null && info.getCapabilitiesForType(MIME)
                    .isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency);
            MediaFormat fmt = MediaFormat.createVideoFormat(MIME, w, h);
            if (ll) fmt.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
            // vendor low-latency switches (MediaTek, Amlogic); decoders ignore keys they don't know
            fmt.setInteger("vdec-lowlatency", 1);
            fmt.setInteger("vendor.low-latency.enable", 1);
            fmt.setInteger(MediaFormat.KEY_PRIORITY, 0);
            fmt.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, Math.max(w * h, 1 << 20));
            c.setCallback(codecCallback, codecHandler);
            c.setOnFrameRenderedListener(this::onRendered, codecHandler);
            c.configure(fmt, s, null, 0);
            decW = w;
            decH = h;
            codecName = c.getName();
            lowLatency = ll;
            codec = c;
            c.start();
            assembler.requestKey();
        } catch (IOException | RuntimeException e) {
            Log.e(TAG, "decoder " + w + "x" + h, e);
            codec = null;
        }
    }

    private void releaseCodec() {
        MediaCodec c = codec;
        codec = null;
        inputs.clear();
        inFlight.set(0);
        if (c == null) return;
        try {
            c.stop();
        } catch (RuntimeException ignored) {}
        c.release();
    }

    /** a hardware AVC decoder, preferring one that advertises low-latency mode */
    private static MediaCodecInfo pickDecoder() {
        MediaCodecInfo best = null;
        boolean bestLl = false;
        for (MediaCodecInfo i : new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos()) {
            if (i.isEncoder() || !i.isHardwareAccelerated() || i.getName().endsWith(".secure")) continue;
            boolean avc = false;
            for (String t : i.getSupportedTypes()) avc |= t.equalsIgnoreCase(MIME);
            if (!avc) continue;
            boolean ll = i.getCapabilitiesForType(MIME)
                    .isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency);
            if (best == null || (ll && !bestLl)) {
                best = i;
                bestLl = ll;
            }
        }
        return best;
    }

    private final MediaCodec.Callback codecCallback = new MediaCodec.Callback() {
        @Override
        public void onInputBufferAvailable(MediaCodec c, int index) {
            if (c == codec) inputs.offer(index);
        }

        @Override
        public void onOutputBufferAvailable(MediaCodec c, int index, MediaCodec.BufferInfo info) {
            boolean show = c == codec;
            if (show) {
                inFlight.decrementAndGet();
                measure(info.presentationTimeUs);
            }
            try {
                // Due "now", not at the presentation time. With a timestamp SurfaceFlinger
                // drops a frame when a newer one is ready before the next refresh, so at
                // 120 fps the 60 Hz screen always gets the newest frame instead of a queue.
                if (show) {
                    long now = System.nanoTime(), at = now;
                    if (smooth) {
                        // two frames in one refresh: the second waits for the next one instead of
                        // replacing the first; more than two refreshes behind: catch up at once
                        at = Math.max(now, nextPresentNs);
                        if (at - now > 2 * periodNs) at = now;
                        nextPresentNs = at + periodNs;
                    }
                    c.releaseOutputBuffer(index, at);
                    if (waitingUp) { waitingUp = false; ui.post(() -> { if (streaming) waiting.setVisibility(View.GONE); }); }
                    long slot = at / periodNs;
                    if (slot != lastSlot) { lastSlot = slot; statSlots.incrementAndGet(); }
                    statFrames.incrementAndGet();
                } else c.releaseOutputBuffer(index, false);
            } catch (IllegalStateException ignored) {}
        }

        @Override
        public void onError(MediaCodec c, MediaCodec.CodecException e) {
            Log.w(TAG, "codec error", e);
            if (c == codec) {
                frames.clear();
                frames.offer(new Frame(null, 0, false, decW, decH));
            }
        }

        @Override
        public void onOutputFormatChanged(MediaCodec c, MediaFormat format) {}
    };

    /** decoder time, and capture on the phone -> decoded on the TV */
    private void measure(long ptsUs) {
        long now = System.nanoTime();
        synchronized (queuedPts) {
            for (int i = 0; i < queuedPts.length; i++) {
                if (queuedPts[i] == ptsUs && queuedAt[i] != 0) {
                    statDecNs.addAndGet(now - queuedAt[i]);
                    statDecN.incrementAndGet();
                    queuedAt[i] = 0;
                    break;
                }
            }
        }
        if (clockValid) {
            long e2e = now + clockOffset - ptsUs * 1000;
            if (e2e > 0 && e2e < 2_000_000_000L) {
                statE2eNs.addAndGet(e2e);
                statE2eN.incrementAndGet();
            }
        }
    }

    /** a frame actually reached the screen (dropped ones never get here) */
    private void onRendered(MediaCodec c, long ptsUs, long nanoTime) {
        if (c != codec) return;
        if (clockValid) {
            long e2e = nanoTime + clockOffset - ptsUs * 1000;
            if (e2e > 0 && e2e < 2_000_000_000L) {
                statScreenNs.addAndGet(e2e);
                statScreenN.incrementAndGet();
            }
        }
    }

    private static String avgMs(AtomicLong sum, AtomicInteger n) {
        int c = n.getAndSet(0);
        long s = sum.getAndSet(0);
        return c == 0 ? "-" : String.valueOf(Math.round(s / (double) c / 1e6));
    }

    // ---- UI --------------------------------------------------------------------------------

    private void showWaiting(boolean show) {
        waitingUp = show;
        ui.post(() -> {
            waiting.setVisibility(show ? View.VISIBLE : View.GONE);
            if (show) refreshWaitState();
        });
    }

    /** the title row: a state dot and the title text */
    private LinearLayout statusTitle(float sp) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        waitDot = new View(this);
        waitDot.setBackground(Look.dot(Look.WAIT));
        int d = dp(sp * 0.45f);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(d, d);
        lp.rightMargin = dp(sp * 0.45f);
        row.addView(waitDot, lp);
        waitTitle = Look.text(this, getString(R.string.wait_title), sp, Look.INK, true);
        row.addView(waitTitle);
        return row;
    }

    private void setWaitState(int colour, int title, String pill) {
        setWaitState(colour, getString(title), pill);
    }

    private void setWaitState(int colour, String title, String pill) {
        waitDot.setBackground(Look.dot(colour));
        waitTitle.setText(title);
        waitingIp.setText(pill);
    }

    /** amber: ready and waiting; red: no network address or the port is taken */
    private void refreshWaitState() {
        if (streaming) return;
        String code = pairCode;
        if (code != null) {
            String who = linkCasterName == null || linkCasterName.isEmpty() ? getString(R.string.pair_other) : linkCasterName;
            setWaitState(Look.BLUE, getString(R.string.pair_title, who), getString(R.string.pair_code, code));
            return;
        }
        if (SystemClock.uptimeMillis() - oldSenderAt < 6000) {
            setWaitState(Look.BAD, R.string.old_sender, getString(R.string.old_sender_hint));
            return;
        }
        if (portBusy) { setWaitState(Look.BAD, R.string.wait_problem, getString(R.string.port_busy, Proto.PORT)); return; }
        String ip = localIp();
        if ("?".equals(ip)) setWaitState(Look.BAD, R.string.no_network, getString(R.string.no_network_hint));
        else setWaitState(Look.WAIT, R.string.wait_title, getString(onPhone ? R.string.wait_ip_phone : R.string.wait_ip, ip));
    }

    /** while the waiting screen is up, the network is checked again every 2 s */
    private final Runnable waitCheck = new Runnable() {
        @Override public void run() {
            if (!resumed) return;
            if (waiting.getVisibility() == View.VISIBLE && !streaming) refreshWaitState();
            ui.postDelayed(this, 2000);
        }
    };

    /** a device connected: green until its first picture is on screen (at most 3 s) */
    private void showConnected() {
        ui.post(() -> {
            if (!streaming) return;
            setWaitState(Look.GOOD, R.string.wait_connected, getString(onPhone ? R.string.wait_ip_phone : R.string.wait_ip, localIp()));
        });
        ui.postDelayed(() -> { if (streaming) { waitingUp = false; waiting.setVisibility(View.GONE); } }, 3000);
    }

    /**
     * Centred video rectangle. FIT: whole picture, bars where the aspect ratios differ (a
     * landscape phone is 19.5:9, the TV 16:9). FILL: covers the screen, the overhanging
     * edges are cut off by the screen. STRETCH: the whole screen, picture distorted.
     * A portrait phone is always FIT (Fill would zoom ~4x); the chosen mode comes back
     * when the phone is turned to landscape again.
     */
    private void fitVideo(int w, int h) {
        vidW = w;
        vidH = h;
        int sw = root.getWidth(), sh = root.getHeight();
        if (sw == 0 || sh == 0 || w <= 0 || h <= 0) return;
        int mode = h > w ? FIT_FIT : fitMode;
        int vw = sw, vh = sh;
        if (mode != FIT_STRETCH) {
            double s = mode == FIT_FILL ? Math.max((double) sw / w, (double) sh / h)
                    : Math.min((double) sw / w, (double) sh / h);
            vw = (int) Math.round(w * s);
            vh = (int) Math.round(h * s);
        }
        FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) video.getLayoutParams();
        lp.width = vw;
        lp.height = vh;
        lp.gravity = Gravity.CENTER;
        video.setLayoutParams(lp);
    }

    private final Runnable statsTick = new Runnable() {
        @Override
        public void run() {
            long now = SystemClock.uptimeMillis();
            float secs = Math.max(1, now - statAt) / 1000f;
            statAt = now;
            int fps = Math.round(statFrames.getAndSet(0) / secs);
            float mbps = statBytes.getAndSet(0) * 8 / secs / 1e6f;
            stats.setText(getString(R.string.stats, decW, decH, fps, mbps, assembler.lost(),
                    codecName, getString(lowLatency ? R.string.yes : R.string.no),
                    avgMs(statE2eNs, statE2eN), avgMs(statDecNs, statDecN), Math.round(rttNs / 1e6),
                    statPeak * 100 / 32768, Math.round(statSlots.getAndSet(0) / secs),
                    avgMs(statScreenNs, statScreenN)));
            statPeak = 0;
            if (streaming) Log.i(TAG, "stats " + stats.getText().toString().replace('\n', ' ')
                    + " | q=" + frames.size() + " inflight=" + inFlight.get() + " maxq=" + maxQueued.getAndSet(0)
                    + " maxin=" + maxInFlight.getAndSet(0) + " rejected=" + statRejected.get());
            ui.postDelayed(this, 1000);
        }
    };
}
