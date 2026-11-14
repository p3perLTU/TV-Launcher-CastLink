package lt.tvcast.ui;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.RadialGradient;
import android.graphics.Rect;
import android.graphics.Shader;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.util.TypedValue;
import android.view.Gravity;
import android.widget.TextView;

/**
 * The CastLink look, shared by the TV and the phone app (and matching the iPad page and the PC
 * window): near-black page with a blue glow top left and a violet one bottom right, glass cards,
 * blue-violet gradient buttons. Everything is drawn in code, no image files.
 */
public final class Look {
    public static final int BG = 0xFF0A0A10, CARD = 0xB81C1C22, LINE = 0x17FFFFFF, INK = 0xFFF2F2F7,
            MUTED = 0xFFA1A1AA, FAINT = 0xFF8E8E93, BLUE = 0xFF3884FF, VIOLET = 0xFF7C4DFF,
            GOOD = 0xFF34C759, WAIT = 0xFFFFB340, BAD = 0xFFFF6961;

    private Look() {}

    public static int dp(Context c, float v) {
        return Math.round(v * c.getResources().getDisplayMetrics().density);
    }

    /** the page background (opaque) */
    public static Drawable background() {
        return new Drawable() {
            private final Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);

            @Override
            public void draw(Canvas c) {
                Rect b = getBounds();
                float w = b.width(), h = b.height(), r = Math.max(w, h);
                c.drawColor(BG);
                p.setShader(new RadialGradient(w * 0.15f, -h * 0.1f, r * 0.75f, 0x613884FF, 0x003884FF, Shader.TileMode.CLAMP));
                c.drawRect(b, p);
                p.setShader(new RadialGradient(w * 1.1f, h * 1.1f, r * 0.7f, 0x577C4DFF, 0x007C4DFF, Shader.TileMode.CLAMP));
                c.drawRect(b, p);
            }

            @Override public void setAlpha(int a) {}
            @Override public void setColorFilter(ColorFilter f) {}
            @Override public int getOpacity() { return PixelFormat.OPAQUE; }
        };
    }

    /** a glass card */
    public static GradientDrawable card(Context c) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(CARD);
        g.setStroke(dp(c, 1), LINE);
        g.setCornerRadius(dp(c, 22));
        return g;
    }

    /** a glass card that can be tapped (ripple) and, on a TV, focused (accent ring) */
    public static Drawable tappableCard(Context c) {
        GradientDrawable focused = card(c);
        focused.setStroke(dp(c, 3), BLUE);
        StateListDrawable s = new StateListDrawable();
        s.addState(new int[] { android.R.attr.state_focused }, focused);
        s.addState(new int[0], card(c));
        return new RippleDrawable(ColorStateList.valueOf(0x22FFFFFF), s, null);
    }

    /** the big gradient button; red while it means "stop" */
    public static Drawable button(Context c, boolean stop) {
        GradientDrawable g = new GradientDrawable(GradientDrawable.Orientation.TL_BR,
                stop ? new int[] { 0xFFFF5E57, 0xFFE0384F } : new int[] { BLUE, VIOLET });
        g.setCornerRadius(dp(c, 16));
        return new RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), g, null);
    }

    /** a text field: dark, rounded */
    public static GradientDrawable field(Context c) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(0x59000000);
        g.setStroke(dp(c, 1), 0x24FFFFFF);
        g.setCornerRadius(dp(c, 14));
        return g;
    }

    /** a small rounded label (key hints, the TV address) */
    public static GradientDrawable pill(Context c, int color) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(color);
        g.setCornerRadius(dp(c, 100));
        return g;
    }

    /** the state dot: green running, amber waiting, red problem */
    public static GradientDrawable dot(int color) {
        GradientDrawable g = new GradientDrawable();
        g.setShape(GradientDrawable.OVAL);
        g.setColor(color);
        return g;
    }

    public static TextView text(Context c, CharSequence s, float sp, int color, boolean bold) {
        TextView t = new TextView(c);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        t.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
        if (bold) t.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        t.setLineSpacing(0, 1.12f);
        return t;
    }
}
