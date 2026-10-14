using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// The optional independent charge of a drop-in attendance (RF-11, D-20). Attendance and payment are independent
/// facts: this record only exists when the owner asked for the charge with the attendance, and it is written in the
/// same transaction. <see cref="Amount"/> is the agreed amount typed at the desk and <see cref="PriceReason"/> is the
/// optional reason kept next to it, exactly like the individual path (<c>OwnerDayCharge</c>).
/// </summary>
public sealed record ClassAttendanceCharge(decimal Amount, string? PriceReason = null);

/// <summary>
/// What the owner sends when registering the attendance of one student (RF-11): the optional charge and the optional
/// observation for the next visit. A class attendance never requires a charge, because a fixed student already paid
/// their membership and only a drop-in pays on arrival; the note belongs to the client's record, not to the booking.
/// The absence path takes no request at all, because an absence is a fact and never charges or notes anything.
/// </summary>
public sealed record ClassAttendanceRequest(
    ClassAttendanceCharge? Charge = null,
    string? Note = null);

/// <summary>
/// One class annotation read under the attendance lock (RF-11, RF-12, D-16, D-20, D-23), with everything the
/// status rules, the coverage rule and the optional charge need: the booking, its client, its session, the modality,
/// the enrollment status and every paid membership period. The tenant is part of the row, so a booking of another
/// business is simply not found. <see cref="EnrollmentStatus"/> and <see cref="Periods"/> are empty or <c>null</c> for
/// a drop-in, which has no enrollment. <see cref="ListPrice"/> is the tariff of the class service, recorded on the
/// charge beside the amount actually charged, exactly like the individual path records it.
/// </summary>
public sealed record ClassAttendanceBookingState(
    int BusinessId,
    int BookingId,
    int ClientId,
    int AppointmentId,
    DateTime StartsAt,
    AppointmentKind Kind,
    BookingStatus BookingStatus,
    BookingModality? Modality,
    int? EnrollmentId,
    EnrollmentStatus? EnrollmentStatus,
    decimal ListPrice,
    IReadOnlyList<MembershipPeriod> Periods)
{
    public DateOnly ClassDate => DateOnly.FromDateTime(StartsAt);
}

/// <summary>
/// The charge written for a drop-in attendance (RF-11, D-20). It references the concrete origin through its
/// <see cref="BookingId"/> and its concept is always <see cref="PaymentConcept.DropInClass"/>, so
/// <c>CK_Payments_Reference</c> is satisfied with a booking and no enrollment. <see cref="PaidAt"/> is the single
/// clock read of the operation: the charge and the attendance it accompanies carry the same instant.
/// </summary>
public sealed record ClassAttendancePayment(
    int BusinessId,
    int ClientId,
    int BookingId,
    PaymentConcept Concept,
    decimal Amount,
    decimal ListPrice,
    string? PriceReason,
    DateTime PaidAt);
