package lt.tvcast.sender;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.media.projection.MediaProjectionManager;
import android.net.Uri;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.InputType;
import android.util.Log;
import android.util.TypedValue;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.Switch;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import android.graphics.Color;
import android.view.Gravity;
import android.view.View;

import lt.tvcast.common.Proto;
import lt.tvcast.ui.Look;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Finds the TVs (mDNS), asks for screen capture and hands the result to {@link CastService}. */
public class MainActivity extends Activity {
    private static final String TAG = "phonecast";
    private static final int REQ_PERMS = 1;
    private static final int REQ_CAPTURE = 2;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private SharedPreferences prefs;
    private TextView status;
    // the USB cable: Android lets no app switch USB tethering on, so a card opens the very
    // settings screen with the switch, as soon as a cable is plugged in
    private LinearLayout usbCard;
    private TextView usbText;
    private Button usbButton;
    private final android.content.BroadcastReceiver usbState = new android.content.BroadcastReceiver() {
        @Override
        public void onReceive(android.content.Context c, Intent i) {
            showUsb(i.getBooleanExtra("connected", false), i.getBooleanExtra("rndis", false));
        }
    };
    private View statusDot;
    private LinearLayout tvList;
    private EditText hostField;
    /** the TV being started, kept only while the permission / capture screens are open (not saved) */
    private String castHost = "";
    private Switch fps120;
    private Button go;

    private NsdManager nsd;
    private NsdManager.DiscoveryListener discovery;
    private final ArrayDeque<NsdServiceInfo> toResolve = new ArrayDeque<>();
    private boolean resolving;
    /** back from the overlay permission screen: carry on with the cast */
    private boolean startAfterOverlay;
    /** service name -> address */
    private final Map<String, String> tvs = new LinkedHashMap<>();

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = getSharedPreferences("cast", MODE_PRIVATE);
        nsd = getSystemService(NsdManager.class);

        // the CastLink look (as on the iPad page): glow background, glass cards, gradient button
        getWindow().setStatusBarColor(Look.BG);
        getWindow().setNavigationBarColor(Look.BG);
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        col.setPadding(dp(20), dp(28), dp(20), dp(28));

        LinearLayout head = new LinearLayout(this);
        head.setGravity(Gravity.CENTER_VERTICAL);
        ImageView logo = new ImageView(this);
        logo.setImageResource(R.drawable.app_icon);
        head.addView(logo, new LinearLayout.LayoutParams(dp(60), dp(60)));
        LinearLayout names = new LinearLayout(this);
        names.setOrientation(LinearLayout.VERTICAL);
        names.setPadding(dp(14), 0, 0, 0);
        names.addView(Look.text(this, getString(R.string.app_name), 28, Look.INK, true));
        names.addView(Look.text(this, getString(R.string.subtitle), 14, Look.MUTED, false));
        head.addView(names);
        col.addView(head);

        // state: a dot and one or two lines
        LinearLayout stateCard = card(dp(16), dp(14));
        stateCard.setOrientation(LinearLayout.HORIZONTAL);
        stateCard.setGravity(Gravity.CENTER_VERTICAL);
        statusDot = new View(this);
        LinearLayout.LayoutParams dl = new LinearLayout.LayoutParams(dp(10), dp(10));
        dl.rightMargin = dp(12);
        stateCard.addView(statusDot, dl);
        status = Look.text(this, "", 15, Look.INK, false);
        stateCard.addView(status, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        col.addView(stateCard, gap(22));

        usbCard = card(dp(16), dp(14));
        usbCard.setOrientation(LinearLayout.VERTICAL);
        usbText = Look.text(this, "", 15, Look.INK, false);
        usbCard.addView(usbText);
        usbButton = new Button(this);
        usbButton.setAllCaps(false);
        usbButton.setText(R.string.usb_button);
        usbButton.setTextColor(Color.WHITE);
        usbButton.setStateListAnimator(null);
        usbButton.setBackground(Look.button(this, false));
        usbButton.setOnClickListener(v -> openTethering());
        LinearLayout.LayoutParams ub = gap(10);
        ub.height = dp(46);
        usbCard.addView(usbButton, ub);
        usbCard.setVisibility(View.GONE);
        col.addView(usbCard, gap(12));

        col.addView(label(R.string.tvs), gap(22));
        tvList = new LinearLayout(this);
        tvList.setOrientation(LinearLayout.VERTICAL);
        col.addView(tvList);

        col.addView(label(R.string.manual), gap(18));
        hostField = new EditText(this);
        hostField.setHint(R.string.ip_hint);
        hostField.setSingleLine(true);
        hostField.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        prefs.edit().remove("host").apply(); // older versions kept the last TV address here; the field stays empty
        hostField.setTextColor(Look.INK);
        hostField.setHintTextColor(Look.FAINT);
        hostField.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18);
        hostField.setBackground(Look.field(this));
        hostField.setPadding(dp(16), dp(14), dp(16), dp(14));
        col.addView(hostField, gap(8));

