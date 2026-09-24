using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// A dated class session as storage returns it for a drop-in booking (RF-07 cupo &gt; 1). The bookings are reduced to
/// their status because the count of places only needs to know which ones occupy a place; cancelled ones are
/// included so the caller, not the query, decides what a cancellation frees (D-25).
/// </summary>
public sealed record ClassSessionRow(
    int AppointmentId,
    int ServiceId,
    int WorkerId,
    DateTime StartsAt,
    int MaxCapacity,
    AppointmentStatus Status,
    IReadOnlyList<BookingStatus> BookingStatuses)
{
    public DateOnly Date => DateOnly.FromDateTime(StartsAt);

    public TimeOnly StartTime => TimeOnly.FromDateTime(StartsAt);
}

/// <summary>One bookable session offered to the client, with its occupancy already resolved by the capacity rule.</summary>
public sealed record ClassSessionOption(
    int AppointmentId,
    int ServiceId,
    int WorkerId,
    DateOnly Date,
    TimeOnly StartTime,
    int MaxCapacity,
    int PlacesTaken,
    int RemainingPlaces);

/// <summary>
/// The client the booking belongs to: identified by email, created without a password when the email is unknown
/// (RF-07 step 5, the T09 flow).
/// </summary>
public sealed record DropInClient(int ClientId, string Email, string FirstName, string LastName);

/// <summary>
/// The drop-in booking to write. It is created in <c>Pendiente</c> and carries its created timestamp so the caller's
/// single clock read is what gets persisted.
/// </summary>
public sealed record DropInInsert(int BusinessId, int AppointmentId, int ClientId, DateTime CreatedAt);

/// <summary>
/// The email to queue in the outbox, never to send (RF-17, D-22). <see cref="DedupKey"/> is stable per booking so a
/// retry and the scheduled reminder cannot send the same notice twice.
/// </summary>
public sealed record DropInNotification(
    int BusinessId,
    int BookingId,
    NotificationType Type,
    string DedupKey,
    DateTime CreatedAt);

/// <summary>What the public form sends: the chosen session and the contact data (RF-07 steps 3-4).</summary>
public sealed record DropInBookingRequest(
    int AppointmentId,
    string Email,
    string FirstName,
    string LastName,
    string? Phone);

/// <summary>Result of a booked drop-in, enough for the confirmation the client receives (RF-07 outputs).</summary>
public sealed record DropInBookingOutcome(
    int AppointmentId,
    int BookingId,
    int ClientId,
    DateOnly Date,
    TimeOnly StartTime,
    bool ImmediateEmailQueued);
