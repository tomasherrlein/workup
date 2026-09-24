using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// One booking of a session reduced to the facts the drop-in flow needs: which booking it is, who holds it, whether
/// it occupies a place and — on the owner roster — the client's name (D-25). The status is kept per booking instead
/// of as a bare list so the caller can both count the places and detect that the same client already holds a
/// non-cancelled booking of the session. <see cref="BookingId"/>, <see cref="Modality"/> and <see cref="ClientName"/>
/// are filled by every read of the port; <see cref="BookingId"/> has no meaning for a booking that was not read yet
/// and is <c>0</c> there, and <see cref="Modality"/> is <c>null</c> for an individual appointment, which has no
/// modality at all.
/// </summary>
public sealed record ClassSessionBooking(
    int ClientId,
    BookingStatus Status,
    int BookingId = 0,
    BookingModality? Modality = null,
    string ClientName = "");

/// <summary>
/// A dated class session as storage returns it for a drop-in booking (RF-07 cupo &gt; 1). <see cref="Kind"/> is the
/// discriminant: only a <c>ClassSession</c> can receive drop-ins, because an individual turn is also <c>Programado</c>
/// with capacity 1 and would otherwise look bookable. The bookings are kept with their client, modality and status
/// because the count of places needs the statuses, the owner roster needs the modality and the client's name, and the
/// same shape then serves both reads; cancelled ones are included so the caller, not the query, decides what a
/// cancellation frees (D-25).
/// </summary>
public sealed record ClassSessionRow(
    int AppointmentId,
    int ServiceId,
    int WorkerId,
    DateTime StartsAt,
    int MaxCapacity,
    AppointmentKind Kind,
    AppointmentStatus Status,
    IReadOnlyList<ClassSessionBooking> Bookings)
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
/// One drop-in annotation of an upcoming session as the owner roster reads it (RF-22, RF-07 cupo &gt; 1): which
/// booking it is, which client holds it, that client's full name and the status of the annotation. Only an annotation
/// with <c>Modality = Suelta</c> is ever projected here: a fixed annotation of the same session is not a drop-in, and
/// the roster must never present it as one.
/// </summary>
public sealed record ClassDropInRosterEntry(int BookingId, int ClientId, string ClientName, BookingStatus Status);

/// <summary>
/// An upcoming class session of the business with its drop-in annotations, for the owner screen (RF-22). Cancelled
/// annotations are part of the answer, with <see cref="BookingStatus.Cancelled"/> as their status: the owner has to
/// know that the place was released and by whom, and the status is what lets the screen tell an active annotation
/// from a cancelled one without a second query. A suspended session is part of the answer too, because suspending a
/// class does not cancel its annotations and the owner still needs to see who is booked in it.
/// </summary>
public sealed record ClassDropInRosterSession(
    int AppointmentId,
    int ServiceId,
    int WorkerId,
    DateTime StartsAt,
    int MaxCapacity,
    AppointmentStatus Status,
    IReadOnlyList<ClassDropInRosterEntry> DropIns)
{
    public DateOnly Date => DateOnly.FromDateTime(StartsAt);

    public TimeOnly StartTime => TimeOnly.FromDateTime(StartsAt);
}

/// <summary>
/// The drop-in annotation read under the cancellation lock (RF-22): the booking and the session facts the rules
/// need. It is read inside the caller's transaction, scoped by business, so <see cref="BookingId"/> of another
/// business is simply not there. <see cref="Modality"/> and <see cref="Kind"/> are what let the caller refuse a
/// booking that is not a drop-in of a class with the same answer as a missing one.
/// </summary>
public sealed record DropInCancellationState(
    int BookingId,
    int AppointmentId,
    DateTime StartsAt,
    AppointmentKind Kind,
    AppointmentStatus AppointmentStatus,
    BookingStatus Status,
    BookingModality? Modality);

/// <summary>
/// The client the booking belongs to: identified by email, created without a password when the email is unknown
/// (RF-07 step 5, the T09 flow). <see cref="Email"/> is the recipient of the drop-in notice, so it must never be
/// dropped: the public path needs it to compose the outbox email.
/// </summary>
public sealed record DropInClient(int ClientId, string Email, string FirstName, string LastName);

/// <summary>
/// The drop-in booking to write. It is created in <c>Pendiente</c> with <c>Modality = Suelta</c> and carries its
/// created timestamp so the caller's single clock read is what gets persisted.
/// </summary>
public sealed record DropInInsert(int BusinessId, int AppointmentId, int ClientId, DateTime CreatedAt);

/// <summary>
/// How the guarded drop-in insert ended. <see cref="Inserted"/> carries the new booking id; <see cref="NoPlaceLeft"/>
/// is the lost race for the last place; <see cref="AlreadyBooked"/> is <c>UX_Bookings_ActiveClient</c> firing because
/// the same client already holds a non-cancelled booking of the session. The last two are distinguishable so the
/// caller can answer each with its own stable code instead of leaking an unhandled unique-key exception. Only these
/// three members are outcomes, and none of them is <c>0</c>: an undefined value, the enum default included, is a
/// broken implementation, and the caller must fail loudly instead of reading it as a successful insert with a zero
/// booking id.
/// </summary>
public enum DropInInsertResultType
{
    Inserted = 1,
    NoPlaceLeft = 2,
    AlreadyBooked = 3
}

/// <summary>Three-way result of the guarded insert. <see cref="BookingId"/> is only meaningful when it inserted.</summary>
public sealed record DropInInsertResult(DropInInsertResultType Result, int BookingId)
{
    public static DropInInsertResult Inserted(int bookingId) => new(DropInInsertResultType.Inserted, bookingId);

    public static DropInInsertResult NoPlaceLeft() => new(DropInInsertResultType.NoPlaceLeft, 0);

    public static DropInInsertResult AlreadyBooked() => new(DropInInsertResultType.AlreadyBooked, 0);

    public bool IsInserted => Result == DropInInsertResultType.Inserted;
}

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
