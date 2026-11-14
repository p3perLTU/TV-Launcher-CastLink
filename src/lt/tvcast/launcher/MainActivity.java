package lt.tvcast.launcher;

import android.animation.ValueAnimator;
import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.content.res.Resources;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.graphics.Shader;
import android.graphics.Typeface;
import android.graphics.drawable.BitmapDrawable;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.media.tv.TvContract;
import android.media.tv.TvInputInfo;
import android.media.tv.TvInputManager;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Bundle;
import android.os.Handler;
import android.provider.Settings;
import android.util.DisplayMetrics;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.animation.DecelerateInterpolator;
import android.view.animation.OvershootInterpolator;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Calendar;
import java.util.Collections;
import java.util.Comparator;
import java.util.Date;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Minimal Apple TV style launcher. No AndroidX, no Gradle: plain views and a hand-made
 * D-pad state machine so that navigation is fully deterministic.
 */
public class MainActivity extends Activity {

    // Home screen: two 2x2 blocks with the weather between them, like the reference picture.
    // Order = left block row by row, then right block row by row.
    private static final String[] DOCK_PKGS = {
            "lt.go3.android.tv",                    // left:  Go3      YouTube
            "com.google.android.youtube.tv",
            "com.plexapp.android",                  //        Plex     Spotify
            "com.spotify.tv.android",
            "com.netflix.ninja",                    // right: Netflix  Twitch
            "tv.twitch.android.app",
            "com.google.android.youtube.tvmusic",   //        YT Music Play Store
            "com.android.vending"
    };
    private static final int COLS = 6; // "all apps" page

    // top bar buttons, left to right
    private static final int P_APPS = 0, P_INPUTS = 1, P_WIFI = 2, P_SETTINGS = 3;

    // Weather from Open-Meteo (no API key). The city is chosen on the TV (apps page -> Weather)
    // and kept in prefs cityName/cityLat/cityLon; Vilnius until then.
    private static final String DEFAULT_CITY = "Vilnius";
    private static final float DEFAULT_LAT = 54.6872f, DEFAULT_LON = 25.2797f;
    private static final long WEATHER_EVERY = 60 * 60 * 1000L;
    // quick picks, no typing needed; anything else via "Search city…"
    private static final Object[][] LT_CITIES = {
            {"Vilnius", 54.6872f, 25.2797f}, {"Kaunas", 54.9027f, 23.9096f},
            {"Klaipėda", 55.7033f, 21.1443f}, {"Šiauliai", 55.9333f, 23.3167f},
            {"Panevėžys", 55.7333f, 24.35f}, {"Alytus", 54.4f, 24.05f},
            {"Marijampolė", 54.5667f, 23.35f}, {"Mažeikiai", 56.3167f, 22.3333f},
            {"Jonava", 55.0833f, 24.2833f}, {"Utena", 55.5f, 25.6f},
            {"Kėdainiai", 55.2833f, 23.9667f}, {"Telšiai", 55.9833f, 22.25f},
            {"Tauragė", 55.25f, 22.2833f}, {"Ukmergė", 55.25f, 24.75f},
            {"Visaginas", 55.6f, 26.4333f}, {"Palanga", 55.9175f, 21.0686f},
            {"Plungė", 55.9167f, 21.85f}, {"Kretinga", 55.8833f, 21.2333f},
            {"Radviliškis", 55.8f, 23.5333f}, {"Druskininkai", 54.0167f, 23.9667f}};

    private String weatherUrl() {
        SharedPreferences p = getPreferences(MODE_PRIVATE);
        return "https://api.open-meteo.com/v1/forecast"
                + "?latitude=" + p.getFloat("cityLat", DEFAULT_LAT)
                + "&longitude=" + p.getFloat("cityLon", DEFAULT_LON)
                + "&current=temperature_2m,apparent_temperature,weather_code,is_day"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
                + "&timezone=auto&forecast_days=5";
    }

    private String cityName() {
        return getPreferences(MODE_PRIVATE).getString("cityName", DEFAULT_CITY);
    }

    private static final int MAX_HTTP_BYTES = 1024 * 1024;
    private static final long MAX_VIDEO_BYTES = 500L * 1024 * 1024;
    private static final long KEEP_FREE_BYTES = 300L * 1024 * 1024; // left for the system and apps

