using WorkUp.Domain.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Inscripción a clases fijas. Starts pending payment and retains its places (D-21); the first payment activates
/// the membership. Membership coverage lives in <see cref="MembershipPeriod"/>.
/// </summary>
public class Enrollment : BusinessEntity
{
    public int ClientId { get; set; }
    public int WeeklyFrequency { get; set; }
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.PendingPayment;

    public decimal AgreedPrice { get; set; }
    public string? PriceReason { get; set; }

    /// <summary>Día de referencia used to compute expirations (D-23). Set when the membership is activated.</summary>
    public int? ReferenceDay { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? AnnulledAt { get; set; }
    public DateTime? TerminatedAt { get; set; }

    public List<EnrollmentFixedClass> FixedClasses { get; set; } = [];

    /// <summary>Whether the enrollment still holds its fixed places.</summary>
    public bool HoldsPlaces => Status is EnrollmentStatus.PendingPayment or EnrollmentStatus.Active;
}
