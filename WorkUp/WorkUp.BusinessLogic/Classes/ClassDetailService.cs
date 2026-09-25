using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Read-only detail of one class session (RF-10, RF-11, RF-12, RF-22, D-15, D-16, D-23). A single tenant-scoped read
/// returns the session with every annotation it holds — fixed and drop-in, cancelled included — and the service
/// projects it without writing anything: the fixed states are derived from the enrollment status and the membership
/// periods that cover the class date, and the occupancy is computed exclusively with <see cref="ClassCapacity"/>, so
/// no parallel count can disagree with the booking rule.
/// <para>
/// The membership state that governs a fixed annotation is the one in force on the class date, which is the same day
/// the attendance rule will judge, so the derived state never depends on when the owner opens the screen. A drop-in
/// annotation has no fixed state: its modality is the whole story.
/// </para>
/// </summary>
public sealed class ClassDetailService(IClassDetailRepository repository, IClock clock)
{
    private const string InvalidRequestCode = "class_detail_invalid_request";
    private const string NotFoundCode = "class_detail_not_found";

    /// <summary>
    /// Reads the class session of the business and returns it with its derived detail. Invalid identifiers are
    /// rejected with <c>class_detail_invalid_request</c> before any storage call, and a session that does not exist,
    /// belongs to another business or is not a class session answers <c>class_detail_not_found</c>: the caller cannot
    /// tell those cases apart, and none of them leaks another tenant's rows. The read opens no transaction and writes
    /// nothing.
    /// </summary>
    public async Task<Result<ClassDetail>> GetAsync(
        int businessId, int appointmentId, CancellationToken ct = default)
    {
        if (businessId <= 0 || appointmentId <= 0)
            return Result<ClassDetail>.Failure(InvalidRequestCode, "No pudimos identificar la clase.");

        // The single clock read of the read model: it pins the whole answer to one instant and supplies the fallback
        // for a row without a usable start. No later step reads the clock again, so the answer cannot drift.
        var now = clock.Now;

        var session = await repository.GetSessionAsync(businessId, appointmentId, ct);
        if (session is null || session.Kind != AppointmentKind.ClassSession)
            return Result<ClassDetail>.Failure(NotFoundCode, "No encontramos esa clase.");

        // The membership state of a fixed annotation is evaluated on the class date: the day the session happens and
        // the same reference the attendance rule will use (D-16, D-23). It never depends on the moment of the read.
        var referenceDate = session.StartsAt == default
            ? DateOnly.FromDateTime(now)
            : DateOnly.FromDateTime(session.StartsAt);

        var students = new List<ClassDetailStudent>(session.Bookings.Count);
        foreach (var booking in session.Bookings.OrderBy(booking => booking.BookingId))
        {
            students.Add(new ClassDetailStudent(
                booking.BookingId,
                booking.ClientId,
                booking.ClientName,
                booking.Modality,
                booking.EnrollmentId,
                booking.Status,
                ResolveFixedState(booking, referenceDate)));
        }

        // A single application of the capacity rule resolves the occupancy. Cancelled annotations travel in the
        // statuses but release their place, exactly as they do for booking a drop-in.
        var statuses = session.Bookings.Select(booking => booking.Status).ToArray();

        return Result<ClassDetail>.Success(new ClassDetail(
            new ClassDetailSession(
                session.AppointmentId,
                session.ServiceId,
                session.WorkerId,
                session.StartsAt,
                session.MaxCapacity,
                session.Status,
                session.SuspensionReason),
            students,
            ClassCapacity.PlacesTaken(session.MaxCapacity, statuses),
            ClassCapacity.RemainingPlaces(session.MaxCapacity, statuses)));
    }

    /// <summary>
    /// Derived state of one fixed annotation, or <c>null</c> for a drop-in, which has no fixed state. Pending payment
    /// is decided by the enrollment status alone; the rest is the membership calendar: an active enrollment whose
    /// periods cover the class date is active coverage, and one whose periods do not cover it keeps its place pending
    /// the owner's renewal (D-16, D-23). The state is never persisted.
    /// </summary>
    private static ClassFixedStudentState? ResolveFixedState(ClassDetailBookingRow booking, DateOnly classDate)
    {
        if (booking.Modality != BookingModality.Fixed)
            return null;

        if (booking.EnrollmentStatus == EnrollmentStatus.PendingPayment)
            return ClassFixedStudentState.PendingPayment;

        return MembershipCalendar.StateOn(booking.Periods, classDate) == MembershipState.Current
            ? ClassFixedStudentState.Active
            : ClassFixedStudentState.PendingRenewal;
    }
}