    // small GET helper (worker threads only); null on any failure
    private static String httpGet(String url) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(8000);
            c.setReadTimeout(8000);
            if (c.getResponseCode() != 200) return null;
            InputStream in = c.getInputStream();
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[4096];
            int n;
            while ((n = in.read(buf)) > 0) {
                out.write(buf, 0, n);
                // weather / geocoding answers are a few KB; never buffer an unbounded body
                if (out.size() > MAX_HTTP_BYTES) {
                    in.close();
                    return null;
                }
            }
            in.close();
            return out.toString("UTF-8");
        } catch (Exception e) {
            return null;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    private static final int PILLS = 0, DOCK = 1, GRID = 2, DROP = 3, BG = 4, PHOTO = 5, AVATAR = 6,
            MENU = 7, PICK = 8, MOVE = 9, // long-press OK on a dock tile: menu, app list, moving
            CITY = 10, CITYTYPE = 11;      // weather city list / typing a search

    // Letter in the account circle next to the greeting, like the Google TV launcher.
    // Android doesn't show Google accounts to other apps (and the photo needs Google sign-in
    // libraries), so it is chosen on the TV: hold OK on the circle, Left/Right, OK.
    // Pref "accountInitial"; "" (the default on a new TV) shows a person icon.
    private static final String[] INITIALS = {"", "A", "Ą", "B", "C", "Č", "D", "E", "Ę", "Ė", "F",
            "G", "H", "I", "Į", "Y", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "Š", "T", "U",
            "Ų", "Ū", "V", "W", "X", "Z", "Ž"};
    private boolean avatarEdit;
    private int avatarIdx;
    private static final int ACCOUNT_COLOR = 0xFF1A73E8;
    private static final String AVATAR_KEY = "__avatar__";

    // Background themes, picked from the "Background" tile at the end of the apps page.
    // Wave themes: sky x3, light crest x2, main wave x3, fold x2, bottom x2.
    private static final int[][] WAVE_THEMES = {
            {0xFF0A2A6E, 0xFF1456B8, 0xFF2F86E0, 0x803D9BF0, 0x001456B8,
                    0xFFD81B60, 0xFFF4511E, 0xFFFF9800, 0xCCFFA94D, 0x99E8456B, 0xCCAD1457, 0xCC6A1B9A},
            {0xFF05070F, 0xFF0B1530, 0xFF142850, 0x60304F8F, 0x00142850,
                    0xFF1E2A5A, 0xFF2B2F6E, 0xFF3A2E6A, 0x99404A8A, 0x77302858, 0xCC1A1238, 0xCC0E0A20},
            {0xFF1A0B3D, 0xFF3A1670, 0xFF5B2A9E, 0x809C6BE0, 0x003A1670,
                    0xFF8E24AA, 0xFFD81B60, 0xFFFF4081, 0xCCF48FB1, 0x99AB47BC, 0xCC4A148C, 0xCC311B92},
            {0xFF032B2F, 0xFF05505A, 0xFF0A7C86, 0x8026C6DA, 0x0005505A,
                    0xFF00897B, 0xFF26A69A, 0xFF9CCC65, 0xCC80CBC4, 0x994DB6AC, 0xCC00695C, 0xCC004D40},
            {0xFF061A40, 0xFF0B3D91, 0xFF1565C0, 0x8064B5F6, 0x000B3D91,
                    0xFF1E88E5, 0xFF42A5F5, 0xFF90CAF9, 0xCCBBDEFB, 0x9964B5F6, 0xCC0D47A1, 0xCC1A237E},
    };
    private static final int[][] SOLID_THEMES = {
            {0xFF000000, 0xFF0D0D12, 0xFF1A1A24},
            {0xFF020814, 0xFF07183A, 0xFF0C2A5E},
            {0xFF1C1C1E, 0xFF2C2C2E, 0xFF3A3A3C},
    };
    private static final int[] THEME_NAMES = {
            R.string.theme_sunset, R.string.theme_night, R.string.theme_violet, R.string.theme_sea,
            R.string.theme_blue, R.string.theme_dark, R.string.theme_navy, R.string.theme_graphite,
            R.string.theme_photo, R.string.theme_video};
    private static final int THEME_PHOTO = 8, THEME_VIDEO = 9;
    private static final int[] DIM_LEVELS = {0, 20, 40, 60}; // % of black over the background
    // how the photo is placed, like Windows' "Choose a fit"
    private static final int FIT_FILL = 0, FIT_FIT = 1, FIT_STRETCH = 2, FIT_CENTER = 3;
    private static final int[] FIT_NAMES = {
            R.string.fit_fill, R.string.fit_fit, R.string.fit_stretch, R.string.fit_center};
    // colour of text/icons on the background: dark for light photos
    private static final int INK_AUTO = 0, INK_LIGHT = 1, INK_DARK = 2;
    private static final int[] INK_NAMES = {R.string.ink_auto, R.string.ink_light, R.string.ink_dark};
    private static final float AUTO_DARK_ABOVE = 0.38f; // average photo luminance, after dimming
    private static final String BG_PKG = "__background__"; // the settings tile on the apps page
    private static final String RAM_PKG = "__clear_ram__"; // the "Clear RAM" tile
    private static final String WX_PKG = "__weather_city__"; // the "Weather" (city) tile

    private static class App {
        String pkg;
        String cls;
        CharSequence label;
        Drawable image;
        Drawable logo; // banner with its flat background cut out, or null
        ResolveInfo ri;
        boolean loaded;
    }

    private static class InputItem {
        String id;
        String label;
        boolean passthrough;
    }

    private float density;
    private int sw, sh;
    private int marginX, dockMarginX;
    private int dockW, dockH, dockGap, dockGapX, dockTop, blockW, weatherW;  // home blocks
    private int tileW, tileH, colW, gridTop, pitch, pageH;          // all-apps page

    private FrameLayout root;
    private FrameLayout content;    // home screen
    private FrameLayout appsPage;   // "all apps" page, scrolls vertically
    private View appsScrim;
    private boolean appsOpen, appsBuilt;
    private HighlightView highlight, gridHighlight;
    private WaveView wave;
    private int bgTheme, bgDim, bgFit, bgInk, bgRow, photoIdx;
    private boolean darkUi; // dark text and icons, light glass (decided once per onCreate)
    private LinearLayout bgPanel, bgRowsBox, photoBox;
    private final TextView[] bgRows = new TextView[5]; // theme, brightness, fit, text, photo
    private static final int ROW_PHOTO = 4;
    private LinearLayout tileMenu, menuRowsBox, pickBox;
    private TextView menuTitle, menuHint;
    private final TextView[] menuRows = new TextView[2]; // change app, move
    private int menuRow, pickIdx;
    private final List<App> pickApps = new ArrayList<App>();
    private final List<android.net.Uri> photoUris = new ArrayList<android.net.Uri>();
    private final List<Boolean> photoIsVideo = new ArrayList<Boolean>();
    // looping, muted video background (THEME_VIDEO); the player exists only while resumed
    private android.view.TextureView videoView;
    private android.media.MediaPlayer player;
    private android.graphics.SurfaceTexture videoSurface;
    private int videoW, videoH;
    private boolean isResumed, recreating;
    private final List<String> photoNames = new ArrayList<String>();
    private TextView greeting, clock, dateView, avatar;
    private RectF avatarRect;
    private FrameLayout dropBox;
    private LinearLayout weatherBox;
    private boolean weatherLoading;

    private final List<App> dockApps = new ArrayList<App>();
    private final List<App> gridApps = new ArrayList<App>();
    private final List<View> dockTiles = new ArrayList<View>();
    private final List<View> gridTiles = new ArrayList<View>();
    private final List<RectF> dockRects = new ArrayList<RectF>();
    private final List<RectF> gridRects = new ArrayList<RectF>();
    private final List<RectF> pillRects = new ArrayList<RectF>();
    private final List<TextView> dropItems = new ArrayList<TextView>();
    private final List<InputItem> inputs = new ArrayList<InputItem>();
    private final TextView[] pills = new TextView[4];

    private int area = DOCK;
    private int pillIdx = 0, dockIdx = 0, gridRow = 0, gridCol = 0, dropIdx = 0;
    private View scaledTile;

    private final Handler handler = new Handler();
    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            updateClock();
            // the clock shows seconds: tick right after the next full second
            handler.postDelayed(this, 1000 - System.currentTimeMillis() % 1000 + 20);
        }
    };
    private final Runnable weatherTick = new Runnable() {
        @Override
        public void run() {
            fetchWeather();
            handler.postDelayed(this, WEATHER_EVERY);
        }
    };

    private int dp(float v) {
        return Math.round(v * density);
    }

    // the TV's system language (Settings -> Language); day/month names follow it
    private Locale loc() {
        return getResources().getConfiguration().getLocales().get(0);
    }

    // ---------------------------------------------------------------- lifecycle

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        DisplayMetrics m = getResources().getDisplayMetrics();
        density = m.density;
        sw = m.widthPixels;
        sh = m.heightPixels;
        marginX = dp(48);

        // home: [2x2 block] [weather] [2x2 block], anchored to the bottom of the screen
        // tiles are 70% of what fits next to a 300dp weather column (user asked for 30% smaller);
        // the blocks stay at the screen edges and the weather centres in the wider gap
        int fullBlockW = (sw - 2 * marginX - dp(300) - 2 * dp(24)) / 2;
        dockGap = dp(24); // between the two rows
        dockW = (fullBlockW - dp(16)) / 2 * 70 / 100;
        dockH = dockW * 10 / 16;
        // blocks line up with the clock (left) and the Settings pill (right)
        dockMarginX = marginX;
        // even rhythm across the row: the gap between two tiles equals the gap between a
        // block and the weather's content (its widest row, the 4-day forecast, is 256dp)
        int wxContent = 4 * dp(64);
        // ...then made 20% tighter at the user's request (the weather gap takes the rest)
        dockGapX = (sw - 2 * dockMarginX - 4 * dockW - wxContent) / 4 * 80 / 100;
        blockW = 2 * dockW + dockGapX;
        weatherW = sw - 2 * dockMarginX - 2 * blockW;
        dockTop = sh - dp(48) - (2 * dockH + dockGap);

        // all-apps page
        colW = (sw - 2 * marginX) / COLS;
        tileW = colW - dp(16);
        tileH = tileW * 11 / 16;
        gridTop = dp(104);
        pitch = tileH + dp(24);

        // background first: whether text is dark depends on it, and logos are built for it
        SharedPreferences prefs = getPreferences(MODE_PRIVATE);
        bgTheme = prefs.getInt("bgTheme", 0);
        bgDim = prefs.getInt("bgDim", 0);
        bgFit = prefs.getInt("bgFit", FIT_FILL);
        bgInk = prefs.getInt("bgInk", INK_AUTO);
        wave = new WaveView(this);
        wave.fit = bgFit;
        if (bgTheme == THEME_PHOTO) wave.photo = loadSavedPhoto();
        if (bgTheme == THEME_PHOTO && wave.photo == null) bgTheme = 0;
        if (bgTheme == THEME_VIDEO && !videoFile().exists()) bgTheme = 0;
        darkUi = wantDarkUi();
        wave.setTheme(bgTheme, DIM_LEVELS[bgDim]);

        loadApps();
        android.content.IntentFilter pf = new android.content.IntentFilter();
        pf.addAction(Intent.ACTION_PACKAGE_ADDED);
        pf.addAction(Intent.ACTION_PACKAGE_REMOVED);
        pf.addDataScheme("package");
        registerReceiver(packageReceiver, pf);
        loadInputs();
        pageH =gridTop + Math.max(gridRows(), 1) * pitch + dp(60);

        root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);
        root.setClipChildren(true);
        // video below the WaveView, which then only draws its veils and dimming on top
        videoView = new android.view.TextureView(this);
        videoView.setSurfaceTextureListener(new android.view.TextureView.SurfaceTextureListener() {
            @Override
            public void onSurfaceTextureAvailable(android.graphics.SurfaceTexture s, int w, int h) {
                videoSurface = s;
                startVideo();
            }

            @Override
            public void onSurfaceTextureSizeChanged(android.graphics.SurfaceTexture s, int w, int h) {
                fitVideo();
            }

            @Override
            public boolean onSurfaceTextureDestroyed(android.graphics.SurfaceTexture s) {
                stopVideo();
                videoSurface = null;
                return true;
            }

            @Override
            public void onSurfaceTextureUpdated(android.graphics.SurfaceTexture s) {
            }
        });
        videoView.setVisibility(bgTheme == THEME_VIDEO ? View.VISIBLE : View.GONE);
        root.addView(videoView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        root.addView(wave, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        setContentView(root);
        getWindow().getDecorView().setBackgroundColor(Color.BLACK);

        content = new FrameLayout(this);
        root.addView(content, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        appsScrim = new View(this);
        appsScrim.setBackgroundColor(darkUi ? 0x66FFFFFF : 0x66000000);
        appsScrim.setVisibility(View.GONE);
        root.addView(appsScrim, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        appsPage = new FrameLayout(this);
        appsPage.setVisibility(View.GONE);
        root.addView(appsPage, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, pageH));

        buildUi();
        buildBgPanel();

        if (dockApps.isEmpty()) area = PILLS;
        restoreFocus();
        if (area == GRID) showApps(true, false);
        applyFocus(false);

        askPermissionsOnce();

        // the text colour was changed from the Background panel: the activity was
        // recreated with the new colours, so put the user back into the panel
        int reopenRow = prefs.getInt("reopenBgRow", -1);
        if (reopenRow >= 0) {
            prefs.edit().remove("reopenBgRow").apply();
            if (!appsOpen) showApps(true, false);
            openBg();
            bgRow = reopenRow;
            styleBgRows();
        }
    }

    // On a TV where nobody ran "pm grant" over ADB the launcher asks itself, once:
    // location (only to read the Wi-Fi name) and storage (background photo / video list).
    private static final String[] RUNTIME_PERMS = {
            android.Manifest.permission.ACCESS_FINE_LOCATION,
            android.Manifest.permission.READ_EXTERNAL_STORAGE};

    private void askPermissionsOnce() {
        SharedPreferences p = getPreferences(MODE_PRIVATE);
        if (p.getBoolean("permsAsked", false)) return;
        List<String> missing = new ArrayList<String>();
        for (String perm : RUNTIME_PERMS) {
            if (checkSelfPermission(perm) != PackageManager.PERMISSION_GRANTED) missing.add(perm);
        }
        p.edit().putBoolean("permsAsked", true).apply();
        if (!missing.isEmpty()) requestPermissions(missing.toArray(new String[0]), 1);
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] perms, int[] results) {
        super.onRequestPermissionsResult(code, perms, results);
        refreshWifiPill(); // the network name may be readable now
        if (code == 2 && checkSelfPermission(android.Manifest.permission.READ_EXTERNAL_STORAGE)
                == PackageManager.PERMISSION_GRANTED) {
            openPhotoList(); // asked from the Photo row: continue where the user was
        }
    }

    // Dark ink when asked for, or (automatic) when the photo is bright on average.
    private boolean wantDarkUi() {
        if (bgInk != INK_AUTO) return bgInk == INK_DARK;
        float lum;
        if (bgTheme == THEME_PHOTO && wave.photo != null) {
            lum = avgLum(wave.photo);
        } else if (bgTheme == THEME_VIDEO) {
            lum = getPreferences(MODE_PRIVATE).getFloat("videoLum", 0f); // from its first frame
        } else {
            return false; // the built-in themes are dark
        }
        return lum * (100 - DIM_LEVELS[bgDim]) / 100f > AUTO_DARK_ABOVE;
    }

    private static float avgLum(Bitmap b) {
        Bitmap small = Bitmap.createScaledBitmap(b, 48, 27, true);
        long sum = 0;
        for (int y = 0; y < 27; y++) {
            for (int x = 0; x < 48; x++) {
                int p = small.getPixel(x, y);
                sum += Color.red(p) * 299 + Color.green(p) * 587 + Color.blue(p) * 114;
            }
        }
        if (small != b) small.recycle();
        return sum / (1000f * 255f * 48 * 27);
    }

    // text/icon colour on the background with the given alpha
    private int ink(int alpha) {
        return darkUi ? Color.argb(alpha, 0x14, 0x14, 0x1A) : Color.argb(alpha, 255, 255, 255);
    }

    // Called after any background change: if the text colour has to change, rebuild the
    // whole screen (simplest way to recolour every view and logo) and reopen the panel.
    private void recolourIfNeeded() {
        if (wantDarkUi() == darkUi) return;
        getPreferences(MODE_PRIVATE).edit().putInt("reopenBgRow", bgRow).commit();
        recreating = true; // keys pressed now would change settings in this dying instance
        recreate();
    }

    // The app list is read once in onCreate, so an app installed or removed while the launcher
    // runs would be missing (or a dead tile) until the next restart: rebuild on such a change.
    // Updates (EXTRA_REPLACING) keep the list as it is, so Play Store updates cause no flicker,
    // but the updated app's ResolveInfo is read again (refreshUpdated): the old one points into
    // the deleted APK, so its banner and icon no longer load (the tile showed the Android robot)
    // and its name stayed the old one.
    private boolean appsChanged;
    private final HashSet<String> updatedPkgs = new HashSet<String>();
    private final android.content.BroadcastReceiver packageReceiver = new android.content.BroadcastReceiver() {
        @Override
        public void onReceive(android.content.Context c, Intent i) {
            if (i.getBooleanExtra(Intent.EXTRA_REPLACING, false)) {
                if (Intent.ACTION_PACKAGE_ADDED.equals(i.getAction()) && i.getData() != null) {
                    updatedPkgs.add(i.getData().getSchemeSpecificPart());
                    if (isResumed && !recreating) refreshUpdated();
                }
                return;
            }
            if (isResumed && !recreating) {
                recreating = true;
                recreate();
            } else {
                appsChanged = true;
            }
        }
    };

    // The launcher process is often killed on this 1 GB TV, so the focused app
    // is kept by package name (indices shift when apps are installed/removed).
    private void saveFocus() {
        String pkg = null;
        if (area == DOCK && !dockApps.isEmpty()) {
            pkg = dockApps.get(dockIdx).pkg;
        } else if (area == GRID && !gridApps.isEmpty()) {
            pkg = gridApps.get(gridRow * COLS + gridCol).pkg;
        } else if (area == BG || area == PHOTO) {
            pkg = BG_PKG;
        } else if (area == AVATAR) {
            pkg = AVATAR_KEY;
        } else if (area == CITY || area == CITYTYPE) {
            pkg = WX_PKG;
        } else if ((area == MENU || area == PICK || area == MOVE) && dockIdx < dockApps.size()) {
            pkg = dockApps.get(dockIdx).pkg;
        }
        getPreferences(MODE_PRIVATE).edit()
                .putString("focusPkg", pkg)
                .putInt("pillIdx", area == PILLS || area == DROP ? pillIdx : -1)
                .apply();
    }

    private void restoreFocus() {
        SharedPreferences p = getPreferences(MODE_PRIVATE);
        int pill = p.getInt("pillIdx", -1);
        if (pill >= 0 && pill < pills.length) {
            area = PILLS;
            pillIdx = pill;
            return;
        }
        String pkg = p.getString("focusPkg", null);
        if (pkg == null) return;
        if (pkg.equals(AVATAR_KEY)) {
            area = AVATAR;
            return;
        }
        for (int i = 0; i < dockApps.size(); i++) {
            if (dockApps.get(i).pkg.equals(pkg)) {
                area = DOCK;
                dockIdx = i;
                return;
            }
        }
        for (int k = 0; k < gridApps.size(); k++) {
            if (gridApps.get(k).pkg.equals(pkg)) {
                area = GRID;
                gridRow = k / COLS;
                gridCol = k % COLS;
                return;
            }
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        isResumed = true;
        if (appsChanged && !recreating) {
            appsChanged = false;
            recreating = true;
            recreate();
            return;
        }
        if (!updatedPkgs.isEmpty() && !recreating) {
            refreshUpdated();
            if (recreating) return;
        }
        startVideo();
        // put back what onTrimMemory dropped
        if (bgTheme == THEME_PHOTO && wave.photo == null) {
            wave.photo = loadSavedPhoto();
            wave.invalidate();
        }
        if (appsOpen && !appsBuilt) {
            buildAppsPage();
            applyFocus(false);
        }
        handler.post(tick);
        refreshWifiPill();
        // fetch only when the cached forecast is an hour old; never while another app is open
        long age = System.currentTimeMillis() - getPreferences(MODE_PRIVATE).getLong("weatherAt", 0);
        handler.postDelayed(weatherTick, Math.max(0, WEATHER_EVERY - age));
    }

    @Override
    protected void onDestroy() {
        unregisterReceiver(packageReceiver);
        super.onDestroy();
    }

    @Override
    protected void onPause() {
        super.onPause();
        isResumed = false;
        if (avatarEdit) endAvatarEdit();
        // free the decoder for the app that is being opened (and the memory with it)
        stopVideo();
        handler.removeCallbacks(tick);
        handler.removeCallbacks(weatherTick);
        saveFocus();
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        // Home pressed while the launcher is already on top: go back to the first dock tile.
        if (hasWindowFocus() && !dockApps.isEmpty()) {
            closeDrop();
            bgPanel.setVisibility(View.GONE);
            tileMenu.setVisibility(View.GONE);
            if (cityPanel != null) {
                hideKeyboard();
                cityPanel.setVisibility(View.GONE);
                highlight.setVisibility(View.VISIBLE);
            }
            if (appsOpen) showApps(false, true);
            area = DOCK;
            dockIdx = 0;
            applyFocus(true);
        }
    }

    @Override
    public void onBackPressed() {
        if (avatarEdit) {
            endAvatarEdit();
            return;
        }
        if (area == DROP) {
            closeDrop();
            applyFocus(true);
        } else if (area == PHOTO) {
            showPhotoList(false);
        } else if (area == BG) {
            closeBg();
        } else if (area == CITYTYPE) {
            hideKeyboard();
            cityEdit.setVisibility(View.GONE);
            area = CITY;
            fillCityList();
        } else if (area == CITY) {
            closeCity();
        } else if (area == PICK) {
            pickBox.setVisibility(View.GONE);
            menuRowsBox.setVisibility(View.VISIBLE);
            area = MENU;
            styleMenu();
        } else if (area == MENU) {
            closeTileMenu();
        } else if (area == MOVE) {
            area = DOCK;
            applyFocus(true);
        } else if (appsOpen) {
            showApps(false, true);
            area = PILLS;
            pillIdx = P_APPS;
            applyFocus(false);
        }
        // Otherwise swallow Back: a launcher has nowhere to go back to.
    }

    private void showApps(boolean show, boolean animate) {
        appsOpen = show;
        if (show) buildAppsPage();
        final View in = show ? appsPage : content;
        final View out = show ? content : appsPage;
        in.setVisibility(View.VISIBLE);
        appsScrim.setVisibility(show ? View.VISIBLE : View.GONE);
        if (!animate) {
            in.setAlpha(1f);
            out.setAlpha(0f);
            out.setVisibility(View.GONE);
            if (!show) releaseAppsPage();
            return;
        }
        in.setAlpha(0f);
        in.animate().alpha(1f).setDuration(200).start();
        out.animate().alpha(0f).setDuration(200).withEndAction(new Runnable() {
            @Override
            public void run() {
                if ((out == appsPage) != appsOpen) {
                    out.setVisibility(View.GONE);
                    if (out == appsPage) releaseAppsPage();
                }
            }
        }).start();
    }

    // The system tells a launcher when it is hidden and when memory gets tight: drop what can
    // be rebuilt (apps page logos; the background photo under real pressure).
    @Override
    public void onTrimMemory(int level) {
        super.onTrimMemory(level);
        if (level >= TRIM_MEMORY_UI_HIDDEN) releaseAppsPage();
        if (level >= TRIM_MEMORY_BACKGROUND && wave.photo != null) {
            wave.photo = null;
            wave.invalidate();
        }
    }

    // ---------------------------------------------------------------- data

    private void loadApps() {
        PackageManager pm = getPackageManager();
        List<ResolveInfo> all = new ArrayList<ResolveInfo>();
        all.addAll(pm.queryIntentActivities(new Intent(Intent.ACTION_MAIN)
                .addCategory("android.intent.category.LEANBACK_LAUNCHER"), 0));
        all.addAll(pm.queryIntentActivities(new Intent(Intent.ACTION_MAIN)
                .addCategory(Intent.CATEGORY_LAUNCHER), 0));

        Map<String, App> byPkg = new LinkedHashMap<String, App>();
        HashSet<String> seen = new HashSet<String>();
        for (ResolveInfo ri : all) {
            String pkg = ri.activityInfo.packageName;
            if (pkg.equals(getPackageName()) || !seen.add(pkg)) continue;
            App app = new App();
            app.pkg = pkg;
            app.cls = ri.activityInfo.name;
            // Netflix's Lithuanian label comes wrapped in „quotes“
            app.label = ri.loadLabel(pm).toString().replaceAll("[„“”\"]", "").trim();
            app.ri = ri; // pictures are loaded only when the tile is built (ensureLoaded)
            byPkg.put(pkg, app);
        }

        for (String pkg : dockPkgs()) {
            App a = byPkg.remove(pkg);
            if (a != null) {
                ensureLoaded(a);
                dockApps.add(a);
            }
        }

        gridApps.addAll(byPkg.values());
        Collections.sort(gridApps, new Comparator<App>() {
            @Override
            public int compare(App x, App y) {
                return x.label.toString().compareToIgnoreCase(y.label.toString());
            }
        });

        // the last tiles of the apps page are the launcher's own: clear RAM, background
        gridApps.add(specialTile(WX_PKG, R.string.wx_title, R.drawable.ic_partly_cloudy_day));
        gridApps.add(specialTile(RAM_PKG, R.string.ram_title, R.drawable.ic_memory));
        gridApps.add(specialTile(BG_PKG, R.string.bg_title, R.drawable.ic_palette));
    }

    private static final int SPECIAL_TILES = 3;

    private App specialTile(String key, int label, int iconRes) {
        App a = new App();
        a.pkg = key;
        a.label = getString(label);
        a.image = icon(iconRes, 48);
        a.loaded = true;
        return a;
    }

    // Closes the background processes of the apps in the launcher's list (what Android itself
    // would close first when memory runs low). System services, the remote, voice search,
    // Chromecast and anything playing in the foreground (music) are not touched.
    private void clearRam() {
        Toast.makeText(this, R.string.ram_working, Toast.LENGTH_SHORT).show();
        final List<String> pkgs = new ArrayList<String>();
        for (App a : dockApps) if (a.ri != null) pkgs.add(a.pkg);
        for (App a : gridApps) if (a.ri != null) pkgs.add(a.pkg);
        new Thread(new Runnable() {
            @Override
            public void run() {
                android.app.ActivityManager am =
                        (android.app.ActivityManager) getSystemService(Context.ACTIVITY_SERVICE);
                android.app.ActivityManager.MemoryInfo mi = new android.app.ActivityManager.MemoryInfo();
                for (String p : pkgs) {
                    try {
                        am.killBackgroundProcesses(p);
                    } catch (Exception ignored) {
                    }
                }
                try {
                    Thread.sleep(1500); // let the kernel hand the pages back
                } catch (InterruptedException ignored) {
                }
                // no "freed X MB": Android already counts cached apps as available memory,
                // so the difference is close to zero and only confuses (measured on this TV)
                am.getMemoryInfo(mi);
                final long free = mi.availMem / (1024 * 1024);
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        Toast.makeText(MainActivity.this,
                                getString(R.string.ram_done, (int) free),
                                Toast.LENGTH_LONG).show();
                    }
                });
            }
        }).start();
    }

    /** Apps updated since the list was read: a fresh ResolveInfo (the pictures load from it the
     *  next time a tile is built); a new name or a vanished launcher entry rebuilds the screen. */
    private void refreshUpdated() {
        PackageManager pm = getPackageManager();
        List<App> all = new ArrayList<App>(dockApps);
        all.addAll(gridApps);
        for (String pkg : updatedPkgs) {
            for (App a : all) {
                if (a.ri == null || !pkg.equals(a.pkg)) continue;
                ResolveInfo fresh = null;
                for (String cat : new String[] { "android.intent.category.LEANBACK_LAUNCHER", Intent.CATEGORY_LAUNCHER }) {
                    List<ResolveInfo> l = pm.queryIntentActivities(new Intent(Intent.ACTION_MAIN).addCategory(cat).setPackage(pkg), 0);
                    if (!l.isEmpty()) { fresh = l.get(0); break; }
                }
                String label = fresh == null ? null : fresh.loadLabel(pm).toString().replaceAll("[„“”\"]", "").trim();
                if (fresh == null || !label.equals(a.label.toString())) {
                    updatedPkgs.clear();
                    recreating = true;
                    recreate();
                    return;
                }
                a.ri = fresh;
                a.cls = fresh.activityInfo.name;
                unload(a); // its tile keeps the picture it shows; a rebuilt tile loads the new one
            }
        }
        updatedPkgs.clear();
    }

    // Logo from the banner; the (big, xxxhdpi) icon only when there is no usable logo.
    private void ensureLoaded(App a) {
        if (a.loaded) return;
        a.loaded = true;
        PackageManager pm = getPackageManager();
        try {
            Drawable banner = a.ri.activityInfo.loadBanner(pm);
            if (banner != null) a.logo = cutOutBackground(banner);
        } catch (Exception ignored) {
        }
        if (a.logo == null) a.image = loadIcon(pm, a.ri);
    }

    private void unload(App a) {
        if (a.ri == null) return; // the Background tile keeps its vector icon
        // no recycle(): the render thread may still hold the bitmap; dropping it is enough
        a.logo = null;
        a.image = null;
        a.loaded = false;
    }

    // Turns a TV banner (logo on a flat colour) into a logo on transparency, cropped to the
    // logo, so it can sit on a glass tile. Returns null when the banner has no flat
    // background (photos, gradients); those tiles show icon + name instead.
    private Drawable cutOutBackground(Drawable banner) {
        int w = 320, h = 180;
        Bitmap b = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888);
        Canvas c = new Canvas(b);
        banner.setBounds(0, 0, w, h);
        banner.draw(c);
        int[] px = new int[w * h];
        b.getPixels(px, 0, w, 0, 0, w, h);

        // background = average of the border; it must be (almost) uniform
        long sr = 0, sg = 0, sb = 0, n = 0;
        for (int i = 0; i < w * h; i++) {
            int x = i % w, y = i / w;
            if (x > 2 && x < w - 3 && y > 2 && y < h - 3) continue;
            sr += Color.red(px[i]);
            sg += Color.green(px[i]);
            sb += Color.blue(px[i]);
            n++;
        }
        int br = (int) (sr / n), bg = (int) (sg / n), bb = (int) (sb / n);
        int same = 0;
        for (int i = 0; i < w * h; i++) {
            int x = i % w, y = i / w;
            if (x > 2 && x < w - 3 && y > 2 && y < h - 3) continue;
            if (dist(px[i], br, bg, bb) < 40) same++;
        }
        if (same < n * 0.9) return null;

        // soft key: alpha grows with the distance from the background colour, and the
        // background tint is removed from anti-aliased edge pixels
        final float t0 = 28f, t1 = 110f;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int i = 0; i < w * h; i++) {
            int p = px[i];
            float d = dist(p, br, bg, bb);
            float a = Math.max(0f, Math.min(1f, (d - t0) / (t1 - t0)));
            a *= Color.alpha(p) / 255f;
            if (a <= 0.02f) {
                px[i] = 0;
                continue;
            }
            int r = unmix(Color.red(p), br, a);
            int g = unmix(Color.green(p), bg, a);
            int bl = unmix(Color.blue(p), bb, a);
            px[i] = Color.argb(Math.round(a * 255), r, g, bl);
            if (a > 0.25f) {
                int x = i % w, y = i / w;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        b.recycle();
        if (maxX < 0 || (maxX - minX) < 8 || (maxY - minY) < 8) return null;
        // light glass: white lettering (Go3, plex) turns dark instead
        if (darkUi) darkenLightParts(px);
        else whitenDarkParts(px);
        Bitmap out = Bitmap.createBitmap(px, w, h, Bitmap.Config.ARGB_8888);
        Bitmap crop = Bitmap.createBitmap(out, minX, minY, maxX - minX + 1, maxY - minY + 1);
        if (crop != out) out.recycle();
        return new BitmapDrawable(getResources(), darkUi ? crop : withGlow(crop));
    }

    // Soft white halo behind the logo so dark logos stay readable on dark photos; stronger
    // for dark logos, barely there for white ones. Computed once when the apps are loaded.
    private Bitmap withGlow(Bitmap logo) {
        try {
            long lum = 0, n = 0;
            int lw = logo.getWidth(), lh = logo.getHeight();
            int[] px = new int[lw * lh];
            logo.getPixels(px, 0, lw, 0, 0, lw, lh);
            for (int p : px) {
                if (Color.alpha(p) < 128) continue;
                lum += (Color.red(p) * 299 + Color.green(p) * 587 + Color.blue(p) * 114) / 1000;
                n++;
            }
            float bright = n == 0 ? 1f : lum / (255f * n); // 0 = black logo, 1 = white logo
            int alpha = Math.round(0xA0 - (0xA0 - 0x30) * bright);

            int radius = 7, pad = radius * 2;
            Paint blur = new Paint();
            blur.setMaskFilter(new android.graphics.BlurMaskFilter(radius,
                    android.graphics.BlurMaskFilter.Blur.NORMAL));
            int[] off = new int[2];
            Bitmap halo = logo.extractAlpha(blur, off);
            Bitmap out = Bitmap.createBitmap(lw + 2 * pad, lh + 2 * pad, Bitmap.Config.ARGB_8888);
            Canvas c = new Canvas(out);
            Paint hp = new Paint();
            hp.setColor(Color.argb(alpha, 255, 255, 255));
            c.drawBitmap(halo, pad + off[0], pad + off[1], hp);
            c.drawBitmap(logo, pad, pad, null);
            halo.recycle();
            logo.recycle();
            return out;
        } catch (Exception e) {
            return logo;
        }
    }

    // logo pixel work lives in LogoPixels (plain Java, unit tested)
    private static void whitenDarkParts(int[] px) {
        LogoPixels.whitenDarkParts(px);
    }

    private static void darkenLightParts(int[] px) {
        LogoPixels.darkenLightParts(px);
    }

    private static float dist(int p, int r, int g, int b) {
        return LogoPixels.dist(p, r, g, b);
    }

    private static int unmix(int c, int bgc, float a) {
        return LogoPixels.unmix(c, bgc, a);
    }

    // The default icon density on this TV (xhdpi) looks soft at tile size, so ask the app
    // for its xxxhdpi version; fall back to the normal icon.
    private Drawable loadIcon(PackageManager pm, ResolveInfo ri) {
        try {
            int id = ri.getIconResource();
            if (id != 0) {
                Resources r = pm.getResourcesForApplication(ri.activityInfo.applicationInfo);
                Drawable d = r.getDrawableForDensity(id, DisplayMetrics.DENSITY_XXXHIGH, null);
                if (d != null) return d;
            }
        } catch (Exception ignored) {
        }
        return ri.loadIcon(pm);
    }

    private void loadInputs() {
        try {
            TvInputManager tim = (TvInputManager) getSystemService(Context.TV_INPUT_SERVICE);
            if (tim == null) return;
            // Sony registers 5 tuner services (plus Google's), and every one of them only
            // opens the channel list, so a single "Live TV" entry goes first.
            InputItem live = null;
            List<String> plain = new ArrayList<String>();
            for (TvInputInfo info : tim.getTvInputList()) {
                boolean pass = info.isPassthroughInput();
                if (!pass && info.getType() != TvInputInfo.TYPE_TUNER) continue;
                if (!pass) {
                    // the remote's TV key tunes Sony's terrestrial/cable service, so prefer it
                    boolean terr = info.getId().contains("DvbTerrCable");
                    if (live == null || (terr && !live.id.contains("DvbTerrCable"))) {
                        live = new InputItem();
                        live.id = info.getId();
                        live.label = getString(R.string.live_tv);
                    }
                    continue;
                }
                CharSequence custom = info.loadCustomLabel(this);
                String def = String.valueOf(info.loadLabel(this));
                InputItem it = new InputItem();
                it.id = info.getId();
                it.label = (custom != null && custom.length() > 0) ? String.valueOf(custom) : def;
                it.passthrough = true;
                inputs.add(it);
                plain.add(def);
            }
            // two inputs renamed to the same thing (e.g. "AK") get their port name added
            List<String> shown = new ArrayList<String>();
            for (InputItem it : inputs) shown.add(it.label);
            for (int i = 0; i < inputs.size(); i++) {
                String l = shown.get(i);
                if (shown.indexOf(l) != shown.lastIndexOf(l) && !l.equals(plain.get(i))) {
                    inputs.get(i).label = l + " (" + plain.get(i) + ")";
                }
            }
            if (live != null) inputs.add(0, live);
        } catch (Exception ignored) {
        }
    }

    private int gridRows() {
        return (gridApps.size() + COLS - 1) / COLS;
    }

    private int colsInRow(int r) {
        int rows = gridRows();
        if (rows == 0) return 0;
        return r < rows - 1 ? COLS : gridApps.size() - r * COLS;
    }

    // ---------------------------------------------------------------- UI

    private View place(View v, int x, int y, int w, int h) {
        return placeIn(content, v, x, y, w, h);
    }

    private View placeIn(FrameLayout parent, View v, int x, int y, int w, int h) {
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(w, h);
        lp.leftMargin = x;
        lp.topMargin = y;
        parent.addView(v, lp);
        return v;
    }

    // dock index <-> (row, col) of the 2-row x 4-column home layout; -1 = empty slot
    private int dockRow(int i) {
        return DockGrid.row(i);
    }

    private int dockCol(int i) {
        return DockGrid.col(i);
    }

    private int dockAt(int row, int col) {
        return DockGrid.at(row, col, dockApps.size());
    }

    private TextView text(float sp, int color, String face) {
        TextView t = new TextView(this);
        t.setTextSize(sp);
        t.setTextColor(color);
        t.setTypeface(Typeface.create(face, Typeface.NORMAL));
        t.setIncludeFontPadding(false);
        t.setSingleLine(true);
        return t;
    }

    // Outfit variable font from assets (OFL); falls back to the system face if it fails to load.
    // One Typeface per weight: every Builder.build() loads its own copy of the font file.
    private final Map<Integer, Typeface> outfitCache = new java.util.HashMap<Integer, Typeface>();

    private Typeface outfit(int weight, String fallback) {
        Typeface cached = outfitCache.get(weight);
        if (cached != null) return cached;
        try {
            Typeface tf = new Typeface.Builder(getAssets(), "fonts/Outfit.ttf")
                    .setFontVariationSettings("'wght' " + weight)
                    .build();
            if (tf != null) {
                outfitCache.put(weight, tf);
                return tf;
            }
        } catch (RuntimeException ignored) { }
        return Typeface.create(fallback, Typeface.NORMAL);
    }

    // text on the colourful background needs a soft shadow to stay readable
    private TextView shadowed(TextView t) {
        t.setShadowLayer(dp(6), 0, dp(1), darkUi ? 0x66FFFFFF : 0x73000000);
        return t;
    }

    private GradientDrawable glass(float radius) {
        GradientDrawable g = new GradientDrawable();
        // on light photos the glass is frosted white so dark text and logos stay readable
        g.setColor(darkUi ? 0x99FFFFFF : 0x2EFFFFFF);
        g.setStroke(Math.max(1, dp(1)), darkUi ? 0x80FFFFFF : 0x40FFFFFF);
        g.setCornerRadius(radius);
        return g;
    }

    private Drawable icon(int res, int sizeDp) {
        Drawable d = getResources().getDrawable(res, null).mutate();
        d.setBounds(0, 0, dp(sizeDp), dp(sizeDp));
        d.setTint(ink(0xFF)); // the vectors are white; dark on light photos
        return d;
    }

    private void showInitial(String s) {
        GradientDrawable circle = new GradientDrawable();
        circle.setShape(GradientDrawable.OVAL);
        circle.setColor(ACCOUNT_COLOR);
        if (s.isEmpty()) {
            Drawable person = getResources().getDrawable(R.drawable.ic_person, null).mutate();
            person.setTint(Color.WHITE);
            avatar.setBackground(new android.graphics.drawable.LayerDrawable(new Drawable[]{
                    circle, new android.graphics.drawable.InsetDrawable(person, dp(8))}));
            avatar.setText("");
        } else {
            avatar.setBackground(circle);
            avatar.setText(s);
        }
    }

    // hold OK on the account circle: Left/Right picks the letter, OK or Back keeps it
    private void startAvatarEdit() {
        avatarEdit = true;
        String cur = getPreferences(MODE_PRIVATE).getString("accountInitial", "");
        avatarIdx = Math.max(0, java.util.Arrays.asList(INITIALS).indexOf(cur));
        Toast.makeText(this, R.string.avatar_hint, Toast.LENGTH_LONG).show();
    }

    private void stepAvatar(int dir) {
        avatarIdx = (avatarIdx + dir + INITIALS.length) % INITIALS.length;
        showInitial(INITIALS[avatarIdx]);
    }

    private void endAvatarEdit() {
        avatarEdit = false;
        getPreferences(MODE_PRIVATE).edit().putString("accountInitial", INITIALS[avatarIdx]).apply();
    }

    private void buildUi() {
        // account circle left of the greeting; opens the TV's account settings
        int av = dp(40);
        avatar = text(18, Color.WHITE, "sans-serif-medium");
        avatar.setTypeface(outfit(500, "sans-serif-medium"));
        avatar.setGravity(Gravity.CENTER);
        showInitial(getPreferences(MODE_PRIVATE).getString("accountInitial", ""));
        place(avatar, marginX, dp(22), av, av);
        avatarRect = new RectF(marginX, dp(22), marginX + av, dp(22) + av);

        greeting = shadowed(text(22, ink(0xCC), "sans-serif"));
        greeting.setTypeface(outfit(400, "sans-serif"));
        place(greeting, marginX + av + dp(14), dp(28), sw / 2, ViewGroup.LayoutParams.WRAP_CONTENT);
        clock = shadowed(text(88, ink(0xFF), "sans-serif-thin"));
        clock.setTypeface(outfit(200, "sans-serif-thin"));        place(clock, marginX, dp(56), sw / 2, ViewGroup.LayoutParams.WRAP_CONTENT);
        dateView = shadowed(text(22, ink(0xCC), "sans-serif"));
        dateView.setTypeface(outfit(400, "sans-serif"));
        place(dateView, marginX, dp(172), sw / 2, ViewGroup.LayoutParams.WRAP_CONTENT);
        updateClock();

        // weather sits between the two blocks
        weatherBox = new LinearLayout(this);
        weatherBox.setOrientation(LinearLayout.VERTICAL);
        weatherBox.setGravity(Gravity.CENTER);
        // the weather is taller than the small blocks: centre it on them vertically
        int wxH = dp(150);
        int wxY = dockTop + (2 * dockH + dockGap) / 2 - wxH / 2;
        place(weatherBox, dockMarginX + blockW, wxY, weatherW, wxH);
        String cached = getPreferences(MODE_PRIVATE).getString("weatherJson", null);
        if (cached != null) showWeather(cached);

        // focus highlight sits below the tiles
        highlight = new HighlightView(this);
        place(highlight, 0, 0, ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);

        buildDock();
        buildPills();
        buildDropdown();
        buildTileMenu();
    }

    // (Re)creates the 8 home tiles from dockApps; called again after the user edits them.
    private void buildDock() {
        for (View t : dockTiles) content.removeView(t);
        dockTiles.clear();
        dockRects.clear();
        if (scaledTile != null && scaledTile.getParent() == null) scaledTile = null;
        int rightX = sw - dockMarginX - blockW;
        for (int i = 0; i < dockApps.size(); i++) {
            int col = dockCol(i), row = dockRow(i);
            int x = (col < 2 ? dockMarginX : rightX) + (col % 2) * (dockW + dockGapX);
            int y = dockTop + row * (dockH + dockGap);
            View tile = makeTile(dockApps.get(i), dockW, dockH);
            place(tile, x, y, dockW, dockH);
            dockTiles.add(tile);
            dockRects.add(new RectF(x, y, x + dockW, y + dockH));
        }
    }

    // The user's tile order (long-press OK on a tile), or the built-in default.
    private String[] dockPkgs() {
        String saved = getPreferences(MODE_PRIVATE).getString("dockPkgs", null);
        return saved == null || saved.isEmpty() ? DOCK_PKGS : saved.split(",");
    }

    private void saveDock() {
        StringBuilder sb = new StringBuilder();
        for (App a : dockApps) {
            if (sb.length() > 0) sb.append(',');
            sb.append(a.pkg);
        }
        getPreferences(MODE_PRIVATE).edit().putString("dockPkgs", sb.toString()).apply();
    }

    // The apps page (with ~25 logos) exists only while it is open; see releaseAppsPage().
    private void buildAppsPage() {
        if (appsBuilt) return;
        appsBuilt = true;
        TextView title = shadowed(text(28, ink(0xFF), "sans-serif-light"));
        title.setTypeface(outfit(300, "sans-serif-light"));
        title.setText(R.string.all_apps);
        placeIn(appsPage, title, marginX, dp(40), ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);

        gridHighlight = new HighlightView(this);
        placeIn(appsPage, gridHighlight, 0, 0, ViewGroup.LayoutParams.MATCH_PARENT, pageH);

        for (int k = 0; k < gridApps.size(); k++) {
            int r = k / COLS, c = k % COLS;
            int x = marginX + c * colW + (colW - tileW) / 2;
            int y = gridTop + r * pitch;
            ensureLoaded(gridApps.get(k));
            View tile = makeTile(gridApps.get(k), tileW, tileH);
            placeIn(appsPage, tile, x, y, tileW, tileH);
            gridTiles.add(tile);
            gridRects.add(new RectF(x, y, x + tileW, y + tileH));
        }
    }

    private void releaseAppsPage() {
        if (!appsBuilt) return;
        appsBuilt = false;
        appsPage.removeAllViews();
        gridTiles.clear();
        gridRects.clear();
        if (scaledTile != null && scaledTile.getParent() == null) scaledTile = null;
        gridHighlight = null;
        for (App a : gridApps) unload(a);
    }

    // Glass card with the app's logo, like the reference picture; apps whose banner
    // could not be cut out get their icon with the name underneath.
    private View makeTile(App a, int w, int h) {
        FrameLayout card = new FrameLayout(this);
        card.setBackground(glass(dp(16)));

        if (a.logo != null) {
            ImageView logo = new ImageView(this);
            logo.setImageDrawable(a.logo);
            logo.setScaleType(ImageView.ScaleType.FIT_CENTER);
            FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
            // smaller margins than before: the glow adds a transparent border around the logo
            lp.leftMargin = lp.rightMargin = w * 8 / 100;
            lp.topMargin = lp.bottomMargin = h * 14 / 100;
            card.addView(logo, lp);
            return card;
        }

        int iconSize = h * 46 / 100;
        ImageView iv = new ImageView(this);
        iv.setImageDrawable(a.image);
        iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
        // most TV app icons are plain squares; round them like the glass card
        final float iconRadius = iconSize * 0.22f;
        iv.setOutlineProvider(new android.view.ViewOutlineProvider() {
            @Override
            public void getOutline(View view, android.graphics.Outline outline) {
                outline.setRoundRect(0, 0, view.getWidth(), view.getHeight(), iconRadius);
            }
        });
        iv.setClipToOutline(true);
        FrameLayout.LayoutParams ip = new FrameLayout.LayoutParams(iconSize, iconSize,
                Gravity.CENTER_HORIZONTAL | Gravity.TOP);
        ip.topMargin = h * 13 / 100;
        card.addView(iv, ip);

        TextView name = text(13, ink(0xFF), "sans-serif");
        name.setTypeface(outfit(500, "sans-serif-medium"));
        name.setText(a.label);
        name.setGravity(Gravity.CENTER);
        name.setEllipsize(android.text.TextUtils.TruncateAt.END);
        name.setPadding(dp(8), 0, dp(8), 0);
        FrameLayout.LayoutParams np = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM);
        np.bottomMargin = h * 11 / 100;
        card.addView(name, np);
        return card;
    }

    private void buildPills() {
        String[] labels = {getString(R.string.pill_apps), getString(R.string.pill_inputs),
                wifiName(), getString(R.string.pill_settings)};
        int[] icons = {R.drawable.ic_apps, R.drawable.ic_input, R.drawable.ic_wifi,
                R.drawable.ic_settings};
        int n = pills.length;
        int[] widths = new int[n];
        int[] heights = new int[n];
        int gap = dp(12);
        int total = gap * (n - 1);
        for (int i = 0; i < n; i++) {
            TextView t = text(15, ink(0xFF), "sans-serif");
            t.setTypeface(outfit(500, "sans-serif-medium"));
            t.setText(labels[i]);
            t.setGravity(Gravity.CENTER);
            t.setCompoundDrawables(icon(icons[i], 20), null, null, null);
            t.setCompoundDrawablePadding(dp(8));
            t.setPadding(dp(16), dp(9), dp(20), dp(9));
            t.measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);
            widths[i] = t.getMeasuredWidth();
            heights[i] = t.getMeasuredHeight();
            total += widths[i];
            t.setBackground(glass(heights[i] / 2f));
            pills[i] = t;
        }
        int x = sw - marginX - total;
        int y = dp(34);
        for (int i = 0; i < n; i++) {
            place(pills[i], x, y, widths[i], heights[i]);
            pillRects.add(new RectF(x, y, x + widths[i], y + heights[i]));
            x += widths[i] + gap;
        }
    }

    // Called from onResume: the network may have changed while another app was open.
    private void refreshWifiPill() {
        String name = wifiName();
        if (name.contentEquals(pills[P_WIFI].getText())) return;
        pills[P_WIFI].setText(name);
        int n = pills.length;
        int gap = dp(12);
        int total = gap * (n - 1);
        int[] widths = new int[n];
        for (int i = 0; i < n; i++) {
            pills[i].measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);
            widths[i] = pills[i].getMeasuredWidth();
            total += widths[i];
        }
        int x = sw - marginX - total;
        for (int i = 0; i < n; i++) {
            FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) pills[i].getLayoutParams();
            lp.leftMargin = x;
            lp.width = widths[i];
            pills[i].setLayoutParams(lp);
            RectF r = pillRects.get(i);
            r.left = x;
            r.right = x + widths[i];
            x += widths[i] + gap;
        }
        // the inputs dropdown hangs under the Inputs pill
        FrameLayout.LayoutParams dl = (FrameLayout.LayoutParams) dropBox.getLayoutParams();
        dl.leftMargin = (int) pillRects.get(P_INPUTS).left;
        dropBox.setLayoutParams(dl);
        if (area == PILLS) applyFocus(false);
    }

    private void buildDropdown() {
        dropBox = new FrameLayout(this);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(0xE62A2A2C);
        bg.setCornerRadius(dp(18));
        dropBox.setBackground(bg);
        dropBox.setVisibility(View.GONE);

        int itemH = dp(40);
        for (int i = 0; i < inputs.size(); i++) {
            TextView t = text(15, Color.WHITE, "sans-serif");
            t.setText(inputs.get(i).label);
            t.setGravity(Gravity.CENTER_VERTICAL);
            t.setPadding(dp(16), 0, dp(16), 0);
            FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(dp(200), itemH);
            lp.leftMargin = dp(8);
            lp.topMargin = dp(8) + i * itemH;
            dropBox.addView(t, lp);
            dropItems.add(t);
        }
        RectF p = pillRects.get(P_INPUTS);
        int w = dp(216);
        int h = dp(16) + Math.max(inputs.size(), 1) * itemH;
        place(dropBox, (int) p.left, (int) p.bottom + dp(10), w, h);
    }

    private RectF dropRect(int i) {
        RectF p = pillRects.get(P_INPUTS);
        float left = p.left + dp(8);
        float top = p.bottom + dp(10) + dp(8) + i * dp(40);
        return new RectF(left, top, left + dp(200), top + dp(40));
    }

    private void closeDrop() {
        dropBox.setVisibility(View.GONE);
        if (area == DROP) area = PILLS;
    }

    // ---------------------------------------------------------------- small helpers

    private String wifiName() {
        try {
            WifiManager wm = (WifiManager) getApplicationContext()
                    .getSystemService(Context.WIFI_SERVICE);
            WifiInfo wi = wm == null ? null : wm.getConnectionInfo();
            String s = wi == null ? null : wi.getSSID();
            if (s != null && s.length() > 0 && !s.contains("unknown")) {
                s = s.replace("\"", "");
                return s.length() > 20 ? s.substring(0, 19) + "…" : s;
            }
        } catch (Exception ignored) {
        }
        return getString(R.string.pill_wifi);
    }

    private void updateClock() {
        Calendar cal = Calendar.getInstance();
        int hour = cal.get(Calendar.HOUR_OF_DAY);
        greeting.setText(hour < 5 ? R.string.greeting_night : hour < 12 ? R.string.greeting_morning
                : hour < 18 ? R.string.greeting_day : R.string.greeting_evening);
        Date now = cal.getTime();
        String time = android.text.format.DateFormat.is24HourFormat(this) ? "HH:mm" : "h:mm";
        String hm = new SimpleDateFormat(time, loc()).format(now);
        String ss = new SimpleDateFormat(":ss", loc()).format(now);
        // seconds smaller and dimmer than hours:minutes
        android.text.SpannableString s = new android.text.SpannableString(hm + ss);
        s.setSpan(new android.text.style.RelativeSizeSpan(0.45f), hm.length(), s.length(), 0);
        s.setSpan(new android.text.style.ForegroundColorSpan(ink(darkUi ? 0xB3 : 0x99)), hm.length(), s.length(), 0);
        clock.setText(s);
        dateView.setText(new SimpleDateFormat(getString(R.string.date_pattern), loc()).format(now));
    }

    // ---------------------------------------------------------------- tile menu (long-press OK)

    private void buildTileMenu() {
        tileMenu = new LinearLayout(this);
        tileMenu.setOrientation(LinearLayout.VERTICAL);
        tileMenu.setPadding(dp(20), dp(18), dp(20), dp(16));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(0xE61C1C24);
        bg.setStroke(Math.max(1, dp(1)), 0x40FFFFFF);
        bg.setCornerRadius(dp(24));
        tileMenu.setBackground(bg);
        tileMenu.setVisibility(View.GONE);

        menuTitle = text(22, Color.WHITE, "sans-serif-light");
        menuTitle.setTypeface(outfit(400, "sans-serif"));
        menuTitle.setPadding(dp(12), 0, 0, dp(10));
        tileMenu.addView(menuTitle);

        menuRowsBox = new LinearLayout(this);
        menuRowsBox.setOrientation(LinearLayout.VERTICAL);
        int[] labels = {R.string.menu_change, R.string.menu_move};
        for (int i = 0; i < menuRows.length; i++) {
            TextView t = text(15, Color.WHITE, "sans-serif");
            t.setTypeface(outfit(500, "sans-serif-medium"));
            t.setText(labels[i]);
            t.setPadding(dp(12), dp(10), dp(12), dp(10));
            menuRowsBox.addView(t);
            menuRows[i] = t;
        }
        tileMenu.addView(menuRowsBox);

        pickBox = new LinearLayout(this);
        pickBox.setOrientation(LinearLayout.VERTICAL);
        pickBox.setVisibility(View.GONE);
        tileMenu.addView(pickBox);

        menuHint = text(12, 0x99FFFFFF, "sans-serif");
        menuHint.setTypeface(outfit(400, "sans-serif"));
        menuHint.setText(R.string.menu_hint);
        menuHint.setPadding(dp(12), dp(8), 0, 0);
        tileMenu.addView(menuHint);

        int w = dp(340);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(w, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.leftMargin = (sw - w) / 2;
        lp.topMargin = dp(90);
        root.addView(tileMenu, lp);
    }

    // ---------------------------------------------------------------- weather city

    private static class City {
        String name, detail;
        float lat, lon;
    }

    private LinearLayout cityPanel, cityList;
    private android.widget.EditText cityEdit;
    private TextView cityHint;
    private final List<City> cities = new ArrayList<City>();
    private int cityIdx;

    // Panel over the home screen (so the weather can be seen changing): row 0 is
    // "Search city…", then Lithuanian cities or the search results.
    private void openCity() {
        if (cityPanel == null) buildCityPanel();
        cities.clear();
        cities.add(null); // the "Search city…" row
        for (Object[] c : LT_CITIES) {
            City x = new City();
            x.name = (String) c[0];
            x.detail = "";
            x.lat = (Float) c[1];
            x.lon = (Float) c[2];
            cities.add(x);
        }
        cityIdx = 1;
        String cur = cityName();
        for (int i = 1; i < cities.size(); i++) {
            if (cities.get(i).name.equals(cur)) cityIdx = i;
        }
        appsPage.setVisibility(View.GONE);
        appsScrim.setVisibility(View.GONE);
        content.setVisibility(View.VISIBLE);
        content.setAlpha(1f);
        highlight.setVisibility(View.INVISIBLE); // its last home position would look focused
        cityEdit.setVisibility(View.GONE);
        cityPanel.setVisibility(View.VISIBLE);
        area = CITY;
        fillCityList();
    }

    private void closeCity() {
        hideKeyboard();
        cityPanel.setVisibility(View.GONE);
        highlight.setVisibility(View.VISIBLE);
        content.setVisibility(View.GONE);
        appsPage.setVisibility(View.VISIBLE);
        appsPage.setAlpha(1f);
        appsScrim.setVisibility(View.VISIBLE);
        area = GRID;
        applyFocus(false);
    }

    private void buildCityPanel() {
        cityPanel = new LinearLayout(this);
        cityPanel.setOrientation(LinearLayout.VERTICAL);
        cityPanel.setPadding(dp(20), dp(18), dp(20), dp(16));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(0xE61C1C24);
        bg.setStroke(Math.max(1, dp(1)), 0x40FFFFFF);
        bg.setCornerRadius(dp(24));
        cityPanel.setBackground(bg);
        cityPanel.setVisibility(View.GONE);

        TextView title = text(22, Color.WHITE, "sans-serif-light");
        title.setTypeface(outfit(400, "sans-serif"));
        title.setText(R.string.wx_city_title);
        title.setPadding(dp(12), 0, 0, dp(10));
        cityPanel.addView(title);

        cityEdit = new android.widget.EditText(this);
        cityEdit.setSingleLine(true);
        cityEdit.setHint(R.string.wx_search_hint);
        cityEdit.setTextColor(Color.WHITE);
        cityEdit.setHintTextColor(0x80FFFFFF);
        cityEdit.setTypeface(outfit(400, "sans-serif"));
        cityEdit.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH);
        cityEdit.setVisibility(View.GONE);
        cityEdit.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override
            public boolean onEditorAction(TextView v, int actionId, KeyEvent e) {
                searchCity(v.getText().toString().trim());
                return true;
            }
        });
        cityPanel.addView(cityEdit);

        cityList = new LinearLayout(this);
        cityList.setOrientation(LinearLayout.VERTICAL);
        cityPanel.addView(cityList);

        cityHint = text(12, 0x99FFFFFF, "sans-serif");
        cityHint.setTypeface(outfit(400, "sans-serif"));
        cityHint.setText(R.string.wx_city_hint);
        cityHint.setPadding(dp(12), dp(8), 0, 0);
        cityPanel.addView(cityHint);

        // top right, above the dock and clear of the weather in the middle
        int w = dp(360);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(w, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.leftMargin = sw - marginX - w;
        lp.topMargin = dp(84);
        root.addView(cityPanel, lp);
    }

    // up to 5 rows around the selected one (more would cover the weather)
    private void fillCityList() {
        cityList.removeAllViews();
        int from = Math.max(0, Math.min(cityIdx - 2, cities.size() - 5));
        for (int i = from; i < Math.min(cities.size(), from + 5); i++) {
            City c = cities.get(i);
            TextView t = text(15, Color.WHITE, "sans-serif");
            t.setTypeface(outfit(c == null ? 500 : 400, "sans-serif"));
            t.setEllipsize(android.text.TextUtils.TruncateAt.END);
            if (c == null) {
                t.setText("🔍  " + getString(R.string.wx_search));
            } else {
                String s = c.name + (c.detail.isEmpty() ? "" : "  ·  " + c.detail);
                t.setText(c.name.equals(cityName()) ? s + "  ✓" : s);
            }
            t.setPadding(dp(12), dp(8), dp(12), dp(8));
            if (area == CITY && i == cityIdx) t.setBackground(focusedRow());
            cityList.addView(t);
        }
    }

    private void moveCity(int dir) {
        cityIdx = Math.max(0, Math.min(cities.size() - 1, cityIdx + dir));
        fillCityList();
    }

    private void activateCity() {
        City c = cities.get(cityIdx);
        if (c == null) {
            // type a name with the TV's keyboard; its Search key runs searchCity()
            area = CITYTYPE;
            fillCityList();
            cityEdit.setVisibility(View.VISIBLE);
            cityEdit.setText("");
            cityEdit.requestFocus();
            android.view.inputmethod.InputMethodManager imm = (android.view.inputmethod.InputMethodManager)
                    getSystemService(Context.INPUT_METHOD_SERVICE);
            imm.showSoftInput(cityEdit, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT);
            return;
        }
        getPreferences(MODE_PRIVATE).edit()
                .putString("cityName", c.name)
                .putFloat("cityLat", c.lat)
                .putFloat("cityLon", c.lon)
                .remove("weatherAt")
                .apply();
        fillCityList();
        fetchWeather(); // the hourly limit is per city: a new city loads now
    }

    private void hideKeyboard() {
        if (cityEdit == null) return;
        android.view.inputmethod.InputMethodManager imm = (android.view.inputmethod.InputMethodManager)
                getSystemService(Context.INPUT_METHOD_SERVICE);
        imm.hideSoftInputFromWindow(cityEdit.getWindowToken(), 0);
        cityEdit.clearFocus();
    }

    // Open-Meteo geocoding; results replace the list (row 0 stays "Search city…")
    private void searchCity(final String name) {
        hideKeyboard();
        cityEdit.setVisibility(View.GONE);
        area = CITY;
        if (name.isEmpty()) {
            fillCityList();
            return;
        }
        Toast.makeText(this, R.string.wx_searching, Toast.LENGTH_SHORT).show();
        final String url;
        try {
            url = "https://geocoding-api.open-meteo.com/v1/search?count=10&format=json"
                    + "&language=" + loc().getLanguage()
                    + "&name=" + java.net.URLEncoder.encode(name, "UTF-8");
        } catch (Exception e) {
            return;
        }
        new Thread(new Runnable() {
            @Override
            public void run() {
                final List<City> found = new ArrayList<City>();
                try {
                    JSONArray r = new JSONObject(httpGet(url)).getJSONArray("results");
                    for (int i = 0; i < r.length(); i++) {
                        JSONObject o = r.getJSONObject(i);
                        City c = new City();
                        c.name = o.getString("name");
                        String a1 = o.optString("admin1", ""), co = o.optString("country", "");
                        c.detail = a1.isEmpty() ? co : (co.isEmpty() ? a1 : a1 + ", " + co);
                        c.lat = (float) o.getDouble("latitude");
                        c.lon = (float) o.getDouble("longitude");
                        found.add(c);
                    }
                } catch (Exception ignored) {
                    // no "results" key, no network, bad JSON: nothing found
                }
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        if (found.isEmpty()) {
                            Toast.makeText(MainActivity.this, R.string.wx_not_found, Toast.LENGTH_LONG).show();
                            fillCityList();
                            return;
                        }
                        cities.clear();
                        cities.add(null);
                        cities.addAll(found);
                        cityIdx = 1;
                        fillCityList();
                    }
                });
            }
        }).start();
    }

    private void openTileMenu() {
        menuTitle.setText(dockApps.get(dockIdx).label);
        menuRowsBox.setVisibility(View.VISIBLE);
        pickBox.setVisibility(View.GONE);
        menuHint.setText(R.string.menu_hint);
        tileMenu.setVisibility(View.VISIBLE);
        area = MENU;
        menuRow = 0;
        styleMenu();
    }

    private void closeTileMenu() {
        tileMenu.setVisibility(View.GONE);
        area = DOCK;
        applyFocus(true);
    }

    private void styleMenu() {
        for (int i = 0; i < menuRows.length; i++) {
            menuRows[i].setBackground(area == MENU && i == menuRow ? focusedRow() : null);
        }
        for (int i = 0; i < pickBox.getChildCount(); i++) {
            TextView t = (TextView) pickBox.getChildAt(i);
            t.setBackground((Integer) t.getTag() == pickIdx ? focusedRow() : null);
        }
    }

    // every installed app, alphabetically; the current one is preselected
    private void openPickList() {
        pickApps.clear();
        pickApps.addAll(dockApps);
        for (App a : gridApps) if (a.ri != null) pickApps.add(a);
        Collections.sort(pickApps, new Comparator<App>() {
            @Override
            public int compare(App x, App y) {
                return x.label.toString().compareToIgnoreCase(y.label.toString());
            }
        });
        pickIdx = Math.max(0, pickApps.indexOf(dockApps.get(dockIdx)));
        menuRowsBox.setVisibility(View.GONE);
        pickBox.setVisibility(View.VISIBLE);
        area = PICK;
        fillPickList();
    }

    // up to 8 names around the selected one
    private void fillPickList() {
        pickBox.removeAllViews();
        int from = Math.max(0, Math.min(pickIdx - 4, pickApps.size() - 8));
        for (int i = from; i < Math.min(pickApps.size(), from + 8); i++) {
            App a = pickApps.get(i);
            TextView t = text(14, Color.WHITE, "sans-serif");
            t.setTypeface(outfit(400, "sans-serif"));
            t.setEllipsize(android.text.TextUtils.TruncateAt.END);
            t.setText(dockApps.contains(a) ? a.label + "  ·  " + (dockApps.indexOf(a) + 1) : a.label);
            t.setTag(i);
            t.setPadding(dp(12), dp(8), dp(12), dp(8));
            pickBox.addView(t);
        }
        styleMenu();
    }

    // Puts the chosen app on the tile; if it already was on another tile they swap places.
    private void pickApp() {
        App chosen = pickApps.get(pickIdx);
        App old = dockApps.get(dockIdx);
        if (chosen != old) {
            int other = dockApps.indexOf(chosen);
            if (other >= 0) {
                dockApps.set(other, old);
            } else {
                gridApps.remove(chosen);
                gridApps.add(gridApps.size() - SPECIAL_TILES, old); // before the launcher's own tiles
                Collections.sort(gridApps.subList(0, gridApps.size() - SPECIAL_TILES), new Comparator<App>() {
                    @Override
                    public int compare(App x, App y) {
                        return x.label.toString().compareToIgnoreCase(y.label.toString());
                    }
                });
                unload(old);
                ensureLoaded(chosen);
            }
            dockApps.set(dockIdx, chosen);
            saveDock();
            buildDock();
        }
        tileMenu.setVisibility(View.GONE);
        area = DOCK;
        applyFocus(false);
    }

    private void startMove() {
        tileMenu.setVisibility(View.GONE);
        area = MOVE;
        Toast.makeText(this, R.string.move_hint, Toast.LENGTH_SHORT).show();
        applyFocus(true);
    }

    // swaps the tile with its neighbour in that direction
    private void moveTile(int dRow, int dCol) {
        int target = dockAt(dockRow(dockIdx) + dRow, dockCol(dockIdx) + dCol);
        if (target < 0) return;
        App a = dockApps.get(dockIdx);
        dockApps.set(dockIdx, dockApps.get(target));
        dockApps.set(target, a);
        dockIdx = target;
        saveDock();
        buildDock();
        applyFocus(false);
    }

    // ---------------------------------------------------------------- background settings

    private void buildBgPanel() {
        bgPanel = new LinearLayout(this);
        bgPanel.setOrientation(LinearLayout.VERTICAL);
        bgPanel.setPadding(dp(20), dp(18), dp(20), dp(16));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(0xD91C1C24);
        bg.setStroke(Math.max(1, dp(1)), 0x40FFFFFF);
        bg.setCornerRadius(dp(24));
        bgPanel.setBackground(bg);
        bgPanel.setVisibility(View.GONE);

        TextView title = text(22, Color.WHITE, "sans-serif-light");
        title.setTypeface(outfit(400, "sans-serif"));
        title.setText(R.string.bg_title);
        title.setPadding(dp(12), 0, 0, dp(10));
        bgPanel.addView(title);

        bgRowsBox = new LinearLayout(this);
        bgRowsBox.setOrientation(LinearLayout.VERTICAL);
        for (int i = 0; i < bgRows.length; i++) {
            TextView t = text(15, Color.WHITE, "sans-serif");
            t.setTypeface(outfit(500, "sans-serif-medium"));
            t.setPadding(dp(12), dp(10), dp(12), dp(10));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lp.bottomMargin = dp(4);
            bgRowsBox.addView(t, lp);
            bgRows[i] = t;
        }
        bgPanel.addView(bgRowsBox);

        photoBox = new LinearLayout(this);
        photoBox.setOrientation(LinearLayout.VERTICAL);
        photoBox.setVisibility(View.GONE);
        bgPanel.addView(photoBox);

        TextView hint = text(12, 0x99FFFFFF, "sans-serif");
        hint.setTypeface(outfit(400, "sans-serif"));
        hint.setText(R.string.bg_hint);
        hint.setPadding(dp(12), dp(8), 0, 0);
        bgPanel.addView(hint);

        int w = dp(460);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(w, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.leftMargin = sw - marginX - w;
        lp.topMargin = dp(120);
        root.addView(bgPanel, lp);
    }

    // Shows the bare background (no tiles) with the panel on top, so changes are visible live.
    private void openBg() {
        appsPage.setVisibility(View.GONE);
        appsScrim.setVisibility(View.GONE);
        bgPanel.setVisibility(View.VISIBLE);
        area = BG;
        bgRow = 0;
        applyFocus(false);
    }

    private void closeBg() {
        bgPanel.setVisibility(View.GONE);
        showPhotoList(false);
        appsPage.setVisibility(View.VISIBLE);
        appsPage.setAlpha(1f);
        appsScrim.setVisibility(View.VISIBLE);
        area = GRID;
        applyFocus(false);
    }

    private void styleBgRows() {
        String[] labels = {
                getString(R.string.bg_theme) + ":   ‹ " + getString(THEME_NAMES[bgTheme]) + " ›",
                getString(R.string.bg_brightness) + ":   ‹ " + (100 - DIM_LEVELS[bgDim]) + "% ›",
                getString(R.string.bg_fit) + ":   ‹ " + getString(FIT_NAMES[bgFit]) + " ›",
                getString(R.string.bg_ink) + ":   ‹ " + getString(INK_NAMES[bgInk]) + " ›",
                getString(R.string.bg_photo) + ":   " + getString(R.string.bg_photo_pick)};
        for (int i = 0; i < bgRows.length; i++) {
            bgRows[i].setText(labels[i]);
            bgRows[i].setBackground(area == BG && i == bgRow ? focusedRow() : null);
        }
        for (int i = 0; i < photoBox.getChildCount(); i++) {
            TextView t = (TextView) photoBox.getChildAt(i);
            int idx = (Integer) t.getTag();
            t.setBackground(idx == photoIdx ? focusedRow() : null);
        }
    }

    private GradientDrawable focusedRow() {
        GradientDrawable g = new GradientDrawable();
        g.setColor(0x40FFFFFF);
        g.setCornerRadius(dp(12));
        return g;
    }

    // Left/Right on the theme or brightness row; applied and saved immediately.
    private void changeBg(int dir) {
        if (bgRow == 0) {
            // "My photo" / "My video" only once one was picked
            boolean hasPhoto = new java.io.File(getFilesDir(), "bg.jpg").exists();
            boolean hasVideo = videoFile().exists();
            int count = THEME_NAMES.length;
            do {
                bgTheme = (bgTheme + dir + count) % count;
            } while ((bgTheme == THEME_PHOTO && !hasPhoto) || (bgTheme == THEME_VIDEO && !hasVideo));
            if (bgTheme == THEME_PHOTO && wave.photo == null) wave.photo = loadSavedPhoto();
            applyVideoTheme();
        } else if (bgRow == 1) {
            bgDim = Math.max(0, Math.min(DIM_LEVELS.length - 1, bgDim - dir));
        } else if (bgRow == 2) {
            bgFit = (bgFit + dir + FIT_NAMES.length) % FIT_NAMES.length;
            wave.fit = bgFit;
            fitVideo();
        } else if (bgRow == 3) {
            bgInk = (bgInk + dir + INK_NAMES.length) % INK_NAMES.length;
        } else {
            return;
        }
        wave.setTheme(bgTheme, DIM_LEVELS[bgDim]);
        getPreferences(MODE_PRIVATE).edit().putInt("bgTheme", bgTheme).putInt("bgDim", bgDim)
                .putInt("bgFit", bgFit).putInt("bgInk", bgInk).apply();
        styleBgRows();
        recolourIfNeeded();
    }

    private void showPhotoList(boolean show) {
        photoBox.setVisibility(show ? View.VISIBLE : View.GONE);
        bgRowsBox.setVisibility(show ? View.GONE : View.VISIBLE);
        if (show) {
            area = PHOTO;
            photoIdx = 0;
            fillPhotoList();
        } else if (area == PHOTO) {
            area = BG;
        }
        styleBgRows();
    }

    // up to 7 names around the selected one
    private void fillPhotoList() {
        photoBox.removeAllViews();
        int from = Math.max(0, Math.min(photoIdx - 3, photoNames.size() - 7));
        for (int i = from; i < Math.min(photoNames.size(), from + 7); i++) {
            TextView t = text(14, Color.WHITE, "sans-serif");
            t.setTypeface(outfit(400, "sans-serif"));
            t.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
            t.setText(photoNames.get(i));
            t.setTag(i);
            t.setPadding(dp(12), dp(8), dp(12), dp(8));
            photoBox.addView(t);
        }
    }

    // Images that Android's media scanner knows about, USB sticks included.
    private void openPhotoList() {
        if (checkSelfPermission(android.Manifest.permission.READ_EXTERNAL_STORAGE)
                != PackageManager.PERMISSION_GRANTED) {
            if (shouldShowRequestPermissionRationale(android.Manifest.permission.READ_EXTERNAL_STORAGE)
                    || !getPreferences(MODE_PRIVATE).getBoolean("storageAskedHere", false)) {
                getPreferences(MODE_PRIVATE).edit().putBoolean("storageAskedHere", true).apply();
                requestPermissions(new String[]{android.Manifest.permission.READ_EXTERNAL_STORAGE}, 2);
            } else {
                // refused for good: only TV Settings -> Apps can grant it now
                Toast.makeText(this, R.string.bg_photo_noperm, Toast.LENGTH_LONG).show();
            }
            return;
        }
        photoUris.clear();
        photoNames.clear();
        photoIsVideo.clear();
        try {
            for (String vol : android.provider.MediaStore.getExternalVolumeNames(this)) {
                String where = "external_primary".equals(vol) ? "" : "USB · ";
                // videos first (marked ▶), then pictures; newest first in each
                addMedia(android.provider.MediaStore.Video.Media.getContentUri(vol), where + "▶ ", true);
                addMedia(android.provider.MediaStore.Images.Media.getContentUri(vol), where, false);
            }
        } catch (Exception ignored) {
        }
        if (photoUris.isEmpty()) {
            Toast.makeText(this, R.string.bg_photo_none, Toast.LENGTH_LONG).show();
            return;
        }
        showPhotoList(true);
    }

    private void addMedia(android.net.Uri base, String prefix, boolean video) {
        android.database.Cursor c = getContentResolver().query(base,
                new String[]{android.provider.MediaStore.MediaColumns._ID,
                        android.provider.MediaStore.MediaColumns.DISPLAY_NAME},
                null, null, android.provider.MediaStore.MediaColumns.DATE_MODIFIED + " DESC");
        if (c == null) return;
        while (c.moveToNext() && photoUris.size() < 100) {
            photoUris.add(android.content.ContentUris.withAppendedId(base, c.getLong(0)));
            photoNames.add(prefix + c.getString(1));
            photoIsVideo.add(video);
        }
        c.close();
    }

    // Scales and crops the picture to the screen once and keeps it as files/bg.jpg, so the
    // USB stick can be removed afterwards.
    private void pickPhoto(final android.net.Uri uri) {
        final int w = root.getWidth(), h = root.getHeight();
        new Thread(new Runnable() {
            @Override
            public void run() {
                Bitmap out = null;
                try {
                    BitmapFactory.Options o = new BitmapFactory.Options();
                    o.inJustDecodeBounds = true;
                    InputStream in = getContentResolver().openInputStream(uri);
                    BitmapFactory.decodeStream(in, null, o);
                    in.close();
                    int sample = 1;
                    while (o.outWidth / (sample * 2) >= w && o.outHeight / (sample * 2) >= h) sample *= 2;
                    o = new BitmapFactory.Options();
                    o.inSampleSize = sample;
                    in = getContentResolver().openInputStream(uri);
                    Bitmap src = BitmapFactory.decodeStream(in, null, o);
                    in.close();
                    if (src != null) {
                        // keep the whole picture (the fit mode is applied when drawing), only
                        // shrink it until it just covers the screen; small pictures stay small
                        float scale = Math.min(1f, Math.max((float) w / src.getWidth(),
                                (float) h / src.getHeight()));
                        int ow = Math.max(1, Math.round(src.getWidth() * scale));
                        int oh = Math.max(1, Math.round(src.getHeight() * scale));
                        // full colour for the saved JPEG; shown as dithered RGB_565 below
                        out = Bitmap.createBitmap(ow, oh, Bitmap.Config.ARGB_8888);
                        new Canvas(out).drawBitmap(src, null, new android.graphics.Rect(0, 0, ow, oh),
                                new Paint(Paint.FILTER_BITMAP_FLAG));
                        src.recycle();
                        java.io.FileOutputStream fo = new java.io.FileOutputStream(
                                new java.io.File(getFilesDir(), "bg.jpg"));
                        out.compress(Bitmap.CompressFormat.JPEG, 95, fo);
                        fo.close();
                        out = toDithered565(out);
                    }
                } catch (Exception e) {
                    out = null;
                }
                final Bitmap result = out;
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        if (result == null) {
                            Toast.makeText(MainActivity.this, R.string.bg_photo_failed, Toast.LENGTH_LONG).show();
                            return;
                        }
                        wave.photo = result;
                        bgTheme = THEME_PHOTO;
                        wave.setTheme(bgTheme, DIM_LEVELS[bgDim]);
                        applyVideoTheme(); // stops a video background, if there was one
                        getPreferences(MODE_PRIVATE).edit().putInt("bgTheme", bgTheme).apply();
                        showPhotoList(false);
                        recolourIfNeeded(); // a light photo may need dark text
                    }
                });
            }
        }).start();
    }

    // ---------------------------------------------------------------- video background

    private java.io.File videoFile() {
        return new java.io.File(getFilesDir(), "bg.mp4");
    }

    private void startVideo() {
        if (player != null || bgTheme != THEME_VIDEO || videoSurface == null || !isResumed) return;
        if (!videoFile().exists()) return;
        try {
            final android.media.MediaPlayer mp = new android.media.MediaPlayer();
            mp.setDataSource(videoFile().getPath());
            mp.setSurface(new android.view.Surface(videoSurface));
            mp.setLooping(true);
            mp.setVolume(0f, 0f);
            mp.setOnVideoSizeChangedListener(new android.media.MediaPlayer.OnVideoSizeChangedListener() {
                @Override
                public void onVideoSizeChanged(android.media.MediaPlayer m, int w, int h) {
                    videoW = w;
                    videoH = h;
                    fitVideo();
                }
            });
            mp.setOnPreparedListener(new android.media.MediaPlayer.OnPreparedListener() {
                @Override
                public void onPrepared(android.media.MediaPlayer m) {
                    if (player == m) m.start();
                }
            });
            mp.setOnErrorListener(new android.media.MediaPlayer.OnErrorListener() {
                @Override
                public boolean onError(android.media.MediaPlayer m, int what, int extra) {
                    stopVideo();
                    return true;
                }
            });
            player = mp;
            mp.prepareAsync();
        } catch (Exception e) {
            stopVideo();
        }
    }

    private void stopVideo() {
        if (player == null) return;
        try {
            player.release();
        } catch (Exception ignored) {
        }
        player = null;
    }

    // The same fit modes as for photos, done with the TextureView's transform.
    private void fitVideo() {
        int w = videoView.getWidth(), h = videoView.getHeight();
        if (w == 0 || h == 0 || videoW == 0 || videoH == 0) return;
        float sx, sy;
        if (bgFit == FIT_STRETCH) {
            sx = sy = 1f;
        } else {
            float s;
            if (bgFit == FIT_FILL) s = Math.max((float) w / videoW, (float) h / videoH);
            else if (bgFit == FIT_FIT) s = Math.min((float) w / videoW, (float) h / videoH);
            else s = 1f; // centre, natural size
            sx = videoW * s / w;
            sy = videoH * s / h;
        }
        android.graphics.Matrix m = new android.graphics.Matrix();
        m.setScale(sx, sy, w / 2f, h / 2f);
        videoView.setTransform(m);
    }

    // shows or hides the video layer for the current theme
    private void applyVideoTheme() {
        boolean on = bgTheme == THEME_VIDEO;
        videoView.setVisibility(on ? View.VISIBLE : View.GONE);
        if (on) {
            startVideo();
            fitVideo();
        } else {
            stopVideo();
        }
    }

    // Copies the chosen video into the launcher (the USB stick can be removed) and samples a
    // frame for the automatic text colour.
    private void pickVideo(final android.net.Uri uri) {
        Toast.makeText(this, R.string.bg_video_copying, Toast.LENGTH_SHORT).show();
        new Thread(new Runnable() {
            @Override
            public void run() {
                boolean ok = false;
                int error = R.string.bg_photo_failed;
                float lum = 0f;
                java.io.File tmp = new java.io.File(getFilesDir(), "bg.mp4.part");
                try {
                    // refuse huge files up front, and never fill the TV's own storage
                    long size = -1;
                    android.database.Cursor sc = getContentResolver().query(uri,
                            new String[]{android.provider.OpenableColumns.SIZE}, null, null, null);
                    if (sc != null) {
                        if (sc.moveToFirst() && !sc.isNull(0)) size = sc.getLong(0);
                        sc.close();
                    }
                    long free = getFilesDir().getUsableSpace();
                    if (size > MAX_VIDEO_BYTES) throw new SizeException(R.string.bg_video_too_big);
                    if (size > 0 && free - size < KEEP_FREE_BYTES) {
                        throw new SizeException(R.string.bg_video_no_space);
                    }
                    InputStream in = getContentResolver().openInputStream(uri);
                    java.io.FileOutputStream out = new java.io.FileOutputStream(tmp);
                    byte[] buf = new byte[256 * 1024];
                    int n;
                    long copied = 0;
                    try {
                        while ((n = in.read(buf)) > 0) {
                            out.write(buf, 0, n);
                            copied += n;
                            // the reported size may be missing or wrong: check while copying too
                            if (copied > MAX_VIDEO_BYTES) throw new SizeException(R.string.bg_video_too_big);
                            if (free - copied < KEEP_FREE_BYTES) throw new SizeException(R.string.bg_video_no_space);
                        }
                    } finally {
                        in.close();
                        out.close();
                    }
                    android.media.MediaMetadataRetriever r = new android.media.MediaMetadataRetriever();
                    r.setDataSource(tmp.getPath());
                    Bitmap frame = r.getFrameAtTime(0);
                    r.release();
                    if (frame != null) {
                        lum = avgLum(frame);
                        frame.recycle();
                        ok = true;
                    }
                } catch (SizeException e) {
                    ok = false;
                    error = e.message;
                } catch (Exception e) {
                    ok = false;
                }
                if (ok) {
                    stopVideoOnUi();
                    ok = tmp.renameTo(videoFile());
                } else {
                    tmp.delete();
                }
                final boolean done = ok;
                final int err = error;
                final float l = lum;
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        if (!done) {
                            Toast.makeText(MainActivity.this, err, Toast.LENGTH_LONG).show();
                            return;
                        }
                        bgTheme = THEME_VIDEO;
                        getPreferences(MODE_PRIVATE).edit().putInt("bgTheme", bgTheme)
                                .putFloat("videoLum", l).apply();
                        wave.setTheme(bgTheme, DIM_LEVELS[bgDim]);
                        applyVideoTheme();
                        showPhotoList(false);
                        recolourIfNeeded();
                    }
                });
            }
        }).start();
    }

    private static class SizeException extends Exception {
        final int message;

        SizeException(int message) {
            this.message = message;
        }
    }

    // the old bg.mp4 is replaced while it might be playing
    private void stopVideoOnUi() {
        final Object lock = new Object();
        synchronized (lock) {
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    stopVideo();
                    synchronized (lock) {
                        lock.notify();
                    }
                }
            });
            try {
                lock.wait(2000);
            } catch (InterruptedException ignored) {
            }
        }
    }

    private Bitmap loadSavedPhoto() {
        java.io.File f = new java.io.File(getFilesDir(), "bg.jpg");
        if (!f.exists()) return null;
        Bitmap full = BitmapFactory.decodeFile(f.getPath());
        return full == null ? null : toDithered565(full);
    }

    // Half the memory of ARGB_8888 (~4.6 MB instead of ~9 MB for a 16:10 photo). Plain
    // RGB_565 bands smooth skies, so the conversion is dithered.
    private static Bitmap toDithered565(Bitmap full) {
        Bitmap out = Bitmap.createBitmap(full.getWidth(), full.getHeight(), Bitmap.Config.RGB_565);
        Paint p = new Paint(Paint.FILTER_BITMAP_FLAG);
        p.setDither(true);
        new Canvas(out).drawBitmap(full, 0, 0, p);
        full.recycle();
        return out;
    }

    // ---------------------------------------------------------------- weather

    private void fetchWeather() {
        if (weatherLoading) return;
        weatherLoading = true;
        final String url = weatherUrl();
        new Thread(new Runnable() {
            @Override
            public void run() {
                final String result = httpGet(url);
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        weatherLoading = false;
                        // on failure keep the cached forecast and try again next hour
                        if (result == null || !showWeather(result)) return;
                        getPreferences(MODE_PRIVATE).edit()
                                .putString("weatherJson", result)
                                .putLong("weatherAt", System.currentTimeMillis())
                                .apply();
                    }
                });
            }
        }).start();
    }

    // WMO weather code -> icon and text
    private static int weatherIcon(int code, boolean day) {
        switch (WeatherCode.of(code)) {
            case CLEAR: return day ? R.drawable.ic_sunny : R.drawable.ic_clear_night;
            case MAINLY_CLEAR:
            case PARTLY_CLOUDY: return day ? R.drawable.ic_partly_cloudy_day : R.drawable.ic_partly_cloudy_night;
            case OVERCAST: return R.drawable.ic_cloud;
            case FOG: return R.drawable.ic_foggy;
            case SNOW: return R.drawable.ic_weather_snowy;
            case THUNDER: return R.drawable.ic_thunderstorm;
            default: return R.drawable.ic_rainy;
        }
    }

    private static int weatherText(int code) {
        switch (WeatherCode.of(code)) {
            case CLEAR: return R.string.wx_clear;
            case MAINLY_CLEAR: return R.string.wx_mainly_clear;
            case PARTLY_CLOUDY: return R.string.wx_partly_cloudy;
            case OVERCAST: return R.string.wx_overcast;
            case FOG: return R.string.wx_fog;
            case DRIZZLE: return R.string.wx_drizzle;
            case SNOW: return R.string.wx_snow;
            case THUNDER: return R.string.wx_thunder;
            default: return R.string.wx_rain;
        }
    }

    private static String deg(double t) {
        return WeatherCode.degrees(t);
    }

    private boolean showWeather(String json) {
        try {
            JSONObject o = new JSONObject(json);
            JSONObject cur = o.getJSONObject("current");
            JSONObject daily = o.getJSONObject("daily");
            int code = cur.getInt("weather_code");
            boolean day = cur.optInt("is_day", 1) == 1;

            weatherBox.removeAllViews();

            // row 1: icon + big temperature + description column
            LinearLayout top = new LinearLayout(this);
            top.setGravity(Gravity.CENTER);
            ImageView big = new ImageView(this);
            big.setImageDrawable(icon(weatherIcon(code, day), 44));
            top.addView(big, new LinearLayout.LayoutParams(dp(44), dp(44)));

            TextView temp = shadowed(text(48, ink(0xFF), "sans-serif-light"));
            temp.setTypeface(outfit(300, "sans-serif-light"));
            temp.setText(deg(cur.getDouble("temperature_2m")));
            temp.setPadding(dp(8), 0, dp(10), 0);
            top.addView(temp);

            LinearLayout desc = new LinearLayout(this);
            desc.setOrientation(LinearLayout.VERTICAL);
            TextView city = shadowed(text(13, ink(0xB3), "sans-serif"));
            city.setTypeface(outfit(500, "sans-serif-medium"));
            city.setText(cityName().toUpperCase(loc()));
            TextView what = shadowed(text(16, ink(0xFF), "sans-serif"));
            what.setTypeface(outfit(400, "sans-serif"));
            what.setText(weatherText(code));
            TextView feels = shadowed(text(13, ink(0xB3), "sans-serif"));
            feels.setTypeface(outfit(400, "sans-serif"));
            feels.setText(getString(R.string.feels_like, deg(cur.getDouble("apparent_temperature"))));
            desc.addView(city);
            desc.addView(what);
            desc.addView(feels);
            top.addView(desc);
            weatherBox.addView(top);

            // row 2: next four days
            JSONArray dates = daily.getJSONArray("time");
            JSONArray codes = daily.getJSONArray("weather_code");
            JSONArray max = daily.getJSONArray("temperature_2m_max");
            JSONArray min = daily.getJSONArray("temperature_2m_min");
            SimpleDateFormat iso = new SimpleDateFormat("yyyy-MM-dd", Locale.US);
            SimpleDateFormat dow = new SimpleDateFormat("EEE", loc());
            LinearLayout days = new LinearLayout(this);
            for (int i = 1; i < Math.min(5, dates.length()); i++) {
                LinearLayout col = new LinearLayout(this);
                col.setOrientation(LinearLayout.VERTICAL);
                col.setGravity(Gravity.CENTER_HORIZONTAL);
                TextView dn = shadowed(text(13, ink(0xB3), "sans-serif"));
                dn.setTypeface(outfit(500, "sans-serif-medium"));
                String d = dow.format(iso.parse(dates.getString(i)));
                dn.setText(d.substring(0, 1).toUpperCase(loc()) + d.substring(1));
                dn.setGravity(Gravity.CENTER_HORIZONTAL);
                ImageView di = new ImageView(this);
                di.setImageDrawable(icon(weatherIcon(codes.getInt(i), true), 22));
                LinearLayout.LayoutParams dip = new LinearLayout.LayoutParams(dp(22), dp(22));
                dip.topMargin = dp(4);
                dip.bottomMargin = dp(4);
                TextView dt = shadowed(text(13, ink(0xFF), "sans-serif"));
                dt.setTypeface(outfit(400, "sans-serif"));
                dt.setText(deg(max.getDouble(i)) + " " + deg(min.getDouble(i)));
                dt.setGravity(Gravity.CENTER_HORIZONTAL);
                col.addView(dn);
                col.addView(di, dip);
                col.addView(dt);
                LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(dp(64),
                        ViewGroup.LayoutParams.WRAP_CONTENT);
                days.addView(col, cp);
            }
            LinearLayout.LayoutParams dp2 = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            dp2.topMargin = dp(12);
            weatherBox.addView(days, dp2);
            return true;
        } catch (Exception e) {
            return false;
        }
    }

    private static RectF inflate(RectF r, float by) {
        return new RectF(r.left - by, r.top - by, r.right + by, r.bottom + by);
    }

    // ---------------------------------------------------------------- focus

    private void applyFocus(boolean animate) {
        if (area == BG || area == PHOTO) {
            styleBgRows();
            return;
        }
        if (area == MENU || area == PICK) {
            styleMenu();
            return;
        }
        if (area == CITY || area == CITYTYPE) {
            fillCityList();
            return;
        }
        RectF target;
        View tile = null;

        // pills and the account circle get a fully rounded plate, tiles a 16dp corner
        highlight.round = area == PILLS || area == AVATAR;
        if (area == PILLS) {
            target = inflate(pillRects.get(pillIdx), dp(4));
        } else if (area == AVATAR) {
            target = inflate(avatarRect, dp(5));
            tile = avatar;
        } else if (area == DOCK || area == MOVE) {
            target = inflate(dockRects.get(dockIdx), dp(area == MOVE ? 14 : 9));
            tile = dockTiles.get(dockIdx);
        } else if (area == GRID) {
            int k = gridRow * COLS + gridCol;
            target = inflate(gridRects.get(k), dp(9));
            tile = gridTiles.get(k);
        } else {
            target = dropRect(dropIdx);
        }
        (area == GRID ? gridHighlight : highlight).moveTo(target, animate);

        if (scaledTile != null && scaledTile != tile) {
            scaledTile.animate().scaleX(1f).scaleY(1f).setDuration(160).start();
        }
        if (tile != null) {
            float s = area == MOVE ? 1.18f : 1.08f; // a tile being moved is lifted higher
            tile.animate().scaleX(s).scaleY(s).setDuration(160).start();
        }
        scaledTile = tile;

        if (area == GRID) {
            float offset = Math.max(0, gridTop + gridRow * pitch - dp(220));
            appsPage.animate().translationY(-offset).setDuration(280)
                    .setInterpolator(new DecelerateInterpolator()).start();
        }
    }

    // ---------------------------------------------------------------- keys

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (recreating) return true;
        if (area == CITYTYPE) return super.onKeyDown(keyCode, event); // keys belong to the text field
        switch (keyCode) {
            case KeyEvent.KEYCODE_DPAD_LEFT:
                moveHorizontal(-1);
                return true;
            case KeyEvent.KEYCODE_DPAD_RIGHT:
                moveHorizontal(1);
                return true;
            case KeyEvent.KEYCODE_DPAD_UP:
                moveUp();
                return true;
            case KeyEvent.KEYCODE_DPAD_DOWN:
                moveDown();
                return true;
            case KeyEvent.KEYCODE_DPAD_CENTER:
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER:
                // OK acts on release, so that holding it can open the tile menu instead.
                // Long press is detected here from the key repeats (held >= 450 ms or the
                // system's long-press flag); onKeyLongPress alone was not reliable.
                if (event.getRepeatCount() == 0) {
                    okDown = true;
                    okConsumed = false;
                } else if (okDown && !okConsumed
                        && (event.isLongPress() || event.getEventTime() - event.getDownTime() >= 450)) {
                    okConsumed = true;
                    if (area == DOCK && !dockApps.isEmpty()) openTileMenu();
                    else if (area == AVATAR && !avatarEdit) startAvatarEdit();
                }
                return true;
            default:
                return super.onKeyDown(keyCode, event);
        }
    }

    private boolean okDown, okConsumed;

    private static boolean isOk(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER
                || keyCode == KeyEvent.KEYCODE_NUMPAD_ENTER;
    }

    @Override
    public boolean onKeyUp(int keyCode, KeyEvent event) {
        if (recreating) return true;
        if (area == CITYTYPE) return super.onKeyUp(keyCode, event);
        if (isOk(keyCode)) {
            // only a press that started here and wasn't a long press activates; this also
            // ignores the release of the OK that launched us from another screen
            if (okDown && !okConsumed) activate();
            okDown = false;
            return true;
        }
        return super.onKeyUp(keyCode, event);
    }

    private void moveHorizontal(int dir) {
        if (avatarEdit) {
            stepAvatar(dir);
            return;
        }
        if (area == BG) {
            changeBg(dir);
            return;
        }
        if (area == MOVE) {
            moveTile(0, dir);
            return;
        }
        if (area == MENU || area == PICK || area == CITY) return;
        if (area == PILLS) {
            if (pillIdx == 0 && dir < 0) {
                area = AVATAR; // the account circle sits left of the whole top bar
            } else {
                pillIdx = Math.max(0, Math.min(pills.length - 1, pillIdx + dir));
            }
        } else if (area == AVATAR) {
            if (dir < 0) return;
            area = PILLS;
            pillIdx = 0;
        } else if (area == DOCK) {
            // crosses the weather gap between the blocks; stops at the screen edges
            int next = dockAt(dockRow(dockIdx), dockCol(dockIdx) + dir);
            if (next < 0) return;
            dockIdx = next;
        } else if (area == GRID) {
            gridCol = Math.max(0, Math.min(colsInRow(gridRow) - 1, gridCol + dir));
        } else {
            return; // dropdown: left/right do nothing
        }
        applyFocus(true);
    }

    private void moveUp() {
        if (avatarEdit) return;
        if (area == BG || area == PHOTO) {
            moveBgVertical(-1);
            return;
        }
        if (area == MENU || area == PICK || area == MOVE) {
            moveMenuVertical(-1);
            return;
        }
        if (area == CITY) {
            moveCity(-1);
            return;
        }
        if (area == DOCK) {
            if (dockRow(dockIdx) == 1) {
                dockIdx = dockAt(0, dockCol(dockIdx));
            } else {
                area = PILLS;
            }
        } else if (area == GRID) {
            if (gridRow == 0) return; // Back closes the page
            gridRow--;
        } else if (area == DROP) {
            if (dropIdx > 0) {
                dropIdx--;
            } else {
                closeDrop();
            }
        } else {
            return; // top bar: nothing above
        }
        applyFocus(true);
    }

    private void moveBgVertical(int dir) {
        if (area == BG) {
            bgRow = Math.max(0, Math.min(bgRows.length - 1, bgRow + dir));
        } else {
            photoIdx = Math.max(0, Math.min(photoNames.size() - 1, photoIdx + dir));
            fillPhotoList();
        }
        styleBgRows();
    }

    private void moveDown() {
        if (avatarEdit) return;
        if (area == BG || area == PHOTO) {
            moveBgVertical(1);
            return;
        }
        if (area == MENU || area == PICK || area == MOVE) {
            moveMenuVertical(1);
            return;
        }
        if (area == CITY) {
            moveCity(1);
            return;
        }
        if (area == PILLS) {
            if (dockApps.isEmpty()) return;
            area = DOCK;
        } else if (area == AVATAR) {
            if (dockApps.isEmpty()) return;
            area = DOCK;
            dockIdx = 0;
        } else if (area == DOCK) {
            int below = dockAt(1, dockCol(dockIdx));
            if (dockRow(dockIdx) == 1 || below < 0) return;
            dockIdx = below;
        } else if (area == GRID) {
            if (gridRow >= gridRows() - 1) return;
            gridRow++;
            gridCol = Math.min(gridCol, colsInRow(gridRow) - 1);
        } else if (area == DROP) {
            if (dropIdx >= inputs.size() - 1) return;
            dropIdx++;
        }
        applyFocus(true);
    }

    private void moveMenuVertical(int dir) {
        if (area == MOVE) {
            moveTile(dir, 0);
        } else if (area == MENU) {
            menuRow = Math.max(0, Math.min(menuRows.length - 1, menuRow + dir));
            styleMenu();
        } else {
            pickIdx = Math.max(0, Math.min(pickApps.size() - 1, pickIdx + dir));
            fillPickList();
        }
    }

    private void activate() {
        if (avatarEdit) {
            endAvatarEdit();
            return;
        }
        if (area == CITY) {
            activateCity();
            return;
        }
        if (area == MENU) {
            if (menuRow == 0) openPickList();
            else startMove();
            return;
        }
        if (area == PICK) {
            pickApp();
            return;
        }
        if (area == MOVE) {
            area = DOCK;
            applyFocus(true);
            return;
        }
        if (area == BG) {
            if (bgRow == ROW_PHOTO) openPhotoList();
            return;
        }
        if (area == PHOTO) {
            if (photoIsVideo.get(photoIdx)) pickVideo(photoUris.get(photoIdx));
            else pickPhoto(photoUris.get(photoIdx));
            return;
        }
        if (area == DOCK) {
            launch(dockApps.get(dockIdx));
        } else if (area == GRID) {
            launch(gridApps.get(gridRow * COLS + gridCol));
        } else if (area == DROP) {
            switchInput(inputs.get(dropIdx));
        } else if (area == AVATAR) {
            // Google TV's own "Accounts & sign-in" screen
            open(new Intent(Settings.ACTION_SYNC_SETTINGS));
        } else if (area == PILLS) {
            if (pillIdx == P_APPS) {
                if (gridApps.isEmpty()) return;
                showApps(true, true);
                area = GRID;
                applyFocus(false);
            } else if (pillIdx == P_INPUTS) {
                if (inputs.isEmpty()) {
                    Toast.makeText(this, R.string.no_inputs, Toast.LENGTH_SHORT).show();
                    return;
                }
                dropBox.setVisibility(View.VISIBLE);
                area = DROP;
                dropIdx = 0;
                applyFocus(true);
            } else if (pillIdx == P_WIFI) {
                open(new Intent(Settings.ACTION_WIFI_SETTINGS));
            } else {
                open(new Intent(Settings.ACTION_SETTINGS));
            }
        }
    }

    private void launch(App a) {
        if (WX_PKG.equals(a.pkg)) {
            openCity();
            return;
        }
        if (RAM_PKG.equals(a.pkg)) {
            clearRam();
            return;
        }
        if (BG_PKG.equals(a.pkg)) {
            openBg();
            return;
        }
        Intent i = new Intent(Intent.ACTION_MAIN);
        i.setComponent(new ComponentName(a.pkg, a.cls));
        open(i);
    }

    private void open(Intent i) {
        try {
            i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED);
            startActivity(i);
        } catch (Exception e) {
            Toast.makeText(this, getString(R.string.open_failed, e.getMessage()), Toast.LENGTH_LONG).show();
        }
    }

    private void switchInput(InputItem it) {
        try {
            Intent i;
            if (it.passthrough) {
                i = new Intent(Intent.ACTION_VIEW, TvContract.buildChannelUriForPassthroughInput(it.id));
            } else {
                i = new Intent(Intent.ACTION_VIEW, TvContract.buildChannelsUriForInput(it.id));
            }
            closeDrop();
            applyFocus(false);
            // Not open(): with RESET_TASK_IF_NEEDED Sony's TV app is only brought to the
            // front and never sees the new URI, so it stays on whatever it showed before.
            i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(i);
        } catch (Exception e) {
            Toast.makeText(this, getString(R.string.switch_failed, e.getMessage()), Toast.LENGTH_LONG).show();
        }
    }

    // ---------------------------------------------------------------- glass highlight

    private class HighlightView extends View {
        private final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint stroke = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF from = new RectF();
        private final RectF to = new RectF();
        private final RectF cur = new RectF();
        private boolean has;
        private ValueAnimator anim;
        boolean round;

        HighlightView(Context c) {
            super(c);
            // no coloured ring (user didn't want the yellow one): a brighter glass plate
            // behind the tile plus the tile's own zoom mark the focus
            fill.setColor(darkUi ? 0xE6FFFFFF : 0x4DFFFFFF);
            stroke.setStyle(Paint.Style.STROKE);
            stroke.setStrokeWidth(Math.max(1, dp(1)));
            stroke.setColor(darkUi ? 0x33000000 : 0x33FFFFFF);
        }

        void moveTo(RectF r, boolean animate) {
            if (anim != null) anim.cancel();
            if (!has || !animate) {
                cur.set(r);
                to.set(r);
                has = true;
                invalidate();
                return;
            }
            from.set(cur);
            to.set(r);
            anim = ValueAnimator.ofFloat(0f, 1f);
            anim.setDuration(300);
            anim.setInterpolator(new OvershootInterpolator(1.1f));
            anim.addUpdateListener(new ValueAnimator.AnimatorUpdateListener() {
                @Override
                public void onAnimationUpdate(ValueAnimator a) {
                    float f = (Float) a.getAnimatedValue();
                    cur.left = from.left + (to.left - from.left) * f;
                    cur.top = from.top + (to.top - from.top) * f;
                    cur.right = from.right + (to.right - from.right) * f;
                    cur.bottom = from.bottom + (to.bottom - from.bottom) * f;
                    invalidate();
                }
            });
            anim.start();
        }

        @Override
        protected void onDraw(Canvas canvas) {
            if (!has) return;
            float rad = round ? cur.height() / 2f : dp(16);
            canvas.drawRoundRect(cur, rad, rad, fill);
            canvas.drawRoundRect(cur, rad, rad, stroke);
        }
    }

    // ---------------------------------------------------------------- background

    // Background: a wave theme drawn with gradients (no bitmap, nothing animated), a plain
    // gradient, or the user's photo; plus an optional black dim layer.
    private class WaveView extends View {
        private final Paint sky = new Paint();
        private final Paint[] paints = new Paint[4];
        private final Path[] waves = new Path[4];
        private final Paint shadeTop = new Paint();
        private final Paint shadeBottom = new Paint();
        private final Paint photoPaint = new Paint(Paint.FILTER_BITMAP_FLAG);
        private final RectF full = new RectF();
        private final RectF dst = new RectF();
        private final android.graphics.Rect src = new android.graphics.Rect();
        Bitmap photo;
        int fit;
        private int theme, dimPercent;

        WaveView(Context c) {
            super(c);
            for (int i = 0; i < 4; i++) {
                paints[i] = new Paint(Paint.ANTI_ALIAS_FLAG);
                waves[i] = new Path();
            }
        }

        void setTheme(int theme, int dimPercent) {
            this.theme = theme;
            this.dimPercent = dimPercent;
            if (getWidth() > 0) build(getWidth(), getHeight());
            invalidate();
        }

        @Override
        protected void onSizeChanged(int w, int h, int ow, int oh) {
            build(w, h);
        }

        private void drawPhoto(Canvas canvas, int w, int h) {
            int pw = photo.getWidth(), ph = photo.getHeight();
            src.set(0, 0, pw, ph);
            if (fit == FIT_STRETCH) {
                dst.set(full);
            } else if (fit == FIT_FILL) {
                // crop the middle of the picture to the screen's shape
                float s = Math.max((float) w / pw, (float) h / ph);
                int cw = Math.round(w / s), ch = Math.round(h / s);
                src.set((pw - cw) / 2, (ph - ch) / 2, (pw + cw) / 2, (ph + ch) / 2);
                dst.set(full);
            } else {
                // FIT: whole picture, black bars; CENTER: as stored (never upscaled)
                float s = fit == FIT_FIT ? Math.min((float) w / pw, (float) h / ph) : 1f;
                float dw = pw * s, dh = ph * s;
                dst.set((w - dw) / 2f, (h - dh) / 2f, (w + dw) / 2f, (h + dh) / 2f);
                canvas.drawColor(Color.BLACK);
            }
            canvas.drawBitmap(photo, src, dst, photoPaint);
        }

        private void build(int w, int h) {
            full.set(0, 0, w, h);
            int[] solid = theme >= WAVE_THEMES.length && theme < THEME_PHOTO
                    ? SOLID_THEMES[theme - WAVE_THEMES.length] : null;
            int[] t = theme < WAVE_THEMES.length ? WAVE_THEMES[theme] : WAVE_THEMES[0];
            sky.setShader(new LinearGradient(0, 0, w, h * 0.6f,
                    solid != null ? solid : new int[]{t[0], t[1], t[2]}, null, Shader.TileMode.CLAMP));

            // light blue crest behind everything
            Path p = waves[0];
            p.reset();
            p.moveTo(0, h * 0.42f);
            p.cubicTo(w * 0.25f, h * 0.30f, w * 0.45f, h * 0.12f, w * 0.70f, h * 0.20f);
            p.cubicTo(w * 0.85f, h * 0.25f, w * 0.93f, h * 0.16f, w, h * 0.14f);
            p.lineTo(w, h);
            p.lineTo(0, h);
            p.close();
            paints[0].setShader(new LinearGradient(0, h * 0.12f, 0, h * 0.6f,
                    t[3], t[4], Shader.TileMode.CLAMP));

            // red -> orange main wave
            p = waves[1];
            p.reset();
            p.moveTo(0, h * 0.30f);
            p.cubicTo(w * 0.22f, h * 0.26f, w * 0.30f, h * 0.50f, w * 0.55f, h * 0.44f);
            p.cubicTo(w * 0.75f, h * 0.39f, w * 0.88f, h * 0.30f, w, h * 0.34f);
            p.lineTo(w, h);
            p.lineTo(0, h);
            p.close();
            paints[1].setShader(new LinearGradient(0, 0, w, 0,
                    new int[]{t[5], t[6], t[7]}, null, Shader.TileMode.CLAMP));

            // lighter orange fold
            p = waves[2];
            p.reset();
            p.moveTo(0, h * 0.62f);
            p.cubicTo(w * 0.28f, h * 0.50f, w * 0.52f, h * 0.48f, w * 0.72f, h * 0.40f);
            p.cubicTo(w * 0.86f, h * 0.35f, w * 0.95f, h * 0.40f, w, h * 0.42f);
            p.lineTo(w, h);
            p.lineTo(0, h);
            p.close();
            paints[2].setShader(new LinearGradient(0, h * 0.40f, 0, h,
                    t[8], t[9], Shader.TileMode.CLAMP));

            // deep magenta at the bottom
            p = waves[3];
            p.reset();
            p.moveTo(0, h * 0.80f);
            p.cubicTo(w * 0.30f, h * 0.70f, w * 0.60f, h * 0.95f, w, h * 0.76f);
            p.lineTo(w, h);
            p.lineTo(0, h);
            p.close();
            paints[3].setShader(new LinearGradient(0, h * 0.70f, w, h,
                    t[10], t[11], Shader.TileMode.CLAMP));

            // keep the clock and the tiles readable
            // darken (or, with dark text, lighten) behind the clock and the tiles
            int veil = darkUi ? 0xFFFFFF : 0x000000;
            shadeTop.setShader(new LinearGradient(0, 0, 0, h * 0.35f,
                    0x59000000 | veil, veil, Shader.TileMode.CLAMP));
            shadeBottom.setShader(new LinearGradient(0, h * 0.45f, 0, h,
                    veil, 0x66000000 | veil, Shader.TileMode.CLAMP));
        }

        @Override
        protected void onDraw(Canvas canvas) {
            int w = getWidth(), h = getHeight();
            if (theme == THEME_VIDEO) {
                // the TextureView below shows the video; only veils and dimming go on top
            } else if (theme == THEME_PHOTO && photo != null) {
                drawPhoto(canvas, w, h);
            } else {
                canvas.drawRect(0, 0, w, h, sky);
                if (theme < WAVE_THEMES.length) {
                    for (int i = 0; i < 4; i++) canvas.drawPath(waves[i], paints[i]);
                }
            }
            canvas.drawRect(0, 0, w, h * 0.35f, shadeTop);
            canvas.drawRect(0, h * 0.45f, w, h, shadeBottom);
            if (dimPercent > 0) canvas.drawColor(Color.argb(dimPercent * 255 / 100, 0, 0, 0));
        }
    }
}
