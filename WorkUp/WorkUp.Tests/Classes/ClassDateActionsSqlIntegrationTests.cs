using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Data;
using WorkUp.Data.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Classes;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlClassDateActionsRepository"/> and the service that
/// drives it (RF-10, RF-22, D-15, D-24): cancelling one annotation of a class date and suspending the whole class with
/// a mandatory reason and one deduplicated notice per non-cancelled annotation. Requires a new, dedicated database
/// supplied through WORKUP_T16_SQL_CONNECTION; without it every test returns N/A and no SQL connection is opened. Each
/// test derives its own database name from the supplied one, so the whole class runs in a single command, and nothing
/// is torn down: the target is retained on purpose. The unit tests cannot prove what lives in storage — the released
/// place, the untouched enrollment and payment, the exact outbox rows with their stable keys, the caught duplicate
/// that keeps the unit committable and the suspension erased by a failing notice — so this fixture is the runtime
/// proof of those claims.
/// </summary>
public sealed class ClassDateActionsSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T16_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>The single clock read of every operation: the cancellation, the suspension and every notice instant.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 8, 0, 0);

    /// <summary>The start of every seeded class: the availability rule judges this instant, never the moment of the call.</summary>
    private static readonly DateTime ClassStartsAt = new(2026, 3, 12, 18, 0, 0); // Thursday

    /// <summary>Places of every seeded class session, the value the domain capacity rule is applied against.</summary>
    private const int SessionCapacity = 6;

    [Fact]
    public async Task Cancelling_fixed_drop_in_and_pre_recorded_annotations_releases_each_place_and_leaves_membership_and_payment_untouched()
    {
        var target = await ResolveTargetAsync("_Cancel");
        if (target is null)
            return;

        var ids = await SeedClassAsync(target.ConnectionString, "synthetic-t16-date-cancel", "20201616180");

        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassDateActionsService(
            new SqlClassDateActionsRepository(session), session, new FakeClock { Now = FixedNow });

        // Four non-cancelled annotations occupy four of the six places before the action, counted by the single
        // domain rule the detail screen also uses.
        Assert.Equal(4, ClassCapacity.PlacesTaken(
            SessionCapacity, await ReadBookingStatusesAsync(target.ConnectionString, ids.Session)));

        // The explicit user decision of 2026-09-25: while the class is Programado and has not started, any
        // non-cancelled annotation can be cancelled, fixed or drop-in, including one already recorded as attended or
        // absent, because the status is what stops counting once the annotation is cancelled.
        foreach (var bookingId in new[] { ids.Fixed, ids.DropIn, ids.Attended, ids.Absent })
        {
            var result = await service.CancelBookingAsync(ids.BusinessA, bookingId);
            Assert.True(result.Succeeded, result.Error?.Code);
        }

        // Each annotation is stored cancelled by the owner with the single clock instant, and its modality survives.
        foreach (var bookingId in new[] { ids.Fixed, ids.DropIn, ids.Attended, ids.Absent })
        {
            var booking = await ReadBookingAsync(target.ConnectionString, bookingId);
            Assert.Equal(BookingStatus.Cancelled, booking.Status);
            Assert.Equal(CancelledBy.Owner, booking.CancelledBy);
            Assert.Equal(FixedNow, booking.CancelledAt);
        }

        Assert.Equal(BookingModality.Fixed, (await ReadBookingAsync(target.ConnectionString, ids.Fixed)).Modality);
        Assert.Equal(BookingModality.DropIn, (await ReadBookingAsync(target.ConnectionString, ids.Attended)).Modality);
        // The recorded attendance fact is not erased by the cancellation, only superseded by the cancelled status.
        Assert.Equal(FixedNow, (await ReadBookingAsync(target.ConnectionString, ids.Attended)).AttendanceRecordedAt);
        Assert.Equal(
            AbsenceSource.Owner, (await ReadBookingAsync(target.ConnectionString, ids.Absent)).AbsenceSource);

        // The place of that date is released by the shared capacity rule and by nothing else (RF-22).
        Assert.Equal(0, ClassCapacity.PlacesTaken(
            SessionCapacity, await ReadBookingStatusesAsync(target.ConnectionString, ids.Session)));

        // Saving a date touches neither the enrollment, nor the paid membership period, nor any payment: no refund, no
        // credit and no extra charge is created (RF-22, D-24).
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings AS b WHERE b.AppointmentId = @appointmentId AND b.Modality = @fixed AND b.EnrollmentId = @enrollmentId",
            ("@appointmentId", ids.Session), ("@fixed", BookingModality.Fixed), ("@enrollmentId", ids.Enrollment)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Enrollments WHERE Id = @enrollmentId AND Status = @active",
            ("@enrollmentId", ids.Enrollment), ("@active", EnrollmentStatus.Active)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM MembershipPeriods WHERE Id = @periodId AND EnrollmentId = @enrollmentId AND CoverageStart = @coverageStart AND ExpiresOn = @expiresOn AND PaymentId = @paymentId",
            ("@periodId", ids.Period), ("@enrollmentId", ids.Enrollment), ("@coverageStart", new DateOnly(2026, 3, 1)),
            ("@expiresOn", new DateOnly(2026, 4, 1)), ("@paymentId", ids.MembershipPayment)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BookingId IS NOT NULL"));
        // The only difference in storage between the four annotations is the cancelled status itself.
        Assert.Equal(4, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings AS b WHERE b.AppointmentId = @appointmentId AND b.Status = @cancelled AND b.CancelledBy = @owner AND b.CancelledAt = @now",
            ("@appointmentId", ids.Session), ("@cancelled", BookingStatus.Cancelled),
            ("@owner", CancelledBy.Owner), ("@now", FixedNow)));

        output.WriteLine("Cancellation assertions passed: fixed, drop-in, attended and absent annotations were each stored Cancelled by the owner with the single instant, the four places were released through ClassCapacity, and the enrollment, the membership period and the only payment stayed exactly as they were.");
    }

    [Fact]
    public async Task A_repeated_cancellation_is_refused_and_keeps_the_original_instant_and_author()
    {
        var target = await ResolveTargetAsync("_CancelRepeat");
        if (target is null)
            return;

        var ids = await SeedClassAsync(target.ConnectionString, "synthetic-t16-date-repeat", "20201616181");

        await using var session = new DbSession(target.ConnectionString);
        var clock = new FakeClock { Now = FixedNow };
        var service = new ClassDateActionsService(new SqlClassDateActionsRepository(session), session, clock);

        var first = await service.CancelBookingAsync(ids.BusinessA, ids.DropIn);
        Assert.True(first.Succeeded, first.Error?.Code);
        var recorded = await ReadBookingAsync(target.ConnectionString, ids.DropIn);
        Assert.Equal(FixedNow, recorded.CancelledAt);

        // The clock advanced and a second submission arrives: the annotation is refused and the originally recorded
        // instant and author are never overwritten (the guarded statement repeats that the row is still open).
        clock.Now = FixedNow.AddHours(2);
        var repeat = await service.CancelBookingAsync(ids.BusinessA, ids.DropIn);
        Assert.False(repeat.Succeeded);
        Assert.Equal("class_date_booking_cancelled", repeat.Error!.Code);
        var afterRepeat = await ReadBookingAsync(target.ConnectionString, ids.DropIn);
        Assert.Equal(BookingStatus.Cancelled, afterRepeat.Status);
        Assert.Equal(FixedNow, afterRepeat.CancelledAt);
        Assert.Equal(CancelledBy.Owner, afterRepeat.CancelledBy);

        // A cancellation the client had already recorded is refused the same way and keeps its own author and instant.
        var seededCancellation = await service.CancelBookingAsync(ids.BusinessA, ids.ClientCancelled);
        Assert.False(seededCancellation.Succeeded);
        Assert.Equal("class_date_booking_cancelled", seededCancellation.Error!.Code);
        var clientCancelled = await ReadBookingAsync(target.ConnectionString, ids.ClientCancelled);
        Assert.Equal(BookingStatus.Cancelled, clientCancelled.Status);
        Assert.Equal(CancelledBy.Client, clientCancelled.CancelledBy);
        Assert.Equal(FixedNow.AddDays(-3), clientCancelled.CancelledAt);

        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE Status = @cancelled AND CancelledAt = @now",
            ("@cancelled", BookingStatus.Cancelled), ("@now", FixedNow)));

        output.WriteLine("Repeat cancellation assertions passed: the second submission was refused with class_date_booking_cancelled and the first instant survived a clock that advanced two hours, and the client's own cancellation kept its author and its older instant.");
    }

    [Fact]
    public async Task A_foreign_session_and_annotation_are_invisible_to_cancellation_and_suspension()
    {
        var target = await ResolveTargetAsync("_Isolation");
        if (target is null)
            return;

        var ids = await SeedTenantsAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassDateActionsService(
            new SqlClassDateActionsRepository(session), session, new FakeClock { Now = FixedNow });

        // A booking of the other business is not found, even though the identifier exists, and nothing about its rows
        // leaks through the answer: the empty result is the same answer a missing identifier gets.
        var foreignCancellation = await service.CancelBookingAsync(ids.BusinessA, ids.ForeignBooking);
        Assert.False(foreignCancellation.Succeeded);
        Assert.Equal("class_date_booking_not_found", foreignCancellation.Error!.Code);
        var foreignBooking = await ReadBookingAsync(target.ConnectionString, ids.ForeignBooking);
        Assert.Equal(BookingStatus.Pending, foreignBooking.Status);
        Assert.Null(foreignBooking.CancelledAt);

        var foreignSuspension = await service.SuspendClassAsync(ids.BusinessA, ids.ForeignSession, "Feria del barrio");
        Assert.False(foreignSuspension.Succeeded);
        Assert.Equal("class_date_class_not_found", foreignSuspension.Error!.Code);

        // And the reverse direction: the first business's rows are just as invisible to the second one.
        var reverseCancellation = await service.CancelBookingAsync(ids.BusinessB, ids.Booking);
        Assert.False(reverseCancellation.Succeeded);
        Assert.Equal("class_date_booking_not_found", reverseCancellation.Error!.Code);
        var reverseSuspension = await service.SuspendClassAsync(ids.BusinessB, ids.Session, "Feria del barrio");
        Assert.False(reverseSuspension.Succeeded);
        Assert.Equal("class_date_class_not_found", reverseSuspension.Error!.Code);

        // Nothing at all was written anywhere: neither tenant was cancelled nor suspended and no notice was queued.
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Appointments WHERE Status = @scheduled", ("@scheduled", AppointmentStatus.Scheduled)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Appointments WHERE SuspensionReason IS NOT NULL"));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Bookings WHERE Status = @cancelled",
            ("@cancelled", BookingStatus.Cancelled)));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        var ownSuspension = await service.SuspendClassAsync(ids.BusinessA, ids.Session, "Feria del barrio");
        Assert.True(ownSuspension.Succeeded, ownSuspension.Error?.Code);
        Assert.True((await ReadSessionAsync(target.ConnectionString, ids.Session)).Status == AppointmentStatus.Suspended);

        output.WriteLine("Tenant isolation assertions passed: a session and an annotation of the other business were class_date_booking_not_found and class_date_class_not_found in both directions, no suspension, cancellation or notice was written, and after the refusals the owner's own suspension landed.");
    }

    [Fact]
    public async Task Suspension_writes_only_the_session_and_queues_one_encoded_notice_per_non_cancelled_annotation()
    {
        var target = await ResolveTargetAsync("_Suspend");
        if (target is null)
            return;

        var ids = await SeedClassAsync(target.ConnectionString, "synthetic-t16-date-suspend", "20201616182");

        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassDateActionsService(
            new SqlClassDateActionsRepository(session), session, new FakeClock { Now = FixedNow });

        const string reason = "  Feria <del>barrio</del> & lluvia  ";
        var result = await service.SuspendClassAsync(ids.BusinessA, ids.Session, reason);
        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(ids.Session, result.Value.AppointmentId);
        // The four non-cancelled annotations (fixed, drop-in, attended and absent) each get one notice; the cancelled
        // one gets none because its place was already released.
        Assert.Equal(4, result.Value.NoticesQueued);

        // Suspending writes the session row only: the trimmed reason, the single instant and the status, while the
        // start, the capacity and the grid stay exactly as seeded.
        var suspended = await ReadSessionAsync(target.ConnectionString, ids.Session);
        Assert.Equal(AppointmentStatus.Suspended, suspended.Status);
        Assert.Equal(FixedNow, suspended.SuspendedAt);
        Assert.Equal("Feria <del>barrio</del> & lluvia", suspended.SuspensionReason);
        Assert.Equal(ClassStartsAt, suspended.StartsAt);
        Assert.Equal(SessionCapacity, suspended.MaxCapacity);
        Assert.NotNull(suspended.GridClassId);

        // Every annotation keeps its status, its modality and its enrollment: nobody was marked absent, nobody lost a
        // place and nothing was recovered, charged, refunded or credited (RF-10, Regla 17, D-15, D-24).
        var fixedBooking = await ReadBookingAsync(target.ConnectionString, ids.Fixed);
        Assert.Equal(BookingStatus.Pending, fixedBooking.Status);
        Assert.Equal(BookingModality.Fixed, fixedBooking.Modality);
        Assert.Equal(ids.Enrollment, fixedBooking.EnrollmentId);
        Assert.Null(fixedBooking.CancelledAt);
        var attended = await ReadBookingAsync(target.ConnectionString, ids.Attended);
        Assert.Equal(BookingStatus.Attended, attended.Status);
        Assert.Equal(FixedNow, attended.AttendanceRecordedAt);
        var absent = await ReadBookingAsync(target.ConnectionString, ids.Absent);
        Assert.Equal(BookingStatus.Absent, absent.Status);
        Assert.Equal(AbsenceSource.Owner, absent.AbsenceSource);
        var cancelled = await ReadBookingAsync(target.ConnectionString, ids.ClientCancelled);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Equal(CancelledBy.Client, cancelled.CancelledBy);
        Assert.Equal(FixedNow.AddDays(-3), cancelled.CancelledAt);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM ClientNotes"));

        // One outbox row per non-cancelled annotation, with the stable key of the SRS error handling, the client's
        // stored address and the single instant of the operation.
        var notices = await ReadNotificationsAsync(target.ConnectionString);
        Assert.Equal(4, notices.Count);
        Assert.All(notices, notice =>
        {
            Assert.Equal(ids.BusinessA, notice.BusinessId);
            Assert.Equal(NotificationType.ClassSuspended, notice.Type);
            Assert.Equal(NotificationStatus.Pending, notice.Status);
            Assert.Equal(FixedNow, notice.CreatedAt);
            Assert.Equal("Clase suspendida", notice.Subject);
        });
        Assert.Equal(4, notices.Select(notice => notice.DedupKey).Distinct().Count());
        Assert.Contains(notices, notice => notice.DedupKey == $"suspension:appointment:{ids.Session}:booking:{ids.Fixed}");
        Assert.Contains(notices, notice => notice.DedupKey == $"suspension:appointment:{ids.Session}:booking:{ids.DropIn}");
        Assert.Contains(notices, notice => notice.DedupKey == $"suspension:appointment:{ids.Session}:booking:{ids.Attended}");
        Assert.Contains(notices, notice => notice.DedupKey == $"suspension:appointment:{ids.Session}:booking:{ids.Absent}");
        Assert.DoesNotContain(notices, notice =>
            notice.DedupKey == $"suspension:appointment:{ids.Session}:booking:{ids.ClientCancelled}");

        // The body carries the reason and the class date and time HTML-encoded, and no management link at all: a class
        // has no private link (RF-23).
        var fixedNotice = notices.Single(notice => notice.DedupKey.EndsWith($":booking:{ids.Fixed}"
            , StringComparison.Ordinal));
        Assert.Equal("ana.fija@example.invalid", fixedNotice.Recipient);
        Assert.Contains("Ana &lt;b&gt;", fixedNotice.BodyHtml);
        Assert.Contains("Feria &lt;del&gt;barrio&lt;/del&gt; &amp; lluvia", fixedNotice.BodyHtml);
        Assert.Contains("12 de marzo", fixedNotice.BodyHtml);
        Assert.Contains("a las 18:00", fixedNotice.BodyHtml);
        Assert.DoesNotContain("<del>", fixedNotice.BodyHtml);
        Assert.DoesNotContain("http", fixedNotice.BodyHtml);
        Assert.DoesNotContain("/t/", fixedNotice.BodyHtml);

        // A second submission is refused before any notice is composed, so the count stays exactly at four.
        var repeat = await service.SuspendClassAsync(ids.BusinessA, ids.Session, "Otra razón");
        Assert.False(repeat.Succeeded);
        Assert.Equal("class_date_not_available", repeat.Error!.Code);
        Assert.Equal(4, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Suspension assertions passed: only the session row changed (Suspendido, the trimmed reason and the instant), every annotation kept its status, modality and enrollment, four notices were queued with the exact suspension:appointment:<id>:booking:<id> keys and the encoded reason in the body, the cancelled annotation got none, and a repeat submission emitted nothing.");
    }

    [Fact]
    public async Task A_notice_already_stored_for_the_same_student_and_class_is_a_dedup_hit_that_keeps_the_unit_committable()
    {
        var target = await ResolveTargetAsync("_Dedup");
        if (target is null)
            return;

        var ids = await SeedClassAsync(target.ConnectionString, "synthetic-t16-date-dedup", "20201616183");

        // A technical retry already left the notice of the fixed student for this class: the stable key must stop a
        // second row instead of failing the unit.
        var duplicateKey = $"suspension:appointment:{ids.Session}:booking:{ids.Fixed}";
        await InsertNotificationAsync(target.ConnectionString, ids.BusinessA, "ana.fija@example.invalid",
            duplicateKey, FixedNow.AddMinutes(-5));

        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassDateActionsService(
            new SqlClassDateActionsRepository(session), session, new FakeClock { Now = FixedNow });

        var result = await service.SuspendClassAsync(ids.BusinessA, ids.Session, "Feria del barrio");

        // The caught duplicate never fails the operation: the suspension committed and only the new rows were counted.
        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(3, result.Value.NoticesQueued);
        var suspended = await ReadSessionAsync(target.ConnectionString, ids.Session);
        Assert.Equal(AppointmentStatus.Suspended, suspended.Status);
        Assert.Equal(FixedNow, suspended.SuspendedAt);

        // Storage holds the pre-seeded row plus the three that landed, and the duplicated key exactly once.
        Assert.Equal(4, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE DedupKey = @dedupKey", ("@dedupKey", duplicateKey)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE DedupKey = @dedupKey",
            ("@dedupKey", $"suspension:appointment:{ids.Session}:booking:{ids.DropIn}")));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE DedupKey = @dedupKey",
            ("@dedupKey", $"suspension:appointment:{ids.Session}:booking:{ids.Attended}")));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE DedupKey = @dedupKey",
            ("@dedupKey", $"suspension:appointment:{ids.Session}:booking:{ids.Absent}")));

        output.WriteLine("Dedup assertions passed: the pre-seeded key did not fail the unit, NoticesQueued counted only the three new rows, the suspension committed, and the duplicated key exists exactly once.");
    }

    [Fact]
    public async Task A_late_notification_write_that_refuses_rolls_the_suspension_back()
    {
        var target = await ResolveTargetAsync("_Rollback");
        if (target is null)
            return;

        var ids = await SeedClassAsync(target.ConnectionString, "synthetic-t16-date-rollback", "20201616184");

        await using var session = new DbSession(target.ConnectionString);
        var real = new SqlClassDateActionsRepository(session);

        // The refusal happens after the suspension write, so the rollback erases a real write instead of a write that
        // never happened: the hook reads the session through the unit's own connection and transaction.
        var observedInUnit = AppointmentStatus.Scheduled;
        var refusing = new NoticeRefusingRepository(real, async () =>
        {
            var status = await ReadScalarInUnitAsync<byte?>(
                session, "SELECT Status FROM Appointments WHERE Id = @appointmentId",
                ("@appointmentId", ids.Session));
            observedInUnit = (AppointmentStatus)status!.Value;
        });
        var service = new ClassDateActionsService(refusing, session, new FakeClock { Now = FixedNow });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SuspendClassAsync(ids.BusinessA, ids.Session, "Feria del barrio"));

        // Inside the unit the session was already suspended, so what the rollback erased was a stored suspension.
        Assert.Equal(AppointmentStatus.Suspended, observedInUnit);

        // Once the unit ended, storage shows the seeded state: programada, no reason, no instant and no notice.
        var persisted = await ReadSessionAsync(target.ConnectionString, ids.Session);
        Assert.Equal(AppointmentStatus.Scheduled, persisted.Status);
        Assert.Null(persisted.SuspendedAt);
        Assert.Null(persisted.SuspensionReason);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Rollback assertions passed: the session was already Suspendido inside the unit when the notice write refused, and after the rollback storage kept it Programado with no reason, no instant and no notice at all.");
    }

    [Fact]
    public async Task A_started_or_suspended_class_refuses_both_actions_without_writing_anything()
    {
        var target = await ResolveTargetAsync("_Guards");
        if (target is null)
            return;

        var ids = await SeedGuardsAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        // The exact start already counts as started: from that moment the absence is the only record (RF-22).
        var clock = new FakeClock { Now = ClassStartsAt };
        var service = new ClassDateActionsService(new SqlClassDateActionsRepository(session), session, clock);

        var startedCancellation = await service.CancelBookingAsync(ids.BusinessA, ids.StartedBooking);
        Assert.False(startedCancellation.Succeeded);
        Assert.Equal("class_date_not_available", startedCancellation.Error!.Code);
        var startedSuspension = await service.SuspendClassAsync(ids.BusinessA, ids.StartedSession, "Feria del barrio");
        Assert.False(startedSuspension.Succeeded);
        Assert.Equal("class_date_not_available", startedSuspension.Error!.Code);

        // An already suspended class offers neither action, and the recorded reason and instant are never rewritten.
        var suspendedCancellation = await service.CancelBookingAsync(ids.BusinessA, ids.SuspendedBooking);
        Assert.False(suspendedCancellation.Succeeded);
        Assert.Equal("class_date_not_available", suspendedCancellation.Error!.Code);
        var suspendedSuspension = await service.SuspendClassAsync(ids.BusinessA, ids.SuspendedSession, "Otra razón");
        Assert.False(suspendedSuspension.Succeeded);
        Assert.Equal("class_date_not_available", suspendedSuspension.Error!.Code);

        // One hour after the start the same guard still holds, and the clock never split the judgement from a write.
        clock.Now = ClassStartsAt.AddHours(1);
        var lateCancellation = await service.CancelBookingAsync(ids.BusinessA, ids.StartedBooking);
        Assert.False(lateCancellation.Succeeded);
        Assert.Equal("class_date_not_available", lateCancellation.Error!.Code);
        var lateSuspension = await service.SuspendClassAsync(ids.BusinessA, ids.StartedSession, "Feria del barrio");
        Assert.False(lateSuspension.Succeeded);
        Assert.Equal("class_date_not_available", lateSuspension.Error!.Code);

        // Nothing was written by any of the four refusals.
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.StartedBooking)).Status);
        Assert.Null((await ReadBookingAsync(target.ConnectionString, ids.StartedBooking)).CancelledAt);
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.SuspendedBooking)).Status);
        Assert.Null((await ReadBookingAsync(target.ConnectionString, ids.SuspendedBooking)).CancelledAt);
        var scheduled = await ReadSessionAsync(target.ConnectionString, ids.StartedSession);
        Assert.Equal(AppointmentStatus.Scheduled, scheduled.Status);
        Assert.Null(scheduled.SuspensionReason);
        Assert.Null(scheduled.SuspendedAt);
        var suspended = await ReadSessionAsync(target.ConnectionString, ids.SuspendedSession);
        Assert.Equal(AppointmentStatus.Suspended, suspended.Status);
        Assert.Equal("Feriado nacional", suspended.SuspensionReason);
        Assert.Equal(FixedNow.AddDays(-1), suspended.SuspendedAt);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Guard assertions passed: the started class and the suspended class both refused cancellation and suspension with class_date_not_available, before and one hour after the start, and no annotation, session reason or notice was written.");
    }

    [Fact]
    public async Task The_T16_suspension_waits_for_the_annotation_row_a_T14_drop_in_cancellation_holds()
    {
        var target = await ResolveTargetAsync("_LockOrder");
        if (target is null)
            return;

        var ids = await SeedClassAsync(target.ConnectionString, "synthetic-t16-date-lockorder", "20201616185");

        // T14's drop-in cancellation takes the annotation row inside its own transaction and holds it until it
        // commits. This is the real port and the real lock statement, with the same isolation the service uses.
        await using (var t14 = new DbSession(target.ConnectionString))
        {
            var transaction = await t14.BeginAsync();
            var dropInRepository = new SqlClassDropInRepository(t14);
            var locked = await dropInRepository.LockDropInForCancellationAsync(ids.BusinessA, ids.DropIn);
            Assert.NotNull(locked);

            // The T14 lock covers the annotation row only: the class session row it later joins is still free. What
            // proves it is that an update lock on that session row is granted while the T14 lock is alive.
            Assert.True(await SessionRowUpdateLockLandsAsync(target.ConnectionString, ids.Session));
            // And the annotation row really is held, which is what makes the probe below mean what it says.
            Assert.True(await BookingRowUpdateLockTimesOutAsync(target.ConnectionString, ids.DropIn));

            // T16's suspension locks the class session row first and only then needs the annotations: while T14 holds
            // the annotation row, the suspension cannot complete its own sequence and waits for that row.
            Assert.True(await T16SessionLockTimesOutAsync(target.ConnectionString, ids.BusinessA, ids.Session));

            await transaction.DisposeAsync();
        }

        // This probe observes one direction only: this port's class-first order makes the suspension wait for an
        // annotation row a T14 drop-in cancellation already holds. T14's cancellation is the opposite order — the
        // annotation row first and the session row afterwards — so this observation neither proves a two-writer
        // lock inversion nor rules one out, and it never forces both writers to wait at the same instant: it claims
        // no deadlock. A plain shared read of Appointments inside T14's guarded UPDATE does not wait behind an update
        // lock on that row, because S and U are compatible, which is why no mirror probe is asserted here.
        var booking = await ReadBookingAsync(target.ConnectionString, ids.DropIn);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Null(booking.CancelledAt);
        var classSession = await ReadSessionAsync(target.ConnectionString, ids.Session);
        Assert.Equal(AppointmentStatus.Scheduled, classSession.Status);

        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassDateActionsService(
            new SqlClassDateActionsRepository(session), session, new FakeClock { Now = FixedNow });

        // Once the probe transaction ended, the action lands normally on the row nothing holds anymore.
        var result = await service.CancelBookingAsync(ids.BusinessA, ids.DropIn);
        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(BookingStatus.Cancelled, (await ReadBookingAsync(target.ConnectionString, ids.DropIn)).Status);

        output.WriteLine("Lock-order assertions passed: while the T14 drop-in cancellation held the annotation row, the class session row was still free and lockable but T16's suspension waited for the annotation row. That is the one wait this probe observes; T14 covers annotation then session while this port covers session then annotation, so the probe makes no claim about whether two writers could deadlock.");
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

    /// <summary>
    /// The shared shape of the cancellation, repeat, suspension, dedup and rollback tests: one tenant with one
    /// scheduled class and the five annotations the rules have to tell apart, plus the membership inputs of the fixed
    /// one. The fixed annotation carries an active enrollment and a paid period, so a cancellation that touched the
    /// membership would show up in storage.
    /// </summary>
    private static async Task<ClassIds> SeedClassAsync(string connectionString, string slug, string cuil)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var tenant = await InsertTenantAsync(connection, transaction, slug, slug, cuil, SessionCapacity, FixedNow);
        var fixedClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Ana <b>", "Pérez",
            "ana.fija@example.invalid", FixedNow);
        var dropInClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Suelta", "Alumna",
            "suelta.suelta@example.invalid", FixedNow);
        var attendedClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Atendida", "Alumna",
            "atendida@example.invalid", FixedNow);
        var absentClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Ausente", "Alumna",
            "ausente@example.invalid", FixedNow);
        var cancelledClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Cancelada", "Alumna",
            "cancelada@example.invalid", FixedNow);

        var classSession = await InsertClassSessionAsync(connection, transaction, tenant.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenant.BusinessId, tenant.ServiceId, tenant.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenant.ServiceId, tenant.WorkerId, ClassStartsAt, 60, SessionCapacity,
            AppointmentStatus.Scheduled, null, null, FixedNow);

        var enrollment = await InsertEnrollmentAsync(connection, transaction, tenant.BusinessId, fixedClient,
            EnrollmentStatus.Active, FixedNow);
        var (period, membershipPayment) = await InsertMembershipAsync(connection, transaction, tenant.BusinessId,
            enrollment, new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), FixedNow);

        var fixedBooking = await InsertBookingAsync(connection, transaction, tenant.BusinessId, classSession,
            fixedClient, enrollment, BookingModality.Fixed, BookingStatus.Pending, FixedNow);
        var dropIn = await InsertBookingAsync(connection, transaction, tenant.BusinessId, classSession,
            dropInClient, null, BookingModality.DropIn, BookingStatus.Pending, FixedNow);
        var attended = await InsertBookingAsync(connection, transaction, tenant.BusinessId, classSession,
            attendedClient, null, BookingModality.DropIn, BookingStatus.Attended, FixedNow,
            attendanceRecordedAt: FixedNow);
        var absent = await InsertBookingAsync(connection, transaction, tenant.BusinessId, classSession,
            absentClient, null, BookingModality.DropIn, BookingStatus.Absent, FixedNow,
            attendanceRecordedAt: FixedNow, absenceSource: AbsenceSource.Owner);
        var clientCancelled = await InsertBookingAsync(connection, transaction, tenant.BusinessId, classSession,
            cancelledClient, null, BookingModality.DropIn, BookingStatus.Cancelled, FixedNow.AddDays(-3),
            cancelledAt: FixedNow.AddDays(-3), cancelledBy: CancelledBy.Client);

        await transaction.CommitAsync();
        return new ClassIds(tenant.BusinessId, classSession, fixedBooking, dropIn, attended, absent, clientCancelled,
            enrollment, period, membershipPayment);
    }

    /// <summary>Two tenants, each with its own scheduled class and one drop-in annotation of it.</summary>
    private static async Task<TenantIds> SeedTenantsAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var tenantA = await InsertTenantAsync(connection, transaction, "synthetic-t16-date-tenant-a",
            "synthetic-t16-date-tenant-a", "20201616186", SessionCapacity, now);
        var tenantB = await InsertTenantAsync(connection, transaction, "synthetic-t16-date-tenant-b",
            "synthetic-t16-date-tenant-b", "20201616187", SessionCapacity, now);
        var clientA = await InsertClientAsync(connection, transaction, tenantA.BusinessId, "Propia", "Alumna",
            "t16.date.own@example.invalid", now);
        var clientB = await InsertClientAsync(connection, transaction, tenantB.BusinessId, "Ajena", "Alumna",
            "t16.date.foreign@example.invalid", now);

        var classA = await InsertClassSessionAsync(connection, transaction, tenantA.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenantA.BusinessId, tenantA.ServiceId, tenantA.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenantA.ServiceId, tenantA.WorkerId, ClassStartsAt, 60, SessionCapacity,
            AppointmentStatus.Scheduled, null, null, now);
        var classB = await InsertClassSessionAsync(connection, transaction, tenantB.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenantB.BusinessId, tenantB.ServiceId, tenantB.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenantB.ServiceId, tenantB.WorkerId, ClassStartsAt, 60, SessionCapacity,
            AppointmentStatus.Scheduled, null, null, now);
        var bookingA = await InsertBookingAsync(connection, transaction, tenantA.BusinessId, classA, clientA,
            null, BookingModality.DropIn, BookingStatus.Pending, now);
        var bookingB = await InsertBookingAsync(connection, transaction, tenantB.BusinessId, classB, clientB,
            null, BookingModality.DropIn, BookingStatus.Pending, now);

        await transaction.CommitAsync();
        return new TenantIds(tenantA.BusinessId, tenantB.BusinessId, classA, classB, bookingA, bookingB);
    }

    /// <summary>
    /// One started class and one already suspended class of the same tenant, each with a drop-in annotation: the two
    /// states that close both owner actions.
    /// </summary>
    private static async Task<GuardIds> SeedGuardsAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var tenant = await InsertTenantAsync(connection, transaction, "synthetic-t16-date-guards",
            "synthetic-t16-date-guards", "20201616188", SessionCapacity, now);
        var startedClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Empezada", "Alumna",
            "t16.date.started@example.invalid", now);
        var suspendedClient = await InsertClientAsync(connection, transaction, tenant.BusinessId, "Suspendida", "Alumna",
            "t16.date.suspended@example.invalid", now);

        var startedSession = await InsertClassSessionAsync(connection, transaction, tenant.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenant.BusinessId, tenant.ServiceId, tenant.WorkerId,
                DayOfWeek.Thursday, new TimeOnly(18, 0)),
            tenant.ServiceId, tenant.WorkerId, ClassStartsAt, 60, SessionCapacity,
            AppointmentStatus.Scheduled, null, null, now);
        var suspendedSession = await InsertClassSessionAsync(connection, transaction, tenant.BusinessId,
            await InsertGridClassAsync(connection, transaction, tenant.BusinessId, tenant.ServiceId, tenant.WorkerId,
                DayOfWeek.Saturday, new TimeOnly(10, 0)),
            tenant.ServiceId, tenant.WorkerId, ClassStartsAt.AddDays(2).AddHours(-8), 60, SessionCapacity,
            AppointmentStatus.Suspended, "Feriado nacional", now.AddDays(-1), now);
        var startedBooking = await InsertBookingAsync(connection, transaction, tenant.BusinessId, startedSession,
            startedClient, null, BookingModality.DropIn, BookingStatus.Pending, now);
        var suspendedBooking = await InsertBookingAsync(connection, transaction, tenant.BusinessId, suspendedSession,
            suspendedClient, null, BookingModality.DropIn, BookingStatus.Pending, now);

        await transaction.CommitAsync();
        return new GuardIds(tenant.BusinessId, startedSession, suspendedSession, startedBooking, suspendedBooking);
    }

    /// <summary>
    /// Tries to take the class session row with an update lock on its own connection and reports whether the lock was
    /// granted. It runs under READ COMMITTED with ROWLOCK, so a granted lock means the row was free and nothing was
    /// left behind by the probe.
    /// </summary>
    private static async Task<bool> SessionRowUpdateLockLandsAsync(string connectionString, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var timeout = new SqlCommand("SET LOCK_TIMEOUT 3000", connection))
            await timeout.ExecuteNonQueryAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await using var command = new SqlCommand(
                "SELECT Id FROM Appointments WITH (UPDLOCK, ROWLOCK) WHERE Id = @appointmentId",
                connection, transaction);
            AddParameter(command, "@appointmentId", appointmentId);
            await command.ExecuteScalarAsync();
            return true;
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            return false;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>Reports whether the annotation row is held by someone else, by failing to update-lock it in time.</summary>
    private static async Task<bool> BookingRowUpdateLockTimesOutAsync(string connectionString, int bookingId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var timeout = new SqlCommand("SET LOCK_TIMEOUT 3000", connection))
            await timeout.ExecuteNonQueryAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await using var command = new SqlCommand(
                "SELECT Id FROM Bookings WITH (UPDLOCK, ROWLOCK) WHERE Id = @bookingId", connection, transaction);
            AddParameter(command, "@bookingId", bookingId);
            await command.ExecuteScalarAsync();
            return false;
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            return true;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>
    /// Runs the suspension's own read — the class lock and then the annotations under it — on its own connection and
    /// reports whether it could not finish in time. A timeout means some annotation row was held by another writer,
    /// which is exactly the row the suspension needs after taking the class session row.
    /// </summary>
    private static async Task<bool> T16SessionLockTimesOutAsync(
        string connectionString, int businessId, int appointmentId)
    {
        await using var session = new DbSession(connectionString);
        var connection = await session.GetOpenConnectionAsync();
        await using (var timeout = new SqlCommand("SET LOCK_TIMEOUT 2000", connection))
            await timeout.ExecuteNonQueryAsync();
        var transaction = await session.BeginAsync();
        var repository = new SqlClassDateActionsRepository(session);
        try
        {
            await repository.LockSessionAsync(businessId, appointmentId);
            return false;
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            return true;
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    /// <summary>The minimum the shared seeds need: one tenant with its class service and its teacher.</summary>
    private sealed record Tenant(int BusinessId, int ServiceId, int WorkerId);

    private static async Task<Tenant> InsertTenantAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string slug, string cuil,
        int maxCapacity, DateTime now)
    {
        var businessId = await InsertBusinessAsync(connection, transaction, name, slug, now);
        var serviceId = await InsertServiceAsync(connection, transaction, businessId, $"{name} Class", 60, maxCapacity);
        var workerId = await InsertWorkerAsync(connection, transaction, businessId, name, "Teacher", cuil);
        return new Tenant(businessId, serviceId, workerId);
    }

    private static Task<int> InsertBusinessAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string slug, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @allowsWalkIns, @createdAt)",
            ("@name", name), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@allowsWalkIns", true), ("@createdAt", now));

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
        AppointmentStatus status, string? suspensionReason, DateTime? suspendedAt, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, SuspensionReason, SuspendedAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, @gridClassId, @startsAt, @endsAt, @capacity, NULL, @status, @suspensionReason, @suspendedAt, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.ClassSession), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@gridClassId", gridClassId), ("@startsAt", startsAt),
            ("@endsAt", startsAt.AddMinutes(durationMinutes)), ("@capacity", maxCapacity), ("@status", status),
            ("@suspensionReason", suspensionReason), ("@suspendedAt", suspendedAt), ("@createdAt", now));

    private static Task<int> InsertEnrollmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId,
        EnrollmentStatus status, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @createdAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", 1),
            ("@status", status), ("@price", 100.00m), ("@createdAt", now));

    private static async Task<(int Period, int Payment)> InsertMembershipAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int enrollmentId,
        DateOnly coverageStart, DateOnly expiresOn, DateTime now)
    {
        var paymentId = await InsertPaymentAsync(connection, transaction, businessId, null, PaymentConcept.Membership,
            null, enrollmentId, 100.00m, null, null, now, now);
        var periodId = await InsertIdAsync(connection, transaction,
            "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) OUTPUT INSERTED.Id VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
            ("@enrollmentId", enrollmentId), ("@coverageStart", coverageStart), ("@expiresOn", expiresOn),
            ("@paymentId", paymentId), ("@createdAt", now));
        return (periodId, paymentId);
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
    /// cancelled one carries its instant and its author.
    /// </summary>
    private static Task<int> InsertBookingAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int appointmentId, int clientId,
        int? enrollmentId, BookingModality? modality, BookingStatus status, DateTime createdAt,
        DateTime? cancelledAt = null, CancelledBy? cancelledBy = null, DateTime? attendanceRecordedAt = null,
        AbsenceSource? absenceSource = null) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, AbsenceSource, CancelledBy, CreatedAt, CancelledAt, AttendanceRecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, @absenceSource, @cancelledBy, @createdAt, @cancelledAt, @attendanceRecordedAt)",
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@enrollmentId", enrollmentId), ("@modality", modality), ("@status", status),
            ("@absenceSource", absenceSource), ("@cancelledBy", cancelledBy), ("@createdAt", createdAt),
            ("@cancelledAt", cancelledAt), ("@attendanceRecordedAt", attendanceRecordedAt));

    /// <summary>One outbox row written outside the service, so a test can pre-seed the notice a retry would leave.</summary>
    private static async Task<int> InsertNotificationAsync(
        string connectionString, int businessId, string recipient, string dedupKey, DateTime createdAt)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt) OUTPUT INSERTED.Id VALUES (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL)",
            connection);
        AddParameter(command, "@businessId", businessId);
        AddParameter(command, "@type", NotificationType.ClassSuspended);
        AddParameter(command, "@recipient", recipient);
        AddParameter(command, "@subject", "Clase suspendida");
        AddParameter(command, "@bodyHtml", "<p>Hola</p>");
        AddParameter(command, "@dedupKey", dedupKey);
        AddParameter(command, "@status", NotificationStatus.Pending);
        AddParameter(command, "@createdAt", createdAt);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
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
    /// Reads through the unit of work's own connection and transaction, so a write that is not committed yet is
    /// visible: that is what makes the rollback assertion about an erasure instead of about a write that never
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
            "SELECT BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CancelledBy, CancelledAt, AttendanceRecordedAt, AbsenceSource FROM Bookings WHERE Id = @id",
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
            reader.IsDBNull(6) ? null : (CancelledBy)reader.GetByte(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : (AbsenceSource)reader.GetByte(9));
    }

    private static async Task<SessionSnapshot> ReadSessionAsync(string connectionString, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BusinessId, Kind, GridClassId, StartsAt, EndsAt, MaxCapacity, Status, SuspensionReason, SuspendedAt FROM Appointments WHERE Id = @id",
            connection);
        AddParameter(command, "@id", appointmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Appointment {appointmentId} was not found.");
        return new SessionSnapshot(
            reader.GetInt32(0),
            (AppointmentKind)reader.GetByte(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetDateTime(3),
            reader.GetDateTime(4),
            reader.GetInt32(5),
            (AppointmentStatus)reader.GetByte(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8));
    }

    /// <summary>The statuses of every annotation of the session, the input of the single capacity rule.</summary>
    private static async Task<IReadOnlyList<BookingStatus>> ReadBookingStatusesAsync(
        string connectionString, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Status FROM Bookings WHERE AppointmentId = @appointmentId ORDER BY Id", connection);
        AddParameter(command, "@appointmentId", appointmentId);
        await using var reader = await command.ExecuteReaderAsync();
        var statuses = new List<BookingStatus>();
        while (await reader.ReadAsync())
            statuses.Add((BookingStatus)reader.GetByte(0));
        return statuses;
    }

    private static async Task<IReadOnlyList<NotificationSnapshot>> ReadNotificationsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, CreatedAt FROM Notifications ORDER BY Id",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var notices = new List<NotificationSnapshot>();
        while (await reader.ReadAsync())
        {
            notices.Add(new NotificationSnapshot(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                (NotificationType)reader.GetByte(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                (NotificationStatus)reader.GetByte(7),
                reader.GetDateTime(8)));
        }

        return notices;
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
    /// Wraps the real SQL port and refuses the notice write, which is the only refusal that can realistically happen
    /// once the class lock guarantees that the session belongs to the business and is still open. Every other call
    /// reaches SQL, so what the rollback test inspects are real rows, and the hook reads the session inside the unit
    /// before the refusal to prove the suspension write had already landed.
    /// </summary>
    private sealed class NoticeRefusingRepository(IClassDateActionsRepository inner, Func<Task> onRefusal)
        : IClassDateActionsRepository
    {
        public Task<ClassBookingCancellationState?> LockBookingAsync(
            int businessId, int bookingId, CancellationToken ct = default) =>
            inner.LockBookingAsync(businessId, bookingId, ct);

        public Task<bool> CancelBookingAsync(
            int businessId, int bookingId, DateTime now, CancellationToken ct = default) =>
            inner.CancelBookingAsync(businessId, bookingId, now, ct);

        public Task<ClassSuspensionSession?> LockSessionAsync(
            int businessId, int appointmentId, CancellationToken ct = default) =>
            inner.LockSessionAsync(businessId, appointmentId, ct);

        public Task<bool> SuspendAsync(
            int businessId, int appointmentId, string reason, DateTime now, CancellationToken ct = default) =>
            inner.SuspendAsync(businessId, appointmentId, reason, now, ct);

        public async Task<bool> EnqueueNotificationAsync(Notification notification, CancellationToken ct = default)
        {
            await onRefusal();
            throw new InvalidOperationException("The outbox refused the suspension notice.");
        }
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record ClassIds(
        int BusinessA, int Session, int Fixed, int DropIn, int Attended, int Absent, int ClientCancelled,
        int Enrollment, int Period, int MembershipPayment);

    private sealed record TenantIds(
        int BusinessA, int BusinessB, int Session, int ForeignSession, int Booking, int ForeignBooking);

    private sealed record GuardIds(
        int BusinessA, int StartedSession, int SuspendedSession, int StartedBooking, int SuspendedBooking);

    private sealed record BookingSnapshot(
        int BusinessId,
        int AppointmentId,
        int ClientId,
        int? EnrollmentId,
        BookingModality? Modality,
        BookingStatus Status,
        CancelledBy? CancelledBy,
        DateTime? CancelledAt,
        DateTime? AttendanceRecordedAt,
        AbsenceSource? AbsenceSource);

    private sealed record SessionSnapshot(
        int BusinessId,
        AppointmentKind Kind,
        int? GridClassId,
        DateTime StartsAt,
        DateTime EndsAt,
        int MaxCapacity,
        AppointmentStatus Status,
        string? SuspensionReason,
        DateTime? SuspendedAt);

    private sealed record NotificationSnapshot(
        int Id,
        int? BusinessId,
        NotificationType Type,
        string Recipient,
        string Subject,
        string BodyHtml,
        string DedupKey,
        NotificationStatus Status,
        DateTime CreatedAt);
}
