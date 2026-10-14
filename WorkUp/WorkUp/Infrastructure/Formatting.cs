using System.Globalization;

namespace WorkUp.Infrastructure;

/// <summary>
/// Display formatting in Argentine Spanish. Model binding stays culture-invariant on purpose, so "12500.50" from a
/// number input is never read as 1250050; format only for display, through these helpers.
/// </summary>
public static class Formatting
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-AR");

    /// <summary><c>$ 12.500</c>, or <c>$ 12.500,50</c> when there are cents.</summary>
    public static string Money(decimal amount) =>
        "$ " + amount.ToString(amount % 1 == 0 ? "N0" : "N2", Culture);

    /// <summary><c>10:00</c></summary>
    public static string Time(DateTime value) => value.ToString("HH:mm", Culture);

    public static string Time(TimeOnly value) => value.ToString("HH:mm", Culture);

    /// <summary><c>martes 14 de mayo</c></summary>
    public static string LongDate(DateOnly date) => date.ToString("dddd d 'de' MMMM", Culture);

    public static string LongDate(DateTime value) => LongDate(DateOnly.FromDateTime(value));

    /// <summary><c>14/05/2026</c></summary>
    public static string ShortDate(DateOnly date) => date.ToString("dd/MM/yyyy", Culture);

    public static string ShortDate(DateTime value) => ShortDate(DateOnly.FromDateTime(value));

    /// <summary><c>lunes</c></summary>
    public static string DayName(DayOfWeek day) => Culture.DateTimeFormat.GetDayName(day);

    private static readonly string[] ShortDayNames = ["Dom", "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb"];

    /// <summary><c>Lun</c>. Used for one-line summaries (worker rows) where the full day name would not fit.</summary>
    public static string ShortDayName(DayOfWeek day) => ShortDayNames[(int)day];

    /// <summary>
    /// <c>••••••5678</c>: every digit but the last four hidden behind a bullet (DESIGN.md "masked phone" secondary
    /// line, owner lists only). Non-digit characters (spaces, dashes) are dropped instead of preserved, since the
    /// point is to shorten what is shown, not to reproduce the original formatting.
    /// </summary>
    public static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length <= 4
            ? new string('•', digits.Length)
            : new string('•', digits.Length - 4) + digits[^4..];
    }
}