        // a switch, not a check box: Samsung's theme drew a checked box as a faint grey tick
        fps120 = new Switch(this);
        fps120.setText(R.string.fps120);
        fps120.setTextColor(Look.MUTED);
        fps120.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        fps120.setThumbTintList(new android.content.res.ColorStateList(
                new int[][] { { android.R.attr.state_checked }, {} }, new int[] { Color.WHITE, 0xFFC7C7CC }));
        fps120.setTrackTintList(new android.content.res.ColorStateList(
                new int[][] { { android.R.attr.state_checked }, {} }, new int[] { Look.BLUE, 0xFF48484A }));
        fps120.setChecked(prefs.getBoolean("fps120", false));
        fps120.setOnCheckedChangeListener((b, on) -> prefs.edit().putBoolean("fps120", on).apply());
        fps120.setPadding(dp(4), dp(8), 0, dp(8));
        col.addView(fps120, gap(12));

        go = new Button(this);
        go.setAllCaps(false);
        go.setTextColor(Color.WHITE);
        go.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18);
        go.setStateListAnimator(null);
        go.setBackground(Look.button(this, false));
        go.setOnClickListener(v -> {
            if (CastService.running) stopCast();
            else startCast(hostField.getText().toString().trim());
        });
        LinearLayout.LayoutParams gl = gap(20);
        gl.height = dp(58);
        col.addView(go, gl);

        // the other way round: the phone shows the PC's screen (the receiver of the TV app)
        Button showPc = new Button(this);
        showPc.setAllCaps(false);
        showPc.setText(R.string.show_pc);
        showPc.setTextColor(Look.INK);
        showPc.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        showPc.setStateListAnimator(null);
        showPc.setBackground(Look.tappableCard(this));
        showPc.setOnClickListener(v -> startActivity(new Intent(this, lt.tvcast.receiver.ReceiverActivity.class)));
        LinearLayout.LayoutParams pl = gap(12);
        pl.height = dp(52);
        col.addView(showPc, pl);

        ScrollView sv = new ScrollView(this);
        sv.setBackground(Look.background());
        sv.setFillViewport(true);
        sv.addView(col);
        setContentView(sv);
    }

    /** cable in, tethering off: the card with the button; tethering on: a short note; no cable: hidden */
    private void showUsb(boolean connected, boolean tethering) {
        if (usbCard == null) return;
        usbCard.setVisibility(connected ? View.VISIBLE : View.GONE);
        usbText.setText(tethering ? R.string.usb_ready : R.string.usb_hint);
        usbButton.setVisibility(tethering ? View.GONE : View.VISIBLE);
    }

    /** the tethering screen (the switch "USB tethering"); its name differs between makers */
    private void openTethering() {
        Intent[] tries = {
            new Intent("android.settings.TETHER_SETTINGS"), // Samsung and most phones (a hidden but public action)
            new Intent().setClassName("com.android.settings", "com.android.settings.TetherSettings"),
            new Intent().setClassName("com.android.settings", "com.android.settings.Settings$TetherSettingsActivity"),
            new Intent(Settings.ACTION_WIRELESS_SETTINGS),
        };
        for (Intent i : tries) {
            try {
                startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                Toast.makeText(this, R.string.usb_toast, Toast.LENGTH_LONG).show();
                return;
            } catch (android.content.ActivityNotFoundException | SecurityException ignored) {
            }
        }
    }

    private LinearLayout card(int padX, int padY) {
        LinearLayout c = new LinearLayout(this);
        c.setBackground(Look.card(this));
        c.setPadding(padX, padY, padX, padY);
        return c;
    }

    private TextView label(int res) {
        TextView t = Look.text(this, getString(res), 13, Look.FAINT, true);
        t.setAllCaps(true);
        t.setLetterSpacing(0.06f);
        t.setPadding(dp(4), 0, 0, 0);
        return t;
    }

    /** full width, this much space above */
    private LinearLayout.LayoutParams gap(int topDp) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        p.topMargin = dp(topDp);
        return p;
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }

    @Override
    protected void onResume() {
        // a sticky system broadcast: the current state comes at once, then every change
        android.content.IntentFilter f = new android.content.IntentFilter("android.hardware.usb.action.USB_STATE");
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(usbState, f, android.content.Context.RECEIVER_EXPORTED);
        else registerReceiver(usbState, f);
        super.onResume();
        startDiscovery();
        refresh.run();
        if (startAfterOverlay) {
            startAfterOverlay = false;
            startCast(castHost);
        }
    }

    @Override
    protected void onPause() {
        try { unregisterReceiver(usbState); } catch (IllegalArgumentException ignored) {}
        ui.removeCallbacks(refresh);
        stopDiscovery();
        super.onPause();
    }

    private final Runnable refresh = new Runnable() {
        @Override
        public void run() {
            int dot;
            if (CastService.running) {
                String s = getString(R.string.casting_fps, CastService.host, CastService.runningFps);
                if (CastService.noAudio) s += "\n" + getString(R.string.no_audio);
                if (CastService.oldReceiver) s = getString(R.string.old_receiver);
                askPairing();
                status.setText(s);
                dot = Look.GOOD;
                if (!getString(R.string.stop).contentEquals(go.getText())) {
                    go.setText(R.string.stop);
                    go.setBackground(Look.button(MainActivity.this, true));
                }
            } else {
                String s = tvs.isEmpty() ? getString(R.string.searching) + "\n" + getString(R.string.found_none) : getString(R.string.ready);
                if (!Settings.canDrawOverlays(MainActivity.this)) s += "\n" + getString(R.string.overlay_hint);
                status.setText(s);
                dot = tvs.isEmpty() ? Look.WAIT : Look.BLUE;
                if (!getString(R.string.start).contentEquals(go.getText())) {
                    go.setText(R.string.start);
                    go.setBackground(Look.button(MainActivity.this, false));
                }
            }
            statusDot.setBackground(Look.dot(dot));
            ui.postDelayed(this, 1000);
        }
    };

    private android.app.AlertDialog pairDialog;

    /** the first cast to a TV: the user compares the code with the TV's screen */
    private void askPairing() {
        String code = CastService.pairCode;
        if (code == null || pairDialog != null) return;
        String who = CastService.pairPeer == null || CastService.pairPeer.isEmpty() ? "TV" : CastService.pairPeer;
        pairDialog = new android.app.AlertDialog.Builder(this)
                .setTitle(getString(R.string.pair_title, who))
                .setMessage(getString(R.string.pair_message, code))
                .setCancelable(false)
                .setPositiveButton(R.string.pair_yes, (d, w) -> { CastService.pairAnswer = 1; pairDialog = null; })
                .setNegativeButton(R.string.pair_no, (d, w) -> { CastService.pairAnswer = -1; pairDialog = null; })
                .show();
    }

    // ---- casting ---------------------------------------------------------------------------

    private void startCast(String host) {
        if (host.isEmpty()) {
            Toast.makeText(this, R.string.bad_ip, Toast.LENGTH_SHORT).show();
            return;
        }
        castHost = host;
        // once: "Display over other apps" lets the service keep the screen on (see CastService.keepAwake)
        if (!Settings.canDrawOverlays(this) && !prefs.getBoolean("askedOverlay", false)) {
            prefs.edit().putBoolean("askedOverlay", true).apply();
            startAfterOverlay = true;
            Toast.makeText(this, R.string.overlay_hint, Toast.LENGTH_LONG).show();
            startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:" + getPackageName())));
            return;
        }
        List<String> need = new ArrayList<>();
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            need.add(Manifest.permission.RECORD_AUDIO);
        }
        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            need.add(Manifest.permission.POST_NOTIFICATIONS);
        }
        if (need.isEmpty() || prefs.getBoolean("asked", false)) {
            askCapture();
        } else {
            prefs.edit().putBoolean("asked", true).apply();
            requestPermissions(need.toArray(new String[0]), REQ_PERMS);
        }
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] perms, int[] results) {
        // sound is optional: without RECORD_AUDIO the service sends video only
        if (code == REQ_PERMS) askCapture();
    }

    private void askCapture() {
        MediaProjectionManager mpm = getSystemService(MediaProjectionManager.class);
        startActivityForResult(mpm.createScreenCaptureIntent(), REQ_CAPTURE);
    }

    @Override
    protected void onActivityResult(int code, int result, Intent data) {
        if (code != REQ_CAPTURE || result != RESULT_OK || data == null) return;
        Intent i = new Intent(this, CastService.class)
                .putExtra(CastService.EXTRA_CODE, result)
                .putExtra(CastService.EXTRA_DATA, data)
                .putExtra(CastService.EXTRA_HOST, castHost)
                .putExtra(CastService.EXTRA_FPS, prefs.getBoolean("fps120", false) ? 120 : 60);
        startForegroundService(i);
    }

    private void stopCast() {
        startService(new Intent(this, CastService.class).setAction(CastService.ACTION_STOP));
    }

    // ---- finding TVs -----------------------------------------------------------------------

    private void startDiscovery() {
        discovery = new NsdManager.DiscoveryListener() {
            @Override public void onStartDiscoveryFailed(String type, int err) { Log.w(TAG, "discovery " + err); }
            @Override public void onStopDiscoveryFailed(String type, int err) {}
            @Override public void onDiscoveryStarted(String type) {}
            @Override public void onDiscoveryStopped(String type) {}

            @Override
            public void onServiceFound(NsdServiceInfo info) {
                ui.post(() -> {
                    toResolve.add(info);
                    resolveNext();
                });
            }

            @Override
            public void onServiceLost(NsdServiceInfo info) {
                ui.post(() -> {
                    tvs.remove(info.getServiceName());
                    showTvs();
                });
            }
        };
        nsd.discoverServices(Proto.NSD_TYPE, NsdManager.PROTOCOL_DNS_SD, discovery);
    }

    private void stopDiscovery() {
        try {
            nsd.stopServiceDiscovery(discovery);
        } catch (IllegalArgumentException ignored) {}
        toResolve.clear();
    }

    /** NsdManager resolves one service at a time */
    @SuppressWarnings("deprecation")
    private void resolveNext() {
        if (resolving || toResolve.isEmpty()) return;
        resolving = true;
        nsd.resolveService(toResolve.poll(), new NsdManager.ResolveListener() {
            @Override
            public void onResolveFailed(NsdServiceInfo info, int err) {
                ui.post(() -> {
                    resolving = false;
                    resolveNext();
                });
            }

            @Override
            public void onServiceResolved(NsdServiceInfo info) {
                String addr = info.getHost() != null ? info.getHost().getHostAddress() : null;
                ui.post(() -> {
                    if (addr != null) tvs.put(info.getServiceName(), addr);
                    showTvs();
                    resolving = false;
                    resolveNext();
                });
            }
        });
    }

    private void showTvs() {
        tvList.removeAllViews();
        boolean first = true;
        for (Map.Entry<String, String> e : tvs.entrySet()) {
            // a glass card per TV: a TV icon, its name with the address below; tap to cast
            LinearLayout b = new LinearLayout(this);
            b.setOrientation(LinearLayout.HORIZONTAL);
            b.setGravity(android.view.Gravity.CENTER_VERTICAL);
            b.setBackground(Look.tappableCard(this));
            b.setPadding(dp(16), dp(14), dp(18), dp(14));
            b.setClickable(true);
            b.setFocusable(true);
            android.widget.ImageView icon = new android.widget.ImageView(this);
            icon.setImageResource(R.drawable.ic_tv);
            android.graphics.drawable.GradientDrawable tile = new android.graphics.drawable.GradientDrawable(
                    android.graphics.drawable.GradientDrawable.Orientation.TL_BR, new int[] { Look.BLUE, Look.VIOLET });
            tile.setCornerRadius(dp(12));
            icon.setBackground(tile);
            icon.setPadding(dp(10), dp(10), dp(10), dp(10));
            LinearLayout.LayoutParams il = new LinearLayout.LayoutParams(dp(46), dp(46));
            il.rightMargin = dp(14);
            b.addView(icon, il);
            LinearLayout texts = new LinearLayout(this);
            texts.setOrientation(LinearLayout.VERTICAL);
            texts.addView(Look.text(this, e.getKey(), 18, Look.INK, true));
            texts.addView(Look.text(this, e.getValue(), 14, Look.MUTED, false));
            b.addView(texts, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
            String addr = e.getValue();
            b.setOnClickListener(v -> {
                if (!CastService.running) startCast(addr);
            });
            tvList.addView(b, gap(first ? 8 : 10));
            first = false;
        }
        refresh.run();
        ui.removeCallbacks(refresh);
        ui.postDelayed(refresh, 1000);
    }
}
