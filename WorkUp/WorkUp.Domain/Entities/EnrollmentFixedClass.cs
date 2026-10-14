using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Clase fija of an enrollment, valid in a date range. A fixed schedule change (RF-21) closes the current row the
/// day before the effective date and opens a new one from it, so past dates are never modified.
/// </summary>
public class EnrollmentFixedClass : Entity
{
    public int EnrollmentId { get; set; }
    public int GridClassId { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }

    public bool IsValidOn(DateOnly date) => date >= ValidFrom && (ValidTo is null || date <= ValidTo);
}
