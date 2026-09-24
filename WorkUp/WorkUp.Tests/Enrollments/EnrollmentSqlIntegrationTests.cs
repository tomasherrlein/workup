using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Data;
using WorkUp.Data.Catalog;
using WorkUp.Data.Enrollments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Enrollments;

// T15 SQL persistence (RF-05, RF-20, RF-29, D-16, D-20, D-21, D-23, D-24, Reglas 16-19 and 23): the permanent-seat
// read and its guarded insert, the membership charge and renewal, the annulment and termination releases and the
// membership list. Opt-in, retained synthetic LocalDB fixture for `SqlEnrollmentRepository`. It requires a new,
// dedicated database supplied through WORKUP_T15_SQL_CONNECTION; without it every test returns N/A and no SQL
// connection is opened. Each test derives its own database name from the supplied one, so the whole class runs in a
// single command, and nothing is torn down: the target is retained on purpose.
public sealed class EnrollmentSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T15_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>Sunday 2026-05-10 09:00, the registration instant every test derives its dates from.</summary>
    private static readonly DateTime FixedNow = new(2026, 5, 10, 9, 0, 0);

    private static readonly DateOnly ReferenceDate = new(2026, 5, 10);

    [Fact]
    public async Task Seat_read_counts_a_future_start_and_excludes_the_ended_and_released_assignments()
    {
        var target = await ResolveTargetAsync("_Seats");
        if (target is null)
            return;

        var ids = await SeedSeatsAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlEnrollmentRepository(session);

        // Hand-derived: the future-start assignment (window opens on 2026-06-01) and the one that ends exactly on the
        // reference date both retain their seat; the ended one, the one that ended the day before, the Annulled and
        // the Terminated enrollment do not, and neither does another grid class or another business.
        var seats = await repository.ListFixedSeatsAsync(ids.BusinessA, ids.GridClass, ReferenceDate);

        Assert.Equal(2, seats.Count);
        Assert.Equal(
            new[] { ids.FutureEnrollment, ids.EndsOnReferenceEnrollment },
            seats.Select(assignment => assignment.EnrollmentId).ToArray());

        var future = Assert.Single(seats, assignment => assignment.EnrollmentId == ids.FutureEnrollment);
        Assert.Equal(EnrollmentStatus.PendingPayment, future.Status);
        Assert.Equal(new DateOnly(2026, 6, 1), future.ValidFrom);
        Assert.Null(future.ValidTo);
        Assert.True(future.RetainsSeatOn(ReferenceDate));

        var endsOnReference = Assert.Single(
            seats, assignment => assignment.EnrollmentId == ids.EndsOnReferenceEnrollment);
        Assert.Equal(EnrollmentStatus.Active, endsOnReference.Status);
        Assert.Equal(new DateOnly(2026, 1, 1), endsOnReference.ValidFrom);
        Assert.Equal(ReferenceDate, endsOnReference.ValidTo);

        Assert.DoesNotContain(seats, assignment => assignment.EnrollmentId == ids.EndedBeforeReferenceEnrollment);
        Assert.DoesNotContain(seats, assignment => assignment.EnrollmentId == ids.EndedDayBeforeReferenceEnrollment);
        Assert.DoesNotContain(seats, assignment => assignment.EnrollmentId == ids.AnnulledEnrollment);
        Assert.DoesNotContain(seats, assignment => assignment.EnrollmentId == ids.TerminatedEnrollment);
        Assert.All(seats, assignment => Assert.True(assignment.HoldsSeat));

        // Exactly the rows the caller's rule consumes: two seats taken out of three, so a third enrollment fits and a
        // fourth does not.
        Assert.Equal(2, FixedSeatCapacity.SeatsTaken(3, seats, ReferenceDate));
        Assert.True(FixedSeatCapacity.HasSeat(3, seats, ReferenceDate));
        Assert.False(FixedSeatCapacity.HasSeat(2, seats, ReferenceDate));

        // Grid class and tenant scoping: the other grid class of the same business resolves only its own assignment,
        // and another business sees nothing of this grid class — the cross-tenant assignment is excluded by the
        // enrollment's own business, not just by the grid class.
        var otherSeats = await repository.ListFixedSeatsAsync(ids.BusinessA, ids.OtherGridClass, ReferenceDate);
        var otherSeat = Assert.Single(otherSeats);
        Assert.Equal(ids.OtherGridEnrollment, otherSeat.EnrollmentId);
        Assert.Empty(await repository.ListFixedSeatsAsync(ids.BusinessB, ids.GridClass, ReferenceDate));
        Assert.DoesNotContain(seats, assignment => assignment.EnrollmentId == ids.ForeignEnrollment);

        output.WriteLine("Seat assertions passed: the future-start assignment is counted, the ended one is not, Annulled and Terminated are excluded, the inclusive end date still holds the seat, and other grid classes and tenants are invisible.");
    }

    [Fact]
    public async Task A_future_start_registered_first_refuses_an_earlier_start_and_writes_no_row()
    {
        var target = await ResolveTargetAsync("_Regression");
        if (target is null)
            return;

        var ids = await SeedRegressionAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlEnrollmentRepository(session);
        var service = new EnrollmentService(
            repository, new SqlMembershipPriceRepository(session), session, new FakeClock { Now = FixedNow });

        // Capacity 1. The first enrollment is registered today for a future start: it already retains the only seat.
        var first = await service.CreatePendingAsync(
            ids.BusinessA,
            new CreateEnrollmentRequest(ids.FirstClient, 1, null, null, new[] { ids.GridClass }, new DateOnly(2026, 6, 1)));
        Assert.True(first.Succeeded, first.Error?.Code);

        // The guarded insert re-reads the seat count with the corrected predicate, so an enrollment with an earlier
        // start is refused even though its window starts before the first one's. A guard narrowing by ValidFrom would
        // count no seat here and oversubscribe the grid class.
        var contested = new EnrollmentInsert(
            new Enrollment
            {
                BusinessId = ids.BusinessA,
                ClientId = ids.SecondClient,
                WeeklyFrequency = 1,
                Status = EnrollmentStatus.PendingPayment,
                AgreedPrice = 5000m,
                CreatedAt = FixedNow
            },
            new[] { new EnrollmentFixedClass { GridClassId = ids.GridClass, ValidFrom = new DateOnly(2026, 5, 11) } });

        await using (var transaction = await session.BeginAsync())
        {
            var guarded = await repository.TryInsertEnrollmentAsync(contested);
            Assert.Equal(EnrollmentInsertResultType.NoSeatLeft, guarded.Result);
            Assert.Equal(ids.GridClass, guarded.GridClassId);
            Assert.False(guarded.IsInserted);
            Assert.Equal(0, guarded.EnrollmentId);
            await transaction.CommitAsync();
        }

        // The service's own seat read refuses it for the same reason, before the insert.
        var second = await service.CreatePendingAsync(
            ids.BusinessA,
            new CreateEnrollmentRequest(ids.SecondClient, 1, null, null, new[] { ids.GridClass }, new DateOnly(2026, 5, 11)));
        Assert.False(second.Succeeded);
        Assert.Equal("enrollment_no_seat", second.Error!.Code);

        // Both refused attempts wrote nothing: one enrollment and one assignment exist.
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Enrollments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE GridClassId = @gridClassId", ("@gridClassId", ids.GridClass)));

        output.WriteLine("Regression assertions passed: a future-start enrollment registered first blocked the earlier start both at the guarded insert and at the service read, and neither refused attempt wrote an enrollment or an assignment.");
    }

    [Fact]
    public async Task Two_enrollments_for_the_last_permanent_seat_leave_exactly_one_inserted()
    {
        var target = await ResolveTargetAsync("_Race");
        if (target is null)
            return;

        var ids = await SeedRaceAsync(target.ConnectionString, FixedNow);

        // A bare Task.WhenAll does not force real overlap, so the barrier pins both enrollments on their own
        // thread-pool thread and releases them right before each opens its transaction and asks for the grid class
        // lock. That is what proves the UPDLOCK serializes the seat read and the guarded insert.
        using var startGate = new Barrier(participantCount: 2);

        Task<Result<PendingEnrollmentOutcome>> EnrollAsync(int clientId) => Task.Run(async () =>
        {
            await using var raceSession = new DbSession(target.ConnectionString);
            var raceService = new EnrollmentService(
                new SqlEnrollmentRepository(raceSession),
                new SqlMembershipPriceRepository(raceSession),
                raceSession,
                new FakeClock { Now = FixedNow });
            startGate.SignalAndWait();
            return await raceService.CreatePendingAsync(
                ids.BusinessA,
                new CreateEnrollmentRequest(clientId, 1, null, null, new[] { ids.GridClass }, ReferenceDate));
        });

        var results = await Task.WhenAll(EnrollAsync(ids.FirstClient), EnrollAsync(ids.SecondClient));

        // Which one wins depends on the interleaving; the outcome does not: one takes the only seat and the other
        // reports it lost.
        var winner = Assert.Single(results, result => result.Succeeded);
        var loser = Assert.Single(results, result => !result.Succeeded);
        Assert.Equal("enrollment_no_seat", loser.Error!.Code);
        Assert.True(winner.Value.EnrollmentId > 0);

        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Enrollments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE GridClassId = @gridClassId", ("@gridClassId", ids.GridClass)));

        await using var session = new DbSession(target.ConnectionString);
        var seats = await new SqlEnrollmentRepository(session)
            .ListFixedSeatsAsync(ids.BusinessA, ids.GridClass, ReferenceDate);
        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(1, seats, ReferenceDate));

        output.WriteLine("Race assertions passed: two concurrent enrollments for the last permanent seat ended with exactly one inserted and one NoSeatLeft, and exactly one seat taken.");
    }

    [Fact]
    public async Task The_first_charge_activates_the_membership_with_one_payment_and_one_period()
    {
        var target = await ResolveTargetAsync("_Charge");
        if (target is null)
            return;

        var ids = await SeedChargeAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlEnrollmentRepository(session);
        var service = new EnrollmentService(
            repository, new SqlMembershipPriceRepository(session), session, new FakeClock { Now = FixedNow });

        var created = await service.CreatePendingAsync(
            ids.BusinessA,
            new CreateEnrollmentRequest(ids.Client, 1, null, null, new[] { ids.GridClass }, ReferenceDate));
        Assert.True(created.Succeeded, created.Error?.Code);
        var enrollmentId = created.Value.EnrollmentId;

        var charged = await service.ChargeAsync(ids.BusinessA, new ChargeMembershipRequest(enrollmentId, null, null));
        Assert.True(charged.Succeeded, charged.Error?.Code);

        var outcome = charged.Value;
        var expected = MembershipCalendar.FirstPeriod(ReferenceDate);
        Assert.True(outcome.Activated);
        Assert.Equal(expected.CoverageStart, outcome.CoverageStart);
        Assert.Equal(expected.ExpiresOn, outcome.ExpiresOn);
        Assert.Equal(expected.ReferenceDay, outcome.ReferenceDay);
        Assert.Equal(5000m, outcome.Amount);

        // The sync wrote the free session, omitted nothing and refused the full one, and it inspected both sessions.
        Assert.Equal(1, outcome.Sync.CreatedAnnotations);
        Assert.Equal(0, outcome.Sync.OmittedAnnotations);
        Assert.Equal(1, outcome.Sync.RefusedWithoutSeat);
        Assert.Equal(2, outcome.Sync.SessionIds.Count);
        Assert.Contains(ids.SessionFree, outcome.Sync.SessionIds);
        Assert.Contains(ids.SessionFull, outcome.Sync.SessionIds);

        // The enrollment flipped to Active with the activation instant and the reference day of the payment.
        var enrollment = await ReadEnrollmentAsync(target.ConnectionString, enrollmentId);
        Assert.Equal(EnrollmentStatus.Active, enrollment.Status);
        Assert.Equal(FixedNow, enrollment.ActivatedAt);
        Assert.Equal(ReferenceDate.Day, enrollment.ReferenceDay);
        Assert.Null(enrollment.AnnulledAt);
        Assert.Null(enrollment.TerminatedAt);

        // Exactly one membership payment: Concept=Membership, the enrollment and no booking (CK_Payments_Reference).
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE EnrollmentId = @enrollmentId", ("@enrollmentId", enrollmentId)));
        var payment = await ReadPaymentAsync(target.ConnectionString, enrollmentId);
        Assert.Equal(ids.BusinessA, payment.BusinessId);
        Assert.Equal(ids.Client, payment.ClientId);
        Assert.Equal(PaymentConcept.Membership, payment.Concept);
        Assert.Equal(enrollmentId, payment.EnrollmentId);
        Assert.Null(payment.BookingId);
        Assert.Equal(5000m, payment.Amount);
        Assert.Equal(5000m, payment.ListPrice);
        Assert.Equal(FixedNow, payment.PaidAt);
        Assert.Equal(FixedNow, payment.RecordedAt);

        // Exactly one period, the coverage MembershipCalendar computed, pointing at that payment.
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId", ("@enrollmentId", enrollmentId)));
        var period = await ReadSinglePeriodAsync(target.ConnectionString, enrollmentId);
        Assert.Equal(expected.CoverageStart, period.CoverageStart);
        Assert.Equal(expected.ExpiresOn, period.ExpiresOn);
        Assert.Equal(payment.Id, period.PaymentId);
        Assert.Equal(FixedNow, period.CreatedAt);

        // UQ_MembershipPeriods_Payment: one payment carries exactly one period, and the index rejects a second one.
        Assert.Equal(
            await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM MembershipPeriods"),
            await CountAsync(target.ConnectionString, "SELECT COUNT(DISTINCT PaymentId) FROM MembershipPeriods"));
        var duplicate = await InsertDuplicatePeriodFailureAsync(
            target.ConnectionString, enrollmentId, payment.Id, expected.ExpiresOn, FixedNow);
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });

        // One fixed annotation in the free session, none in the full one.
        var annotation = await ReadSingleBookingAsync(target.ConnectionString, ids.SessionFree);
        Assert.Equal(ids.BusinessA, annotation.BusinessId);
        Assert.Equal(ids.Client, annotation.ClientId);
        Assert.Equal(enrollmentId, annotation.EnrollmentId);
        Assert.Equal(BookingModality.Fixed, annotation.Modality);
        Assert.Equal(BookingStatus.Pending, annotation.Status);
        Assert.Equal(FixedNow, annotation.CreatedAt);
        Assert.Null(annotation.CancelledAt);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.SessionFree)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND EnrollmentId = @enrollmentId",
            ("@appointmentId", ids.SessionFull), ("@enrollmentId", enrollmentId)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.SessionFull)));

        // The guarded insert answers per annotation and in the requested order: the full session has no place left and
        // the free one already holds the enrollment's annotation, so neither the capacity nor the fixed identity is
        // ever violated.
        var guarded = await repository.InsertFixedAnnotationsAsync(ids.BusinessA, new[]
        {
            new FixedAnnotationInsert(ids.SessionFull, enrollmentId, ids.Client, FixedNow),
            new FixedAnnotationInsert(ids.SessionFree, enrollmentId, ids.Client, FixedNow)
        });
        Assert.Equal(2, guarded.Count);
        Assert.Equal(ids.SessionFull, guarded[0].AppointmentId);
        Assert.Equal(FixedAnnotationInsertResultType.NoSeatLeft, guarded[0].Result);
        Assert.Equal(ids.SessionFree, guarded[1].AppointmentId);
        Assert.Equal(FixedAnnotationInsertResultType.AlreadyPresent, guarded[1].Result);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.SessionFree)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.SessionFull)));

        // A cancelled annotation is never resurrected: the row stays, and the guarded insert refuses it again.
        Assert.Equal(1, await repository.CancelFixedAnnotationsAsync(ids.BusinessA, enrollmentId, FixedNow.AddDays(1)));
        var cancelledRow = await ReadSingleBookingAsync(target.ConnectionString, ids.SessionFree);
        Assert.Equal(BookingStatus.Cancelled, cancelledRow.Status);
        Assert.Equal(FixedNow.AddDays(1), cancelledRow.CancelledAt);
        Assert.Equal(CancelledBy.Owner, cancelledRow.CancelledBy);
        var resurrection = await repository.InsertFixedAnnotationsAsync(ids.BusinessA, new[]
        {
            new FixedAnnotationInsert(ids.SessionFree, enrollmentId, ids.Client, FixedNow)
        });
        Assert.Equal(FixedAnnotationInsertResultType.AlreadyPresent, Assert.Single(resurrection).Result);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.SessionFree)));
        Assert.Equal(BookingStatus.Cancelled,
            (await ReadSingleBookingAsync(target.ConnectionString, ids.SessionFree)).Status);

        output.WriteLine("Charge assertions passed: the first payment activated the enrollment, wrote exactly one Membership payment and one period matching MembershipCalendar, the unique payment index held, the sync created the free annotation and refused the full one, and a cancelled annotation was never resurrected.");
    }

    [Fact]
    public async Task Early_renewal_extends_from_the_expiry_and_late_renewal_starts_at_the_payment_day()
    {
        var target = await ResolveTargetAsync("_Renewal");
        if (target is null)
            return;

        var ids = await SeedRenewalAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlEnrollmentRepository(session);
        var clock = new FakeClock { Now = FixedNow };
        var service = new EnrollmentService(
            repository, new SqlMembershipPriceRepository(session), session, clock);

        var created = await service.CreatePendingAsync(
            ids.BusinessA,
            new CreateEnrollmentRequest(ids.Client, 1, null, null, new[] { ids.GridClass }, ReferenceDate));
        Assert.True(created.Succeeded, created.Error?.Code);
        var enrollmentId = created.Value.EnrollmentId;

        // First charge: coverage starts on the payment day, reference day 10.
        var first = await service.ChargeAsync(ids.BusinessA, new ChargeMembershipRequest(enrollmentId, null, null));
        Assert.True(first.Succeeded, first.Error?.Code);
        Assert.True(first.Value.Activated);
        Assert.Equal(new DateOnly(2026, 5, 10), first.Value.CoverageStart);
        Assert.Equal(new DateOnly(2026, 6, 10), first.Value.ExpiresOn);
        Assert.Equal(10, first.Value.ReferenceDay);

        // Early renewal, paid before the expiry: it continues from that expiry without a gap and keeps the day.
        clock.Now = new DateTime(2026, 5, 20, 9, 0, 0);
        var early = await service.ChargeAsync(ids.BusinessA, new ChargeMembershipRequest(enrollmentId, null, null));
        Assert.True(early.Succeeded, early.Error?.Code);
        Assert.False(early.Value.Activated);
        Assert.Equal(new DateOnly(2026, 6, 10), early.Value.CoverageStart);
        Assert.Equal(new DateOnly(2026, 7, 10), early.Value.ExpiresOn);
        Assert.Equal(10, early.Value.ReferenceDay);

        // Late renewal, paid after the expiry: it starts on the payment day with no retroactive coverage and the
        // payment day becomes the new reference day.
        clock.Now = new DateTime(2026, 8, 15, 9, 0, 0);
        var late = await service.ChargeAsync(ids.BusinessA, new ChargeMembershipRequest(enrollmentId, null, null));
        Assert.True(late.Succeeded, late.Error?.Code);
        Assert.False(late.Value.Activated);
        Assert.Equal(new DateOnly(2026, 8, 15), late.Value.CoverageStart);
        Assert.Equal(new DateOnly(2026, 9, 15), late.Value.ExpiresOn);
        Assert.Equal(15, late.Value.ReferenceDay);

        var periods = await ReadPeriodsAsync(target.ConnectionString, enrollmentId);
        Assert.Equal(3, periods.Count);
        Assert.Equal(new DateOnly(2026, 5, 10), periods[0].CoverageStart);
        Assert.Equal(new DateOnly(2026, 6, 10), periods[0].ExpiresOn);
        Assert.Equal(new DateOnly(2026, 6, 10), periods[1].CoverageStart);
        Assert.Equal(new DateOnly(2026, 7, 10), periods[1].ExpiresOn);
        Assert.Equal(new DateOnly(2026, 8, 15), periods[2].CoverageStart);
        Assert.Equal(new DateOnly(2026, 9, 15), periods[2].ExpiresOn);
        for (var index = 1; index < periods.Count; index++)
            Assert.True(periods[index - 1].ExpiresOn <= periods[index].CoverageStart, "Periods must not overlap.");

        Assert.Equal(3, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE EnrollmentId = @enrollmentId", ("@enrollmentId", enrollmentId)));
        Assert.Equal(3, await CountAsync(target.ConnectionString,
            "SELECT COUNT(DISTINCT PaymentId) FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", enrollmentId)));
        Assert.Equal(15, (await ReadEnrollmentAsync(target.ConnectionString, enrollmentId)).ReferenceDay);

        output.WriteLine("Renewal assertions passed: the early renewal extended from the previous expiry and the late one restarted on the payment day, three non-overlapping non-prorated periods and three payments were written, and the reference day followed the late payment.");
    }

    [Fact]
    public async Task Annulling_a_pending_enrollment_releases_the_seat_and_cancels_without_deleting()
    {
        var target = await ResolveTargetAsync("_Annulment");
        if (target is null)
            return;

        var ids = await SeedAnnulmentAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlEnrollmentRepository(session);
        var service = new EnrollmentService(
            repository, new SqlMembershipPriceRepository(session), session, new FakeClock { Now = FixedNow });

        var created = await service.CreatePendingAsync(
            ids.BusinessA,
            new CreateEnrollmentRequest(ids.FirstClient, 1, null, null, new[] { ids.GridClass }, ReferenceDate));
        Assert.True(created.Succeeded, created.Error?.Code);
        var enrollmentId = created.Value.EnrollmentId;

        // An annotation of a session that already ran and one of a future session: only the future one is released.
        await InsertFixedAnnotationAsync(
            target.ConnectionString, ids.BusinessA, ids.SessionFuture, ids.FirstClient, enrollmentId, FixedNow);
        await InsertFixedAnnotationAsync(
            target.ConnectionString, ids.BusinessA, ids.SessionPast, ids.FirstClient, enrollmentId, FixedNow);

        var annulled = await service.AnnulPendingAsync(ids.BusinessA, new AnnulEnrollmentRequest(enrollmentId));
        Assert.True(annulled.Succeeded, annulled.Error?.Code);
        Assert.Equal(1, annulled.Value.CancelledAnnotations);

        var enrollment = await ReadEnrollmentAsync(target.ConnectionString, enrollmentId);
        Assert.Equal(EnrollmentStatus.Annulled, enrollment.Status);
        Assert.Equal(FixedNow, enrollment.AnnulledAt);

        // The assignment is closed on its own start day — ValidTo can never precede ValidFrom — and never deleted.
        var fixedClass = await ReadSingleFixedClassAsync(target.ConnectionString, enrollmentId);
        Assert.Equal(ReferenceDate, fixedClass.ValidFrom);
        Assert.Equal(ReferenceDate, fixedClass.ValidTo);

        // Both annotation rows survive; only the future one is cancelled, with its instant and its author.
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE EnrollmentId = @enrollmentId", ("@enrollmentId", enrollmentId)));
        var future = await ReadSingleBookingAsync(target.ConnectionString, ids.SessionFuture);
        Assert.Equal(BookingStatus.Cancelled, future.Status);
        Assert.Equal(FixedNow, future.CancelledAt);
        Assert.Equal(CancelledBy.Owner, future.CancelledBy);
        var past = await ReadSingleBookingAsync(target.ConnectionString, ids.SessionPast);
        Assert.Equal(BookingStatus.Pending, past.Status);
        Assert.Null(past.CancelledAt);
        Assert.Null(past.CancelledBy);

        // The released seat fits another enrollment in the same grid class.
        var replacement = await service.CreatePendingAsync(
            ids.BusinessA,
            new CreateEnrollmentRequest(ids.SecondClient, 1, null, null, new[] { ids.GridClass }, ReferenceDate));
        Assert.True(replacement.Succeeded, replacement.Error?.Code);

        var seats = await repository.ListFixedSeatsAsync(ids.BusinessA, ids.GridClass, ReferenceDate);
        var onlySeat = Assert.Single(seats);
        Assert.Equal(replacement.Value.EnrollmentId, onlySeat.EnrollmentId);
        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(1, seats, ReferenceDate));

        output.WriteLine("Annulment assertions passed: the pending enrollment flipped to Annulled, the assignment was closed on its own start day without deleting a row, the future annotation was cancelled with its instant and the past one was untouched, and the released seat fit another enrollment.");
    }

    [Fact]
    public async Task Termination_cancels_only_from_the_effective_date_forward()
    {
        var target = await ResolveTargetAsync("_Termination");
        if (target is null)
            return;

        var ids = await SeedTerminationAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlEnrollmentRepository(session);
        var service = new EnrollmentService(
            repository, new SqlMembershipPriceRepository(session), session, new FakeClock { Now = FixedNow });

        var created = await service.CreatePendingAsync(
            ids.BusinessA,
            new CreateEnrollmentRequest(ids.Client, 1, null, null, new[] { ids.GridClass }, ReferenceDate));
        Assert.True(created.Succeeded, created.Error?.Code);
        var enrollmentId = created.Value.EnrollmentId;

        await InsertFixedAnnotationAsync(
            target.ConnectionString, ids.BusinessA, ids.SessionBefore, ids.Client, enrollmentId, FixedNow);
        await InsertFixedAnnotationAsync(
            target.ConnectionString, ids.BusinessA, ids.SessionOnEffective, ids.Client, enrollmentId, FixedNow);
        await InsertFixedAnnotationAsync(
            target.ConnectionString, ids.BusinessA, ids.SessionAfter, ids.Client, enrollmentId, FixedNow);

        var effectiveFrom = new DateOnly(2026, 5, 15);
        var terminated = await service.TerminateAsync(
            ids.BusinessA, new TerminateEnrollmentRequest(enrollmentId, effectiveFrom));
        Assert.True(terminated.Succeeded, terminated.Error?.Code);
        Assert.Equal(effectiveFrom, terminated.Value.EffectiveFrom);
        Assert.Equal(2, terminated.Value.CancelledAnnotations);

        var enrollment = await ReadEnrollmentAsync(target.ConnectionString, enrollmentId);
        Assert.Equal(EnrollmentStatus.Terminated, enrollment.Status);
        Assert.Equal(FixedNow, enrollment.TerminatedAt);

        // The assignment is closed the day before the effective date: earlier coverage is kept.
        var fixedClass = await ReadSingleFixedClassAsync(target.ConnectionString, enrollmentId);
        Assert.Equal(new DateOnly(2026, 5, 14), fixedClass.ValidTo);

        // Three rows survive; the ones starting on the effective day and later are cancelled with that instant, and
        // the one before it is untouched.
        Assert.Equal(3, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE EnrollmentId = @enrollmentId", ("@enrollmentId", enrollmentId)));
        var before = await ReadSingleBookingAsync(target.ConnectionString, ids.SessionBefore);
        Assert.Equal(BookingStatus.Pending, before.Status);
        Assert.Null(before.CancelledAt);
        var onEffective = await ReadSingleBookingAsync(target.ConnectionString, ids.SessionOnEffective);
        Assert.Equal(BookingStatus.Cancelled, onEffective.Status);
        Assert.Equal(effectiveFrom.ToDateTime(TimeOnly.MinValue), onEffective.CancelledAt);
        Assert.Equal(CancelledBy.Owner, onEffective.CancelledBy);
        var after = await ReadSingleBookingAsync(target.ConnectionString, ids.SessionAfter);
        Assert.Equal(BookingStatus.Cancelled, after.Status);
        Assert.Equal(effectiveFrom.ToDateTime(TimeOnly.MinValue), after.CancelledAt);

        output.WriteLine("Termination assertions passed: the membership flipped to Terminated, the assignment was closed the day before the effective date, and only the annotations from that date on were cancelled with the earlier one untouched.");
    }

    [Fact]
    public async Task The_list_read_returns_the_periods_and_statuses_the_service_filters_on()
    {
        var target = await ResolveTargetAsync("_List");
        if (target is null)
            return;

        var ids = await SeedListAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlEnrollmentRepository(session);

        var rows = await repository.ListEnrollmentsAsync(ids.BusinessA);

        // Hand-derived: six enrollments ordered by id, each with its client name and its own status, the annulled and
        // the terminated ones included because they are what the "all" view shows.
        Assert.Equal(6, rows.Count);
        Assert.Equal(
            new[] { "Ana Perez", "Bruno Diaz", "Carla Lopez", "Diego Ruiz", "Elena Sosa", "Fabio Vera" },
            rows.Select(row => row.ClientName).ToArray());
        Assert.Equal(
            new[]
            {
                EnrollmentStatus.Active,
                EnrollmentStatus.Active,
                EnrollmentStatus.Active,
                EnrollmentStatus.PendingPayment,
                EnrollmentStatus.Annulled,
                EnrollmentStatus.Terminated
            },
            rows.Select(row => row.Status).ToArray());
        Assert.DoesNotContain(rows, row => row.EnrollmentId == ids.ForeignEnrollment);

        var ana = Assert.Single(rows, row => row.EnrollmentId == ids.AnaEnrollment);
        Assert.Equal(2, ana.WeeklyFrequency);
        Assert.Equal(10, ana.ReferenceDay);
        Assert.Equal(FixedNow.AddDays(-30), ana.ActivatedAt);
        Assert.Equal(ids.AnaEnrollment, ana.EnrollmentId);
        var anaPeriod = Assert.Single(ana.Periods);
        Assert.Equal(new DateOnly(2026, 4, 10), anaPeriod.CoverageStart);
        Assert.Equal(new DateOnly(2026, 5, 13), anaPeriod.ExpiresOn);
        Assert.Empty(Assert.Single(rows, row => row.EnrollmentId == ids.DiegoEnrollment).Periods);

        // The derived filters the service computes from exactly these periods and statuses.
        var service = new EnrollmentService(
            repository, new SqlMembershipPriceRepository(session), session, new FakeClock { Now = FixedNow });

        var all = await service.ListAsync(ids.BusinessA, EnrollmentListFilter.All);
        Assert.True(all.Succeeded, all.Error?.Code);
        Assert.Equal(6, all.Value.Count);

        var current = await service.ListAsync(ids.BusinessA, EnrollmentListFilter.Current);
        Assert.Equal(
            new[] { ids.AnaEnrollment, ids.BrunoEnrollment },
            current.Value.Select(item => item.EnrollmentId).ToArray());

        var expiringSoon = await service.ListAsync(ids.BusinessA, EnrollmentListFilter.ExpiringSoon);
        var soon = Assert.Single(expiringSoon.Value);
        Assert.Equal(ids.AnaEnrollment, soon.EnrollmentId);
        Assert.True(soon.ExpiresSoon);
        Assert.Equal(new DateOnly(2026, 5, 13), soon.CurrentPeriodEnd);
        Assert.Equal(MembershipState.Current, soon.MembershipState);

        var expired = await service.ListAsync(ids.BusinessA, EnrollmentListFilter.Expired);
        var lapsed = Assert.Single(expired.Value);
        Assert.Equal(ids.CarlaEnrollment, lapsed.EnrollmentId);
        Assert.Equal(MembershipState.Expired, lapsed.MembershipState);
        Assert.Equal(new DateOnly(2026, 4, 1), lapsed.CurrentPeriodEnd);

        var pending = await service.ListAsync(ids.BusinessA, EnrollmentListFilter.PendingPayment);
        var pendingItem = Assert.Single(pending.Value);
        Assert.Equal(ids.DiegoEnrollment, pendingItem.EnrollmentId);
        Assert.True(pendingItem.PendingPayment);
        Assert.Null(pendingItem.CurrentPeriodEnd);
        Assert.Equal(MembershipState.None, pendingItem.MembershipState);

        // Annulled and terminated enrollments belong only to the "all" view.
        Assert.Contains(all.Value, item => item.EnrollmentId == ids.ElenaEnrollment
            && item.Status == EnrollmentStatus.Annulled);
        Assert.Contains(all.Value, item => item.EnrollmentId == ids.FabioEnrollment
            && item.Status == EnrollmentStatus.Terminated);

        output.WriteLine("List assertions passed: the read returned every enrollment of the business with its client and periods, and the derived Current, ExpiringSoon, Expired and PendingPayment filters matched the hand-derived counts with the annulled and terminated ones visible only in All.");
    }

    [Fact]
    public async Task A_two_class_enrollment_writes_one_assignment_per_class_and_the_guard_refuses_when_one_is_full()
    {
        var target = await ResolveTargetAsync("_MultiClass");
        if (target is null)
            return;

        var ids = await SeedMultiClassAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlEnrollmentRepository(session);
        var service = new EnrollmentService(
            repository, new SqlMembershipPriceRepository(session), session, new FakeClock { Now = FixedNow });

        // Two grid classes of the same business chosen together: class A has two permanent seats and class B only
        // one, and both are free. This is the only insert of the fixture whose guard and diagnosis build the multi-row
        // (VALUES (@gridClassId0), (@gridClassId1)) derived table instead of a single-id one.
        // The weekly frequency has to cover the number of chosen fixed classes: a client with a weekly frequency of 2
        // may choose at most two grid classes, so the two-class request below needs frequency 2, not 1.
        var created = await service.CreatePendingAsync(
            ids.BusinessId,
            new CreateEnrollmentRequest(
                ids.Client, 2, null, null, new[] { ids.ClassA, ids.ClassB }, ReferenceDate));
        Assert.True(created.Succeeded, created.Error?.Code);
        var enrollmentId = created.Value.EnrollmentId;
        Assert.True(enrollmentId > 0);
        Assert.Equal(new[] { ids.ClassA, ids.ClassB }, created.Value.GridClassIds);

        // Hand-derived from SQL: exactly one enrollment row and exactly two assignment rows, one per requested class.
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Enrollments WHERE Id = @enrollmentId", ("@enrollmentId", enrollmentId)));
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId", ("@enrollmentId", enrollmentId)));
        Assert.Equal(
            new[] { ids.ClassA, ids.ClassB },
            (await ReadFixedClassIdsAsync(target.ConnectionString, enrollmentId)).ToArray());

        // The seat read afterwards reports one seat held in each class, and B is already sitting on its only seat.
        var seatsA = await repository.ListFixedSeatsAsync(ids.BusinessId, ids.ClassA, ReferenceDate);
        var seatsB = await repository.ListFixedSeatsAsync(ids.BusinessId, ids.ClassB, ReferenceDate);
        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(2, seatsA, ReferenceDate));
        Assert.Equal(enrollmentId, Assert.Single(seatsA).EnrollmentId);
        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(1, seatsB, ReferenceDate));
        Assert.Equal(enrollmentId, Assert.Single(seatsB).EnrollmentId);
        Assert.True(FixedSeatCapacity.HasSeat(2, seatsA, ReferenceDate));
        Assert.False(FixedSeatCapacity.HasSeat(1, seatsB, ReferenceDate));

        // Class B is now at capacity while A still has a seat, so a second two-class insert has to be refused by the
        // same multi-row guard and the diagnosis has to name B: A never fails the guard, so the refused insert can
        // only report the class without a seat. The direct repository call is what reaches the guard with both
        // classes: the service would refuse at its own seat read, before the insert.
        var contested = new EnrollmentInsert(
            new Enrollment
            {
                BusinessId = ids.BusinessId,
                ClientId = ids.OtherClient,
                WeeklyFrequency = 2,
                Status = EnrollmentStatus.PendingPayment,
                AgreedPrice = 5000m,
                CreatedAt = FixedNow
            },
            new[]
            {
                new EnrollmentFixedClass { GridClassId = ids.ClassA, ValidFrom = ReferenceDate },
                new EnrollmentFixedClass { GridClassId = ids.ClassB, ValidFrom = ReferenceDate }
            });

        await using (var transaction = await session.BeginAsync())
        {
            var guarded = await repository.TryInsertEnrollmentAsync(contested);
            Assert.Equal(EnrollmentInsertResultType.NoSeatLeft, guarded.Result);
            Assert.Equal(ids.ClassB, guarded.GridClassId);
            Assert.False(guarded.IsInserted);
            Assert.Equal(0, guarded.EnrollmentId);
            await transaction.CommitAsync();
        }

        // The refused attempt wrote no enrollment and no assignment row: the totals are still the accepted ones.
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Enrollments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessId)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Enrollments WHERE ClientId = @clientId", ("@clientId", ids.OtherClient)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE GridClassId = @gridClassId", ("@gridClassId", ids.ClassA)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE GridClassId = @gridClassId", ("@gridClassId", ids.ClassB)));

        output.WriteLine("Multi-class assertions passed: the two-class enrollment was written with exactly one assignment per class and one seat held in each, and a second two-class insert was refused naming the full class without writing an enrollment or an assignment.");
    }

    /// <summary>
    /// Resolves this test's opt-in target. <c>null</c> means the variable is absent: the test returns N/A without
    /// opening any connection or touching any server.
    /// </summary>
    private async Task<SqlTarget?> ResolveTargetAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"N/A: {VariableName} absent; no SQL connection opened.");
            return null;
        }

        var target = ValidateTarget(supplied, testSuffix);
        var applied = await CreateTargetAsync(target);
        output.WriteLine($"Target: {target.DatabaseName}; DB_ID absent before creation; applied {applied} schema script(s); retained without teardown.");
        return target;
    }

    private static SqlTarget ValidateTarget(string suppliedConnectionString, string testSuffix)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{VariableName} could not be parsed; no SQL connection was opened.");
        }

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T15_SQL_CONNECTION and
        // the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT15Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
        if (!string.Equals(supplied.DataSource, Server, StringComparison.OrdinalIgnoreCase)
            || !supplied.IntegratedSecurity
            || !safeName
            || !string.IsNullOrEmpty(supplied.UserID)
            || !string.IsNullOrEmpty(supplied.Password)
            || !string.IsNullOrEmpty(supplied.AttachDBFilename)
            || supplied.UserInstance
            || !string.IsNullOrEmpty(supplied.FailoverPartner))
        {
            throw new InvalidOperationException(
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT15Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T15 SQL integration test",
            TrustServerCertificate = true
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<int> CreateTargetAsync(SqlTarget target)
    {
        var startInfo = new ProcessStartInfo("sqlcmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-I", "-S", Server, "-d", "master", "-E", "-b", "-h", "-1", "-W", "-Q",
                     $"SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'{target.DatabaseName}') IS NULL THEN 0 ELSE 1 END;"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartSqlCmd(startInfo);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"master preflight failed: {stderr.Trim()}");
        Assert.Equal("0", stdout.Trim());

        // SQL identifiers cannot be parameters. The strict allowlist above excludes brackets and quotes, so quoting
        // the identifier keeps CREATE DATABASE bound to exactly this target, and a competing creator fails loudly
        // instead of letting this run migrate someone else's database.
        var masterConnectionString = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{target.DatabaseName}]", master);
            await create.ExecuteNonQueryAsync();
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        return scripts.Count;
    }

    /// <summary>
    /// Starts <c>sqlcmd</c>, turning a missing binary into an explicit message instead of an uncaught
    /// <see cref="Win32Exception"/> that hides which tool is required.
    /// </summary>
    private static Process StartSqlCmd(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("sqlcmd preflight unavailable.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "sqlcmd could not be started; install the SQL Server command line tools and make sure sqlcmd is on PATH.",
                exception);
        }
    }

    private static async Task<SeatsIds> SeedSeatsAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T15 Seats Business", "synthetic-t15-seats", now);
        var businessB = await InsertBusinessAsync(connection, transaction, "Synthetic T15 Seats Other", "synthetic-t15-seats-other", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T15 Seat Class", 60, 3);
        var serviceB = await InsertServiceAsync(connection, transaction, businessB, "Synthetic T15 Seat Other Class", 60, 3);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Seat", "Teacher", "20202101010");
        var workerB = await InsertWorkerAsync(connection, transaction, businessB, "Seat", "Other", "20202101011");
        var clientA = await InsertClientAsync(connection, transaction, businessA, "Seat", "Student", "t15.seats.client@example.invalid", now);
        var clientB = await InsertClientAsync(connection, transaction, businessB, "Seat", "Foreign", "t15.seats.foreign@example.invalid", now);

        var gridClass = await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Sunday, new TimeOnly(10, 0));
        var otherGridClass = await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Sunday, new TimeOnly(12, 0));
        var foreignGridClass = await InsertGridClassAsync(connection, transaction, businessB, serviceB, workerB, DayOfWeek.Sunday, new TimeOnly(10, 0));

        // Retains its seat: the window opens in the future, so a query narrowing by ValidFrom <= reference would drop
        // it and oversubscribe the grid class.
        var future = await InsertEnrollmentAsync(connection, transaction, businessA, clientA, EnrollmentStatus.PendingPayment, now);
        await InsertFixedClassAsync(connection, transaction, future, gridClass, new DateOnly(2026, 6, 1), null);

        var endedBefore = await InsertEnrollmentAsync(connection, transaction, businessA, clientA, EnrollmentStatus.Active, now);
        await InsertFixedClassAsync(connection, transaction, endedBefore, gridClass, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30));

        // Ends exactly on the reference date: the end of the window is inclusive.
        var endsOnReference = await InsertEnrollmentAsync(connection, transaction, businessA, clientA, EnrollmentStatus.Active, now);
        await InsertFixedClassAsync(connection, transaction, endsOnReference, gridClass, new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 10));

        var endedDayBefore = await InsertEnrollmentAsync(connection, transaction, businessA, clientA, EnrollmentStatus.Active, now);
        await InsertFixedClassAsync(connection, transaction, endedDayBefore, gridClass, new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 9));

        var annulled = await InsertEnrollmentAsync(connection, transaction, businessA, clientA, EnrollmentStatus.Annulled, now);
        await InsertFixedClassAsync(connection, transaction, annulled, gridClass, new DateOnly(2026, 1, 1), null);

        var terminated = await InsertEnrollmentAsync(connection, transaction, businessA, clientA, EnrollmentStatus.Terminated, now);
        await InsertFixedClassAsync(connection, transaction, terminated, gridClass, new DateOnly(2026, 1, 1), null);

        var otherGrid = await InsertEnrollmentAsync(connection, transaction, businessA, clientA, EnrollmentStatus.Active, now);
        await InsertFixedClassAsync(connection, transaction, otherGrid, otherGridClass, new DateOnly(2026, 1, 1), null);

        // Cross-tenant row: another business's enrollment pointing at business A's grid class must stay out of the read.
        var foreign = await InsertEnrollmentAsync(connection, transaction, businessB, clientB, EnrollmentStatus.Active, now);
        await InsertFixedClassAsync(connection, transaction, foreign, gridClass, new DateOnly(2026, 1, 1), null);

        await transaction.CommitAsync();

        return new SeatsIds(businessA, businessB, gridClass, otherGridClass, future, endedBefore, endsOnReference,
            endedDayBefore, annulled, terminated, otherGrid, foreign);
    }

    private static async Task<RegressionIds> SeedRegressionAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var baseIds = await InsertBaseAsync(connection, transaction, "regression", "20202102020", 1, now);
        var first = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Regresion", "Uno", "t15.regression.client1@example.invalid", now);
        var second = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Regresion", "Dos", "t15.regression.client2@example.invalid", now);

        await transaction.CommitAsync();

        return new RegressionIds(baseIds.BusinessId, baseIds.GridClassId, first, second);
    }

    private static async Task<RaceIds> SeedRaceAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var baseIds = await InsertBaseAsync(connection, transaction, "race", "20202103030", 1, now);
        var first = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Race", "One", "t15.race.client1@example.invalid", now);
        var second = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Race", "Two", "t15.race.client2@example.invalid", now);

        await transaction.CommitAsync();

        return new RaceIds(baseIds.BusinessId, baseIds.GridClassId, first, second);
    }

    private static async Task<ChargeIds> SeedChargeAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var baseIds = await InsertBaseAsync(connection, transaction, "charge", "20202104040", 2, now);
        var client = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Charge", "Student", "t15.charge.client@example.invalid", now);
        var otherClient = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Charge", "Other", "t15.charge.other@example.invalid", now);

        // A future session with free places: the activation annotates it. A full one — capacity 1 with another
        // client's pending annotation — is refused because no unique index guards anotados <= MaxCapacity.
        var sessionFree = await InsertClassSessionAsync(connection, transaction, baseIds.BusinessId, baseIds.GridClassId,
            baseIds.ServiceId, baseIds.WorkerId, new DateTime(2026, 5, 17, 10, 0, 0), 60, 2, AppointmentStatus.Scheduled, now);
        var sessionFull = await InsertClassSessionAsync(connection, transaction, baseIds.BusinessId, baseIds.GridClassId,
            baseIds.ServiceId, baseIds.WorkerId, new DateTime(2026, 5, 24, 10, 0, 0), 60, 1, AppointmentStatus.Scheduled, now);
        await InsertBookingAsync(connection, transaction, baseIds.BusinessId, sessionFull, otherClient, null,
            BookingModality.DropIn, BookingStatus.Pending, now);

        await transaction.CommitAsync();

        return new ChargeIds(baseIds.BusinessId, baseIds.GridClassId, client, sessionFree, sessionFull);
    }

    private static async Task<RenewalIds> SeedRenewalAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var baseIds = await InsertBaseAsync(connection, transaction, "renewal", "20202105050", 2, now);
        var client = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Renewal", "Student", "t15.renewal.client@example.invalid", now);

        await transaction.CommitAsync();

        return new RenewalIds(baseIds.BusinessId, baseIds.GridClassId, client);
    }

    private static async Task<AnnulmentIds> SeedAnnulmentAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var baseIds = await InsertBaseAsync(connection, transaction, "annulment", "20202106060", 1, now);
        var first = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Anulacion", "Uno", "t15.annulment.client1@example.invalid", now);
        var second = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Anulacion", "Dos", "t15.annulment.client2@example.invalid", now);

        // One session after the registration instant and one before it: only the future annotation is released.
        var sessionFuture = await InsertClassSessionAsync(connection, transaction, baseIds.BusinessId, baseIds.GridClassId,
            baseIds.ServiceId, baseIds.WorkerId, new DateTime(2026, 5, 17, 10, 0, 0), 60, 1, AppointmentStatus.Scheduled, now);
        var sessionPast = await InsertClassSessionAsync(connection, transaction, baseIds.BusinessId, baseIds.GridClassId,
            baseIds.ServiceId, baseIds.WorkerId, new DateTime(2026, 5, 3, 10, 0, 0), 60, 1, AppointmentStatus.Scheduled, now);

        await transaction.CommitAsync();

        return new AnnulmentIds(baseIds.BusinessId, baseIds.GridClassId, first, second, sessionFuture, sessionPast);
    }

    private static async Task<TerminationIds> SeedTerminationAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var baseIds = await InsertBaseAsync(connection, transaction, "termination", "20202107070", 2, now);
        var client = await InsertClientAsync(connection, transaction, baseIds.BusinessId, "Baja", "Student", "t15.termination.client@example.invalid", now);

        // One session before the effective date (2026-05-15), one on it and one after it.
        var sessionBefore = await InsertClassSessionAsync(connection, transaction, baseIds.BusinessId, baseIds.GridClassId,
            baseIds.ServiceId, baseIds.WorkerId, new DateTime(2026, 5, 14, 10, 0, 0), 60, 2, AppointmentStatus.Scheduled, now);
        var sessionOnEffective = await InsertClassSessionAsync(connection, transaction, baseIds.BusinessId, baseIds.GridClassId,
            baseIds.ServiceId, baseIds.WorkerId, new DateTime(2026, 5, 15, 10, 0, 0), 60, 2, AppointmentStatus.Scheduled, now);
        var sessionAfter = await InsertClassSessionAsync(connection, transaction, baseIds.BusinessId, baseIds.GridClassId,
            baseIds.ServiceId, baseIds.WorkerId, new DateTime(2026, 5, 20, 10, 0, 0), 60, 2, AppointmentStatus.Scheduled, now);

        await transaction.CommitAsync();

        return new TerminationIds(baseIds.BusinessId, baseIds.GridClassId, client, sessionBefore, sessionOnEffective, sessionAfter);
    }

    private static async Task<ListSeedIds> SeedListAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T15 List Business", "synthetic-t15-list", now);
        var businessB = await InsertBusinessAsync(connection, transaction, "Synthetic T15 List Other", "synthetic-t15-list-other", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T15 List Class", 60, 8);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "List", "Teacher", "20202108080");
        await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Sunday, new TimeOnly(10, 0));

        var ana = await InsertClientAsync(connection, transaction, businessA, "Ana", "Perez", "t15.list.ana@example.invalid", now);
        var bruno = await InsertClientAsync(connection, transaction, businessA, "Bruno", "Diaz", "t15.list.bruno@example.invalid", now);
        var carla = await InsertClientAsync(connection, transaction, businessA, "Carla", "Lopez", "t15.list.carla@example.invalid", now);
        var diego = await InsertClientAsync(connection, transaction, businessA, "Diego", "Ruiz", "t15.list.diego@example.invalid", now);
        var elena = await InsertClientAsync(connection, transaction, businessA, "Elena", "Sosa", "t15.list.elena@example.invalid", now);
        var fabio = await InsertClientAsync(connection, transaction, businessA, "Fabio", "Vera", "t15.list.fabio@example.invalid", now);
        var foreign = await InsertClientAsync(connection, transaction, businessB, "Otra", "Alumna", "t15.list.foreign@example.invalid", now);

        // Active with a period that covers the reference date and expires in exactly three days: Current + ExpiringSoon.
        var anaEnrollment = await InsertEnrollmentAsync(connection, transaction, businessA, ana, EnrollmentStatus.Active, now,
            weeklyFrequency: 2, referenceDay: 10, activatedAt: now.AddDays(-30));
        var anaPayment = await InsertMembershipPaymentAsync(connection, transaction, businessA, ana, anaEnrollment, 5000m, now);
        await InsertMembershipPeriodAsync(connection, transaction, anaEnrollment, new DateOnly(2026, 4, 10), new DateOnly(2026, 5, 13), anaPayment, now);

        // Active and covering the reference date, expiring far away: Current only.
        var brunoEnrollment = await InsertEnrollmentAsync(connection, transaction, businessA, bruno, EnrollmentStatus.Active, now,
            referenceDay: 25, activatedAt: now.AddDays(-30));
        var brunoPayment = await InsertMembershipPaymentAsync(connection, transaction, businessA, bruno, brunoEnrollment, 5000m, now);
        await InsertMembershipPeriodAsync(connection, transaction, brunoEnrollment, new DateOnly(2026, 5, 5), new DateOnly(2026, 6, 5), brunoPayment, now);

        // Active with a lapsed period: Expired.
        var carlaEnrollment = await InsertEnrollmentAsync(connection, transaction, businessA, carla, EnrollmentStatus.Active, now,
            referenceDay: 1, activatedAt: now.AddDays(-90));
        var carlaPayment = await InsertMembershipPaymentAsync(connection, transaction, businessA, carla, carlaEnrollment, 5000m, now);
        await InsertMembershipPeriodAsync(connection, transaction, carlaEnrollment, new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), carlaPayment, now);

        // Pending payment with no period at all.
        var diegoEnrollment = await InsertEnrollmentAsync(connection, transaction, businessA, diego, EnrollmentStatus.PendingPayment, now);

        var elenaEnrollment = await InsertEnrollmentAsync(connection, transaction, businessA, elena, EnrollmentStatus.Annulled, now);
        var fabioEnrollment = await InsertEnrollmentAsync(connection, transaction, businessA, fabio, EnrollmentStatus.Terminated, now);

        // Another tenant's enrollment, invisible to business A.
        var foreignEnrollment = await InsertEnrollmentAsync(connection, transaction, businessB, foreign, EnrollmentStatus.Active, now,
            activatedAt: now.AddDays(-30));

        await transaction.CommitAsync();

        return new ListSeedIds(businessA, businessB, anaEnrollment, brunoEnrollment, carlaEnrollment, diegoEnrollment,
            elenaEnrollment, fabioEnrollment, foreignEnrollment);
    }

    /// <summary>
    /// Two grid classes of one business with different permanent capacities: class A admits two enrollments and class
    /// B only one, so a two-class insert is accepted while both are free and refused, naming B, once B is full.
    /// </summary>
    private static async Task<MultiClassIds> SeedMultiClassAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessId = await InsertBusinessAsync(connection, transaction, "Synthetic T15 Multi Class Business", "synthetic-t15-multiclass", now);
        var workerId = await InsertWorkerAsync(connection, transaction, businessId, "Multiclase", "Teacher", "20202109090");
        var serviceA = await InsertServiceAsync(connection, transaction, businessId, "Synthetic T15 Multi Class A", 60, 2);
        var serviceB = await InsertServiceAsync(connection, transaction, businessId, "Synthetic T15 Multi Class B", 60, 1);
        var classA = await InsertGridClassAsync(connection, transaction, businessId, serviceA, workerId, DayOfWeek.Sunday, new TimeOnly(10, 0));
        var classB = await InsertGridClassAsync(connection, transaction, businessId, serviceB, workerId, DayOfWeek.Sunday, new TimeOnly(12, 0));
        await InsertPriceAsync(connection, transaction, businessId, 1, 5000m);
        // The two-class request below uses weekly frequency 2, and the service requires a configured price for the
        // exact requested frequency (no fallback), so frequency 2 has to be offered too.
        await InsertPriceAsync(connection, transaction, businessId, 2, 5000m);
        var client = await InsertClientAsync(connection, transaction, businessId, "Multiclase", "Inscripta", "t15.multiclass.client@example.invalid", now);
        var otherClient = await InsertClientAsync(connection, transaction, businessId, "Multiclase", "Otra", "t15.multiclass.other@example.invalid", now);

        await transaction.CommitAsync();

        return new MultiClassIds(businessId, classA, classB, client, otherClient);
    }

    /// <summary>
    /// The business, service, worker, grid class and frequency-1 price every integration scenario needs. The price is
    /// what the service charges as the agreed amount when the request omits one.
    /// </summary>
    private static async Task<SyntheticBase> InsertBaseAsync(
        SqlConnection connection, SqlTransaction transaction, string slug, string cuil, int maxCapacity, DateTime now)
    {
        var businessId = await InsertBusinessAsync(connection, transaction, $"Synthetic T15 {slug} Business", $"synthetic-t15-{slug}", now);
        var serviceId = await InsertServiceAsync(connection, transaction, businessId, $"Synthetic T15 {slug} Class", 60, maxCapacity);
        var workerId = await InsertWorkerAsync(connection, transaction, businessId, "Synthetic", "Teacher", cuil);
        var gridClassId = await InsertGridClassAsync(connection, transaction, businessId, serviceId, workerId, DayOfWeek.Sunday, new TimeOnly(10, 0));
        await InsertPriceAsync(connection, transaction, businessId, 1, 5000m);
        return new SyntheticBase(businessId, serviceId, workerId, gridClassId);
    }

    private static Task<int> InsertBusinessAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string slug, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @createdAt)",
            ("@name", name), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@createdAt", now));

    private static Task<int> InsertServiceAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name, int durationMinutes,
        int maxCapacity) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, @capacity)",
            ("@businessId", businessId), ("@name", name), ("@price", 100.00m),
            ("@duration", durationMinutes), ("@capacity", maxCapacity));

    private static Task<int> InsertWorkerAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string firstName, string lastName,
        string cuil) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", businessId), ("@firstName", firstName), ("@lastName", lastName),
            ("@phone", "0000000000"), ("@cuil", cuil));

    private static Task<int> InsertClientAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string firstName, string lastName,
        string email, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
            ("@businessId", businessId), ("@firstName", firstName), ("@lastName", lastName),
            ("@email", email), ("@phone", "0000000000"), ("@createdAt", now));

    private static Task<int> InsertPriceAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int weeklyFrequency, decimal price) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO MembershipPrices (BusinessId, WeeklyFrequency, Price) OUTPUT INSERTED.Id VALUES (@businessId, @weeklyFrequency, @price)",
            ("@businessId", businessId), ("@weeklyFrequency", weeklyFrequency), ("@price", price));

    private static Task<int> InsertGridClassAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DayOfWeek day, TimeOnly startTime) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO GridClasses (BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime, IsActive) OUTPUT INSERTED.Id VALUES (@businessId, @serviceId, @workerId, @dayOfWeek, @startTime, 1)",
            ("@businessId", businessId), ("@serviceId", serviceId), ("@workerId", workerId),
            ("@dayOfWeek", day), ("@startTime", startTime));

    private static Task<int> InsertClassSessionAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int gridClassId, int serviceId,
        int workerId, DateTime startsAt, int durationMinutes, int maxCapacity,
        AppointmentStatus status, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, @gridClassId, @startsAt, @endsAt, @capacity, NULL, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.ClassSession), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@gridClassId", gridClassId), ("@startsAt", startsAt),
            ("@endsAt", startsAt.AddMinutes(durationMinutes)), ("@capacity", maxCapacity), ("@status", status),
            ("@createdAt", now));

    /// <summary>
    /// One enrollment row with the caller's status and optional reference day and activation instant, so a scenario
    /// can seed any state without going through the service.
    /// </summary>
    private static Task<int> InsertEnrollmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId, EnrollmentStatus status,
        DateTime now, int weeklyFrequency = 1, int? referenceDay = null, DateTime? activatedAt = null) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, PriceReason, ReferenceDay, CreatedAt, ActivatedAt, AnnulledAt, TerminatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, NULL, @referenceDay, @createdAt, @activatedAt, NULL, NULL)",
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", weeklyFrequency),
            ("@status", status), ("@price", 5000m), ("@referenceDay", referenceDay),
            ("@createdAt", now), ("@activatedAt", activatedAt));

    private static Task<int> InsertFixedClassAsync(
        SqlConnection connection, SqlTransaction transaction, int enrollmentId, int gridClassId,
        DateOnly validFrom, DateOnly? validTo) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO EnrollmentFixedClasses (EnrollmentId, GridClassId, ValidFrom, ValidTo) OUTPUT INSERTED.Id VALUES (@enrollmentId, @gridClassId, @validFrom, @validTo)",
            ("@enrollmentId", enrollmentId), ("@gridClassId", gridClassId),
            ("@validFrom", validFrom), ("@validTo", validTo));

    private static Task<int> InsertMembershipPaymentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId, int enrollmentId,
        decimal amount, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @membership, NULL, @enrollmentId, @amount, NULL, NULL, @paidAt, @recordedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@membership", PaymentConcept.Membership),
            ("@enrollmentId", enrollmentId), ("@amount", amount), ("@paidAt", now), ("@recordedAt", now));

    private static Task<int> InsertMembershipPeriodAsync(
        SqlConnection connection, SqlTransaction transaction, int enrollmentId, DateOnly coverageStart,
        DateOnly expiresOn, int paymentId, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) OUTPUT INSERTED.Id VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
            ("@enrollmentId", enrollmentId), ("@coverageStart", coverageStart), ("@expiresOn", expiresOn),
            ("@paymentId", paymentId), ("@createdAt", now));

    /// <summary>
    /// One booking row with its schema constraints honoured: a fixed annotation carries its enrollment and a cancelled
    /// one carries its cancellation instant.
    /// </summary>
    private static Task<int> InsertBookingAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int appointmentId, int clientId,
        int? enrollmentId, BookingModality? modality, BookingStatus status, DateTime createdAt) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, @createdAt, @cancelledAt)",
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@enrollmentId", enrollmentId), ("@modality", modality), ("@status", status),
            ("@createdAt", createdAt),
            ("@cancelledAt", status == BookingStatus.Cancelled ? createdAt : null));

    /// <summary>Autocommit fixed annotation, used once the service resolved the enrollment id.</summary>
    private static Task<int> InsertFixedAnnotationAsync(
        string connectionString, int businessId, int appointmentId, int clientId, int enrollmentId, DateTime now) =>
        InsertIdAsync(connectionString,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, @createdAt, NULL)",
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@enrollmentId", enrollmentId), ("@modality", BookingModality.Fixed), ("@status", BookingStatus.Pending),
            ("@createdAt", now));

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertIdAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, null, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters) =>
        await ReadScalarAsync<int>(connectionString, sql, parameters);

    private static async Task<T?> ReadScalarAsync<T>(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, null, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
            return default;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Attempts the second period of one payment, which <c>UQ_MembershipPeriods_Payment</c> must reject, and returns
    /// the server exception so the caller can assert its number. Nothing is written by a rejected insert.
    /// </summary>
    private static async Task<SqlException> InsertDuplicatePeriodFailureAsync(
        string connectionString, int enrollmentId, int paymentId, DateOnly expiresOn, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        AddParameter(command, "@coverageStart", expiresOn);
        AddParameter(command, "@expiresOn", expiresOn.AddMonths(1));
        AddParameter(command, "@paymentId", paymentId);
        AddParameter(command, "@createdAt", now);
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    private static async Task<EnrollmentSnapshot> ReadEnrollmentAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, ReferenceDay, CreatedAt, ActivatedAt, AnnulledAt, TerminatedAt FROM Enrollments WHERE Id = @id",
            connection);
        AddParameter(command, "@id", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Enrollment {enrollmentId} was not found.");
        return new EnrollmentSnapshot(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            (EnrollmentStatus)reader.GetByte(4),
            reader.GetDecimal(5),
            reader.IsDBNull(6) ? null : reader.GetByte(6),
            reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            reader.IsDBNull(10) ? null : reader.GetDateTime(10));
    }

    private static async Task<PaymentSnapshot> ReadPaymentAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PaidAt, RecordedAt FROM Payments WHERE EnrollmentId = @enrollmentId",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Payment of enrollment {enrollmentId} was not found.");
        return new PaymentSnapshot(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            (PaymentConcept)reader.GetByte(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.GetDecimal(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.GetDateTime(8),
            reader.GetDateTime(9));
    }

    private static async Task<MembershipPeriod> ReadSinglePeriodAsync(string connectionString, int enrollmentId)
    {
        var periods = await ReadPeriodsAsync(connectionString, enrollmentId);
        return Assert.Single(periods);
    }

    private static async Task<List<MembershipPeriod>> ReadPeriodsAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId ORDER BY CoverageStart, Id",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        var periods = new List<MembershipPeriod>();
        while (await reader.ReadAsync())
        {
            periods.Add(new MembershipPeriod
            {
                Id = reader.GetInt32(0),
                EnrollmentId = reader.GetInt32(1),
                CoverageStart = DateOnly.FromDateTime(reader.GetDateTime(2)),
                ExpiresOn = DateOnly.FromDateTime(reader.GetDateTime(3)),
                PaymentId = reader.GetInt32(4),
                CreatedAt = reader.GetDateTime(5)
            });
        }

        return periods;
    }

    private static async Task<EnrollmentFixedClass> ReadSingleFixedClassAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, EnrollmentId, GridClassId, ValidFrom, ValidTo FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId ORDER BY Id",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Enrollment {enrollmentId} has no fixed assignment.");
        var fixedClass = new EnrollmentFixedClass
        {
            Id = reader.GetInt32(0),
            EnrollmentId = reader.GetInt32(1),
            GridClassId = reader.GetInt32(2),
            ValidFrom = DateOnly.FromDateTime(reader.GetDateTime(3)),
            ValidTo = reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4))
        };
        Assert.False(await reader.ReadAsync(), $"Enrollment {enrollmentId} has more than one fixed assignment.");
        return fixedClass;
    }

    /// <summary>The grid class of every fixed assignment of one enrollment, ordered by the assignment id.</summary>
    private static async Task<List<int>> ReadFixedClassIdsAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT GridClassId FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId ORDER BY Id",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        var gridClassIds = new List<int>();
        while (await reader.ReadAsync())
            gridClassIds.Add(reader.GetInt32(0));
        return gridClassIds;
    }

    private static async Task<BookingSnapshot> ReadSingleBookingAsync(string connectionString, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CancelledAt, CreatedAt, CancelledBy FROM Bookings WHERE AppointmentId = @appointmentId",
            connection);
        AddParameter(command, "@appointmentId", appointmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Appointment {appointmentId} has no annotation.");
        var booking = new BookingSnapshot(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : (BookingModality)reader.GetByte(4),
            (BookingStatus)reader.GetByte(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : (CancelledBy)reader.GetByte(8));
        Assert.False(await reader.ReadAsync(), $"Appointment {appointmentId} has more than one annotation.");
        return booking;
    }

    private static SqlCommand Create(
        SqlConnection connection, SqlTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            AddParameter(command, name, value);
        return command;
    }

    private static void AddParameter(SqlCommand command, string name, object? value)
    {
        switch (value)
        {
            case DateTime dateTime:
                command.Parameters.Add(name, SqlDbType.DateTime2).Value = dateTime;
                return;
            case DateOnly date:
                command.Parameters.Add(name, SqlDbType.Date).Value = date.ToDateTime(TimeOnly.MinValue);
                return;
            case null:
                command.Parameters.AddWithValue(name, DBNull.Value);
                return;
            case Enum enumeration:
                command.Parameters.AddWithValue(name, Convert.ToByte(enumeration, CultureInfo.InvariantCulture));
                return;
            default:
                command.Parameters.AddWithValue(name, value);
                return;
        }
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record SyntheticBase(int BusinessId, int ServiceId, int WorkerId, int GridClassId);

    private sealed record SeatsIds(
        int BusinessA,
        int BusinessB,
        int GridClass,
        int OtherGridClass,
        int FutureEnrollment,
        int EndedBeforeReferenceEnrollment,
        int EndsOnReferenceEnrollment,
        int EndedDayBeforeReferenceEnrollment,
        int AnnulledEnrollment,
        int TerminatedEnrollment,
        int OtherGridEnrollment,
        int ForeignEnrollment);

    private sealed record RegressionIds(int BusinessA, int GridClass, int FirstClient, int SecondClient);

    private sealed record RaceIds(int BusinessA, int GridClass, int FirstClient, int SecondClient);

    private sealed record MultiClassIds(int BusinessId, int ClassA, int ClassB, int Client, int OtherClient);

    private sealed record ChargeIds(int BusinessA, int GridClass, int Client, int SessionFree, int SessionFull);

    private sealed record RenewalIds(int BusinessA, int GridClass, int Client);

    private sealed record AnnulmentIds(
        int BusinessA, int GridClass, int FirstClient, int SecondClient, int SessionFuture, int SessionPast);

    private sealed record TerminationIds(
        int BusinessA, int GridClass, int Client, int SessionBefore, int SessionOnEffective, int SessionAfter);

    private sealed record ListSeedIds(
        int BusinessA,
        int BusinessB,
        int AnaEnrollment,
        int BrunoEnrollment,
        int CarlaEnrollment,
        int DiegoEnrollment,
        int ElenaEnrollment,
        int FabioEnrollment,
        int ForeignEnrollment);

    private sealed record EnrollmentSnapshot(
        int Id,
        int BusinessId,
        int ClientId,
        int WeeklyFrequency,
        EnrollmentStatus Status,
        decimal AgreedPrice,
        int? ReferenceDay,
        DateTime CreatedAt,
        DateTime? ActivatedAt,
        DateTime? AnnulledAt,
        DateTime? TerminatedAt);

    private sealed record PaymentSnapshot(
        int Id,
        int BusinessId,
        int? ClientId,
        PaymentConcept Concept,
        int? BookingId,
        int? EnrollmentId,
        decimal Amount,
        decimal? ListPrice,
        DateTime PaidAt,
        DateTime RecordedAt);

    private sealed record BookingSnapshot(
        int BusinessId,
        int AppointmentId,
        int ClientId,
        int? EnrollmentId,
        BookingModality? Modality,
        BookingStatus Status,
        DateTime? CancelledAt,
        DateTime CreatedAt,
        CancelledBy? CancelledBy);
}
