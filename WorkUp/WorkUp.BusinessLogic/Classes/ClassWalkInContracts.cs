namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// What the owner sends when a client shows up for an already generated class session without a booking (RF-11,
/// Regla 15): the class session, the client picked from the business's records, the optional charge collected at
/// arrival and the optional observation for the next visit. <see cref="Charge"/> reuses the per-student charge of the
/// attendance flow on purpose — amount and reason have exactly the same rule — and may be absent, because attendance
/// and payment are independent facts and the money can be recorded later (D-20, Regla 21). The client is never created
/// here: this path only accepts a client that already belongs to the business, unlike the public booking flow.
/// </summary>
public sealed record ClassWalkInRequest(
    int AppointmentId,
    int ClientId,
    ClassAttendanceCharge? Charge = null,
    string? Note = null);

/// <summary>
/// Result of an at-arrival walk-in (RF-11): the annotation that was created and marked as attended, its client and
/// the start of the class it joined. <see cref="ChargedAmount"/> is the amount that was actually charged, or
/// <c>null</c> when the owner registered the arrival without a charge.
/// </summary>
public sealed record ClassWalkInOutcome(
    int AppointmentId,
    int BookingId,
    int ClientId,
    DateTime StartsAt,
    decimal? ChargedAmount);
