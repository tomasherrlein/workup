using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// T15 (RF-05, RF-20, D-16, D-21, D-23): the permanent seat of a grid class is held by a fixed-class assignment whose
// enrollment still holds places (`Pendiente de pago` or `Vigente`, never annulled or terminated) and whose validity
// window covers the reference date. The permanent capacity is the service's `MaxCapacity`, the same number the dated
// sessions are generated with, so the permanent seats of a grid class can never exceed a session's places. The rule is
// pure: the caller reads the assignment rows and their enrollment statuses from storage.
public class FixedSeatCapacityTests
{
    [Theory]
    [InlineData(EnrollmentStatus.PendingPayment)]
    [InlineData(EnrollmentStatus.Active)]
    public void A_pending_or_active_enrollment_holds_a_seat(EnrollmentStatus status)
    {
        Assert.True(FixedSeatCapacity.OccupiesSeat(status));
    }

    [Theory]
    [InlineData(EnrollmentStatus.Annulled)]
    [InlineData(EnrollmentStatus.Terminated)]
    public void An_annulled_or_terminated_enrollment_releases_its_seat(EnrollmentStatus status)
    {
        Assert.False(FixedSeatCapacity.OccupiesSeat(status));
    }

    [Fact]
    public void Only_pending_payment_and_active_hold_a_seat()
    {
        // A new enrollment status that accidentally keeps holding a seat would keep a place of the grid class blocked
        // forever after an annulment or a termination, so the rule is pinned to the whole enum instead of two examples.
        var holding = Enum.GetValues<EnrollmentStatus>()
            .Where(FixedSeatCapacity.OccupiesSeat)
            .ToArray();

        Assert.Equal(new[] { EnrollmentStatus.PendingPayment, EnrollmentStatus.Active }, holding);
    }

    [Fact]
    public void Seats_taken_counts_every_enrollment_that_holds_a_place_on_the_reference_date()
    {
        var reference = D(2026, 3, 10);
        var assignments = new[]
        {
            Seat(1, EnrollmentStatus.PendingPayment),
            Seat(2, EnrollmentStatus.Active),
            Seat(3, EnrollmentStatus.Annulled),
            Seat(4, EnrollmentStatus.Terminated)
        };

        Assert.Equal(2, FixedSeatCapacity.SeatsTaken(8, assignments, reference));
        Assert.Equal(6, FixedSeatCapacity.RemainingSeats(8, assignments, reference));
        Assert.True(FixedSeatCapacity.HasSeat(8, assignments, reference));
    }

    [Fact]
    public void A_grid_class_without_assignments_has_every_seat_free()
    {
        var assignments = Array.Empty<FixedSeatAssignment>();

        Assert.Equal(0, FixedSeatCapacity.SeatsTaken(3, assignments, D(2026, 3, 10)));
        Assert.Equal(3, FixedSeatCapacity.RemainingSeats(3, assignments, D(2026, 3, 10)));
        Assert.True(FixedSeatCapacity.HasSeat(3, assignments, D(2026, 3, 10)));
    }

    [Fact]
    public void A_full_grid_class_admits_nobody_else()
    {
        var assignments = new[] { Seat(1), Seat(2) };

        Assert.Equal(2, FixedSeatCapacity.SeatsTaken(2, assignments, D(2026, 3, 10)));
        Assert.Equal(0, FixedSeatCapacity.RemainingSeats(2, assignments, D(2026, 3, 10)));
        Assert.False(FixedSeatCapacity.HasSeat(2, assignments, D(2026, 3, 10)));
    }

    [Fact]
    public void The_validity_window_is_inclusive_on_both_ends()
    {
        var assignments = new[] { Seat(1, validFrom: D(2026, 3, 10), validTo: D(2026, 3, 20)) };

        Assert.Equal(0, FixedSeatCapacity.SeatsTaken(2, assignments, D(2026, 3, 9)));
        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(2, assignments, D(2026, 3, 10)));
        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(2, assignments, D(2026, 3, 20)));
        Assert.Equal(0, FixedSeatCapacity.SeatsTaken(2, assignments, D(2026, 3, 21)));
    }

    [Fact]
    public void An_open_validity_window_never_expires()
    {
        var assignments = new[] { Seat(1, validFrom: D(2026, 3, 10)) };

        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(2, assignments, D(2026, 12, 31)));
    }

    [Fact]
    public void Two_assignment_rows_of_the_same_enrollment_are_a_single_seat()
    {
        // A schedule change (RF-21) closes the previous row the day before the effective date and opens a new one, so
        // the same enrollment can legitimately own two rows of one grid class. Counting rows would block a seat twice.
        var assignments = new[]
        {
            Seat(7, validFrom: D(2026, 3, 1), validTo: D(2026, 3, 9)),
            Seat(7, validFrom: D(2026, 3, 10))
        };

        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(3, assignments, D(2026, 3, 10)));
    }

    [Fact]
    public void Remaining_seats_is_never_negative_when_the_grid_class_is_overbooked()
    {
        var assignments = new[] { Seat(1), Seat(2), Seat(3) };

        Assert.Equal(3, FixedSeatCapacity.SeatsTaken(2, assignments, D(2026, 3, 10)));
        Assert.Equal(0, FixedSeatCapacity.RemainingSeats(2, assignments, D(2026, 3, 10)));
        Assert.False(FixedSeatCapacity.HasSeat(2, assignments, D(2026, 3, 10)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_capacity_below_one_is_rejected_instead_of_silently_closing_the_grid_class(int maxCapacity)
    {
        // MaxCapacity comes from Services.MaxCapacity (CK_Services_MaxCapacity >= 1) and a group class always admits at
        // least one student, so a value below one is a corrupt row. Failing loud beats announcing that grid class as
        // permanently full (or as having negative seats) for the rest of its life.
        var assignments = Array.Empty<FixedSeatAssignment>();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = FixedSeatCapacity.SeatsTaken(maxCapacity, assignments, D(2026, 3, 10)); });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = FixedSeatCapacity.RemainingSeats(maxCapacity, assignments, D(2026, 3, 10)); });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = FixedSeatCapacity.HasSeat(maxCapacity, assignments, D(2026, 3, 10)); });
    }

    [Fact]
    public void The_assignments_are_required()
    {
        Assert.Throws<ArgumentNullException>(
            () => { _ = FixedSeatCapacity.SeatsTaken(2, assignments: null!, D(2026, 3, 10)); });
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static FixedSeatAssignment Seat(
        int enrollmentId,
        EnrollmentStatus status = EnrollmentStatus.Active,
        DateOnly? validFrom = null,
        DateOnly? validTo = null) =>
        new(enrollmentId, status, validFrom ?? D(2026, 3, 1), validTo);
}
