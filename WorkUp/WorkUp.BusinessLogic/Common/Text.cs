namespace WorkUp.BusinessLogic.Common;

public static class Text
{
    /// <summary>Emails identify clients (Regla 7), so they are compared and stored trimmed and lower-case.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
