using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// RF-07 (cupo > 1), D-16, D-23, D-25: the occupancy of a class session counts every booking that is not `Cancelado`
// against `Appointments.MaxCapacity`. The fixed annotations hold their places through their own booking status, so the
// rule never needs to know whether an annotation is `Fija` or `Suelta`. A session can only be booked while it is
// `Programado` and has not started yet: the exact start is already late (RF-22).
public class ClassCapacityTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Attended)]
    [InlineData(BookingStatus.Absent)]
    public void Every_status_but_cancelled_occupies_a_place(BookingStatus status)
    {
        Assert.True(ClassCapacity.OccupiesPlace(status));
    }

    [Fact]
    public void Cancelled_does_not_occupy_a_place()
    {
        Assert.False(ClassCapacity.OccupiesPlace(BookingStatus.Cancelled));
    }

    [Fact]
    public void Cancelled_is_the_only_status_that_frees_its_place()
    {
        // A new booking status that accidentally frees its place would silently overbook every class, so the rule is
        // pinned to the whole enum instead of a single example.
        var freeing = Enum.GetValues<BookingStatus>()
            .Where(status => !ClassCapacity.OccupiesPlace(status))
            .ToArray();

        Assert.Equal(new[] { BookingStatus.Cancelled }, freeing);
    }

    [Fact]
    public void Places_taken_counts_every_non_cancelled_booking()
    {
        var statuses = new[]
        {
            BookingStatus.Pending,
            BookingStatus.Cancelled,
            BookingStatus.Confirmed,
            BookingStatus.Attended,
            BookingStatus.Absent
        };

        Assert.Equal(4, ClassCapacity.PlacesTaken(5, statuses));
    }

    [Fact]
    public void A_session_without_bookings_has_every_place_free()
    {
        var statuses = Array.Empty<BookingStatus>();

        Assert.Equal(0, ClassCapacity.PlacesTaken(3, statuses));
        Assert.Equal(3, ClassCapacity.RemainingPlaces(3, statuses));
        Assert.True(ClassCapacity.HasPlace(3, statuses));
    }

    [Fact]
    public void A_full_session_has_no_place()
    {
        var statuses = new[] { BookingStatus.Pending, BookingStatus.Confirmed };

        Assert.Equal(2, ClassCapacity.PlacesTaken(2, statuses));
        Assert.Equal(0, ClassCapacity.RemainingPlaces(2, statuses));
        Assert.False(ClassCapacity.HasPlace(2, statuses));
    }

    [Fact]
    public void A_cancelled_booking_frees_its_place()
    {
        var statuses = new[] { BookingStatus.Pending, BookingStatus.Cancelled };

        Assert.Equal(1, ClassCapacity.RemainingPlaces(2, statuses));
        Assert.True(ClassCapacity.HasPlace(2, statuses));
    }

    [Fact]
    public void Remaining_places_is_never_negative_when_the_session_is_overbooked()
    {
        var statuses = new[] { BookingStatus.Pending, BookingStatus.Confirmed, BookingStatus.Attended };

        Assert.Equal(0, ClassCapacity.RemainingPlaces(2, statuses));
        Assert.False(ClassCapacity.HasPlace(2, statuses));
    }

    [Fact]
    public void Is_bookable_accepts_a_future_scheduled_session()
    {
        Assert.True(ClassCapacity.IsBookable(AppointmentStatus.Scheduled, Now.AddMinutes(1), Now));
    }

    [Fact]
    public void Is_bookable_rejects_a_suspended_session_even_in_the_future()
    {
        Assert.False(ClassCapacity.IsBookable(AppointmentStatus.Suspended, Now.AddDays(1), Now));
    }

    [Fact]
    public void Is_bookable_rejects_an_already_started_session()
    {
        Assert.False(ClassCapacity.IsBookable(AppointmentStatus.Scheduled, Now.AddMinutes(-1), Now));
    }

    [Fact]
    public void Is_bookable_rejects_the_exact_start_of_the_session()
    {
        // RF-22: from the start on, the class is no longer bookable; that moment is an absence, not a booking.
        Assert.False(ClassCapacity.IsBookable(AppointmentStatus.Scheduled, Now, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_capacity_below_one_is_rejected_instead_of_silently_closing_the_class(int maxCapacity)
    {
        // MaxCapacity comes from Appointments.MaxCapacity and a class always admits at least one student, so a value
        // below one is a corrupt row. Failing loud beats announcing that session as permanently full (or as having
        // negative places) for the rest of its life.
        var statuses = Array.Empty<BookingStatus>();

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ClassCapacity.PlacesTaken(maxCapacity, statuses); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ClassCapacity.RemainingPlaces(maxCapacity, statuses); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ClassCapacity.HasPlace(maxCapacity, statuses); });
    }
}
