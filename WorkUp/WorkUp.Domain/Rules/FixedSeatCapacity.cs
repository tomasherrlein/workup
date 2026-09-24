using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Rules;

/// <summary>
/// One fixed-class assignment reduced to the facts the permanent-seat rule needs: which enrollment owns it, whether
/// that enrollment still holds places, and the date window in which it is valid. The entity
/// <c>EnrollmentFixedClass</c> carries the window but not the enrollment status, so the caller pairs both rows when it
/// reads them from storage. <see cref="ValidTo"/> is <c>null</c> while the assignment is open, and the window is
/// inclusive on both ends, exactly like <c>EnrollmentFixedClass.IsValidOn</c>.
/// </summary>
public readonly record struct FixedSeatAssignment(
    int EnrollmentId,
    EnrollmentStatus Status,
    DateOnly ValidFrom,
    DateOnly? ValidTo)
{
    public bool IsValidOn(DateOnly date) => date >= ValidFrom && (ValidTo is null || date <= ValidTo);

    /// <summary>Whether this assignment is holding one of the permanent seats of its grid class.</summary>
    public bool HoldsSeat => FixedSeatCapacity.OccupiesSeat(Status);
}

/// <summary>
/// Permanent seats of one grid class (RF-05, RF-20, D-16, D-21, D-23). A seat is the reservation an enrollment makes
/// on a grid class, independent of how many dated sessions were generated from it: while the enrollment is
/// <c>Pendiente de pago</c> or <c>Vigente</c> and its assignment covers the reference date, it occupies one of the
/// <c>Services.MaxCapacity</c> places of that grid class and no other student can take it. An annulled or terminated
/// enrollment releases its seat (RF-20, RF-29 step 6); the row stays for history and only its status changes.
/// <para>
/// Pure, no I/O: the caller reads the assignment rows with their enrollment statuses from storage and decides which
/// date is the reference one (the day the new enrollment becomes effective).
/// </para>
/// <para>
/// The permanent capacity is the same <c>MaxCapacity</c> the dated sessions are generated with, so counting seats
/// here and counting bookings in a session (<see cref="ClassCapacity"/>) are two readings of the same limit: this one
/// answers whether a grid class has a free permanent seat to enrol somebody, and that one whether a dated session has
/// a free place to annotate somebody.
/// </para>
/// </summary>
public static class FixedSeatCapacity
{
    /// <summary>
    /// Whether an enrollment in this status still holds its permanent seat. Only annulled and terminated release it;
    /// a pending-payment enrollment keeps its places from the moment it is registered and the start of the first class
    /// does not free them by itself (Regla 23, D-21).
    /// </summary>
    public static bool OccupiesSeat(EnrollmentStatus status) =>
        status is EnrollmentStatus.PendingPayment or EnrollmentStatus.Active;

    /// <summary>
    /// Seats of the grid class already taken on <paramref name="referenceDate"/>: one per distinct enrollment whose
    /// assignment covers that date and still holds places. Counting distinct enrollments matters because a schedule
    /// change (RF-21) leaves the previous row closed and a new open one for the same enrollment, and counting rows
    /// would block two seats for one student.
    /// <para>
    /// The count is never clamped, so an overbooked grid class (historical data written before the capacity was
    /// lowered, or corrupted by hand) reports the real number of students. Hiding it behind the capacity would leave
    /// the owner unable to see who is actually enrolled, and the count is exactly what a repair needs.
    /// </para>
    /// </summary>
    public static int SeatsTaken(
        int maxCapacity, IEnumerable<FixedSeatAssignment> assignments, DateOnly referenceDate)
    {
        ValidateMaxCapacity(maxCapacity);
        ArgumentNullException.ThrowIfNull(assignments);

        return assignments
            .Where(assignment => assignment.HoldsSeat && assignment.IsValidOn(referenceDate))
            .Select(assignment => assignment.EnrollmentId)
            .Distinct()
            .Count();
    }

    /// <summary>
    /// Free seats of the grid class. It never goes negative, even for an overbooked row, so the caller asking "is
    /// there room for one more?" always receives a truthful answer instead of a negative capacity.
    /// </summary>
    public static int RemainingSeats(
        int maxCapacity, IEnumerable<FixedSeatAssignment> assignments, DateOnly referenceDate) =>
        Math.Max(0, maxCapacity - SeatsTaken(maxCapacity, assignments, referenceDate));

    /// <summary>
    /// Whether the grid class admits one more enrollment on the reference date. An overbooked grid class answers
    /// <c>false</c>: the corruption is already there, and accepting another seat would compound it on every future
    /// session generated from that grid class. The owner resolves it by annulling, terminating or moving a student
    /// (RF-20, RF-29), never by adding one more.
    /// </summary>
    public static bool HasSeat(
        int maxCapacity, IEnumerable<FixedSeatAssignment> assignments, DateOnly referenceDate) =>
        RemainingSeats(maxCapacity, assignments, referenceDate) > 0;

    private static void ValidateMaxCapacity(int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);
    }
}
