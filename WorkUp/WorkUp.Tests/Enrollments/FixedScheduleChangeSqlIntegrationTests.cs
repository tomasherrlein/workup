using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Data;
using WorkUp.Data.Classes;
using WorkUp.Data.Enrollments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Enrollments;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlFixedScheduleChangeRepository"/> and the guided
/// schedule change that drives it (RF-21, D-19): the range read that decides whether future generated sessions block
/// the change, the range lock that keeps a concurrent week generation from slipping a session into the affected
/// range before the change commits — observed as a real timeout of the T13 insert while the guard transaction was
/// open — and the guarded close-and-open write. It requires a new, dedicated database
/// supplied through WORKUP_T17_SQL_CONNECTION; without it every test returns N/A and no SQL connection is opened. Each
/// test derives its own database name from the supplied one, so the whole class runs in a single command, and nothing
/// is torn down: the target is retained on purpose.
/// <para>
/// The unit tests cannot prove what lives in storage — the window the change committed, the past annotation it never
/// touched, the enrollment, the membership period and the payment it never rewrote, the seat the previous window still
/// retained through 2026-03-15 and the one the new window holds from 2026-03-16 on, the generated session that
/// refused the change, the tenant that stayed invisible and the write that neither committed on its own nor opened a
/// second window on the same slot — so this fixture is the runtime proof of those claims. It also drives the real week
/// generation (T13) as the competing writer, because a copy of its insert statement would prove nothing about the
/// statement production runs.
/// </para>
/// </summary>
public sealed class FixedScheduleChangeSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T17_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>Tuesday 2026-03-10 09:00, the single clock instant every test decides from.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    /// <summary>Monday, the first day the new weekly class is valid: strictly after the old window started.</summary>
    private static readonly DateOnly EffectiveFrom = new(2026, 3, 16);

    /// <summary>The day the change closes the previous window with: the day before the effective date.</summary>
    private static readonly DateOnly PreviousValidTo = new(2026, 3, 15);

    /// <summary>Start of the open assignment the change replaces, strictly before the effective date.</summary>
    private static readonly DateOnly OldValidFrom = new(2026, 1, 5);

    /// <summary>Thursday before the effective date: the past session whose annotation the change must never touch.</summary>
    private static readonly DateTime PastSession = new(2026, 3, 5, 18, 0, 0);

    /// <summary>Thursday on or after the effective date, of the old grid class (Thursday 18:00).</summary>
    private static readonly DateTime OldClassFutureSession = new(2026, 3, 19, 18, 0, 0);

    /// <summary>Tuesday on or after the effective date, of the new grid class (Tuesday 19:00).</summary>
    private static readonly DateTime NewClassFutureSession = new(2026, 3, 17, 19, 0, 0);

    /// <summary>Saturday, of a grid class the change does not affect (Saturday 10:00).</summary>
    private static readonly DateTime UnrelatedSession = new(2026, 3, 21, 10, 0, 0);

    private const int SessionCapacity = 4;
    private const int ServiceDurationMinutes = 60;
    private const int WeeklyFrequency = 1;
    private const decimal AgreedPrice = 5000m;
    private const int ReferenceDay = 10;
    private static readonly DateOnly PeriodStart = new(2026, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 4, 1);

    /// <summary>A seat registered for a window that has not started yet: it still blocks the grid class (Regla 23).</summary>
    private static readonly DateOnly FutureSeatValidFrom = new(2026, 6, 1);

    [Fact]
    public async Task The_change_commits_the_new_window_and_leaves_the_past_annotation_membership_and_payment_untouched()
    {
        var target = await ResolveTargetAsync("_Committed");
        if (target is null)
            return;

        var ids = await SeedChangeAsync(
            target.ConnectionString, new ChangeSeed("synthetic-t17-committed", "20201717170", SeedParts.PastSession));

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session);

        var result = await service.ChangeAsync(
            ids.BusinessId,
            new ChangeFixedScheduleRequest(ids.Enrollment, ids.OldGridClass, ids.NewGridClass, EffectiveFrom));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(ids.Enrollment, result.Value.EnrollmentId);
        Assert.Equal(ids.OldGridClass, result.Value.CurrentGridClassId);
        Assert.Equal(ids.NewGridClass, result.Value.NewGridClassId);
        Assert.Equal(EffectiveFrom, result.Value.EffectiveFrom);
        Assert.Equal(PreviousValidTo, result.Value.PreviousValidTo);
        Assert.True(result.Value.NewFixedClassId > 0);

        // Two assignments exist and exactly one of them is open: the previous one ends the day before the effective
        // date with its start untouched, and the new one opens on it. No row was deleted.
        var assignments = await ReadFixedClassesAsync(target.ConnectionString, ids.Enrollment);
        Assert.Equal(2, assignments.Count);

        var previous = Assert.Single(assignments, row => row.Id == ids.OldFixedClass);
        Assert.Equal(ids.OldGridClass, previous.GridClassId);
        Assert.Equal(OldValidFrom, previous.ValidFrom);
        Assert.Equal(PreviousValidTo, previous.ValidTo);

        var opened = Assert.Single(assignments, row => row.ValidTo is null);
        Assert.Equal(result.Value.NewFixedClassId, opened.Id);
        Assert.Equal(ids.NewGridClass, opened.GridClassId);
        Assert.Equal(EffectiveFrom, opened.ValidFrom);

        // The window moved, the seat does not move in the same instant: the old grid class keeps the enrollment's
        // permanent seat while its window has not ended — ValidTo 2026-03-15 is inclusive, so the seat is still held
        // on 2026-03-10 and through 2026-03-15 — and it releases it from 2026-03-16 on, which is exactly the day the
        // new window opens. Both temporal boundaries are asserted, and the seat is judged by the single domain rule
        // with the change's own reference day.
        var today = DateOnly.FromDateTime(FixedNow);
        var previousSeats = await new SqlEnrollmentRepository(session)
            .ListFixedSeatsAsync(ids.BusinessId, ids.OldGridClass, today);
        var previousSeat = Assert.Single(previousSeats);
        Assert.Equal(ids.Enrollment, previousSeat.EnrollmentId);
        Assert.Equal(OldValidFrom, previousSeat.ValidFrom);
        Assert.Equal(PreviousValidTo, previousSeat.ValidTo);
        Assert.True(previousSeat.RetainsSeatOn(today));
        Assert.True(previousSeat.RetainsSeatOn(PreviousValidTo));
        Assert.False(previousSeat.RetainsSeatOn(EffectiveFrom));
        Assert.True(previousSeat.IsValidOn(PreviousValidTo));
        Assert.False(previousSeat.IsValidOn(EffectiveFrom));
        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(SessionCapacity, previousSeats, today));

        var newSeats = await new SqlEnrollmentRepository(session)
            .ListFixedSeatsAsync(ids.BusinessId, ids.NewGridClass, today);
        var newSeat = Assert.Single(newSeats);
        Assert.Equal(ids.Enrollment, newSeat.EnrollmentId);
        Assert.Equal(EffectiveFrom, newSeat.ValidFrom);
        Assert.Null(newSeat.ValidTo);
        Assert.True(newSeat.RetainsSeatOn(today));
        Assert.True(newSeat.RetainsSeatOn(EffectiveFrom));
        Assert.True(newSeat.IsValidOn(EffectiveFrom));
        Assert.False(newSeat.IsValidOn(PreviousValidTo));
        Assert.Equal(1, FixedSeatCapacity.SeatsTaken(SessionCapacity, newSeats, today));

        // The generated session that already happened and its fixed annotation are exactly as they were seeded: the
        // change never migrates, cancels or rewrites the past (RF-21).
        Assert.NotNull(ids.PastSession);
        var sessions = await ReadSessionsAsync(target.ConnectionString, ids.BusinessId);
        var past = Assert.Single(sessions);
        Assert.Equal(ids.PastSession!.Value, past.Id);
        Assert.Equal(ids.OldGridClass, past.GridClassId);
        Assert.Equal(PastSession, past.StartsAt);
        Assert.Equal(AppointmentStatus.Scheduled, past.Status);

        var bookings = await ReadBookingsAsync(target.ConnectionString, ids.BusinessId);
        var pastBooking = Assert.Single(bookings);
        Assert.Equal(ids.PastSession!.Value, pastBooking.AppointmentId);
        Assert.Equal(ids.Enrollment, pastBooking.EnrollmentId);
        Assert.Equal(BookingModality.Fixed, pastBooking.Modality);
        Assert.Equal(BookingStatus.Pending, pastBooking.Status);

        // The enrollment keeps its status, its frequency, its agreed price, its reference day and its activation
        // instant, and it is not annulled nor terminated: only the weekly slot moved (RF-29).
        var enrollment = await ReadEnrollmentAsync(target.ConnectionString, ids.Enrollment);
        Assert.Equal(EnrollmentStatus.Active, enrollment.Status);
        Assert.Equal(WeeklyFrequency, enrollment.WeeklyFrequency);
        Assert.Equal(AgreedPrice, enrollment.AgreedPrice);
        Assert.Equal(ReferenceDay, enrollment.ReferenceDay);
        Assert.Equal(FixedNow, enrollment.ActivatedAt);
        Assert.Null(enrollment.AnnulledAt);
        Assert.Null(enrollment.TerminatedAt);

        // One member, one paid month, one payment: the change writes no charge, no refund and no extra period.
        var periods = await ReadPeriodsAsync(target.ConnectionString, ids.Enrollment);
        var period = Assert.Single(periods);
        Assert.Equal(ids.Period, period.Id);
        Assert.Equal(PeriodStart, period.CoverageStart);
        Assert.Equal(PeriodEnd, period.ExpiresOn);
        Assert.Equal(ids.Payment, period.PaymentId);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessId)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", ids.Enrollment)));
        // And it generated nothing: creating the future sessions stays in T13 (RF-06).
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessId)));

        output.WriteLine("Committed-change assertions passed: the previous window closed on 2026-03-15 keeping its 2026-01-05 start and still retaining its permanent seat through that date (released from 2026-03-16 on), the new one opened on 2026-03-16 holding the seat from that day, and the past session with its fixed annotation, the enrollment and the only payment and period were untouched.");
    }

    [Fact]
    public async Task A_generated_session_of_either_affected_class_refuses_the_change_and_an_unrelated_or_earlier_one_does_not()
    {
        var target = await ResolveTargetAsync("_Generated");
        if (target is null)
            return;

        var relatedOld = await SeedChangeAsync(
            target.ConnectionString,
            new ChangeSeed("synthetic-t17-genera-old", "20201717270", SeedParts.OldClassFutureSession));
        var relatedNew = await SeedChangeAsync(
            target.ConnectionString,
            new ChangeSeed("synthetic-t17-genera-new", "20201717271", SeedParts.NewClassFutureSession));
        var unrelated = await SeedChangeAsync(
            target.ConnectionString,
            new ChangeSeed("synthetic-t17-genera-unrelated", "20201717272", SeedParts.UnrelatedSession));
        var earlier = await SeedChangeAsync(
            target.ConnectionString,
            new ChangeSeed("synthetic-t17-genera-earlier", "20201717273", SeedParts.PastSession));

        await using var session = new DbSession(target.ConnectionString);
        var schedules = new SqlFixedScheduleChangeRepository(session);
        var service = CreateService(session);

        // The read answers with the latest generated date of the affected classes only, and never with a session of an
        // unrelated grid class or one before the effective date.
        var affected = new[] { relatedOld.OldGridClass, relatedOld.NewGridClass };
        Assert.Equal(DateOnly.FromDateTime(OldClassFutureSession),
            await schedules.LastGeneratedSessionDateAsync(relatedOld.BusinessId, affected, EffectiveFrom));
        Assert.Equal(DateOnly.FromDateTime(NewClassFutureSession),
            await schedules.LastGeneratedSessionDateAsync(
                relatedNew.BusinessId, new[] { relatedNew.OldGridClass, relatedNew.NewGridClass }, EffectiveFrom));
        Assert.Null(await schedules.LastGeneratedSessionDateAsync(
            unrelated.BusinessId, new[] { unrelated.OldGridClass, unrelated.NewGridClass }, EffectiveFrom));
        Assert.Equal(DateOnly.FromDateTime(UnrelatedSession),
            await schedules.LastGeneratedSessionDateAsync(
                unrelated.BusinessId, new[] { unrelated.UnrelatedGridClass }, EffectiveFrom));
        Assert.Null(await schedules.LastGeneratedSessionDateAsync(
            earlier.BusinessId, new[] { earlier.OldGridClass, earlier.NewGridClass }, EffectiveFrom));

        // A session of the old class from the effective date on refuses the change and asks for a later date. Nothing
        // is written: the previous window is still open and no new row exists.
        var refusedOld = await service.ChangeAsync(
            relatedOld.BusinessId,
            new ChangeFixedScheduleRequest(
                relatedOld.Enrollment, relatedOld.OldGridClass, relatedOld.NewGridClass, EffectiveFrom));
        Assert.False(refusedOld.Succeeded);
        Assert.Equal("fixed_schedule_change_sessions_generated", refusedOld.Error!.Code);
        Assert.Contains(FirstUsable(OldClassFutureSession), refusedOld.Error.Message);
        var oldAssignments = await ReadFixedClassesAsync(target.ConnectionString, relatedOld.Enrollment);
        var oldOpen = Assert.Single(oldAssignments);
        Assert.Equal(relatedOld.OldFixedClass, oldOpen.Id);
        Assert.Null(oldOpen.ValidTo);

        // The same for the new class: sessions of either affected grid class block the change, never only one.
        var refusedNew = await service.ChangeAsync(
            relatedNew.BusinessId,
            new ChangeFixedScheduleRequest(
                relatedNew.Enrollment, relatedNew.OldGridClass, relatedNew.NewGridClass, EffectiveFrom));
        Assert.False(refusedNew.Succeeded);
        Assert.Equal("fixed_schedule_change_sessions_generated", refusedNew.Error!.Code);
        Assert.Contains(FirstUsable(NewClassFutureSession), refusedNew.Error.Message);
        Assert.Single(await ReadFixedClassesAsync(target.ConnectionString, relatedNew.Enrollment));

        // A generated session of an unrelated grid class is not a reason to refuse anything, and the session itself is
        // never touched by the change.
        var acceptedUnrelated = await service.ChangeAsync(
            unrelated.BusinessId,
            new ChangeFixedScheduleRequest(
                unrelated.Enrollment, unrelated.OldGridClass, unrelated.NewGridClass, EffectiveFrom));
        Assert.True(acceptedUnrelated.Succeeded, acceptedUnrelated.Error?.Code);
        var unrelatedSessions = await ReadSessionsAsync(target.ConnectionString, unrelated.BusinessId);
        var otherSession = Assert.Single(unrelatedSessions);
        Assert.Equal(unrelated.UnrelatedGridClass, otherSession.GridClassId);
        Assert.Equal(UnrelatedSession, otherSession.StartsAt);
        Assert.Equal(2, (await ReadFixedClassesAsync(target.ConnectionString, unrelated.Enrollment)).Count);

        // And a session that already happened is not a reason either: the change only cares about the effective date on.
        var acceptedEarlier = await service.ChangeAsync(
            earlier.BusinessId,
            new ChangeFixedScheduleRequest(
                earlier.Enrollment, earlier.OldGridClass, earlier.NewGridClass, EffectiveFrom));
        Assert.True(acceptedEarlier.Succeeded, acceptedEarlier.Error?.Code);
        var earlierSessions = await ReadSessionsAsync(target.ConnectionString, earlier.BusinessId);
        var earlierSession = Assert.Single(earlierSessions);
        Assert.Equal(PastSession, earlierSession.StartsAt);
        Assert.Equal(2, (await ReadFixedClassesAsync(target.ConnectionString, earlier.Enrollment)).Count);

        output.WriteLine("Generated-session assertions passed: the guard read returned 2026-03-19 for the old class and 2026-03-17 for the new one, refused both with fixed_schedule_change_sessions_generated and a later date in the message, wrote nothing on either refusal, and let the change through when the only generated session was of an unrelated grid class or of a date before the effective one.");
    }

    [Fact]
    public async Task A_full_new_grid_class_refuses_the_change_and_the_seat_rule_counts_a_future_start()
    {
        var target = await ResolveTargetAsync("_Capacity");
        if (target is null)
            return;

        // Capacity 2 with two seats already retained on the new grid class, both registered with a window that starts
        // in June: the place is held from registration (Regla 23, D-21), so the change must be refused today.
        var ids = await SeedChangeAsync(
            target.ConnectionString,
            new ChangeSeed("synthetic-t17-capacity", "20201717370", Capacity: 2, AdditionalOccupiedSeats: 2));

        await using var session = new DbSession(target.ConnectionString);
        var enrollments = new SqlEnrollmentRepository(session);
        var service = CreateService(session);
        var today = DateOnly.FromDateTime(FixedNow);

        // Exactly the rows the single capacity rule consumes, and the future window still holds its place.
        var seats = await enrollments.ListFixedSeatsAsync(ids.BusinessId, ids.NewGridClass, today);
        Assert.Equal(2, seats.Count);
        Assert.Contains(seats, seat => seat.ValidFrom == FutureSeatValidFrom && seat.RetainsSeatOn(today));
        Assert.Equal(2, FixedSeatCapacity.SeatsTaken(2, seats, today));
        Assert.False(FixedSeatCapacity.HasSeat(2, seats, today));
        Assert.True(FixedSeatCapacity.HasSeat(3, seats, today));

        var result = await service.ChangeAsync(
            ids.BusinessId,
            new ChangeFixedScheduleRequest(ids.Enrollment, ids.OldGridClass, ids.NewGridClass, EffectiveFrom));
        Assert.False(result.Succeeded);
        Assert.Equal("fixed_schedule_change_no_seat", result.Error!.Code);

        // The refusal wrote nothing: the changing enrollment keeps its one open assignment on the old class and the
        // two occupying assignments are still open.
        var assignments = await ReadFixedClassesAsync(target.ConnectionString, ids.Enrollment);
        var open = Assert.Single(assignments);
        Assert.Equal(ids.OldFixedClass, open.Id);
        Assert.Equal(ids.OldGridClass, open.GridClassId);
        Assert.Null(open.ValidTo);
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE GridClassId = @gridClassId AND ValidTo IS NULL",
            ("@gridClassId", ids.NewGridClass)));

        output.WriteLine("Capacity assertions passed: ListFixedSeatsAsync returned the two occupying assignments of the new grid class including the one registered for June, FixedSeatCapacity refused the second seat from today, the change answered fixed_schedule_change_no_seat and neither the previous window nor the occupying rows changed.");
    }

    [Fact]
    public async Task An_enrollment_or_grid_class_of_another_business_is_invisible_to_the_change_and_to_the_write()
    {
        var target = await ResolveTargetAsync("_ForeignTenant");
        if (target is null)
            return;

        var own = await SeedChangeAsync(
            target.ConnectionString, new ChangeSeed("synthetic-t17-tenant-own", "20201717470"));
        var foreign = await SeedChangeAsync(
            target.ConnectionString,
            new ChangeSeed("synthetic-t17-tenant-foreign", "20201717471", SeedParts.OldClassFutureSession));

        await using var session = new DbSession(target.ConnectionString);
        var schedules = new SqlFixedScheduleChangeRepository(session);
        var enrollments = new SqlEnrollmentRepository(session);
        var service = CreateService(session);

        // The other business's generated session does not exist for this one: the read is scoped by the tenant and by
        // the requested grid classes.
        Assert.Null(await schedules.LastGeneratedSessionDateAsync(
            own.BusinessId, new[] { foreign.OldGridClass }, EffectiveFrom));
        Assert.Equal(DateOnly.FromDateTime(OldClassFutureSession),
            await schedules.LastGeneratedSessionDateAsync(
                foreign.BusinessId, new[] { foreign.OldGridClass }, EffectiveFrom));
        Assert.Empty(await enrollments.ListFixedSeatsAsync(
            own.BusinessId, foreign.OldGridClass, DateOnly.FromDateTime(FixedNow)));
        Assert.Single(await enrollments.ListFixedSeatsAsync(
            foreign.BusinessId, foreign.OldGridClass, DateOnly.FromDateTime(FixedNow)));

        // A grid class of the other business is not found, and the refusal writes nothing anywhere.
        var foreignGridClass = await service.ChangeAsync(
            own.BusinessId,
            new ChangeFixedScheduleRequest(own.Enrollment, own.OldGridClass, foreign.NewGridClass, EffectiveFrom));
        Assert.False(foreignGridClass.Succeeded);
        Assert.Equal("fixed_schedule_change_grid_class_not_found", foreignGridClass.Error!.Code);

        // Nor is the other business's enrollment: the two lookups answer the same way, without revealing whether the
        // identifier exists.
        var foreignEnrollment = await service.ChangeAsync(
            own.BusinessId,
            new ChangeFixedScheduleRequest(foreign.Enrollment, foreign.OldGridClass, own.NewGridClass, EffectiveFrom));
        Assert.False(foreignEnrollment.Succeeded);
        Assert.Equal("fixed_schedule_change_enrollment_not_found", foreignEnrollment.Error!.Code);

        // The guarded write repeats the tenant scope: called with a foreign enrollment and grid class it closes
        // nothing and inserts nothing, so the losing side of that call is a lost race instead of a cross-tenant write.
        await using (var transaction = await session.BeginAsync())
        {
            var lost = await schedules.TryReplaceFixedClassAsync(
                own.BusinessId, foreign.Enrollment, foreign.OldGridClass, PreviousValidTo,
                NewFixedClass(foreign.Enrollment, foreign.NewGridClass));
            Assert.False(lost.IsReplaced);
            Assert.Equal(FixedScheduleChangeWriteResultType.PreviousAssignmentChanged, lost.Result);
            await transaction.CommitAsync();
        }

        // Neither tenant was modified by any of the three refusals.
        var ownAssignments = await ReadFixedClassesAsync(target.ConnectionString, own.Enrollment);
        var ownOpen = Assert.Single(ownAssignments);
        Assert.Null(ownOpen.ValidTo);
        Assert.Equal(own.OldFixedClass, ownOpen.Id);
        var foreignAssignments = await ReadFixedClassesAsync(target.ConnectionString, foreign.Enrollment);
        var foreignOpen = Assert.Single(foreignAssignments);
        Assert.Equal(foreign.OldFixedClass, foreignOpen.Id);
        Assert.Null(foreignOpen.ValidTo);
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE GridClassId = @gridClassId",
            ("@gridClassId", own.NewGridClass)));

        output.WriteLine("Tenant-isolation assertions passed: the other business's session and grid class were invisible to the read, its grid class answered fixed_schedule_change_grid_class_not_found and its enrollment fixed_schedule_change_enrollment_not_found, the guarded write closed and inserted nothing across tenants, and both businesses kept exactly their one open assignment.");
    }

    [Fact]
    public async Task Two_concurrent_replacements_of_the_same_open_assignment_leave_exactly_one_new_window()
    {
        var target = await ResolveTargetAsync("_LostRace");
        if (target is null)
            return;

        var racing = await SeedChangeAsync(
            target.ConnectionString, new ChangeSeed("synthetic-t17-lostrace-run", "20201717570"));
        var preclosed = await SeedChangeAsync(
            target.ConnectionString, new ChangeSeed("synthetic-t17-lostrace-closed", "20201717571"));

        // Two writers close the same open row at the same time on their own connections. Whichever arrives second finds
        // the window closed and writes nothing, so the guarded close is what keeps a second window off the slot.
        using var startGate = new Barrier(participantCount: 2);
        Task<FixedScheduleChangeWriteResult> ReplaceAsync() => Task.Run(async () =>
        {
            await using var raceSession = new DbSession(target.ConnectionString);
            var repository = new SqlFixedScheduleChangeRepository(raceSession);
            await using var transaction = await raceSession.BeginAsync();
            startGate.SignalAndWait();
            var write = await repository.TryReplaceFixedClassAsync(
                racing.BusinessId, racing.Enrollment, racing.OldGridClass, PreviousValidTo,
                NewFixedClass(racing.Enrollment, racing.NewGridClass));
            await transaction.CommitAsync();
            return write;
        });

        var results = await Task.WhenAll(ReplaceAsync(), ReplaceAsync());

        var winner = Assert.Single(results, write => write.IsReplaced);
        var loser = Assert.Single(results, write => !write.IsReplaced);
        Assert.True(winner.NewFixedClassId > 0);
        Assert.Equal(0, loser.NewFixedClassId);
        Assert.Equal(FixedScheduleChangeWriteResultType.PreviousAssignmentChanged, loser.Result);

        // Exactly one new row and exactly one open window for the slot, whatever the interleaving was.
        var assignments = await ReadFixedClassesAsync(target.ConnectionString, racing.Enrollment);
        Assert.Equal(2, assignments.Count);
        var opened = Assert.Single(assignments, row => row.ValidTo is null);
        Assert.Equal(racing.NewGridClass, opened.GridClassId);
        Assert.Equal(EffectiveFrom, opened.ValidFrom);
        Assert.Equal(winner.NewFixedClassId, opened.Id);
        var closed = Assert.Single(assignments, row => row.ValidTo is not null);
        Assert.Equal(racing.OldFixedClass, closed.Id);
        Assert.Equal(PreviousValidTo, closed.ValidTo);

        // The same guard seen from the deterministic side: a competition that already closed the expected row turns the
        // call into a lost race that writes nothing, and the closure it found is never rewritten.
        await ExecuteAsync(target.ConnectionString,
            "UPDATE EnrollmentFixedClasses SET ValidTo = ValidFrom WHERE Id = @id", ("@id", preclosed.OldFixedClass));
        await using (var session = new DbSession(target.ConnectionString))
        {
            var schedules = new SqlFixedScheduleChangeRepository(session);
            await using var transaction = await session.BeginAsync();
            var lost = await schedules.TryReplaceFixedClassAsync(
                preclosed.BusinessId, preclosed.Enrollment, preclosed.OldGridClass, PreviousValidTo,
                NewFixedClass(preclosed.Enrollment, preclosed.NewGridClass));
            Assert.False(lost.IsReplaced);
            Assert.Equal(FixedScheduleChangeWriteResultType.PreviousAssignmentChanged, lost.Result);
            await transaction.CommitAsync();
        }

        var preclosedAssignments = await ReadFixedClassesAsync(target.ConnectionString, preclosed.Enrollment);
        var stillClosed = Assert.Single(preclosedAssignments);
        Assert.Equal(preclosed.OldFixedClass, stillClosed.Id);
        Assert.Equal(OldValidFrom, stillClosed.ValidTo);

        output.WriteLine("Lost-race assertions passed: two concurrent replacements of the same open window ended with exactly one Replaced and one PreviousAssignmentChanged, one new row and one open window on the slot, and a window already closed by someone else turned the call into a lost race that wrote nothing and never rewrote the closure.");
    }

    [Fact]
    public async Task The_guarded_write_commits_only_with_the_callers_transaction()
    {
        var target = await ResolveTargetAsync("_Rollback");
        if (target is null)
            return;

        var ids = await SeedChangeAsync(
            target.ConnectionString, new ChangeSeed("synthetic-t17-rollback", "20201717670"));

        await using var session = new DbSession(target.ConnectionString);
        var schedules = new SqlFixedScheduleChangeRepository(session);
        var transaction = await session.BeginAsync();

        var write = await schedules.TryReplaceFixedClassAsync(
            ids.BusinessId, ids.Enrollment, ids.OldGridClass, PreviousValidTo,
            NewFixedClass(ids.Enrollment, ids.NewGridClass));
        Assert.True(write.IsReplaced);

        // Inside the unit the new window is already there, so what the rollback below erases is a real write and not a
        // write that never happened.
        Assert.Equal(2, await ReadScalarInUnitAsync<int>(
            session,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", ids.Enrollment)));
        Assert.Equal(1, await ReadScalarInUnitAsync<int>(
            session,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId AND ValidTo IS NULL",
            ("@enrollmentId", ids.Enrollment)));

        // The repository never commits: leaving the unit without committing puts storage back where it was.
        await transaction.DisposeAsync();

        var assignments = await ReadFixedClassesAsync(target.ConnectionString, ids.Enrollment);
        var open = Assert.Single(assignments);
        Assert.Equal(ids.OldFixedClass, open.Id);
        Assert.Equal(OldValidFrom, open.ValidFrom);
        Assert.Null(open.ValidTo);

        output.WriteLine("Rollback assertions passed: inside the unit both assignments existed and one was open, and after leaving it without committing storage kept only the original open window with its null ValidTo.");
    }

    [Fact]
    public async Task The_generated_session_of_the_affected_range_cannot_slip_in_between_the_guard_and_the_commit()
    {
        var target = await ResolveTargetAsync("_RangeLock");
        if (target is null)
            return;

        var ids = await SeedChangeAsync(
            target.ConnectionString, new ChangeSeed("synthetic-t17-rangelock", "20201717770"));

        // The affected range is empty: no session of either grid class exists yet, which is exactly the case where the
        // guard answers "no generated session" and lets the change proceed.
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessId)));

        await using var session = new DbSession(target.ConnectionString);
        var enrollments = new SqlEnrollmentRepository(session);
        var schedules = new SqlFixedScheduleChangeRepository(session);
        var transaction = await session.BeginAsync();

        // The same sequence the service runs, on a transaction this test keeps open: the grid class locks and the
        // enrollment lock, then the range read that finds nothing.
        await enrollments.LockGridClassesAsync(ids.BusinessId, new[] { ids.OldGridClass, ids.NewGridClass });
        Assert.NotNull(await enrollments.LockEnrollmentAsync(ids.BusinessId, ids.Enrollment));
        Assert.Null(await schedules.LastGeneratedSessionDateAsync(
            ids.BusinessId, new[] { ids.OldGridClass, ids.NewGridClass }, EffectiveFrom));

        // The real week generation (T13) tries to create the session of the new grid class inside that empty range on
        // another connection: the range lock the guard holds must not let it through before this unit commits. The
        // assertion is empirical — the real insert has to time out with error 1222 — and does not claim which lock
        // type the engine's plan took.
        Assert.True(
            await WeekGenerationTimesOutAsync(target.ConnectionString, ids, NewClassFutureSession),
            "The week generation wrote a session inside the range while the change's guard held it.");

        var write = await schedules.TryReplaceFixedClassAsync(
            ids.BusinessId, ids.Enrollment, ids.OldGridClass, PreviousValidTo,
            NewFixedClass(ids.Enrollment, ids.NewGridClass));
        Assert.True(write.IsReplaced);
        await transaction.CommitAsync();

        // Once the change committed, the same generation goes through: the block was the guard's range lock and not a
        // broken insert.
        var outcome = await ConfirmSessionAsync(target.ConnectionString, ids, NewClassFutureSession);
        Assert.Equal(1, outcome.CreatedSessions);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessId)));
        var generated = Assert.Single(await ReadSessionsAsync(target.ConnectionString, ids.BusinessId));
        Assert.Equal(ids.NewGridClass, generated.GridClassId);
        Assert.Equal(NewClassFutureSession, generated.StartsAt);

        // What the unit wrote while it held the range is the change itself: one open window from the effective date on.
        var assignments = await ReadFixedClassesAsync(target.ConnectionString, ids.Enrollment);
        Assert.Equal(2, assignments.Count);
        var opened = Assert.Single(assignments, row => row.ValidTo is null);
        Assert.Equal(ids.NewGridClass, opened.GridClassId);

        output.WriteLine("Range-lock assertions passed: with no session of either affected grid class the guard answered null, the real week generation of the new class timed out while the guard transaction was open, the change then committed, and the very same generation succeeded right after the commit with exactly one session created.");
    }

    /// <summary>
    /// The first usable date the refusal asks for, formatted with the same invariant Gregorian pattern the service
    /// uses, so the assertion cannot depend on the date separator of the culture the machine runs under.
    /// </summary>
    private static string FirstUsable(DateTime lastGeneratedSession) =>
        DateOnly.FromDateTime(lastGeneratedSession).AddDays(1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// The service under test, driven by the two real SQL repositories and the same unit of work they share.
    /// </summary>
    private static FixedScheduleChangeService CreateService(DbSession session) =>
        new(new SqlEnrollmentRepository(session), new SqlFixedScheduleChangeRepository(session), session,
            new FakeClock { Now = FixedNow });

    /// <summary>The new fixed assignment the change opens: the new grid class from the effective date, open ended.</summary>
    private static EnrollmentFixedClass NewFixedClass(int enrollmentId, int gridClassId) => new()
    {
        EnrollmentId = enrollmentId,
        GridClassId = gridClassId,
        ValidFrom = EffectiveFrom,
        ValidTo = null
    };

    /// <summary>
    /// Runs the real week generation (T13) for one session of the new grid class on its own connection and reports
    /// whether the insert could not finish in time because someone else was holding the range. It drives the
    /// production repository, not a copy of its statement, so what blocks is the actual T13 insert.
    /// </summary>
    private static async Task<bool> WeekGenerationTimesOutAsync(
        string connectionString, ChangeIds ids, DateTime startsAt)
    {
        await using var session = new DbSession(connectionString);
        var connection = await session.GetOpenConnectionAsync();
        await using (var timeout = new SqlCommand("SET LOCK_TIMEOUT 2000", connection))
            await timeout.ExecuteNonQueryAsync();

        try
        {
            await ConfirmSessionAsync(session, ids, startsAt);
            return false;
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            return true;
        }
    }

    /// <summary>Builds and confirms a one-session week for the new grid class, with no student to annotate.</summary>
    private static async Task<WeekGenerationWriteOutcome> ConfirmSessionAsync(
        string connectionString, ChangeIds ids, DateTime startsAt)
    {
        await using var session = new DbSession(connectionString);
        return await ConfirmSessionAsync(session, ids, startsAt);
    }

    private static async Task<WeekGenerationWriteOutcome> ConfirmSessionAsync(
        DbSession session, ChangeIds ids, DateTime startsAt)
    {
        var plan = new WeekGenerationPlan(
            ids.BusinessId,
            DateOnly.FromDateTime(startsAt),
            DateOnly.FromDateTime(startsAt),
            FixedNow,
            new[]
            {
                new WeekSessionPlan(
                    ids.NewGridClass,
                    ids.ServiceId,
                    ids.WorkerId,
                    DateOnly.FromDateTime(startsAt),
                    TimeOnly.FromDateTime(startsAt),
                    ServiceDurationMinutes,
                    SessionCapacity,
                    null,
                    Array.Empty<WeekStudentToAnnotate>())
            },
            ExistingSessionCount: 0,
            PendingRenewalStudentCount: 0);

        await using var transaction = await session.BeginAsync();
        var outcome = await new SqlWeekGenerationRepository(session).ConfirmWeekAsync(plan);
        await transaction.CommitAsync();
        return outcome;
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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T17_SQL_CONNECTION and
        // the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT17Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT17Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T17 SQL integration test",
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
    /// One tenant ready for a schedule change: the two grid classes it moves between, an unrelated third one, an
    /// enrollment with an open assignment on the old class and a paid month, and only the generated sessions the
    /// requested parts ask for.
    /// </summary>
    private static async Task<ChangeIds> SeedChangeAsync(
        string connectionString, ChangeSeed seed, DateTime? now = null)
    {
        var instant = now ?? FixedNow;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessId = await InsertBusinessAsync(connection, transaction, seed.Slug, instant);
        var serviceId = await InsertServiceAsync(
            connection, transaction, businessId, $"{seed.Slug} class", ServiceDurationMinutes, seed.Capacity);
        var workerId = await InsertWorkerAsync(connection, transaction, businessId, seed.Slug, seed.Cuil);
        var clientId = await InsertClientAsync(connection, transaction, businessId, $"{seed.Slug}-client", instant);

        var oldGridClass = await InsertGridClassAsync(
            connection, transaction, businessId, serviceId, workerId, DayOfWeek.Thursday, new TimeOnly(18, 0));
        var newGridClass = await InsertGridClassAsync(
            connection, transaction, businessId, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(19, 0));
        var unrelatedGridClass = await InsertGridClassAsync(
            connection, transaction, businessId, serviceId, workerId, DayOfWeek.Saturday, new TimeOnly(10, 0));

        var enrollment = await InsertEnrollmentAsync(
            connection, transaction, businessId, clientId, seed.Status, instant);
        var oldFixedClass = await InsertFixedClassAsync(
            connection, transaction, enrollment, oldGridClass, OldValidFrom, null);
        var (period, payment) = await InsertMembershipAsync(
            connection, transaction, businessId, enrollment, PeriodStart, PeriodEnd, instant);

        int? pastSession = null;
        if (seed.Parts.HasFlag(SeedParts.PastSession))
        {
            // The session already happened: it carries the fixed annotation the change must leave exactly as it is.
            pastSession = await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, oldGridClass, PastSession, instant);
            await InsertBookingAsync(
                connection, transaction, businessId, pastSession.Value, clientId, enrollment, instant);
        }

        if (seed.Parts.HasFlag(SeedParts.OldClassFutureSession))
            await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, oldGridClass, OldClassFutureSession, instant);
        if (seed.Parts.HasFlag(SeedParts.NewClassFutureSession))
            await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, newGridClass, NewClassFutureSession, instant);
        if (seed.Parts.HasFlag(SeedParts.UnrelatedSession))
            await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, unrelatedGridClass, UnrelatedSession, instant);

        // Seats retained on the contested grid class by other members. Their windows start in June, so they prove the
        // place is held from registration (Regla 23, D-21) instead of from the start of the window.
        for (var seat = 0; seat < seed.AdditionalOccupiedSeats; seat++)
        {
            var occupyingClient = await InsertClientAsync(
                connection, transaction, businessId, $"{seed.Slug}-seat-{seat}", instant);
            var occupyingEnrollment = await InsertEnrollmentAsync(
                connection, transaction, businessId, occupyingClient, EnrollmentStatus.Active, instant);
            await InsertFixedClassAsync(
                connection, transaction, occupyingEnrollment, newGridClass, FutureSeatValidFrom, null);
        }

        await transaction.CommitAsync();
        return new ChangeIds(
            businessId, serviceId, workerId, clientId, oldGridClass, newGridClass, unrelatedGridClass,
            enrollment, oldFixedClass, period, payment, pastSession);
    }

    private static Task<int> InsertBusinessAsync(
        SqlConnection connection, SqlTransaction transaction, string slug, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @allowsWalkIns, @createdAt)",
            ("@name", slug), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@allowsWalkIns", true), ("@createdAt", now));

    private static Task<int> InsertServiceAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name, int durationMinutes,
        int maxCapacity) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, @capacity)",
            ("@businessId", businessId), ("@name", name), ("@price", 100.00m),
            ("@duration", durationMinutes), ("@capacity", maxCapacity));

    private static Task<int> InsertWorkerAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string lastName, string cuil) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", businessId), ("@firstName", "Synthetic"), ("@lastName", lastName),
            ("@phone", "0000000000"), ("@cuil", cuil));

    private static Task<int> InsertClientAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
            ("@businessId", businessId), ("@firstName", name), ("@lastName", "Synthetic"),
            ("@email", $"{name}@example.invalid"), ("@phone", "0000000000"), ("@createdAt", now));

    private static Task<int> InsertGridClassAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DayOfWeek day, TimeOnly startTime) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO GridClasses (BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime, IsActive) OUTPUT INSERTED.Id VALUES (@businessId, @serviceId, @workerId, @dayOfWeek, @startTime, 1)",
            ("@businessId", businessId), ("@serviceId", serviceId), ("@workerId", workerId),
            ("@dayOfWeek", day), ("@startTime", startTime));

    private static Task<int> InsertEnrollmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId,
        EnrollmentStatus status, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, ReferenceDay, CreatedAt, ActivatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @referenceDay, @createdAt, @activatedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", WeeklyFrequency),
            ("@status", status), ("@price", AgreedPrice), ("@referenceDay", ReferenceDay),
            ("@createdAt", now), ("@activatedAt", now));

    private static Task<int> InsertFixedClassAsync(
        SqlConnection connection, SqlTransaction transaction, int enrollmentId, int gridClassId,
        DateOnly validFrom, DateOnly? validTo) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO EnrollmentFixedClasses (EnrollmentId, GridClassId, ValidFrom, ValidTo) OUTPUT INSERTED.Id VALUES (@enrollmentId, @gridClassId, @validFrom, @validTo)",
            ("@enrollmentId", enrollmentId), ("@gridClassId", gridClassId), ("@validFrom", validFrom),
            ("@validTo", validTo));

    private static async Task<(int Period, int Payment)> InsertMembershipAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int enrollmentId,
        DateOnly coverageStart, DateOnly expiresOn, DateTime now)
    {
        var paymentId = await InsertIdAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, NULL, @concept, NULL, @enrollmentId, @amount, NULL, NULL, @paidAt, @recordedAt)",
            ("@businessId", businessId), ("@concept", PaymentConcept.Membership), ("@enrollmentId", enrollmentId),
            ("@amount", AgreedPrice), ("@paidAt", now), ("@recordedAt", now));
        var periodId = await InsertIdAsync(connection, transaction,
            "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) OUTPUT INSERTED.Id VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
            ("@enrollmentId", enrollmentId), ("@coverageStart", coverageStart), ("@expiresOn", expiresOn),
            ("@paymentId", paymentId), ("@createdAt", now));
        return (periodId, paymentId);
    }

    private static Task<int> InsertClassSessionAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        int gridClassId, DateTime startsAt, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, @gridClassId, @startsAt, @endsAt, @capacity, NULL, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.ClassSession), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@gridClassId", gridClassId), ("@startsAt", startsAt),
            ("@endsAt", startsAt.AddMinutes(ServiceDurationMinutes)), ("@capacity", SessionCapacity),
            ("@status", AppointmentStatus.Scheduled), ("@createdAt", now));

    private static Task<int> InsertBookingAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int appointmentId, int clientId,
        int enrollmentId, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, @createdAt)",
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@enrollmentId", enrollmentId), ("@modality", BookingModality.Fixed), ("@status", BookingStatus.Pending),
            ("@createdAt", now));

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
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

    private static async Task<List<FixedClassSnapshot>> ReadFixedClassesAsync(
        string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, EnrollmentId, GridClassId, ValidFrom, ValidTo FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId ORDER BY ValidFrom, Id",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<FixedClassSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new FixedClassSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                DateOnly.FromDateTime(reader.GetDateTime(3)),
                reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4))));
        }

        return rows;
    }

    private static async Task<EnrollmentSnapshot> ReadEnrollmentAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Status, WeeklyFrequency, AgreedPrice, ReferenceDay, ActivatedAt, AnnulledAt, TerminatedAt FROM Enrollments WHERE Id = @enrollmentId",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Enrollment {enrollmentId} was not found.");
        return new EnrollmentSnapshot(
            (EnrollmentStatus)reader.GetByte(0),
            reader.GetInt32(1),
            reader.GetDecimal(2),
            reader.IsDBNull(3) ? null : reader.GetByte(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6));
    }

    private static async Task<List<PeriodSnapshot>> ReadPeriodsAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, CoverageStart, ExpiresOn, PaymentId FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId ORDER BY CoverageStart, Id",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<PeriodSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new PeriodSnapshot(
                reader.GetInt32(0),
                DateOnly.FromDateTime(reader.GetDateTime(1)),
                DateOnly.FromDateTime(reader.GetDateTime(2)),
                reader.GetInt32(3)));
        }

        return rows;
    }

    private static async Task<List<SessionSnapshot>> ReadSessionsAsync(string connectionString, int businessId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, GridClassId, StartsAt, Status FROM Appointments WHERE BusinessId = @businessId ORDER BY Id",
            connection);
        AddParameter(command, "@businessId", businessId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<SessionSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new SessionSnapshot(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDateTime(2),
                (AppointmentStatus)reader.GetByte(3)));
        }

        return rows;
    }

    private static async Task<List<BookingSnapshot>> ReadBookingsAsync(string connectionString, int businessId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, AppointmentId, ClientId, EnrollmentId, Modality, Status FROM Bookings WHERE BusinessId = @businessId ORDER BY Id",
            connection);
        AddParameter(command, "@businessId", businessId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<BookingSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new BookingSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : (BookingModality)reader.GetByte(4),
                (BookingStatus)reader.GetByte(5)));
        }

        return rows;
    }

    private static SqlCommand Create(
        SqlConnection connection, SqlTransaction? transaction, string sql,
        (string Name, object? Value)[] parameters)
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
            case TimeOnly time:
                command.Parameters.Add(name, SqlDbType.Time).Value = time;
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

    /// <summary>Which generated sessions the tenant of a seed has to carry before the change runs.</summary>
    [Flags]
    private enum SeedParts
    {
        None = 0,
        PastSession = 1,
        OldClassFutureSession = 2,
        NewClassFutureSession = 4,
        UnrelatedSession = 8
    }

    private sealed record ChangeSeed(
        string Slug,
        string Cuil,
        SeedParts Parts = SeedParts.None,
        int Capacity = SessionCapacity,
        EnrollmentStatus Status = EnrollmentStatus.Active,
        int AdditionalOccupiedSeats = 0);

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record ChangeIds(
        int BusinessId,
        int ServiceId,
        int WorkerId,
        int ClientId,
        int OldGridClass,
        int NewGridClass,
        int UnrelatedGridClass,
        int Enrollment,
        int OldFixedClass,
        int Period,
        int Payment,
        int? PastSession);

    private sealed record FixedClassSnapshot(
        int Id, int EnrollmentId, int GridClassId, DateOnly ValidFrom, DateOnly? ValidTo);

    private sealed record EnrollmentSnapshot(
        EnrollmentStatus Status,
        int WeeklyFrequency,
        decimal AgreedPrice,
        int? ReferenceDay,
        DateTime? ActivatedAt,
        DateTime? AnnulledAt,
        DateTime? TerminatedAt);

    private sealed record PeriodSnapshot(int Id, DateOnly CoverageStart, DateOnly ExpiresOn, int PaymentId);

    private sealed record SessionSnapshot(int Id, int? GridClassId, DateTime StartsAt, AppointmentStatus Status);

    private sealed record BookingSnapshot(
        int Id, int AppointmentId, int ClientId, int? EnrollmentId, BookingModality? Modality, BookingStatus Status);
}
