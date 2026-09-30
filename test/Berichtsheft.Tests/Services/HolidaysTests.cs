using Berichtsheft.Core.Services;
using Xunit;

namespace Berichtsheft.Tests.Services;

// Ported from tests/test_misc.py::test_holidays
public class HolidaysTests
{
    [Fact]
    public void BavariaHasHeiligeDreiKoenigeAndKarfreitag()
    {
        var h = Holidays.GermanHolidays(2026, "BY");
        Assert.Equal("Tag der Deutschen Einheit", h[new DateOnly(2026, 10, 3)]);
        Assert.True(h.ContainsKey(new DateOnly(2026, 4, 3))); // Karfreitag 2026
        Assert.True(h.ContainsKey(new DateOnly(2026, 1, 6))); // Heilige Drei Koenige in Bayern
        Assert.False(Holidays.GermanHolidays(2026, "NW").ContainsKey(new DateOnly(2026, 1, 6)));
    }
}
