namespace WorkUp.BusinessLogic.Abstractions;

/// <summary>
/// Current time in the business time zone (plan assumption 12). Every rule that depends on "now" uses this port,
/// never <see cref="DateTime.Now"/>, so tests and the simulated clock (T26) control time.
/// </summary>
public interface IClock
{
    DateTime Now { get; }
    DateOnly Today => DateOnly.FromDateTime(Now);
}
