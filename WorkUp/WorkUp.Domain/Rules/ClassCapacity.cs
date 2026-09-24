using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Rules;

/// <summary>
/// Occupancy of a dated class session (RF-07 cupo &gt; 1, D-16, D-23, D-25). The places of a session are counted
/// against <c>Appointments.MaxCapacity</c>, and every booking that is not <c>Cancelado</c> occupies one: a fixed
/// annotation holds its place through its own booking status (pending payment, active or pending renewal are all
/// non-cancelled), and so does a drop-in. Cancelling the only booking of a session frees its place again (RF-22).
/// Pure, no I/O: the caller reads the statuses and the capacity from storage.
/// </summary>
public static class ClassCapacity
{
    /// <summary>
    /// Whether a booking of the session occupies one of its places. Only <see cref="BookingStatus.Cancelled"/>
    /// releases the place; every other status keeps it, whatever the booking modality is.
    /// </summary>
    public static bool OccupiesPlace(BookingStatus status) => status != BookingStatus.Cancelled;

    /// <summary>
    /// Places already taken by <paramref name="statuses"/>. It validates the capacity because a class that does not
    /// admit at least one student cannot be a real session: <c>Appointments.MaxCapacity</c> below one is a corrupt
    /// row, and failing loud beats announcing that class as permanently full (or as having negative places) forever.
    /// </summary>
    public static int PlacesTaken(int maxCapacity, IEnumerable<BookingStatus> statuses)
    {
        ValidateMaxCapacity(maxCapacity);
        ArgumentNullException.ThrowIfNull(statuses);

        var taken = 0;
        foreach (var status in statuses)
        {
            if (OccupiesPlace(status))
                taken++;
        }

        return taken;
    }

    /// <summary>Free places of the session. It never goes negative, even for an overbooked row.</summary>
    public static int RemainingPlaces(int maxCapacity, IEnumerable<BookingStatus> statuses) =>
        Math.Max(0, maxCapacity - PlacesTaken(maxCapacity, statuses));

    /// <summary>Whether the session still admits one more booking.</summary>
    public static bool HasPlace(int maxCapacity, IEnumerable<BookingStatus> statuses) =>
        RemainingPlaces(maxCapacity, statuses) > 0;

    /// <summary>
    /// Whether the session can still be booked: it must be <c>Programado</c> and start strictly after
    /// <paramref name="now"/> (RF-07 step 2, RF-22). A suspended session is never offered, and from its start on it
    /// is no longer bookable: the exact start already counts as started, because from that moment the client is late
    /// and the absence flow applies (RF-12).
    /// </summary>
    public static bool IsBookable(AppointmentStatus status, DateTime startsAt, DateTime now) =>
        status == AppointmentStatus.Scheduled && startsAt > now;

    private static void ValidateMaxCapacity(int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);
    }
}
