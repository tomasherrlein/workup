using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Classes;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Classes;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlClassAttendanceRepository"/> and the service that
/// drives it (RF-11, RF-12, RF-10, D-16, D-20, D-23). Requires a new, dedicated database supplied through
/// WORKUP_T16_SQL_CONNECTION; without it every test returns N/A and no SQL connection is opened. Each test derives
/// its own database name from the supplied one, so the whole class runs in a single command, and nothing is torn
/// down: the target is retained on purpose. The unit tests cannot prove what lives in storage — the mass of the
/// statuses, the payment row that must not exist, the erased rollback or the annulled payment — so this fixture is
/// the runtime proof of those claims.
/// </summary>
public sealed class ClassAttendanceSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T16_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>The single clock read of every operation: the status instant, the charge instant and the note instant.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 8, 0, 0);

    /// <summary>The date of every seeded class: the coverage rule judges this date, never the moment of the call.</summary>
    private static readonly DateTime ClassStartsAt = new(2026, 3, 12, 18, 0, 0);

    private static readonly DateOnly ClassDate = new(2026, 3, 12);

    /// <summary>
    /// The single clock read of every at-arrival walk-in: inside <c>[StartsAt, EndsAt]</c>, which is the arrival
    /// window of the walk-in and a moment at which a booking of the same class would already be late.
    /// </summary>
    private static readonly DateTime WalkInNow = new(2026, 3, 12, 18, 10, 0);

    [Fact]
    public async Task Drop_in_attendance_with_a_charge_is_stored_once_with_the_recorded_instant()
    {
        var target = await ResolveTargetAsync("_Attendance");
        if (target is null)
            return;

        var ids = await SeedDropInAsync(target.ConnectionString, "synthetic-t16-attendance", "t16.attendance");

        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassAttendanceService(
            new SqlClassAttendanceRepository(session), session, new FakeClock { Now = FixedNow });

        var result = await service.MarkAttendedAsync(ids.BusinessA, ids.Booking,
            new ClassAttendanceRequest(
                new ClassAttendanceCharge(450m, "Clase suelta con descuento"), "Alergia al amoníaco"));

        Assert.True(result.Succeeded, result.Error?.Code);

        // The annotation carries the attendance, the single clock instant and no absence source at all.
        var booking = await ReadBookingAsync(target.ConnectionString, ids.Booking);
        Assert.Equal(BookingStatus.Attended, booking.Status);
        Assert.Equal(FixedNow, booking.AttendanceRecordedAt);
        Assert.Null(booking.AbsenceSource);
        Assert.Null(booking.CancelledAt);

        // Exactly one charge, of the drop-in concept, with its own booking and no enrollment, the agreed amount beside
        // the class list price, and the very same instant as the attendance it accompanied.
        var payment = await ReadSinglePaymentAsync(target.ConnectionString, ids.Booking);
        Assert.Equal(ids.BusinessA, payment.BusinessId);
        Assert.Equal(ids.Client, payment.ClientId);
        Assert.Equal(PaymentConcept.DropInClass, payment.Concept);
        Assert.Equal(ids.Booking, payment.BookingId);
        Assert.Null(payment.EnrollmentId);
        Assert.Equal(450m, payment.Amount);
        Assert.Equal(100m, payment.ListPrice);
        Assert.Equal("Clase suelta con descuento", payment.PriceReason);
        Assert.Equal(booking.AttendanceRecordedAt, payment.PaidAt);
        Assert.Equal(FixedNow, payment.RecordedAt);

        // The observation landed in the same unit, on the client's record and tied to the session.
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM ClientNotes WHERE ClientId = @clientId AND AppointmentId = @appointmentId AND Text = @text AND CreatedAt = @now",
            ("@clientId", ids.Client), ("@appointmentId", ids.Session), ("@text", "Alergia al amoníaco"),
            ("@now", FixedNow)));

        output.WriteLine("Attendance assertions passed: one drop-in charge with the class list price and the single recorded instant, the status Attended with no absence source, and the observation attached to the session.");
    }

    [Fact]
    public async Task Absence_is_stored_with_the_owner_source_and_writes_no_payment()
    {
        var target = await ResolveTargetAsync("_Absence");
        if (target is null)
            return;

        var ids = await SeedDropInAsync(target.ConnectionString, "synthetic-t16-absence", "t16.absence");

        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassAttendanceService(
            new SqlClassAttendanceRepository(session), session, new FakeClock { Now = FixedNow });

        var result = await service.MarkAbsentAsync(ids.BusinessA, ids.Booking);
        Assert.True(result.Succeeded, result.Error?.Code);

        // The absence is a fact recorded by the owner: the status, the source and the single instant, and the money
        // stays completely untouched.
        var booking = await ReadBookingAsync(target.ConnectionString, ids.Booking);
        Assert.Equal(BookingStatus.Absent, booking.Status);
        Assert.Equal(AbsenceSource.Owner, booking.AbsenceSource);
        Assert.Equal(FixedNow, booking.AttendanceRecordedAt);
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BookingId = @bookingId", ("@bookingId", ids.Booking)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM ClientNotes WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));

        output.WriteLine("Absence assertions passed: Ausente with AbsenceSource=Owner and the recorded instant, and no payment and no note for the annotation.");
    }

    [Fact]
    public async Task Guards_refuse_the_second_write_the_cancelled_annotation_the_wrong_kind_the_missing_modality_and_the_suspended_class()
    {
        var target = await ResolveTargetAsync("_Guards");
        if (target is null)
            return;

        var ids = await SeedGuardsAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassAttendanceRepository(session);
        var service = new ClassAttendanceService(repository, session, new FakeClock { Now = FixedNow });

        // First attendance through the service, so the second guarded write has a real instant to protect.
        Assert.True((await service.MarkAttendedAsync(ids.BusinessA, ids.DropIn,
            new ClassAttendanceRequest(new ClassAttendanceCharge(500m)))).Succeeded);
        var afterFirst = await ReadBookingAsync(target.ConnectionString, ids.DropIn);
        Assert.Equal(BookingStatus.Attended, afterFirst.Status);
        Assert.Equal(FixedNow, afterFirst.AttendanceRecordedAt);

        // The guarded statement changes zero rows on a recorded annotation, whatever status it is asked to write, so
        // the original instant survives a later attempt.
        Assert.False(await repository.SetStatusAsync(ids.BusinessA, ids.DropIn, BookingStatus.Absent, FixedNow.AddMinutes(5)));
        var unchanged = await ReadBookingAsync(target.ConnectionString, ids.DropIn);
        Assert.Equal(BookingStatus.Attended, unchanged.Status);
        Assert.Equal(FixedNow, unchanged.AttendanceRecordedAt);
        Assert.Null(unchanged.AbsenceSource);
        var second = await service.MarkAttendedAsync(ids.BusinessA, ids.DropIn, new ClassAttendanceRequest());
        Assert.False(second.Succeeded);
        Assert.Equal("class_attendance_already_attended", second.Error!.Code);

        // A cancelled annotation is never revived, by the statement or by the service.
        Assert.False(await repository.SetStatusAsync(ids.BusinessA, ids.Cancelled, BookingStatus.Attended, FixedNow));
        var cancelled = await ReadBookingAsync(target.ConnectionString, ids.Cancelled);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.AttendanceRecordedAt);
        var cancelledResult = await service.MarkAttendedAsync(ids.BusinessA, ids.Cancelled, new ClassAttendanceRequest());
        Assert.False(cancelledResult.Succeeded);
        Assert.Equal("class_attendance_cancelled", cancelledResult.Error!.Code);

        // An individual turn is not a class annotation even when its booking carries a modality, and a class
        // annotation without a modality is not one the rules can judge: neither changes a row.
        Assert.False(await repository.SetStatusAsync(ids.BusinessA, ids.Individual, BookingStatus.Attended, FixedNow));
        Assert.False(await repository.SetStatusAsync(ids.BusinessA, ids.NoModality, BookingStatus.Attended, FixedNow));
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.Individual)).Status);
        Assert.Null((await ReadBookingAsync(target.ConnectionString, ids.Individual)).AttendanceRecordedAt);
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.NoModality)).Status);
        Assert.Null((await ReadBookingAsync(target.ConnectionString, ids.NoModality)).AttendanceRecordedAt);

        // A suspended class is lost (RF-10): attendance, the drop-in charge and the absence are all refused with the
        // stable code before any write, and the guarded statement refuses them too.
        var suspendedAttendance = await service.MarkAttendedAsync(ids.BusinessA, ids.Suspended,
            new ClassAttendanceRequest(new ClassAttendanceCharge(500m)));
        Assert.False(suspendedAttendance.Succeeded);
        Assert.Equal("class_attendance_suspended", suspendedAttendance.Error!.Code);
        var suspendedAbsence = await service.MarkAbsentAsync(ids.BusinessA, ids.Suspended);
        Assert.False(suspendedAbsence.Succeeded);
        Assert.Equal("class_attendance_suspended", suspendedAbsence.Error!.Code);
        Assert.False(await repository.SetStatusAsync(ids.BusinessA, ids.Suspended, BookingStatus.Attended, FixedNow));
        var suspended = await ReadBookingAsync(target.ConnectionString, ids.Suspended);
        Assert.Equal(BookingStatus.Pending, suspended.Status);
        Assert.Null(suspended.AttendanceRecordedAt);
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BookingId = @bookingId", ("@bookingId", ids.Suspended)));

        // The only charge of the whole fixture is the first, accepted attendance's.
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));

        output.WriteLine("Guard assertions passed: the second status write changed no row and kept the first instant, the cancelled annotation was not revived, the individual turn and the annotation without modality wrote nothing, and the suspended class refused attendance, charge and absence with class_attendance_suspended.");
    }

    [Fact]
    public async Task Coverage_read_enables_the_current_period_expires_the_old_one_lets_the_drop_in_through_and_refuses_pending_payment()
    {
        var target = await ResolveTargetAsync("_Coverage");
        if (target is null)
            return;

        var ids = await SeedCoverageAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassAttendanceRepository(session);
        var service = new ClassAttendanceService(repository, session, new FakeClock { Now = FixedNow });

        // The lock reads the paid periods of the fixed annotation, and the covering one covers the class date.
        var currentLock = await repository.LockAsync(ids.BusinessA, ids.FixedCurrent);
        Assert.NotNull(currentLock);
        var currentPeriod = Assert.Single(currentLock!.Annotation.Periods);
        Assert.Equal(new DateOnly(2026, 3, 1), currentPeriod.CoverageStart);
        Assert.Equal(new DateOnly(2026, 4, 1), currentPeriod.ExpiresOn);
        Assert.True(currentPeriod.Covers(ClassDate));

        // A drop-in has no enrollment, so it reads no period at all.
        var dropInLock = await repository.LockAsync(ids.BusinessA, ids.DropIn);
        Assert.NotNull(dropInLock);
        Assert.Null(dropInLock!.Annotation.EnrollmentId);
        Assert.Empty(dropInLock.Annotation.Periods);

        // Current coverage attends; the expired period is a pending renewal and is refused; the drop-in pays on
        // arrival and is always allowed; and a `Pendiente de pago` enrollment is refused even though its stored period
        // covers the class date, because it never paid its first month (D-16, D-23).
        Assert.True((await service.MarkAttendedAsync(ids.BusinessA, ids.FixedCurrent, new ClassAttendanceRequest())).Succeeded);
        var expired = await service.MarkAttendedAsync(ids.BusinessA, ids.FixedExpired, new ClassAttendanceRequest());
        Assert.False(expired.Succeeded);
        Assert.Equal("class_attendance_without_coverage", expired.Error!.Code);
        Assert.True((await service.MarkAttendedAsync(ids.BusinessA, ids.DropIn, new ClassAttendanceRequest())).Succeeded);
        var pendingPayment = await service.MarkAttendedAsync(ids.BusinessA, ids.FixedPending, new ClassAttendanceRequest());
        Assert.False(pendingPayment.Succeeded);
        Assert.Equal("class_attendance_without_coverage", pendingPayment.Error!.Code);

        Assert.Equal(BookingStatus.Attended, (await ReadBookingAsync(target.ConnectionString, ids.FixedCurrent)).Status);
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.FixedExpired)).Status);
        Assert.Null((await ReadBookingAsync(target.ConnectionString, ids.FixedExpired)).AttendanceRecordedAt);
        Assert.Equal(BookingStatus.Attended, (await ReadBookingAsync(target.ConnectionString, ids.DropIn)).Status);
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.FixedPending)).Status);
        Assert.Null((await ReadBookingAsync(target.ConnectionString, ids.FixedPending)).AttendanceRecordedAt);
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId AND Concept <> @membership",
            ("@businessId", ids.BusinessA), ("@membership", PaymentConcept.Membership)));

        output.WriteLine("Coverage assertions passed: the current period enabled the attendance, the expired one was refused as pending renewal, the drop-in read no period and attended, and the pending-payment enrollment was refused even with a covering period.");
    }

    [Fact]
    public async Task Effective_payment_probe_is_false_once_its_annulment_exists()
    {
        var target = await ResolveTargetAsync("_Annulment");
        if (target is null)
            return;

        var ids = await SeedAnnulmentAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassAttendanceRepository(session);
        var service = new ClassAttendanceService(repository, session, new FakeClock { Now = FixedNow });

        // The stored charge is effective, so neither a second charge through the service nor the probe allows one.
        Assert.True(await repository.HasEffectivePaymentAsync(ids.BusinessA, ids.Booking));
        var refused = await service.MarkAttendedAsync(ids.BusinessA, ids.Booking,
            new ClassAttendanceRequest(new ClassAttendanceCharge(500m)));
        Assert.False(refused.Succeeded);
        Assert.Equal("class_attendance_already_charged", refused.Error!.Code);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BookingId = @bookingId", ("@bookingId", ids.Booking)));

        // Once the annulment exists the charge is no longer in force — the fake cannot model this table — so the
        // probe turns false and the annotation may carry a new charge.
        await ExecuteAsync(target.ConnectionString,
            "INSERT INTO PaymentAnnulments (PaymentId, Reason, AnnulledAt, AnnulledBy) VALUES (@paymentId, 'Anulación de prueba', @now, 'Owner')",
            ("@paymentId", ids.Payment), ("@now", FixedNow.AddDays(1)));
        Assert.False(await repository.HasEffectivePaymentAsync(ids.BusinessA, ids.Booking));

        var accepted = await service.MarkAttendedAsync(ids.BusinessA, ids.Booking,
            new ClassAttendanceRequest(new ClassAttendanceCharge(500m)));
        Assert.True(accepted.Succeeded, accepted.Error?.Code);
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BookingId = @bookingId", ("@bookingId", ids.Booking)));

        output.WriteLine("Annulment assertions passed: the effective-payment probe was true with the stored charge, the second charge was refused, and after the PaymentAnnulments row it turned false and a new charge landed.");
    }

    [Fact]
    public async Task Tenant_scope_hides_the_foreign_booking_and_the_foreign_note_target()
    {
        var target = await ResolveTargetAsync("_Tenant");
        if (target is null)
            return;

        var ids = await SeedTenantAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassAttendanceRepository(session);

        // The same booking id belongs to business A only: business B locks nothing and writes no status.
        Assert.NotNull(await repository.LockAsync(ids.BusinessA, ids.Booking));
        Assert.Null(await repository.LockAsync(ids.BusinessB, ids.Booking));
        Assert.False(await repository.SetStatusAsync(ids.BusinessB, ids.Booking, BookingStatus.Attended, FixedNow));
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.Booking)).Status);

        // The note insert is tenant-scoped on the client and on the session: a foreign client or a foreign session
        // writes no row and answers false, and only the pair of business A lands.
        Assert.False(await repository.InsertNoteAsync(ids.BusinessA, ids.ForeignClient, ids.Session, "Nota ajena", FixedNow));
        Assert.False(await repository.InsertNoteAsync(ids.BusinessA, ids.Client, ids.ForeignSession, "Nota ajena", FixedNow));
        Assert.True(await repository.InsertNoteAsync(ids.BusinessA, ids.Client, ids.Session, "Nota propia", FixedNow));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM ClientNotes WHERE Text = @text", ("@text", "Nota ajena")));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM ClientNotes WHERE Text = @text", ("@text", "Nota propia")));

        output.WriteLine("Tenant assertions passed: the foreign business locked and wrote nothing for the booking, and the note insert answered false for a foreign client and for a foreign session while the business A pair landed once.");
    }

    [Fact]
    public async Task A_failed_note_leaves_no_status_and_no_charge_even_inside_a_committed_outer_transaction()
    {
        var target = await ResolveTargetAsync("_Rollback");
        if (target is null)
            return;

        var ids = await SeedDropInAsync(target.ConnectionString, "synthetic-t16-rollback", "t16.rollback");
        var request = new ClassAttendanceRequest(new ClassAttendanceCharge(500m), "Alergia al amoníaco");

        // The service owns its transaction: the refused note rolls the whole unit back and nothing is left behind. The
        // real storage cannot be made to refuse the note here — the lock already guarantees the client and the session
        // belong to the business — so the port is wrapped only on that one write, and the lock, the status write and
        // the charge still hit SQL.
        await using (var session = new DbSession(target.ConnectionString))
        {
            var service = new ClassAttendanceService(
                new NoteRefusingRepository(new SqlClassAttendanceRepository(session)),
                session,
                new FakeClock { Now = FixedNow });

            var owned = await service.MarkAttendedAsync(ids.BusinessA, ids.Booking, request);
            Assert.False(owned.Succeeded);
            Assert.Equal("class_attendance_note_failed", owned.Error!.Code);
        }

        await AssertUntouchedAsync(target.ConnectionString, ids);

        // Composed inside an outer unit of work that its opener commits: the note is the only write that can refuse,
        // and it goes first, so the failure leaves no status write and no charge for the outer commit to persist. A
        // note written last would have persisted both here.
        await using (var session = new DbSession(target.ConnectionString))
        {
            await using var outer = await session.BeginAsync();
            var service = new ClassAttendanceService(
                new NoteRefusingRepository(new SqlClassAttendanceRepository(session)),
                session,
                new FakeClock { Now = FixedNow });

            var composed = await service.MarkAttendedAsync(ids.BusinessA, ids.Booking, request);
            Assert.False(composed.Succeeded);
            Assert.Equal("class_attendance_note_failed", composed.Error!.Code);
            await outer.CommitAsync();
        }

        await AssertUntouchedAsync(target.ConnectionString, ids);

        // A joined unit whose opener never commits erases its own writes: the service really writes the status, the
        // charge and the observation through SQL here, and disposing the outer transaction without committing must
        // leave storage exactly as it was seeded.
        await using (var session = new DbSession(target.ConnectionString))
        {
            await using (var outer = await session.BeginAsync())
            {
                var service = new ClassAttendanceService(
                    new SqlClassAttendanceRepository(session), session, new FakeClock { Now = FixedNow });

                var rolledBack = await service.MarkAttendedAsync(ids.BusinessA, ids.Booking, request);
                Assert.True(rolledBack.Succeeded, rolledBack.Error?.Code);

                // Visible inside the unit's own connection and transaction, so what disappears below is the rollback
                // and not a write that never happened.
                Assert.Equal(1, await ReadScalarInUnitAsync<int>(session,
                    "SELECT COUNT(*) FROM Payments WHERE BookingId = @bookingId", ("@bookingId", ids.Booking)));
                Assert.Equal((byte)BookingStatus.Attended, await ReadScalarInUnitAsync<byte>(session,
                    "SELECT Status FROM Bookings WHERE Id = @id", ("@id", ids.Booking)));
            }
        }

        await AssertUntouchedAsync(target.ConnectionString, ids);

        output.WriteLine("Rollback assertions passed: the refused note left the original status, no attendance instant and zero payments, both when the service owned the transaction and when it joined an outer transaction that committed, and the status write, the charge and the observation of a joined unit that was never committed were erased.");
    }

    [Fact]
    public async Task Two_parallel_attendance_with_charge_leave_one_status_and_one_payment()
    {
        var target = await ResolveTargetAsync("_Race");
        if (target is null)
            return;

        var ids = await SeedDropInAsync(target.ConnectionString, "synthetic-t16-race", "t16.race");
        var request = new ClassAttendanceRequest(new ClassAttendanceCharge(500m));

        // A bare Task.WhenAll over two async calls does not force real overlap: each runs synchronously up to its
        // first await. The barrier pins both operations on their own connection inside their own transaction and
        // releases them at the same instant, so both ask for the booking's UPDLOCK together and the second one reads
        // the row the first one committed. Without the lock both could read the annotation as open and write the
        // status and the charge twice.
        using var startGate = new Barrier(participantCount: 2);

        Task<(bool Succeeded, string? Code)> AttendAsync() => Task.Run(async () =>
        {
            await using var raceSession = new DbSession(target.ConnectionString);
            await using var transaction = await raceSession.BeginAsync();
            var service = new ClassAttendanceService(
                new SqlClassAttendanceRepository(raceSession), raceSession, new FakeClock { Now = FixedNow });
            startGate.SignalAndWait();
            var result = await service.MarkAttendedAsync(ids.BusinessA, ids.Booking, request);
            await transaction.CommitAsync();
            return (result.Succeeded, result.Error?.Code);
        });

        var results = await Task.WhenAll(AttendAsync(), AttendAsync());

        // Which one wins depends on the interleaving; the outcome does not: one attendance lands and the other hears
        // that the annotation is already recorded, without writing anything.
        var winner = Assert.Single(results, result => result.Succeeded);
        var loser = Assert.Single(results, result => !result.Succeeded);
        Assert.Null(winner.Code);
        Assert.Equal("class_attendance_already_attended", loser.Code);

        var booking = await ReadBookingAsync(target.ConnectionString, ids.Booking);
        Assert.Equal(BookingStatus.Attended, booking.Status);
        Assert.Equal(FixedNow, booking.AttendanceRecordedAt);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BookingId = @bookingId", ("@bookingId", ids.Booking)));
        Assert.Equal(500m, await ReadScalarAsync<decimal>(target.ConnectionString,
            "SELECT Amount FROM Payments WHERE BookingId = @bookingId", ("@bookingId", ids.Booking)));

        output.WriteLine("Race assertions passed: two concurrent attendances with a charge ended with exactly one status write and exactly one payment row for the annotation.");
    }

    [Fact]
    public async Task A_concurrent_suspension_waits_for_the_class_session_lock_the_attendance_holds()
    {
        var target = await ResolveTargetAsync("_Suspension");
        if (target is null)
            return;

        var ids = await SeedDropInAsync(target.ConnectionString, "synthetic-t16-suspension", "t16.suspension");

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassAttendanceRepository(session);

        // The attendance locks the class session row first and writes the observation, and stays open: the suspension
        // that arrives now is exactly the interleaving the composition report named, a suspension between the note and
        // the guarded status write.
        await using var transaction = await session.BeginAsync();
        Assert.NotNull(await repository.LockAsync(ids.BusinessA, ids.Booking));
        Assert.True(await repository.InsertNoteAsync(
            ids.BusinessA, ids.Client, ids.Session, "Alergia al amoníaco", FixedNow));

        // A second connection runs the write a suspension of a class will issue (RF-10): it asks for the Appointments
        // row this unit already holds, so it cannot land. The lock timeout is the runtime proof that the session lock,
        // and not a rule evaluated once, is what keeps such a writer out of the window.
        var suspensionLanded = await SuspensionLandedAsync(target.ConnectionString, ids.BusinessA, ids.Session);
        Assert.False(suspensionLanded, "The suspension landed while the attendance unit held the class session lock.");

        // With the suspension held out, the guarded status write changes the row and the observation stays with it.
        Assert.True(await repository.SetStatusAsync(
            ids.BusinessA, ids.Booking, BookingStatus.Attended, FixedNow));
        await transaction.CommitAsync();

        var booking = await ReadBookingAsync(target.ConnectionString, ids.Booking);
        Assert.Equal(BookingStatus.Attended, booking.Status);
        Assert.Equal(FixedNow, booking.AttendanceRecordedAt);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM ClientNotes WHERE ClientId = @clientId AND AppointmentId = @appointmentId AND Text = @text",
            ("@clientId", ids.Client), ("@appointmentId", ids.Session), ("@text", "Alergia al amoníaco")));
        Assert.Equal(1, await ReadScalarAsync<byte>(target.ConnectionString,
            "SELECT Status FROM Appointments WHERE Id = @id", ("@id", ids.Session)));

        output.WriteLine("Suspension assertions passed: the competing suspension could not land while the attendance unit held the class session lock, the attendance and its observation committed together, and the class was still Scheduled afterwards.");
    }

    [Fact]
    public async Task Walk_in_registration_stores_the_drop_in_attendance_the_class_charge_and_no_notice()
    {
        var target = await ResolveTargetAsync("_WalkIn");
        if (target is null)
            return;

        var ids = await SeedWalkInAsync(target.ConnectionString, "synthetic-t16-walkin", "t16.walkin");

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateWalkInService(session, WalkInNow);

        var result = await service.RegisterAsync(ids.BusinessA, new ClassWalkInRequest(
            ids.Session, ids.Clients[0], new ClassAttendanceCharge(450m, "Clase suelta con descuento"),
            "Alergia al amoníaco"));

        Assert.True(result.Succeeded, result.Error?.Code);

        // The answer names the annotation this unit wrote and the start of the class it joined.
        Assert.Equal(ids.Session, result.Value.AppointmentId);
        Assert.Equal(ids.Clients[0], result.Value.ClientId);
        Assert.Equal(ClassStartsAt, result.Value.StartsAt);
        Assert.Equal(450m, result.Value.ChargedAmount);

        // The annotation is the `Suelta` of the public path, already attended at the single instant of the call. The
        // acceptance at 18:10 proves the locked read carried the stored EndsAt: a projection that missed the column
        // would leave the default end and put the arrival outside the window.
        var booking = await ReadBookingAsync(target.ConnectionString, result.Value.BookingId);
        Assert.Equal(ids.BusinessA, booking.BusinessId);
        Assert.Equal(ids.Session, booking.AppointmentId);
        Assert.Equal(ids.Clients[0], booking.ClientId);
        Assert.Null(booking.EnrollmentId);
        Assert.Equal(BookingModality.DropIn, booking.Modality);
        Assert.Equal(BookingStatus.Attended, booking.Status);
        Assert.Equal(WalkInNow, booking.AttendanceRecordedAt);
        Assert.Null(booking.AbsenceSource);
        Assert.Null(booking.CancelledAt);

        // Exactly one charge, of the drop-in concept, tied to the annotation and to no enrollment, with the agreed
        // amount beside the class list price and the same instant as the attendance it accompanied.
        var payment = await ReadSinglePaymentAsync(target.ConnectionString, result.Value.BookingId);
        Assert.Equal(ids.BusinessA, payment.BusinessId);
        Assert.Equal(ids.Clients[0], payment.ClientId);
        Assert.Equal(PaymentConcept.DropInClass, payment.Concept);
        Assert.Equal(result.Value.BookingId, payment.BookingId);
        Assert.Null(payment.EnrollmentId);
        Assert.Equal(450m, payment.Amount);
        Assert.Equal(100m, payment.ListPrice);
        Assert.Equal("Clase suelta con descuento", payment.PriceReason);
        Assert.Equal(WalkInNow, payment.PaidAt);
        Assert.Equal(WalkInNow, payment.RecordedAt);

        // The observation landed on the client's record tied to the session, and the class is still scheduled.
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM ClientNotes WHERE ClientId = @clientId AND AppointmentId = @appointmentId AND Text = @text AND CreatedAt = @now",
            ("@clientId", ids.Clients[0]), ("@appointmentId", ids.Session), ("@text", "Alergia al amoníaco"),
            ("@now", WalkInNow)));
        Assert.Equal((byte)AppointmentStatus.Scheduled, await ReadScalarAsync<byte>(target.ConnectionString,
            "SELECT Status FROM Appointments WHERE Id = @id", ("@id", ids.Session)));

        // D-07, RF-11 output: the desk attention queues no notice at all, and it is the only annotation written.
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));

        output.WriteLine("Walk-in assertions passed: one Suelta annotation attended at the arrival instant with a single DropInClass charge and the observation attached to the session, and zero notifications queued.");
    }

    [Fact]
    public async Task Walk_in_window_ends_with_the_stored_end_of_the_class()
    {
        var target = await ResolveTargetAsync("_WalkInWindow");
        if (target is null)
            return;

        var ids = await SeedWalkInAsync(target.ConnectionString, "synthetic-t16-walkin-window", "t16.walkin.window",
            clientCount: 2);

        // The exact end is included: the client who arrives as the class ends is late, not early.
        int accepted;
        await using (var session = new DbSession(target.ConnectionString))
        {
            var atEnd = await CreateWalkInService(session, ClassStartsAt.AddHours(1)).RegisterAsync(
                ids.BusinessA, new ClassWalkInRequest(ids.Session, ids.Clients[0]));
            Assert.True(atEnd.Succeeded, atEnd.Error?.Code);
            accepted = atEnd.Value.BookingId;
        }

        // One second later the class is over, and the second client is refused without writing anything.
        await using (var session = new DbSession(target.ConnectionString))
        {
            var afterEnd = await CreateWalkInService(session, ClassStartsAt.AddHours(1).AddSeconds(1)).RegisterAsync(
                ids.BusinessA, new ClassWalkInRequest(ids.Session, ids.Clients[1]));
            Assert.False(afterEnd.Succeeded);
            Assert.Equal("class_walk_in_outside_window", afterEnd.Error!.Code);
        }

        Assert.Equal(BookingStatus.Attended, (await ReadBookingAsync(target.ConnectionString, accepted)).Status);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));

        output.WriteLine("Walk-in window assertions passed: the arrival at the exact stored end of the class was accepted and marked attended, and the arrival one second later was refused as outside the window with nothing written.");
    }

    [Fact]
    public async Task Walk_in_rejection_rolls_back_the_annotation_the_guarded_insert_already_wrote()
    {
        var target = await ResolveTargetAsync("_WalkInRollback");
        if (target is null)
            return;

        var ids = await SeedWalkInAsync(target.ConnectionString, "synthetic-t16-walkin-rollback",
            "t16.walkin.rollback");

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateWalkInService(session, WalkInNow,
            new NoteRefusingRepository(new SqlClassAttendanceRepository(session)));

        var result = await service.RegisterAsync(ids.BusinessA, new ClassWalkInRequest(
            ids.Session, ids.Clients[0], new ClassAttendanceCharge(450m), "Alergia al amoníaco"));

        // The stable code proves the guarded insert had landed and the attendance lock had resolved the very row it
        // wrote: a lost guard or an unresolvable annotation answers class_walk_in_full or class_walk_in_unavailable
        // instead. The refusal is the observation, the only write of the unit that can fail once the lock holds.
        Assert.False(result.Succeeded);
        Assert.Equal("class_walk_in_note_failed", result.Error!.Code);

        // The operation owned its transaction and returned before the commit, so the rollback erased the annotation
        // the insert had already written together with the status write and the charge that never happened.
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND BusinessId = @businessId",
            ("@appointmentId", ids.Session), ("@businessId", ids.BusinessA)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM ClientNotes WHERE AppointmentId = @appointmentId",
            ("@appointmentId", ids.Session)));

        output.WriteLine("Walk-in rollback assertions passed: the refused observation erased the annotation the guarded insert had written, with no payment and no note left behind.");
    }

    [Fact]
    public async Task Walk_in_protects_the_last_place_and_lands_once_the_cancelled_booking_frees_it()
    {
        var target = await ResolveTargetAsync("_WalkInSeat");
        if (target is null)
            return;

        var ids = await SeedWalkInAsync(target.ConnectionString, "synthetic-t16-walkin-seat", "t16.walkin.seat",
            clientCount: 2, maxCapacity: 1);

        // The only place is taken by another client's pending annotation.
        var taken = await InsertBookingRowAsync(target.ConnectionString, ids.BusinessA, ids.Session,
            ids.Clients[1], BookingModality.DropIn, BookingStatus.Pending, WalkInNow);

        // The guarded insert refuses to write the second annotation even when it is called directly, so the last
        // place does not depend on the caller having counted it first.
        await using (var session = new DbSession(target.ConnectionString))
        {
            var repository = new SqlClassDropInRepository(session);
            await using var transaction = await session.BeginAsync();
            var denied = await repository.TryInsertDropInAsync(
                new DropInInsert(ids.BusinessA, ids.Session, ids.Clients[0], WalkInNow));
            Assert.Equal(DropInInsertResultType.NoPlaceLeft, denied.Result);
            await transaction.CommitAsync();
        }

        // The walk-in answers full before touching the insert, and nothing was written.
        await using (var session = new DbSession(target.ConnectionString))
        {
            var full = await CreateWalkInService(session, WalkInNow).RegisterAsync(
                ids.BusinessA, new ClassWalkInRequest(ids.Session, ids.Clients[0], new ClassAttendanceCharge(450m)));
            Assert.False(full.Succeeded);
            Assert.Equal("class_walk_in_full", full.Error!.Code);
        }

        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));

        // Cancelling the occupying annotation frees its place, and the same walk-in then lands on it.
        await ExecuteAsync(target.ConnectionString,
            "UPDATE Bookings SET Status = @cancelled, CancelledAt = @now, CancelledBy = @owner WHERE Id = @id",
            ("@cancelled", BookingStatus.Cancelled), ("@now", WalkInNow), ("@owner", CancelledBy.Owner),
            ("@id", taken));

        int landed;
        await using (var session = new DbSession(target.ConnectionString))
        {
            var service = CreateWalkInService(session, WalkInNow);
            var walkIn = await service.RegisterAsync(ids.BusinessA, new ClassWalkInRequest(
                ids.Session, ids.Clients[0], new ClassAttendanceCharge(450m)));
            Assert.True(walkIn.Succeeded, walkIn.Error?.Code);
            landed = walkIn.Value.BookingId;
        }

        Assert.Equal(BookingStatus.Attended, (await ReadBookingAsync(target.ConnectionString, landed)).Status);
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND Status <> @cancelled",
            ("@appointmentId", ids.Session), ("@cancelled", BookingStatus.Cancelled)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));

        output.WriteLine("Walk-in seat assertions passed: the guarded insert and the service both refused the full class, the cancelled annotation freed the only place, and the arrival then landed as the single non-cancelled annotation with one charge.");
    }

    [Fact]
    public async Task Walk_in_scope_is_the_named_business_for_the_class_the_client_and_the_setting()
    {
        var target = await ResolveTargetAsync("_WalkInScope");
        if (target is null)
            return;

        // Two tenants that both admit attention without a booking.
        var mine = await SeedWalkInAsync(target.ConnectionString, "synthetic-t16-walkin-scope-a",
            "t16.walkin.scope.a");
        var foreign = await SeedWalkInAsync(target.ConnectionString, "synthetic-t16-walkin-scope-b",
            "t16.walkin.scope.b");

        // The class of the other business is invisible, and its client is as unknown as a missing one.
        await using (var session = new DbSession(target.ConnectionString))
        {
            var service = CreateWalkInService(session, WalkInNow);
            var foreignClass = await service.RegisterAsync(
                mine.BusinessA, new ClassWalkInRequest(foreign.Session, mine.Clients[0]));
            Assert.False(foreignClass.Succeeded);
            Assert.Equal("class_walk_in_not_found", foreignClass.Error!.Code);

            var foreignClient = await service.RegisterAsync(
                mine.BusinessA, new ClassWalkInRequest(mine.Session, foreign.Clients[0]));
            Assert.False(foreignClient.Succeeded);
            Assert.Equal("class_walk_in_client_not_found", foreignClient.Error!.Code);
        }

        // Regla 15: a business that does not admit the attention answers before the class and the seat are judged.
        await ExecuteAsync(target.ConnectionString,
            "UPDATE Businesses SET AllowsWalkIns = 0 WHERE Id = @id", ("@id", mine.BusinessA));
        await using (var session = new DbSession(target.ConnectionString))
        {
            var disabled = await CreateWalkInService(session, WalkInNow).RegisterAsync(
                mine.BusinessA, new ClassWalkInRequest(mine.Session, mine.Clients[0]));
            Assert.False(disabled.Succeeded);
            Assert.Equal("class_walk_in_disabled", disabled.Error!.Code);
        }

        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId IN (@mine, @foreign)",
            ("@mine", mine.BusinessA), ("@foreign", foreign.BusinessA)));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Payments"));

        output.WriteLine("Walk-in scope assertions passed: the foreign class and the foreign client were not found, a business with the setting off refused the arrival, and nothing was written in either tenant.");
    }

    /// <summary>
    /// The at-arrival walk-in service over the real SQL ports of one connection: the drop-in repository, the business
    /// repository that carries <c>AllowsWalkIns</c> and — replaceable so a test can force the one refusal the lock
    /// does not rule out — the attendance repository, all inside the unit of work the operation owns.
    /// </summary>
    private static ClassWalkInService CreateWalkInService(
        DbSession session, DateTime now, IClassAttendanceRepository? attendance = null) =>
        new(
            new SqlClassDropInRepository(session),
            attendance ?? new SqlClassAttendanceRepository(session),
            new SqlBusinessRepository(session),
            session,
            new FakeClock { Now = now });

    /// <summary>
    /// One tenant that admits attention without a booking, its clients and one scheduled class able to receive the
    /// arrival: the class starts at <see cref="ClassStartsAt"/> and lasts an hour, so <see cref="WalkInNow"/> falls
    /// inside <c>[StartsAt, EndsAt]</c>. More than one client is seeded when a test needs repeated arrivals.
    /// </summary>
    private static async Task<WalkInIds> SeedWalkInAsync(
        string connectionString, string slug, string emailPrefix,
        int clientCount = 1, int maxCapacity = 8, bool allowsWalkIns = true)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var tenant = await InsertTenantAsync(connection, transaction, slug, slug, "20201616175", maxCapacity,
            WalkInNow, allowsWalkIns);
        var clients = new List<int>();
        for (var index = 0; index < clientCount; index++)
        {
            clients.Add(await InsertClientAsync(connection, transaction, tenant.BusinessId, "Sin turno",
                $"Alumna {index}", $"{emailPrefix}{index}@example.invalid", WalkInNow));
        }

        var session = await InsertClassSessionAsync(connection, transaction, tenant.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenant.BusinessId, tenant.ServiceId, tenant.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenant.ServiceId, tenant.WorkerId, ClassStartsAt, 60, maxCapacity, AppointmentStatus.Scheduled, null,
            WalkInNow);

        await transaction.CommitAsync();
        return new WalkInIds(tenant.BusinessId, tenant.ServiceId, tenant.WorkerId, session, clients);
    }

    /// <summary>One annotation written outside a seed, so a test can occupy a place directly in storage.</summary>
    private static async Task<int> InsertBookingRowAsync(
        string connectionString, int businessId, int appointmentId, int clientId, BookingModality? modality,
        BookingStatus status, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var bookingId = await InsertBookingAsync(connection, transaction, businessId, appointmentId, clientId,
            null, modality, status, now);
        await transaction.CommitAsync();
        return bookingId;
    }

    /// <summary>
    /// Runs a suspension write of a class session from its own connection and reports whether it landed. It asks for
    /// the <c>Appointments</c> row the attendance unit holds, so a lock timeout means the suspension was serialized
    /// behind that unit instead of slipping in; a landed write means the row was free and the lock is not held. Every
    /// parameter of the statement is bound here, exactly as the repository binds its own SQL, so the only outcome the
    /// statement can report is the lock: a statement that cannot run at all would throw instead of answering.
    /// </summary>
    private static async Task<bool> SuspensionLandedAsync(string connectionString, int businessId, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SET LOCK_TIMEOUT 3000; UPDATE Appointments SET Status = @suspended, SuspendedAt = @now, SuspensionReason = @reason WHERE Id = @id AND BusinessId = @businessId AND Kind = @classSession",
            connection);
        AddParameter(command, "@suspended", AppointmentStatus.Suspended);
        AddParameter(command, "@now", FixedNow.AddMinutes(5));
        AddParameter(command, "@reason", "Feriado nacional");
        AddParameter(command, "@id", appointmentId);
        AddParameter(command, "@businessId", businessId);
        AddParameter(command, "@classSession", AppointmentKind.ClassSession);

        try
        {
            await command.ExecuteNonQueryAsync();
            return true;
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            return false;
        }
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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T16_SQL_CONNECTION and
        // the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT16Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT16Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T16 SQL integration test",
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

    /// <summary>The minimum the shared seeds need: one tenant with its class service and its teacher.</summary>
    private sealed record Tenant(int BusinessId, int ServiceId, int WorkerId);

    private static async Task<Tenant> InsertTenantAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string slug, string cuil,
        int maxCapacity, DateTime now, bool allowsWalkIns = true)
    {
        var businessId = await InsertBusinessAsync(connection, transaction, name, slug, now, allowsWalkIns);
        var serviceId = await InsertServiceAsync(connection, transaction, businessId, $"{name} Class", 60, maxCapacity);
        var workerId = await InsertWorkerAsync(connection, transaction, businessId, name, "Teacher", cuil);
        return new Tenant(businessId, serviceId, workerId);
    }

    /// <summary>
    /// One tenant with one scheduled class and one pending drop-in annotation: the shared shape of the attendance,
    /// absence, rollback and race tests.
    /// </summary>
    private static async Task<DropInIds> SeedDropInAsync(string connectionString, string slug, string emailPrefix)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var tenant = await InsertTenantAsync(connection, transaction, slug, slug, "20201616170", 8, FixedNow);
        var client = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Suelta", "Alumna",
            $"{emailPrefix}@example.invalid", FixedNow);
        var session = await InsertClassSessionAsync(connection, transaction, tenant.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenant.BusinessId, tenant.ServiceId, tenant.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenant.ServiceId, tenant.WorkerId, ClassStartsAt, 60, 8, AppointmentStatus.Scheduled, null, FixedNow);
        var booking = await InsertBookingAsync(connection, transaction, tenant.BusinessId, session, client,
            null, BookingModality.DropIn, BookingStatus.Pending, FixedNow);

        await transaction.CommitAsync();
        return new DropInIds(tenant.BusinessId, tenant.ServiceId, tenant.WorkerId, client, session, booking);
    }

    /// <summary>One tenant, one class and the five annotations the guarded statements must refuse or record.</summary>
    private static async Task<GuardIds> SeedGuardsAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var tenant = await InsertTenantAsync(connection, transaction, "synthetic-t16-guards", "synthetic-t16-guards",
            "20201616171", 8, now);
        var clients = new List<int>();
        for (var index = 0; index < 5; index++)
        {
            clients.Add(await InsertClientAsync(connection, transaction, tenant.BusinessId, "Guardia", $"Alumna {index}",
                $"t16.guards.client{index}@example.invalid", now));
        }

        var session = await InsertClassSessionAsync(connection, transaction, tenant.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenant.BusinessId, tenant.ServiceId, tenant.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenant.ServiceId, tenant.WorkerId, ClassStartsAt, 60, 8, AppointmentStatus.Scheduled, null, now);

        // The open drop-in the first attendance is recorded on, an annotation already cancelled, and a class
        // annotation that lost its modality: only the first one admits a status.
        var dropIn = await InsertBookingAsync(connection, transaction, tenant.BusinessId, session, clients[0],
            null, BookingModality.DropIn, BookingStatus.Pending, now);
        var cancelled = await InsertBookingAsync(connection, transaction, tenant.BusinessId, session, clients[1],
            null, BookingModality.DropIn, BookingStatus.Cancelled, now);
        var noModality = await InsertBookingAsync(connection, transaction, tenant.BusinessId, session, clients[2],
            null, null, BookingStatus.Pending, now);

        // An individual turn whose booking still carries a modality: the kind discriminant alone refuses it.
        var individualAppointment = await InsertIndividualAppointmentAsync(connection, transaction, tenant.BusinessId,
            tenant.ServiceId, tenant.WorkerId, new DateTime(2026, 3, 12, 9, 0, 0), 60, now);
        var individual = await InsertBookingAsync(connection, transaction, tenant.BusinessId, individualAppointment,
            clients[3], null, BookingModality.DropIn, BookingStatus.Pending, now);

        // A suspended class (RF-10) with its reason and a pending drop-in annotation.
        var suspendedSession = await InsertClassSessionAsync(connection, transaction, tenant.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenant.BusinessId, tenant.ServiceId, tenant.WorkerId,
                DayOfWeek.Saturday, new TimeOnly(10, 0)),
            tenant.ServiceId, tenant.WorkerId, new DateTime(2026, 3, 14, 10, 0, 0), 60, 8,
            AppointmentStatus.Suspended, "Feriado nacional", now);
        var suspended = await InsertBookingAsync(connection, transaction, tenant.BusinessId, suspendedSession,
            clients[4], null, BookingModality.DropIn, BookingStatus.Pending, now);

        await transaction.CommitAsync();
        return new GuardIds(tenant.BusinessId, session, dropIn, cancelled, noModality, individual, suspended);
    }

    /// <summary>One class and the four coverage inputs: current, expired, drop-in and pending payment with coverage.</summary>
    private static async Task<CoverageIds> SeedCoverageAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var tenant = await InsertTenantAsync(connection, transaction, "synthetic-t16-coverage", "synthetic-t16-coverage",
            "20201616172", 8, now);
        var currentClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Vigente", "Alumna",
            "t16.coverage.current@example.invalid", now);
        var expiredClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Vencida", "Alumna",
            "t16.coverage.expired@example.invalid", now);
        var dropInClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Suelta", "Alumna",
            "t16.coverage.dropin@example.invalid", now);
        var pendingClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Pendiente", "Alumna",
            "t16.coverage.pending@example.invalid", now);

        var session = await InsertClassSessionAsync(connection, transaction, tenant.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenant.BusinessId, tenant.ServiceId, tenant.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenant.ServiceId, tenant.WorkerId, ClassStartsAt, 60, 8, AppointmentStatus.Scheduled, null, now);

        // The covering and the expired enrollments are active: only the stored period separates them from the class
        // date. The pending-payment one keeps a covering period too, so its refusal comes from the enrollment rule.
        var currentEnrollment = await InsertEnrollmentAsync(connection, transaction, tenant.BusinessId, currentClient,
            EnrollmentStatus.Active, now);
        var expiredEnrollment = await InsertEnrollmentAsync(connection, transaction, tenant.BusinessId, expiredClient,
            EnrollmentStatus.Active, now);
        var pendingEnrollment = await InsertEnrollmentAsync(connection, transaction, tenant.BusinessId, pendingClient,
            EnrollmentStatus.PendingPayment, now);
        await InsertMembershipAsync(connection, transaction, tenant.BusinessId, currentEnrollment,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), now);
        await InsertMembershipAsync(connection, transaction, tenant.BusinessId, expiredEnrollment,
            new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), now);
        await InsertMembershipAsync(connection, transaction, tenant.BusinessId, pendingEnrollment,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), now);

        var fixedCurrent = await InsertBookingAsync(connection, transaction, tenant.BusinessId, session, currentClient,
            currentEnrollment, BookingModality.Fixed, BookingStatus.Pending, now);
        var fixedExpired = await InsertBookingAsync(connection, transaction, tenant.BusinessId, session, expiredClient,
            expiredEnrollment, BookingModality.Fixed, BookingStatus.Pending, now);
        var dropIn = await InsertBookingAsync(connection, transaction, tenant.BusinessId, session, dropInClient,
            null, BookingModality.DropIn, BookingStatus.Pending, now);
        var fixedPending = await InsertBookingAsync(connection, transaction, tenant.BusinessId, session, pendingClient,
            pendingEnrollment, BookingModality.Fixed, BookingStatus.Pending, now);

        await transaction.CommitAsync();
        return new CoverageIds(tenant.BusinessId, fixedCurrent, fixedExpired, dropIn, fixedPending);
    }

    /// <summary>One drop-in annotation that already carries a charge, so an annulment can be added to it.</summary>
    private static async Task<AnnulmentIds> SeedAnnulmentAsync(string connectionString, DateTime now)
    {
        var ids = await SeedDropInAsync(connectionString, "synthetic-t16-annulment", "t16.annulment");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var payment = await InsertPaymentAsync(connection, transaction, ids.BusinessA, ids.Client,
            PaymentConcept.DropInClass, ids.Booking, null, 500m, 100m, null, now, now);
        await transaction.CommitAsync();

        return new AnnulmentIds(ids.BusinessA, ids.Client, ids.Booking, payment);
    }

    /// <summary>Two tenants, each with its own class, plus one annotation of the first one.</summary>
    private static async Task<TenantIds> SeedTenantAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var tenantA = await InsertTenantAsync(connection, transaction, "synthetic-t16-tenant-a", "synthetic-t16-tenant-a",
            "20201616173", 8, now);
        var tenantB = await InsertTenantAsync(connection, transaction, "synthetic-t16-tenant-b", "synthetic-t16-tenant-b",
            "20201616174", 8, now);
        var client = await InsertClientAsync(connection, transaction, tenantA.BusinessId, "Propia", "Alumna",
            "t16.tenant.client@example.invalid", now);
        var foreignClient = await InsertClientAsync(connection, transaction, tenantB.BusinessId, "Ajena", "Alumna",
            "t16.tenant.foreign@example.invalid", now);

        var session = await InsertClassSessionAsync(connection, transaction, tenantA.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenantA.BusinessId, tenantA.ServiceId, tenantA.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenantA.ServiceId, tenantA.WorkerId, ClassStartsAt, 60, 8, AppointmentStatus.Scheduled, null, now);
        var foreignSession = await InsertClassSessionAsync(connection, transaction, tenantB.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenantB.BusinessId, tenantB.ServiceId, tenantB.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenantB.ServiceId, tenantB.WorkerId, ClassStartsAt, 60, 8, AppointmentStatus.Scheduled, null, now);
        var booking = await InsertBookingAsync(connection, transaction, tenantA.BusinessId, session, client,
            null, BookingModality.DropIn, BookingStatus.Pending, now);

        await transaction.CommitAsync();
        return new TenantIds(tenantA.BusinessId, tenantB.BusinessId, client, foreignClient, session, foreignSession,
            booking);
    }

    /// <summary>
    /// Asserts that the failed note left the annotation exactly as it was seeded: still open, no recorded instant and
    /// neither a charge nor an observation in storage.
    /// </summary>
    private static async Task AssertUntouchedAsync(string connectionString, DropInIds ids)
    {
        var booking = await ReadBookingAsync(connectionString, ids.Booking);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Null(booking.AttendanceRecordedAt);
        Assert.Null(booking.AbsenceSource);
        Assert.Equal(0, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM Payments WHERE BookingId = @bookingId", ("@bookingId", ids.Booking)));
        Assert.Equal(0, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM ClientNotes WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));
    }

    private static Task<int> InsertBusinessAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string slug, DateTime now,
        bool allowsWalkIns = true) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @allowsWalkIns, @createdAt)",
            ("@name", name), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@allowsWalkIns", allowsWalkIns), ("@createdAt", now));

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
        AppointmentStatus status, string? suspensionReason, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, SuspensionReason, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, @gridClassId, @startsAt, @endsAt, @capacity, NULL, @status, @suspensionReason, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.ClassSession), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@gridClassId", gridClassId), ("@startsAt", startsAt),
            ("@endsAt", startsAt.AddMinutes(durationMinutes)), ("@capacity", maxCapacity), ("@status", status),
            ("@suspensionReason", suspensionReason), ("@createdAt", now));

    private static Task<int> InsertIndividualAppointmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DateTime startsAt, int durationMinutes, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.Individual), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(durationMinutes)),
            ("@origin", AppointmentOrigin.Reserved), ("@status", AppointmentStatus.Scheduled), ("@createdAt", now));

    private static Task<int> InsertEnrollmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId,
        EnrollmentStatus status, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @createdAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", 1),
            ("@status", status), ("@price", 100.00m), ("@createdAt", now));

    private static async Task<int> InsertMembershipAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int enrollmentId,
        DateOnly coverageStart, DateOnly expiresOn, DateTime now)
    {
        var paymentId = await InsertPaymentAsync(connection, transaction, businessId, null, PaymentConcept.Membership,
            null, enrollmentId, 100.00m, null, null, now, now);
        return await InsertIdAsync(connection, transaction,
            "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) OUTPUT INSERTED.Id VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
            ("@enrollmentId", enrollmentId), ("@coverageStart", coverageStart), ("@expiresOn", expiresOn),
            ("@paymentId", paymentId), ("@createdAt", now));
    }

    private static Task<int> InsertPaymentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int? clientId, PaymentConcept concept,
        int? bookingId, int? enrollmentId, decimal amount, decimal? listPrice, string? priceReason,
        DateTime paidAt, DateTime recordedAt) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @concept, @bookingId, @enrollmentId, @amount, @listPrice, @priceReason, @paidAt, @recordedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@concept", concept), ("@bookingId", bookingId),
            ("@enrollmentId", enrollmentId), ("@amount", amount), ("@listPrice", listPrice),
            ("@priceReason", priceReason), ("@paidAt", paidAt), ("@recordedAt", recordedAt));

    /// <summary>
    /// One booking row with its schema constraints honoured: a fixed annotation carries its enrollment, and a
    /// cancelled one carries its cancellation instant.
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

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, null, sql, parameters);
        await command.ExecuteNonQueryAsync();
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
    /// Reads through the unit of work's own connection and transaction, so a write that is not committed yet is
    /// visible: that is what makes the rollback assertion below about an erasure instead of about a write that never
    /// happened.
    /// </summary>
    private static async Task<T?> ReadScalarInUnitAsync<T>(
        DbSession unit, string sql, params (string Name, object? Value)[] parameters)
    {
        var connection = await unit.GetOpenConnectionAsync();
        await using var command = Create(connection, unit.Transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
            return default;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<BookingSnapshot> ReadBookingAsync(string connectionString, int bookingId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CancelledAt, AttendanceRecordedAt, AbsenceSource FROM Bookings WHERE Id = @id",
            connection);
        AddParameter(command, "@id", bookingId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Booking {bookingId} was not found.");
        return new BookingSnapshot(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : (BookingModality)reader.GetByte(4),
            (BookingStatus)reader.GetByte(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : (AbsenceSource)reader.GetByte(8));
    }

    private static async Task<PaymentSnapshot> ReadSinglePaymentAsync(string connectionString, int bookingId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt FROM Payments WHERE BookingId = @bookingId",
            connection);
        AddParameter(command, "@bookingId", bookingId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Booking {bookingId} has no payment.");
        var payment = new PaymentSnapshot(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            (PaymentConcept)reader.GetByte(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetDecimal(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetDateTime(8),
            reader.GetDateTime(9));
        Assert.False(await reader.ReadAsync(), $"Booking {bookingId} has more than one payment.");
        return payment;
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

    /// <summary>
    /// Wraps the real SQL port and refuses the observation write, which is the only refusal that can realistically
    /// happen once the lock guarantees that the client and the session belong to the business and that the annotation
    /// is open. Every other call reaches SQL, so what the rollback test inspects are real rows.
    /// </summary>
    private sealed class NoteRefusingRepository(IClassAttendanceRepository inner) : IClassAttendanceRepository
    {
        public Task<ClassAttendanceLock?> LockAsync(int businessId, int bookingId, CancellationToken ct = default) =>
            inner.LockAsync(businessId, bookingId, ct);

        public Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default) =>
            inner.HasEffectivePaymentAsync(businessId, bookingId, ct);

        public Task<bool> SetStatusAsync(
            int businessId, int bookingId, BookingStatus status, DateTime now, CancellationToken ct = default) =>
            inner.SetStatusAsync(businessId, bookingId, status, now, ct);

        public Task InsertPaymentAsync(ClassAttendancePayment payment, CancellationToken ct = default) =>
            inner.InsertPaymentAsync(payment, ct);

        public Task<bool> InsertNoteAsync(
            int businessId, int clientId, int appointmentId, string note, DateTime now, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record DropInIds(
        int BusinessA, int ServiceA, int WorkerA, int Client, int Session, int Booking);

    private sealed record GuardIds(
        int BusinessA, int Session, int DropIn, int Cancelled, int NoModality, int Individual, int Suspended);

    private sealed record CoverageIds(
        int BusinessA, int FixedCurrent, int FixedExpired, int DropIn, int FixedPending);

    private sealed record AnnulmentIds(int BusinessA, int Client, int Booking, int Payment);

    private sealed record TenantIds(
        int BusinessA, int BusinessB, int Client, int ForeignClient, int Session, int ForeignSession, int Booking);

    private sealed record WalkInIds(
        int BusinessA, int ServiceA, int WorkerA, int Session, IReadOnlyList<int> Clients);

    private sealed record BookingSnapshot(
        int BusinessId,
        int AppointmentId,
        int ClientId,
        int? EnrollmentId,
        BookingModality? Modality,
        BookingStatus Status,
        DateTime? CancelledAt,
        DateTime? AttendanceRecordedAt,
        AbsenceSource? AbsenceSource);

    private sealed record PaymentSnapshot(
        int BusinessId,
        int? ClientId,
        PaymentConcept Concept,
        int? BookingId,
        int? EnrollmentId,
        decimal Amount,
        decimal? ListPrice,
        string? PriceReason,
        DateTime PaidAt,
        DateTime RecordedAt);
}
