using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>
/// One paid month of a membership (abono). Coverage runs from <see cref="CoverageStart"/> up to the day before
/// <see cref="ExpiresOn"/>: the expiry date is the first day without coverage (D-23).
/// </summary>
public class MembershipPeriod : Entity
{
    public int EnrollmentId { get; set; }
    public DateOnly CoverageStart { get; set; }
    public DateOnly ExpiresOn { get; set; }
    public int PaymentId { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool Covers(DateOnly date) => date >= CoverageStart && date < ExpiresOn;
}
