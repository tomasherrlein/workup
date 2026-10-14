using System.Globalization;

namespace WorkUp.BusinessLogic.Common;

/// <summary>
/// Spanish day names for business rule messages. The web layer formats dates and times for display through its own
/// <c>Formatting</c> helper; this small twin exists so the business layer can compose a Spanish message (for
/// example naming the day of a schedule problem) without depending on the web project.
/// </summary>
public static class SpanishCalendar
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-AR");

    /// <summary><c>lunes</c></summary>
    public static string DayName(DayOfWeek day) => Culture.DateTimeFormat.GetDayName(day);
}
