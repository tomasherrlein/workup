using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

public class AvailabilityCalculatorTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);

    [Fact]
    public void Returns_quarter_hour_slots_that_fit_the_full_service_around_breaks()
    {
        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(30),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 10, 11, 0)],
            breaks: [Break(1, DayOfWeek.Monday, 10, 0, 10, 30)],
            appointments: [],
            bookings: []);

        Assert.Equal(
            [At(9, 15), At(9, 30), At(10, 30)],
            slots);
    }

    [Fact]
    public void Off_quarter_schedule_starts_round_up_and_non_quarter_service_duration_fits_completely()
    {
        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(20),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 10, 10, 0)],
            breaks: [],
            appointments: [],
            bookings: []);

        Assert.Equal([At(9, 15), At(9, 30)], slots);
    }

    [Fact]
    public void Scheduled_reserved_individual_appointment_blocks_overlapping_slots_but_not_touching_slots()
    {
        var appointment = Appointment(
            id: 10,
            workerId: 1,
            kind: AppointmentKind.Individual,
            startsAt: At(9, 15),
            endsAt: At(9, 45),
            origin: AppointmentOrigin.Reserved);

        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(30),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 10, 15)],
            breaks: [],
            appointments: [appointment],
            bookings: [Booking(10, BookingStatus.Pending)]);

        Assert.Equal([At(9, 45)], slots);
    }

    [Fact]
    public void Cancelled_individual_booking_does_not_block_its_scheduled_appointment()
    {
        var appointment = Appointment(
            id: 10,
            workerId: 1,
            kind: AppointmentKind.Individual,
            startsAt: At(9, 0),
            endsAt: At(9, 30),
            origin: AppointmentOrigin.Reserved);

        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(30),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 9, 30)],
            breaks: [],
            appointments: [appointment],
            bookings: [Booking(10, BookingStatus.Cancelled)]);

        Assert.Equal([At(9, 0)], slots);
    }

    [Fact]
    public void Individual_appointment_stays_blocked_until_every_booking_is_cancelled()
    {
        var appointment = Appointment(
            id: 10,
            workerId: 1,
            kind: AppointmentKind.Individual,
            startsAt: At(9, 0),
            endsAt: At(9, 30),
            origin: AppointmentOrigin.WalkIn);

        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(30),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 9, 30)],
            breaks: [],
            appointments: [appointment],
            bookings: [Booking(10, BookingStatus.Cancelled), Booking(10, BookingStatus.Confirmed)]);

        Assert.Empty(slots);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Attended)]
    [InlineData(BookingStatus.Absent)]
    public void Any_non_cancelled_individual_booking_keeps_its_appointment_blocking(BookingStatus bookingStatus)
    {
        var appointment = Appointment(
            id: 10,
            workerId: 1,
            kind: AppointmentKind.Individual,
            startsAt: At(9, 0),
            endsAt: At(9, 30),
            origin: AppointmentOrigin.Reserved);

        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(30),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 9, 30)],
            breaks: [],
            appointments: [appointment],
            bookings: [Booking(10, bookingStatus)]);

        Assert.Empty(slots);
    }

    [Fact]
    public void Scheduled_class_session_blocks_even_without_bookings_while_suspended_class_does_not()
    {
        var scheduledClass = Appointment(
            id: 10,
            workerId: 1,
            kind: AppointmentKind.ClassSession,
            startsAt: At(9, 15),
            endsAt: At(9, 45));
        var suspendedClass = Appointment(
            id: 11,
            workerId: 1,
            kind: AppointmentKind.ClassSession,
            startsAt: At(9, 0),
            endsAt: At(9, 30),
            status: AppointmentStatus.Suspended);

        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(15),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 10, 0)],
            breaks: [],
            appointments: [scheduledClass, suspendedClass],
            bookings: []);

        Assert.Equal([At(9, 0), At(9, 45)], slots);
    }

    [Fact]
    public void A_scheduled_walk_in_blocks_availability_even_without_a_booking()
    {
        var walkIn = Appointment(
            id: 10,
            workerId: 1,
            kind: AppointmentKind.Individual,
            startsAt: At(9, 0),
            endsAt: At(9, 30),
            origin: AppointmentOrigin.WalkIn);

        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(30),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 9, 30)],
            breaks: [],
            appointments: [walkIn],
            bookings: []);

        Assert.Empty(slots);
    }

    [Fact]
    public void An_appointment_for_another_worker_does_not_block_availability()
    {
        var otherWorker = Appointment(
            id: 11,
            workerId: 2,
            kind: AppointmentKind.Individual,
            startsAt: At(9, 0),
            endsAt: At(9, 30),
            origin: AppointmentOrigin.Reserved);

        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(30),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 9, 30)],
            breaks: [],
            appointments: [otherWorker],
            bookings: []);

        Assert.Equal([At(9, 0)], slots);
    }

    [Fact]
    public void Cross_date_appointment_overlap_blocks_a_slot_across_midnight()
    {
        var tuesday = Monday.AddDays(1);
        var overnightClass = Appointment(
            id: 10,
            workerId: 1,
            kind: AppointmentKind.ClassSession,
            startsAt: At(Monday, 23, 45),
            endsAt: At(tuesday, 0, 15));

        var slots = AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: tuesday,
            service: Service(15),
            schedules: [Schedule(1, DayOfWeek.Tuesday, 0, 0, 1, 0)],
            breaks: [],
            appointments: [overnightClass],
            bookings: []);

        Assert.Equal(
            [At(tuesday, 0, 15), At(tuesday, 0, 30), At(tuesday, 0, 45)],
            slots);
    }

    [Fact]
    public void Nearest_slot_uses_the_same_worker_and_date_and_prefers_the_earlier_slot_on_a_tie()
    {
        var nearest = AvailabilityCalculator.FindNearestAvailableSlot(
            workerId: 1,
            requestedStart: At(9, 22, 30),
            searchStartDate: Monday,
            searchEndDate: Monday,
            service: Service(15),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 10, 0)],
            breaks: [],
            appointments: [],
            bookings: []);

        Assert.Equal(At(9, 15), nearest);
    }

    [Fact]
    public void Nearest_slot_does_not_search_outside_the_supplied_horizon()
    {
        var nearest = AvailabilityCalculator.FindNearestAvailableSlot(
            workerId: 1,
            requestedStart: At(23, 59),
            searchStartDate: Monday,
            searchEndDate: Monday,
            service: Service(15),
            schedules: [Schedule(1, DayOfWeek.Tuesday, 0, 0, 1, 0)],
            breaks: [],
            appointments: [],
            bookings: []);

        Assert.Null(nearest);
    }

    [Fact]
    public void Nearest_slot_searches_a_later_date_when_the_requested_date_is_full()
    {
        var tuesday = Monday.AddDays(1);
        var scheduledClass = Appointment(
            id: 10,
            workerId: 1,
            kind: AppointmentKind.ClassSession,
            startsAt: At(9, 0),
            endsAt: At(10, 0));

        var nearest = AvailabilityCalculator.FindNearestAvailableSlot(
            workerId: 1,
            requestedStart: At(9, 0),
            searchStartDate: Monday,
            searchEndDate: tuesday,
            service: Service(15),
            schedules:
            [
                Schedule(1, DayOfWeek.Monday, 9, 0, 10, 0),
                Schedule(1, DayOfWeek.Tuesday, 9, 0, 9, 30)
            ],
            breaks: [],
            appointments: [scheduledClass],
            bookings: []);

        Assert.Equal(tuesday.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Unspecified), nearest);
    }

    [Fact]
    public void Nearest_slot_searches_previous_dates_in_the_horizon_and_prefers_earlier_on_cross_date_tie()
    {
        var tuesday = Monday.AddDays(1);
        var wednesday = Monday.AddDays(2);
        var nearest = AvailabilityCalculator.FindNearestAvailableSlot(
            workerId: 1,
            requestedStart: tuesday.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Unspecified),
            searchStartDate: Monday,
            searchEndDate: wednesday,
            service: Service(15),
            schedules:
            [
                Schedule(1, DayOfWeek.Monday, 9, 0, 9, 15),
                Schedule(1, DayOfWeek.Wednesday, 9, 0, 9, 15)
            ],
            breaks: [],
            appointments: [],
            bookings: []);

        Assert.Equal(At(9, 0), nearest);
    }

    [Fact]
    public void Nearest_slot_only_uses_availability_for_the_same_worker()
    {
        var tuesday = Monday.AddDays(1);
        var nearest = AvailabilityCalculator.FindNearestAvailableSlot(
            workerId: 1,
            requestedStart: At(23, 59),
            searchStartDate: Monday,
            searchEndDate: tuesday,
            service: Service(15),
            schedules: [Schedule(2, DayOfWeek.Tuesday, 9, 0, 10, 0)],
            breaks: [],
            appointments: [],
            bookings: []);

        Assert.Null(nearest);
    }

    [Fact]
    public void Nearest_slot_rejects_a_reversed_search_horizon()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AvailabilityCalculator.FindNearestAvailableSlot(
            workerId: 1,
            requestedStart: At(9, 0),
            searchStartDate: Monday.AddDays(1),
            searchEndDate: Monday,
            service: Service(15),
            schedules: [],
            breaks: [],
            appointments: [],
            bookings: []));
    }

    [Fact]
    public void Nearest_slot_distance_is_safe_at_the_end_of_the_date_domain()
    {
        var latestDate = DateOnly.MaxValue;
        var nearest = AvailabilityCalculator.FindNearestAvailableSlot(
            workerId: 1,
            requestedStart: DateTime.MinValue,
            searchStartDate: latestDate,
            searchEndDate: latestDate,
            service: Service(15),
            schedules: [Schedule(1, latestDate.DayOfWeek, 9, 0, 9, 15)],
            breaks: [],
            appointments: [],
            bookings: []);

        Assert.Equal(latestDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Unspecified), nearest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    public void Rejects_nonpositive_service_duration(int durationMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AvailabilityCalculator.GetAvailableSlots(
            workerId: 1,
            date: Monday,
            service: Service(durationMinutes),
            schedules: [Schedule(1, DayOfWeek.Monday, 9, 0, 10, 0)],
            breaks: [],
            appointments: [],
            bookings: []));
    }

    private static Service Service(int durationMinutes) => new() { DurationMinutes = durationMinutes };

    private static WorkerSchedule Schedule(int workerId, DayOfWeek day, int startHour, int startMinute, int endHour, int endMinute) =>
        new()
        {
            WorkerId = workerId,
            DayOfWeek = day,
            StartTime = new TimeOnly(startHour, startMinute),
            EndTime = new TimeOnly(endHour, endMinute)
        };

    private static WorkerBreak Break(int workerId, DayOfWeek day, int startHour, int startMinute, int endHour, int endMinute) =>
        new()
        {
            WorkerId = workerId,
            DayOfWeek = day,
            StartTime = new TimeOnly(startHour, startMinute),
            EndTime = new TimeOnly(endHour, endMinute)
        };

    private static Appointment Appointment(
        int id,
        int workerId,
        AppointmentKind kind,
        DateTime startsAt,
        DateTime endsAt,
        AppointmentOrigin? origin = null,
        AppointmentStatus status = AppointmentStatus.Scheduled) =>
        new()
        {
            Id = id,
            WorkerId = workerId,
            Kind = kind,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Origin = origin,
            Status = status
        };

    private static Booking Booking(int appointmentId, BookingStatus status) =>
        new() { AppointmentId = appointmentId, Status = status };

    private static DateTime At(int hour, int minute, int second = 0) => At(Monday, hour, minute, second);

    private static DateTime At(DateOnly date, int hour, int minute, int second = 0) =>
        date.ToDateTime(new TimeOnly(hour, minute, second), DateTimeKind.Unspecified);
}
