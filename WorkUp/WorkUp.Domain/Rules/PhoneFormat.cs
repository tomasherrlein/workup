namespace WorkUp.Domain.Rules;

/// <summary>
/// Basic phone format rule (RF 1): only digits, spaces, dashes, parentheses and a leading plus sign, with a
/// plausible amount of digits. This is a shape check, not a real number validator (no country-code rules).
/// Pure, no I/O.
/// </summary>
public static class PhoneFormat
{
    public const int MinDigits = 6;
    public const int MaxDigits = 20;

    public static bool IsValid(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return false;

        var digits = 0;
        for (var index = 0; index < phone.Length; index++)
        {
            var character = phone[index];
            if (char.IsDigit(character))
            {
                digits++;
                continue;
            }

            if (character is ' ' or '-' or '(' or ')' || (character == '+' && index == 0))
                continue;

            return false;
        }

        return digits is >= MinDigits and <= MaxDigits;
    }
}
