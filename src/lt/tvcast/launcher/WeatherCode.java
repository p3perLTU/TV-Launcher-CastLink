package lt.tvcast.launcher;

/**
 * WMO weather interpretation codes as sent by Open-Meteo (`weather_code`), grouped into what
 * the launcher can show. Plain Java (no Android), unit tested in tests/java; MainActivity maps
 * each kind to an icon and a text.
 */
enum WeatherCode {
    CLEAR, MAINLY_CLEAR, PARTLY_CLOUDY, OVERCAST, FOG, DRIZZLE, RAIN, SNOW, THUNDER;

    static WeatherCode of(int code) {
        if (code == 0) return CLEAR;
        if (code == 1) return MAINLY_CLEAR;
        if (code == 2) return PARTLY_CLOUDY;
        if (code == 3) return OVERCAST;
        if (code == 45 || code == 48) return FOG;
        if (code >= 51 && code <= 57) return DRIZZLE;
        if ((code >= 71 && code <= 77) || code == 85 || code == 86) return SNOW;
        if (code >= 95) return THUNDER;
        return RAIN;
    }

    /** temperature as shown on screen: rounded, with a degree sign */
    static String degrees(double t) {
        return Math.round(t) + "°";
    }
}
