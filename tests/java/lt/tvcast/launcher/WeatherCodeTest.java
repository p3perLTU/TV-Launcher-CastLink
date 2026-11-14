package lt.tvcast.launcher;

import static lt.tvcast.testing.Assert.*;

/** Open-Meteo WMO codes -> what the weather widget shows. */
final class WeatherCodeTest {
    static void check(WeatherCode expected, int... codes) {
        for (int c : codes) assertEquals(expected, WeatherCode.of(c), "code " + c);
    }

    static void testCodes() {
        check(WeatherCode.CLEAR, 0);
        check(WeatherCode.MAINLY_CLEAR, 1);
        check(WeatherCode.PARTLY_CLOUDY, 2);
        check(WeatherCode.OVERCAST, 3);
        check(WeatherCode.FOG, 45, 48);
        check(WeatherCode.DRIZZLE, 51, 53, 55, 56, 57);
        check(WeatherCode.RAIN, 61, 63, 65, 66, 67, 80, 81, 82);
        check(WeatherCode.SNOW, 71, 73, 75, 77, 85, 86);
        check(WeatherCode.THUNDER, 95, 96, 99);
    }

    static void testDegrees() {
        assertEquals("22°", WeatherCode.degrees(21.5), "rounds half up");
        assertEquals("21°", WeatherCode.degrees(21.4), "rounds down");
        assertEquals("-3°", WeatherCode.degrees(-2.6), "below zero");
        assertEquals("0°", WeatherCode.degrees(-0.4), "no minus zero");
    }
}
