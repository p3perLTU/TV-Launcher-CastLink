package lt.tvcast.launcher;

import static lt.tvcast.testing.Assert.*;

/** Home screen tile positions: two 2x2 blocks seen as one 2x4 grid. */
final class DockGridTest {
    // tile index at [row][col], see the picture in DockGrid
    static final int[][] LAYOUT = {{0, 1, 4, 5}, {2, 3, 6, 7}};

    static void testIndexToPosition() {
        for (int r = 0; r < 2; r++) {
            for (int c = 0; c < 4; c++) {
                int i = LAYOUT[r][c];
                assertEquals(r, DockGrid.row(i), "row of tile " + i);
                assertEquals(c, DockGrid.col(i), "column of tile " + i);
            }
        }
    }

    static void testPositionToIndex() {
        for (int r = 0; r < 2; r++) {
            for (int c = 0; c < 4; c++) assertEquals(LAYOUT[r][c], DockGrid.at(r, c, 8), "tile at " + r + "," + c);
        }
    }

    static void testRoundTrip() {
        for (int i = 0; i < 8; i++) assertEquals(i, DockGrid.at(DockGrid.row(i), DockGrid.col(i), 8), "tile " + i);
    }

    static void testOutsideTheGrid() {
        assertEquals(-1, DockGrid.at(-1, 0, 8), "above the top row (the pills)");
        assertEquals(-1, DockGrid.at(2, 0, 8), "below the bottom row");
        assertEquals(-1, DockGrid.at(0, -1, 8), "left of the first column");
        assertEquals(-1, DockGrid.at(0, 4, 8), "right of the last column");
    }

    static void testFewerApps() {
        // with only 5 apps the right block has just its first tile
        assertEquals(4, DockGrid.at(0, 2, 5), "tile 4 exists");
        assertEquals(-1, DockGrid.at(0, 3, 5), "tile 5 missing");
        assertEquals(-1, DockGrid.at(1, 2, 5), "tile 6 missing");
        assertEquals(3, DockGrid.at(1, 1, 5), "left block complete");
    }
}
