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
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Classes;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlClassDropInRepository"/>. Requires a new, dedicated
/// database supplied through WORKUP_T14_SQL_CONNECTION; without it every test returns N/A and no SQL connection is
/// opened. Each test derives its own database name from the supplied one, so the whole class runs in a single command,
/// and nothing is torn down: the target is retained on purpose.
/// </summary>
public sealed class ClassDropInSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T14_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>The inclusive date range used by the listing test: Monday 9 to Wednesday 11 of March 2026.</summary>
    private static readonly DateOnly ListFrom = new(2026, 3, 9);
    private static readonly DateOnly ListTo = new(2026, 3, 11);

    private static readonly DateTime FixedNow = new(2026, 3, 9, 8, 0, 0);

    [Fact]
    public async Task Listing_returns_only_class_sessions_of_the_business_inside_the_half_open_date_range()
    {
        var target = await ResolveTargetAsync("_List");
        if (target is null)
            return;

        var ids = await SeedListAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassDropInRepository(session);

        // Without a service filter: four class sessions of business A start inside Monday 9 .. Wednesday 11, the
        // 2026-03-09 00:00 session exactly at the first instant and the 2026-03-11 23:59:59 one at the last
        // representable instant of the last day. The individual turn, the foreign session and the 2026-03-12 00:00
        // session are outside the answer.
        var all = await repository.ListSessionsAsync(ids.BusinessA, ListFrom, ListTo, serviceId: null);
        Assert.Equal(4, all.Count);
        Assert.Equal(
            new[] { ids.SessionAtRangeStart, ids.SessionOfOtherService, ids.SessionInside, ids.SessionAtLastInstantOfEndDay },
            all.Select(row => row.AppointmentId).ToArray());

        Assert.DoesNotContain(all, row => row.AppointmentId == ids.IndividualAppointment);
        Assert.DoesNotContain(all, row => row.AppointmentId == ids.SessionAtDayAfterEnd);
        Assert.DoesNotContain(all, row => row.AppointmentId == ids.SessionOfOtherBusiness);

        // The session exactly at the requested start instant is inside the range.
        var atStart = Assert.Single(all, row => row.AppointmentId == ids.SessionAtRangeStart);
        Assert.Equal(ListFrom, atStart.Date);
        Assert.Equal(TimeOnly.MinValue, atStart.StartTime);
        Assert.Equal(AppointmentKind.ClassSession, atStart.Kind);
        Assert.Equal(AppointmentStatus.Scheduled, atStart.Status);
        Assert.Equal(8, atStart.MaxCapacity);
        // A class nobody booked yet is the most bookable one, so it is returned with an empty booking list.
        Assert.Empty(atStart.Bookings);

        // Every booking of the session travels with it, cancelled ones included, and the caller counts the places.
        var inside = Assert.Single(all, row => row.AppointmentId == ids.SessionInside);
        Assert.Equal(ListFrom.AddDays(1), inside.Date);
        Assert.Equal(new TimeOnly(10, 0), inside.StartTime);
        Assert.Equal(ids.ServiceA, inside.ServiceId);
        Assert.Equal(ids.WorkerA, inside.WorkerId);
        Assert.Equal(2, inside.Bookings.Count);
        Assert.Contains(inside.Bookings, booking =>
            booking.ClientId == ids.FirstClientOfA && booking.Status == BookingStatus.Pending);
        Assert.Contains(inside.Bookings, booking =>
            booking.ClientId == ids.SecondClientOfA && booking.Status == BookingStatus.Cancelled);

        // The last representable instant of the inclusive last day is still inside the half-open bound.
        var lastInstant = Assert.Single(all, row => row.AppointmentId == ids.SessionAtLastInstantOfEndDay);
        Assert.Equal(ListTo, lastInstant.Date);
        Assert.Equal(new TimeOnly(23, 59, 59), lastInstant.StartTime);

        // Optional service filter: the evening class drops out and nothing else changes.
        var filtered = await repository.ListSessionsAsync(ids.BusinessA, ListFrom, ListTo, ids.ServiceA);
        Assert.Equal(3, filtered.Count);
        Assert.Equal(
            new[] { ids.SessionAtRangeStart, ids.SessionInside, ids.SessionAtLastInstantOfEndDay },
            filtered.Select(row => row.AppointmentId).ToArray());
        Assert.DoesNotContain(filtered, row => row.AppointmentId == ids.SessionOfOtherService);

        // A window narrower than the seed keeps both boundary sessions out, in both directions.
        var singleDay = await repository.ListSessionsAsync(ids.BusinessA, ListFrom.AddDays(1), ListFrom.AddDays(1), ids.ServiceA);
        var onlyInside = Assert.Single(singleDay);
        Assert.Equal(ids.SessionInside, onlyInside.AppointmentId);

        output.WriteLine("List assertions passed: only ClassSession rows of business A inside the half-open range, both boundaries pinned, service filter honoured, cancelled booking attached and foreign/individual appointments excluded.");
    }

    [Fact]
    public async Task Identifying_a_client_by_email_stays_inside_the_business_and_never_creates_a_password()
    {
        var target = await ResolveTargetAsync("_Clients");
        if (target is null)
            return;

        var ids = await SeedClientsAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassDropInRepository(session);

        // The requested email is normalized before it is stored, and the client is created without credentials: a
        // password would live in ClientAccounts, which stays empty for it.
        var identified = await repository.IdentifyClientAsync(
            ids.BusinessA, "  New.Client@Example.INVALID ", "New", "Client", "1122334455");
        Assert.True(identified.ClientId > 0);
        Assert.Equal("new.client@example.invalid", identified.Email);
        Assert.Equal("New", identified.FirstName);
        Assert.Equal("Client", identified.LastName);

        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", ids.BusinessA), ("@email", "new.client@example.invalid")));
        Assert.Equal("1122334455", await ReadScalarAsync<string>(target.ConnectionString,
            "SELECT Phone FROM Clients WHERE Id = @clientId", ("@clientId", identified.ClientId)));
        Assert.Null(await ReadScalarAsync<string>(target.ConnectionString,
            "SELECT HealthNotes FROM Clients WHERE Id = @clientId", ("@clientId", identified.ClientId)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM ClientAccounts WHERE ClientId = @clientId", ("@clientId", identified.ClientId)));

        // Idempotent for the same email in the same business, whatever the case or the new contact data: the stored
        // client is returned as it is, never duplicated and never updated.
        var again = await repository.IdentifyClientAsync(
            ids.BusinessA, "NEW.CLIENT@example.invalid", "Otro", "Nombre", null);
        Assert.Equal(identified.ClientId, again.ClientId);
        Assert.Equal("new.client@example.invalid", again.Email);
        Assert.Equal("New", again.FirstName);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", ids.BusinessA), ("@email", "new.client@example.invalid")));

        // The same email in another business is another client, and the identifier stays editable inside the business.
        var otherBusiness = await repository.IdentifyClientAsync(
            ids.BusinessB, "new.client@example.invalid", "New", "Client", null);
        Assert.NotEqual(identified.ClientId, otherBusiness.ClientId);
        Assert.Equal(ids.BusinessB, await ReadScalarAsync<int>(target.ConnectionString,
            "SELECT BusinessId FROM Clients WHERE Id = @clientId", ("@clientId", otherBusiness.ClientId)));

        // Tenant scoping of FindClient: a client of another business is simply not there, in both directions.
        Assert.NotNull(await repository.FindClientAsync(ids.BusinessA, identified.ClientId));
        Assert.Null(await repository.FindClientAsync(ids.BusinessA, otherBusiness.ClientId));
        Assert.Null(await repository.FindClientAsync(ids.BusinessB, identified.ClientId));
        Assert.Null(await repository.FindClientAsync(ids.BusinessA, int.MaxValue));
        var found = await repository.FindClientAsync(ids.BusinessA, identified.ClientId);
        Assert.Equal("new.client@example.invalid", found!.Email);
        Assert.Equal("Client", found.LastName);

        // A stored email with different casing is that same client, not a new one, and the returned Email is the
        // stored value — the recipient of the notice. This rests on the case-insensitive collation the unique index
        // UQ_Clients_Email itself relies on, and the second assertion pins that assumption.
        var legacy = await repository.IdentifyClientAsync(
            ids.BusinessA, "legacy.mixedcase@example.invalid", "Nuevo", "Nombre", null);
        Assert.Equal(ids.LegacyClientOfA, legacy.ClientId);
        Assert.Equal("Legacy.MixedCase@Example.invalid", legacy.Email);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email LIKE @pattern",
            ("@businessId", ids.BusinessA), ("@pattern", "%mixedcase@example.invalid")));
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));

        output.WriteLine("Client assertions passed: email normalized on creation, no ClientAccounts row, idempotent per business, separate client for the same email in another business, foreign ids invisible, and a differently cased stored email resolved to the stored client.");
    }

    [Fact]
    public async Task Guarded_insert_writes_the_drop_in_and_names_a_full_or_already_booked_session()
    {
        var target = await ResolveTargetAsync("_Write");
        if (target is null)
            return;

        var ids = await SeedWriteAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassDropInRepository(session);

        // The session with free places takes the booking, written as Suelta and Pendiente for exactly the requested
        // business, appointment, client and instant.
        var inserted = await repository.TryInsertDropInAsync(
            new DropInInsert(ids.BusinessA, ids.OpenSession, ids.FirstClient, FixedNow));
        Assert.True(inserted.IsInserted, inserted.Result.ToString());
        Assert.True(inserted.BookingId > 0);
        var row = await ReadBookingAsync(target.ConnectionString, inserted.BookingId);
        Assert.Equal(ids.BusinessA, row.BusinessId);
        Assert.Equal(ids.OpenSession, row.AppointmentId);
        Assert.Equal(ids.FirstClient, row.ClientId);
        Assert.Null(row.EnrollmentId);
        Assert.Equal(BookingModality.DropIn, row.Modality);
        Assert.Equal(BookingStatus.Pending, row.Status);
        Assert.Null(row.CancelledAt);
        Assert.Equal(FixedNow, row.CreatedAt);

        // The capacity rule counts every non-cancelled booking against MaxCapacity, whichever modality it carries:
        // the fixed annotation pending payment and the confirmed drop-in fill a class of two, while the third row,
        // cancelled, occupies nothing. The same client's cancelled row is not an "already booked" either.
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND Status <> @cancelled",
            ("@appointmentId", ids.FullSession), ("@cancelled", BookingStatus.Cancelled)));
        var full = await repository.TryInsertDropInAsync(
            new DropInInsert(ids.BusinessA, ids.FullSession, ids.FirstClient, FixedNow));
        Assert.Equal(DropInInsertResultType.NoPlaceLeft, full.Result);
        Assert.False(full.IsInserted);
        Assert.Equal(0, full.BookingId);
        Assert.Equal(3, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.FullSession)));

        // A client that already holds a non-cancelled booking of the session hears that, not the unique-key error,
        // and nothing is written for her.
        var alreadyBooked = await repository.TryInsertDropInAsync(
            new DropInInsert(ids.BusinessA, ids.BookedSession, ids.FirstClient, FixedNow));
        Assert.Equal(DropInInsertResultType.AlreadyBooked, alreadyBooked.Result);
        Assert.False(alreadyBooked.IsInserted);
        Assert.Equal(0, alreadyBooked.BookingId);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.BookedSession)));

        // Cancelling releases the place, so the same client books the same session again.
        await ExecuteAsync(target.ConnectionString,
            "UPDATE Bookings SET Status = @cancelled, CancelledAt = @now WHERE AppointmentId = @appointmentId AND ClientId = @clientId AND Status <> @cancelled",
            ("@cancelled", BookingStatus.Cancelled), ("@now", FixedNow),
            ("@appointmentId", ids.BookedSession), ("@clientId", ids.FirstClient));
        var afterCancellation = await repository.TryInsertDropInAsync(
            new DropInInsert(ids.BusinessA, ids.BookedSession, ids.FirstClient, FixedNow));
        Assert.True(afterCancellation.IsInserted, afterCancellation.Result.ToString());
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.BookedSession)));

        // An individual appointment is not a class session: the guard refuses to write and the port fails loudly
        // instead of reporting an outcome it cannot know about.
        var individual = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryInsertDropInAsync(
            new DropInInsert(ids.BusinessA, ids.IndividualAppointment, ids.FirstClient, FixedNow)));
        Assert.Contains("no defined outcome", individual.Message, StringComparison.Ordinal);

        // Outbox: the same dedup key is queued once, and the stored row keeps what the caller composed.
        var notification = new Notification
        {
            BusinessId = ids.BusinessA,
            Type = NotificationType.DropInReminder,
            Recipient = "t14.write.client1@example.invalid",
            Subject = "Recordatorio de tu clase suelta",
            BodyHtml = "<p>Hola Client,</p>",
            DedupKey = $"reminder:booking:{inserted.BookingId}",
            CreatedAt = FixedNow
        };
        await repository.EnqueueNotificationAsync(notification);
        await repository.EnqueueNotificationAsync(notification);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE DedupKey = @dedupKey",
            ("@dedupKey", notification.DedupKey)));
        var queued = await ReadNotificationAsync(target.ConnectionString, notification.DedupKey);
        Assert.Equal(NotificationType.DropInReminder, queued.Type);
        Assert.Equal("t14.write.client1@example.invalid", queued.Recipient);
        Assert.Equal("Recordatorio de tu clase suelta", queued.Subject);
        Assert.Equal("<p>Hola Client,</p>", queued.BodyHtml);

        output.WriteLine("Write assertions passed: the guarded insert wrote Modality=Suelta/Status=Pendiente with the caller's id and instant, the class filled by a fixed and a drop-in annotation returned NoPlaceLeft without writing, the same client could not double-book, a cancelled booking freed the place, an individual appointment failed loudly and the outbox deduplicated one notice.");
    }

    [Fact]
    public async Task Two_bookings_for_the_last_place_leave_exactly_one_inserted_booking()
    {
        var target = await ResolveTargetAsync("_Race");
        if (target is null)
            return;

        var ids = await SeedRaceAsync(target.ConnectionString, FixedNow);
        Assert.Equal(2, ids.ClientIds.Length);

        // A bare Task.WhenAll over two async calls does not force real overlap: each runs synchronously up to its
        // first await, so the first booking could take the session lock, insert and commit before the second one even
        // starts, leaving nothing to serialize. The barrier pins both bookings on their own thread-pool thread and
        // releases them at the same instant, right before each opens its transaction and asks for the session's
        // UPDLOCK. That is what proves the lock serializes the count and the guarded insert: without it, both could
        // read the last place as free and overbook the class.
        using var startGate = new Barrier(participantCount: 2);

        Task<DropInInsertResult> BookAsync(int clientId) => Task.Run(async () =>
        {
            await using var raceSession = new DbSession(target.ConnectionString);
            var raceRepository = new SqlClassDropInRepository(raceSession);
            await using var transaction = await raceSession.BeginAsync();
            startGate.SignalAndWait();
            var locked = await raceRepository.LockSessionAsync(ids.BusinessA, ids.Session);
            Assert.NotNull(locked);
            var result = await raceRepository.TryInsertDropInAsync(
                new DropInInsert(ids.BusinessA, ids.Session, clientId, FixedNow));
            await transaction.CommitAsync();
            return result;
        });

        var results = await Task.WhenAll(BookAsync(ids.ClientIds[0]), BookAsync(ids.ClientIds[1]));

        // Which one wins depends on the interleaving; the outcome does not: one booking takes the last place and the
        // other reports it lost, without writing anything.
        var winner = Assert.Single(results, result => result.Result == DropInInsertResultType.Inserted);
        var loser = Assert.Single(results, result => result.Result == DropInInsertResultType.NoPlaceLeft);
        Assert.True(winner.BookingId > 0);
        Assert.Equal(0, loser.BookingId);

        // The surviving row is the winner's, on the contended session and with the shape the port promises.
        var winningRow = await ReadBookingAsync(target.ConnectionString, winner.BookingId);
        Assert.Equal(ids.BusinessA, winningRow.BusinessId);
        Assert.Equal(ids.Session, winningRow.AppointmentId);
        Assert.Contains(winningRow.ClientId, ids.ClientIds);
        Assert.Equal(BookingModality.DropIn, winningRow.Modality);
        Assert.Equal(BookingStatus.Pending, winningRow.Status);

        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND Modality = @modality AND Status = @status",
            ("@appointmentId", ids.Session), ("@modality", BookingModality.DropIn), ("@status", BookingStatus.Pending)));

        output.WriteLine("Race assertions passed: two concurrent bookings for the last place ended with exactly one inserted and one NoPlaceLeft, and exactly one booking row exists for the session.");
    }

    [Fact]
    public async Task A_caller_that_never_commits_leaves_no_client_and_no_booking_behind()
    {
        var target = await ResolveTargetAsync("_Rollback");
        if (target is null)
            return;

        var ids = await SeedRollbackAsync(target.ConnectionString, FixedNow);
        const string email = "t14.rollback@example.invalid";

        await using (var session = new DbSession(target.ConnectionString))
        {
            var repository = new SqlClassDropInRepository(session);

            // The transaction owns the commit; leaving this scope without committing rolls the whole unit back,
            // client creation included.
            await using (var transaction = await session.BeginAsync())
            {
                var client = await repository.IdentifyClientAsync(ids.BusinessA, email, "Rollback", "Client", null);
                Assert.True(client.ClientId > 0);
                Assert.NotNull(await repository.FindClientAsync(ids.BusinessA, client.ClientId));

                var insert = await repository.TryInsertDropInAsync(
                    new DropInInsert(ids.BusinessA, ids.Session, client.ClientId, FixedNow));
                Assert.True(insert.IsInserted, insert.Result.ToString());

                // Both writes are visible inside the unit of work, so what disappears below is a rollback and not a
                // pair of calls that never wrote anything.
                var insideUnit = await repository.ListSessionsAsync(
                    ids.BusinessA, ListFrom, ListFrom.AddDays(30), serviceId: null);
                var sessionInsideUnit = Assert.Single(insideUnit, row => row.AppointmentId == ids.Session);
                Assert.Contains(sessionInsideUnit.Bookings, booking => booking.ClientId == client.ClientId);
            }
        }

        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", ids.BusinessA), ("@email", email)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Appointments WHERE Id = @appointmentId", ("@appointmentId", ids.Session)));

        output.WriteLine("Rollback assertions passed: the client and the booking were visible inside the transaction and both were gone after disposing it without committing.");
    }

    [Fact]
    public async Task Roster_returns_only_the_drop_in_annotations_of_the_business_with_the_client_name()
    {
        var target = await ResolveTargetAsync("_Roster");
        if (target is null)
            return;

        var ids = await SeedRosterAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassDropInRepository(session);

        var roster = await repository.ListUpcomingRosterAsync(ids.BusinessA, FixedNow);

        // Two upcoming sessions of the business, ordered by start: the empty one first, then the annotated one. The
        // started session, the individual turn and the session of the other business are not part of the answer.
        Assert.Equal(2, roster.Count);
        Assert.Equal(
            new[] { ids.SessionWithoutAnnotations, ids.SessionUpcoming },
            roster.Select(row => row.AppointmentId).ToArray());

        var empty = roster[0];
        Assert.Empty(empty.Bookings);
        Assert.Equal(4, empty.MaxCapacity);

        var upcoming = roster[1];
        Assert.Equal(ids.ServiceA, upcoming.ServiceId);
        Assert.Equal(ids.WorkerA, upcoming.WorkerId);
        Assert.Equal(8, upcoming.MaxCapacity);
        Assert.Equal(AppointmentKind.ClassSession, upcoming.Kind);
        Assert.Equal(AppointmentStatus.Scheduled, upcoming.Status);

        // Only the drop-in annotations travel with the session, and the fixed one is never among them whatever its
        // status is. The cancelled drop-in is kept, with its status and its client, so the owner can see the released
        // place and who released it.
        Assert.DoesNotContain(upcoming.Bookings, booking => booking.BookingId == ids.FixedBooking);
        Assert.All(upcoming.Bookings, booking => Assert.Equal(BookingModality.DropIn, booking.Modality));
        var pending = Assert.Single(upcoming.Bookings, booking => booking.BookingId == ids.PendingDropIn);
        Assert.Equal(ids.FirstClient, pending.ClientId);
        Assert.Equal("Ana Perez", pending.ClientName);
        Assert.Equal(BookingStatus.Pending, pending.Status);
        var cancelled = Assert.Single(upcoming.Bookings, booking => booking.BookingId == ids.CancelledDropIn);
        Assert.Equal(ids.SecondClient, cancelled.ClientId);
        Assert.Equal("Bella Cancelada", cancelled.ClientName);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);

        Assert.DoesNotContain(roster, row => row.AppointmentId == ids.SessionStarted);
        Assert.DoesNotContain(roster, row => row.AppointmentId == ids.IndividualAppointment);
        Assert.DoesNotContain(roster, row => row.AppointmentId == ids.SessionOfOtherBusiness);
        Assert.DoesNotContain(roster, row => row.Bookings.Any(booking => booking.BookingId == ids.ForeignDropIn));

        // The other tenant sees its own annotation and nothing of business A's.
        var otherRoster = await repository.ListUpcomingRosterAsync(ids.BusinessB, FixedNow);
        var otherSession = Assert.Single(otherRoster);
        Assert.Equal(ids.SessionOfOtherBusiness, otherSession.AppointmentId);
        var foreign = Assert.Single(otherSession.Bookings);
        Assert.Equal(ids.ForeignDropIn, foreign.BookingId);
        Assert.Equal("Otra Alumna", foreign.ClientName);
        Assert.Equal(BookingModality.DropIn, foreign.Modality);

        output.WriteLine("Roster assertions passed: upcoming class sessions of the business ordered by start, empty sessions kept, drop-in annotations with booking id and client name, cancelled annotation kept with its status, and the fixed annotation, the individual turn, the started session and the other business all excluded.");
    }

    [Fact]
    public async Task Cancelling_a_drop_in_marks_the_owner_and_releases_the_place_for_the_same_client()
    {
        var target = await ResolveTargetAsync("_Cancel");
        if (target is null)
            return;

        var ids = await SeedCancellationAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var clock = new FakeClock { Now = FixedNow };
        var service = new ClassDropInService(new SqlClassDropInRepository(session), session, clock);

        // The unit the service opened committed: the row carries the status, the instant of the single clock read and
        // the owner as the author of the cancellation.
        var cancelled = await service.CancelDropInAsync(ids.BusinessA, ids.DropInBooking);
        Assert.True(cancelled.Succeeded, cancelled.Error?.Code);
        var row = await ReadBookingAsync(target.ConnectionString, ids.DropInBooking);
        Assert.Equal(BookingStatus.Cancelled, row.Status);
        Assert.Equal(FixedNow, row.CancelledAt);
        Assert.Equal(CancelledBy.Owner, row.CancelledBy);

        // The second cancellation is rejected and the original instant and author survive: the guarded statement
        // changes nothing once the annotation is cancelled.
        clock.Now = FixedNow.AddMinutes(1);
        var again = await service.CancelDropInAsync(ids.BusinessA, ids.DropInBooking);
        Assert.False(again.Succeeded);
        Assert.Equal("drop_in_already_cancelled", again.Error!.Code);
        var unchanged = await ReadBookingAsync(target.ConnectionString, ids.DropInBooking);
        Assert.Equal(BookingStatus.Cancelled, unchanged.Status);
        Assert.Equal(FixedNow, unchanged.CancelledAt);
        Assert.Equal(CancelledBy.Owner, unchanged.CancelledBy);

        // The cancellation released the place: the class was full — the fixed annotation plus the drop-in — and the
        // released place leaves one non-cancelled booking, which is what lets the same client book it again.
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId",
            ("@appointmentId", ids.Session)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND Status <> @cancelled",
            ("@appointmentId", ids.Session), ("@cancelled", BookingStatus.Cancelled)));
        clock.Now = FixedNow.AddMinutes(2);
        var rebooked = await service.BookForClientAsync(ids.BusinessA, ids.Session, ids.Client);
        Assert.True(rebooked.Succeeded, rebooked.Error?.Code);
        var rebookedRow = await ReadBookingAsync(target.ConnectionString, rebooked.Value.BookingId);
        Assert.Equal(ids.Session, rebookedRow.AppointmentId);
        Assert.Equal(ids.Client, rebookedRow.ClientId);
        Assert.Equal(BookingModality.DropIn, rebookedRow.Modality);
        Assert.Equal(BookingStatus.Pending, rebookedRow.Status);
        Assert.Null(rebookedRow.CancelledAt);
        Assert.Equal(3, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId", ("@appointmentId", ids.Session)));
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND Status <> @cancelled",
            ("@appointmentId", ids.Session), ("@cancelled", BookingStatus.Cancelled)));

        // A fixed annotation is not a drop-in, so it is answered exactly like a booking of another business or a
        // booking that does not exist, and none of the three is written.
        var fixedResult = await service.CancelDropInAsync(ids.BusinessA, ids.FixedBooking);
        var foreignResult = await service.CancelDropInAsync(ids.BusinessA, ids.ForeignDropIn);
        var missingResult = await service.CancelDropInAsync(ids.BusinessA, int.MaxValue);
        Assert.False(fixedResult.Succeeded);
        Assert.False(foreignResult.Succeeded);
        Assert.False(missingResult.Succeeded);
        Assert.Equal("drop_in_not_found", missingResult.Error!.Code);
        Assert.Equal(missingResult.Error!.Code, fixedResult.Error!.Code);
        Assert.Equal(missingResult.Error!.Message, fixedResult.Error!.Message);
        Assert.Equal(missingResult.Error!.Code, foreignResult.Error!.Code);
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.FixedBooking)).Status);
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.ForeignDropIn)).Status);

        // A suspended or already started class cannot be cancelled: the annotation keeps its status and no instant.
        var suspended = await service.CancelDropInAsync(ids.BusinessA, ids.SuspendedDropIn);
        clock.Now = FixedNow.AddMinutes(3);
        var started = await service.CancelDropInAsync(ids.BusinessA, ids.StartedDropIn);
        Assert.False(suspended.Succeeded);
        Assert.False(started.Succeeded);
        Assert.Equal("drop_in_not_available", suspended.Error!.Code);
        Assert.Equal("drop_in_not_available", started.Error!.Code);
        Assert.Equal(BookingStatus.Pending, (await ReadBookingAsync(target.ConnectionString, ids.SuspendedDropIn)).Status);
        var startedRow = await ReadBookingAsync(target.ConnectionString, ids.StartedDropIn);
        Assert.Equal(BookingStatus.Pending, startedRow.Status);
        Assert.Null(startedRow.CancelledAt);
        Assert.Null((await ReadBookingAsync(target.ConnectionString, ids.SuspendedDropIn)).CancelledAt);

        output.WriteLine("Cancellation assertions passed: the drop-in was marked Cancelado with CancelledAt from the clock and CancelledBy=Owner, the second attempt was rejected with the original facts intact, the released place let the same client book again, the fixed, foreign and missing bookings were indistinguishable and untouched, and a suspended or started class was refused without writing.");
    }

    [Fact]
    public async Task Booking_respects_the_configured_reminder_lead_read_from_the_locked_session()
    {
        var target = await ResolveTargetAsync("_Lead");
        if (target is null)
            return;

        var ids = await SeedLeadAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassDropInRepository(session);

        // The locked/session read carries the anticipation the business configured, not the schema default: A
        // configured 12 h and B 48 h, so these two rows are what prove the join on Businesses reaches the field the
        // booking decision consumes. Reading it here also pins that the service gets it without a second read.
        var lockedA = await repository.LockSessionAsync(ids.BusinessA, ids.OutsideSession);
        Assert.NotNull(lockedA);
        Assert.Equal(12, lockedA.ReminderLeadHours);
        var lockedB = await repository.LockSessionAsync(ids.BusinessB, ids.InsideSession);
        Assert.NotNull(lockedB);
        Assert.Equal(48, lockedB.ReminderLeadHours);

        var clock = new FakeClock { Now = FixedNow };
        var service = new ClassDropInService(repository, session, clock);

        // Business A with a 12 h lead: a class 13 h away is outside its window, so the booking queues no immediate
        // email — the scheduled reminder owns it. A service pinned to the schema default of 24 h would queue one
        // here, and the zero count below is what catches that.
        var outside = await service.BookForClientAsync(ids.BusinessA, ids.OutsideSession, ids.ClientA);
        Assert.True(outside.Succeeded, outside.Error?.Code);
        Assert.False(outside.Value.ImmediateEmailQueued);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND ClientId = @clientId",
            ("@appointmentId", ids.OutsideSession), ("@clientId", ids.ClientA)));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));

        // The same 12 h lead on the boundary: a class exactly 12 h away is already inside the inclusive window, so
        // the immediate email is queued for that booking and — as the count shows — for nothing else.
        var boundaryA = await service.BookForClientAsync(ids.BusinessA, ids.BoundarySessionA, ids.ClientA);
        Assert.True(boundaryA.Succeeded, boundaryA.Error?.Code);
        Assert.True(boundaryA.Value.ImmediateEmailQueued);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", ids.BusinessA)));
        var queuedA = await ReadNotificationAsync(target.ConnectionString, $"reminder:booking:{boundaryA.Value.BookingId}");
        Assert.Equal(NotificationType.DropInReminder, queuedA.Type);
        Assert.Equal(ids.ClientAEmail, queuedA.Recipient);

        // Business B with a 48 h lead: a class 47 h away is inside its window, so the booking queues the immediate
        // email — a service pinned to 24 h would queue none, and this direction catches that too.
        var inside = await service.BookForClientAsync(ids.BusinessB, ids.InsideSession, ids.ClientB);
        Assert.True(inside.Succeeded, inside.Error?.Code);
        Assert.True(inside.Value.ImmediateEmailQueued);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", ids.BusinessB)));
        var queuedB = await ReadNotificationAsync(target.ConnectionString, $"reminder:booking:{inside.Value.BookingId}");
        Assert.Equal(NotificationType.DropInReminder, queuedB.Type);
        Assert.Equal(ids.ClientBEmail, queuedB.Recipient);

        // Its 48 h boundary is inside as well: exactly one more email for the second booking, never a stray one for
        // the first.
        var boundaryB = await service.BookForClientAsync(ids.BusinessB, ids.BoundarySessionB, ids.ClientB);
        Assert.True(boundaryB.Succeeded, boundaryB.Error?.Code);
        Assert.True(boundaryB.Value.ImmediateEmailQueued);
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", ids.BusinessB)));
        var queuedAgain = await ReadNotificationAsync(target.ConnectionString, $"reminder:booking:{boundaryB.Value.BookingId}");
        Assert.Equal(NotificationType.DropInReminder, queuedAgain.Type);
        Assert.Equal(ids.ClientBEmail, queuedAgain.Recipient);

        output.WriteLine("Lead assertions passed: the locked session read carried 12 h for business A and 48 h for business B from their Businesses rows, and the immediate drop-in email was queued only inside each configured window — none at 13 h for the 12 h lead, one at 47 h and on both equality boundaries — with no extra email for the outside booking.");
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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T14_SQL_CONNECTION and
        // the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT14Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT14Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T14 SQL integration test",
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

    private static async Task<ListIds> SeedListAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T14 List Business", "synthetic-t14-list", now);
        var businessB = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Other Business", "synthetic-t14-other", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T14 Morning Class", 60, 8);
        var serviceOther = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T14 Evening Class", 60, 8);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Synthetic", "Teacher", "20141414141");
        var workerB = await InsertWorkerAsync(connection, transaction, businessB, "Other", "Teacher", "20999999999");
        var firstClient = await InsertClientAsync(connection, transaction, businessA, "Client", "T14 One", "t14.list.client1@example.invalid", now);
        var secondClient = await InsertClientAsync(connection, transaction, businessA, "Client", "T14 Two", "t14.list.client2@example.invalid", now);

        // Each session gets its own grid class so the seed looks like generated data; the listing never joins it.
        var sessionAtRangeStart = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Monday, new TimeOnly(0, 0)),
            serviceA, workerA, new DateTime(2026, 3, 9, 0, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        var sessionInside = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Tuesday, new TimeOnly(10, 0)),
            serviceA, workerA, new DateTime(2026, 3, 10, 10, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        var sessionOfOtherService = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceOther, workerA, DayOfWeek.Monday, new TimeOnly(12, 0)),
            serviceOther, workerA, new DateTime(2026, 3, 9, 12, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        var sessionAtDayAfterEnd = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Thursday, new TimeOnly(0, 0)),
            serviceA, workerA, new DateTime(2026, 3, 12, 0, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        var sessionAtLastInstantOfEndDay = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Wednesday, new TimeOnly(23, 59)),
            serviceA, workerA, new DateTime(2026, 3, 11, 23, 59, 59), 60, 8, AppointmentStatus.Scheduled, now);

        // An individual turn inside the same window, already booked: capacity 1 and Programado, so only the kind
        // discriminant keeps it out of the offer.
        var individualAppointment = await InsertIndividualAppointmentAsync(connection, transaction, businessA,
            serviceA, workerA, new DateTime(2026, 3, 9, 9, 0, 0), 60, now);
        await InsertBookingAsync(connection, transaction, businessA, individualAppointment, firstClient, null,
            null, BookingStatus.Pending, now);

        // One pending and one cancelled booking of the returned session, so both travel with it.
        await InsertBookingAsync(connection, transaction, businessA, sessionInside, firstClient, null,
            BookingModality.DropIn, BookingStatus.Pending, now);
        await InsertBookingAsync(connection, transaction, businessA, sessionInside, secondClient, null,
            BookingModality.DropIn, BookingStatus.Cancelled, now);

        // The other tenant: a class session at the same instant as one of business A's, which business A must not see.
        var serviceB = await InsertServiceAsync(connection, transaction, businessB, "Synthetic T14 Other Class", 60, 8);
        var sessionOfOtherBusiness = await InsertClassSessionAsync(connection, transaction, businessB,
            await InsertGridClassAsync(connection, transaction, businessB, serviceB, workerB, DayOfWeek.Tuesday, new TimeOnly(10, 0)),
            serviceB, workerB, new DateTime(2026, 3, 10, 10, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);

        await transaction.CommitAsync();

        return new ListIds(businessA, businessB, serviceA, workerA, firstClient, secondClient,
            sessionAtRangeStart, sessionInside, sessionOfOtherService, sessionAtDayAfterEnd,
            sessionAtLastInstantOfEndDay, individualAppointment, sessionOfOtherBusiness);
    }

    private static async Task<ClientIds> SeedClientsAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Client Business", "synthetic-t14-clients", now);
        var businessB = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Client Other", "synthetic-t14-clients-other", now);

        // Stored with a different casing than the request, as the schema's lower-case convention does not rewrite
        // rows that predate it.
        var legacyClientOfA = await InsertClientAsync(connection, transaction, businessA, "Legacy", "MixedCase",
            "Legacy.MixedCase@Example.invalid", now);

        await transaction.CommitAsync();

        return new ClientIds(businessA, businessB, legacyClientOfA);
    }

    private static async Task<WriteIds> SeedWriteAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Write Business", "synthetic-t14-write", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T14 Bookable Class", 60, 8);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Synthetic", "Teacher", "20151515151");
        var firstClient = await InsertClientAsync(connection, transaction, businessA, "Client", "T14 One", "t14.write.client1@example.invalid", now);
        var secondClient = await InsertClientAsync(connection, transaction, businessA, "Client", "T14 Two", "t14.write.client2@example.invalid", now);
        var thirdClient = await InsertClientAsync(connection, transaction, businessA, "Client", "T14 Three", "t14.write.client3@example.invalid", now);

        // Two free places, and nobody booked: the guarded insert may take one.
        var openSession = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Friday, new TimeOnly(10, 0)),
            serviceA, workerA, new DateTime(2026, 3, 20, 10, 0, 0), 60, 2, AppointmentStatus.Scheduled, now);

        // Capacity 2 with two non-cancelled bookings — a fixed annotation pending payment and a confirmed drop-in —
        // plus a cancelled one from the client that will try to book: full, whatever the modalities are.
        var fullSession = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Friday, new TimeOnly(12, 0)),
            serviceA, workerA, new DateTime(2026, 3, 20, 12, 0, 0), 60, 2, AppointmentStatus.Scheduled, now);
        var enrollmentOfSecondClient = await InsertEnrollmentAsync(connection, transaction, businessA, secondClient, now);
        await InsertBookingAsync(connection, transaction, businessA, fullSession, secondClient, enrollmentOfSecondClient,
            BookingModality.Fixed, BookingStatus.Pending, now);
        await InsertBookingAsync(connection, transaction, businessA, fullSession, thirdClient, null,
            BookingModality.DropIn, BookingStatus.Confirmed, now);
        await InsertBookingAsync(connection, transaction, businessA, fullSession, firstClient, null,
            BookingModality.DropIn, BookingStatus.Cancelled, now);

        // Capacity 2 with a single non-cancelled booking: the client holding it cannot book again until it is
        // cancelled, which then frees the place for her.
        var bookedSession = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Saturday, new TimeOnly(10, 0)),
            serviceA, workerA, new DateTime(2026, 3, 21, 10, 0, 0), 60, 2, AppointmentStatus.Scheduled, now);
        await InsertBookingAsync(connection, transaction, businessA, bookedSession, firstClient, null,
            BookingModality.DropIn, BookingStatus.Confirmed, now);

        // An individual turn of the same business: capacity 1 and Programado, never a drop-in target.
        var individualAppointment = await InsertIndividualAppointmentAsync(connection, transaction, businessA,
            serviceA, workerA, new DateTime(2026, 3, 20, 9, 0, 0), 60, now);

        await transaction.CommitAsync();

        return new WriteIds(businessA, openSession, fullSession, bookedSession, individualAppointment,
            firstClient, secondClient, thirdClient);
    }

    private static async Task<RaceIds> SeedRaceAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Race Business", "synthetic-t14-race", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T14 Race Class", 60, 1);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Race", "Teacher", "20161616161");
        var firstClient = await InsertClientAsync(connection, transaction, businessA, "Race", "One", "t14.race.client1@example.invalid", now);
        var secondClient = await InsertClientAsync(connection, transaction, businessA, "Race", "Two", "t14.race.client2@example.invalid", now);

        // One single place to take.
        var session = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Friday, new TimeOnly(10, 0)),
            serviceA, workerA, new DateTime(2026, 3, 20, 10, 0, 0), 60, 1, AppointmentStatus.Scheduled, now);

        await transaction.CommitAsync();

        return new RaceIds(businessA, session, new[] { firstClient, secondClient });
    }

    private static async Task<RollbackIds> SeedRollbackAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Rollback Business", "synthetic-t14-rollback", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T14 Rollback Class", 60, 8);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Rollback", "Teacher", "20171717171");
        var session = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Friday, new TimeOnly(10, 0)),
            serviceA, workerA, new DateTime(2026, 3, 20, 10, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);

        await transaction.CommitAsync();

        return new RollbackIds(businessA, session);
    }

    private static async Task<RosterIds> SeedRosterAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Roster Business", "synthetic-t14-roster", now);
        var businessB = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Roster Other", "synthetic-t14-roster-other", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T14 Roster Class", 60, 8);
        var serviceB = await InsertServiceAsync(connection, transaction, businessB, "Synthetic T14 Roster Other Class", 60, 8);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Roster", "Teacher", "20181818181");
        var workerB = await InsertWorkerAsync(connection, transaction, businessB, "Other", "Teacher", "20181818182");
        var firstClient = await InsertClientAsync(connection, transaction, businessA, "Ana", "Perez", "t14.roster.client1@example.invalid", now);
        var secondClient = await InsertClientAsync(connection, transaction, businessA, "Bella", "Cancelada", "t14.roster.client2@example.invalid", now);
        var fixedClient = await InsertClientAsync(connection, transaction, businessA, "Fija", "Estudiante", "t14.roster.fixed@example.invalid", now);
        var foreignClient = await InsertClientAsync(connection, transaction, businessB, "Otra", "Alumna", "t14.roster.foreign@example.invalid", now);

        // An upcoming class nobody booked as a drop-in: the roster keeps it, with an empty annotation list.
        var sessionWithoutAnnotations = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Tuesday, new TimeOnly(9, 0)),
            serviceA, workerA, new DateTime(2026, 3, 10, 9, 0, 0), 60, 4, AppointmentStatus.Scheduled, now);

        // The annotated class: a fixed annotation, a pending drop-in and a cancelled one. Only the last two are
        // drop-ins, whatever their status is.
        var sessionUpcoming = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Tuesday, new TimeOnly(17, 0)),
            serviceA, workerA, new DateTime(2026, 3, 10, 17, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        var enrollment = await InsertEnrollmentAsync(connection, transaction, businessA, fixedClient, now);
        var fixedBooking = await InsertBookingAsync(connection, transaction, businessA, sessionUpcoming, fixedClient,
            enrollment, BookingModality.Fixed, BookingStatus.Pending, now);
        var pendingDropIn = await InsertBookingAsync(connection, transaction, businessA, sessionUpcoming, firstClient,
            null, BookingModality.DropIn, BookingStatus.Pending, now);
        var cancelledDropIn = await InsertBookingAsync(connection, transaction, businessA, sessionUpcoming, secondClient,
            null, BookingModality.DropIn, BookingStatus.Cancelled, now);

        // A class that already started and an individual turn inside the window: neither one is a roster entry.
        var sessionStarted = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Monday, new TimeOnly(7, 0)),
            serviceA, workerA, new DateTime(2026, 3, 9, 7, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        await InsertBookingAsync(connection, transaction, businessA, sessionStarted, firstClient, null,
            BookingModality.DropIn, BookingStatus.Pending, now);
        var individualAppointment = await InsertIndividualAppointmentAsync(connection, transaction, businessA,
            serviceA, workerA, new DateTime(2026, 3, 9, 9, 0, 0), 60, now);
        await InsertBookingAsync(connection, transaction, businessA, individualAppointment, firstClient, null,
            null, BookingStatus.Pending, now);

        // The other tenant: a class at the same instant as business A's, which business A must never see.
        var sessionOfOtherBusiness = await InsertClassSessionAsync(connection, transaction, businessB,
            await InsertGridClassAsync(connection, transaction, businessB, serviceB, workerB, DayOfWeek.Tuesday, new TimeOnly(17, 0)),
            serviceB, workerB, new DateTime(2026, 3, 10, 17, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        var foreignDropIn = await InsertBookingAsync(connection, transaction, businessB, sessionOfOtherBusiness,
            foreignClient, null, BookingModality.DropIn, BookingStatus.Pending, now);

        await transaction.CommitAsync();

        return new RosterIds(businessA, businessB, serviceA, workerA, firstClient, secondClient, fixedClient,
            sessionWithoutAnnotations, sessionUpcoming, sessionStarted, individualAppointment, sessionOfOtherBusiness,
            fixedBooking, pendingDropIn, cancelledDropIn, foreignDropIn);
    }

    private static async Task<CancellationIds> SeedCancellationAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Cancel Business", "synthetic-t14-cancel", now);
        var businessB = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Cancel Other", "synthetic-t14-cancel-other", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T14 Cancel Class", 60, 8);
        var serviceB = await InsertServiceAsync(connection, transaction, businessB, "Synthetic T14 Cancel Other Class", 60, 8);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Cancel", "Teacher", "20191919191");
        var workerB = await InsertWorkerAsync(connection, transaction, businessB, "Other", "Teacher", "20191919192");
        var client = await InsertClientAsync(connection, transaction, businessA, "Ana", "Perez", "t14.cancel.client@example.invalid", now);
        var fixedClient = await InsertClientAsync(connection, transaction, businessA, "Fija", "Estudiante", "t14.cancel.fixed@example.invalid", now);
        var foreignClient = await InsertClientAsync(connection, transaction, businessB, "Otra", "Alumna", "t14.cancel.foreign@example.invalid", now);

        // Capacity 2 with a fixed annotation of another client and the drop-in to cancel: the class is full, and only
        // the place the cancellation releases lets the same client book it again.
        var session = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Friday, new TimeOnly(10, 0)),
            serviceA, workerA, new DateTime(2026, 3, 20, 10, 0, 0), 60, 2, AppointmentStatus.Scheduled, now);
        var enrollment = await InsertEnrollmentAsync(connection, transaction, businessA, fixedClient, now);
        var fixedBooking = await InsertBookingAsync(connection, transaction, businessA, session, fixedClient,
            enrollment, BookingModality.Fixed, BookingStatus.Pending, now);
        var dropInBooking = await InsertBookingAsync(connection, transaction, businessA, session, client,
            null, BookingModality.DropIn, BookingStatus.Pending, now);

        // A suspended class and one that already started: both refuse the cancellation without writing.
        var suspendedSession = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Saturday, new TimeOnly(10, 0)),
            serviceA, workerA, new DateTime(2026, 3, 21, 10, 0, 0), 60, 8, AppointmentStatus.Suspended, now);
        var suspendedDropIn = await InsertBookingAsync(connection, transaction, businessA, suspendedSession, client,
            null, BookingModality.DropIn, BookingStatus.Pending, now);
        var startedSession = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Monday, new TimeOnly(7, 0)),
            serviceA, workerA, new DateTime(2026, 3, 9, 7, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        var startedDropIn = await InsertBookingAsync(connection, transaction, businessA, startedSession, client,
            null, BookingModality.DropIn, BookingStatus.Pending, now);

        // A drop-in of the other tenant, invisible to business A.
        var sessionOfOtherBusiness = await InsertClassSessionAsync(connection, transaction, businessB,
            await InsertGridClassAsync(connection, transaction, businessB, serviceB, workerB, DayOfWeek.Friday, new TimeOnly(10, 0)),
            serviceB, workerB, new DateTime(2026, 3, 20, 10, 0, 0), 60, 8, AppointmentStatus.Scheduled, now);
        var foreignDropIn = await InsertBookingAsync(connection, transaction, businessB, sessionOfOtherBusiness,
            foreignClient, null, BookingModality.DropIn, BookingStatus.Pending, now);

        await transaction.CommitAsync();

        return new CancellationIds(businessA, businessB, session, client, dropInBooking, fixedBooking,
            suspendedSession, suspendedDropIn, startedSession, startedDropIn, foreignDropIn);
    }

    private static async Task<LeadIds> SeedLeadAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        // The anticipation is a business setting, not a session one: A configures 12 h and B 48 h, both away from
        // the schema default of 24 h, so a read that fell back to the default cannot pass the boundary assertions.
        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Lead Business", "synthetic-t14-lead", now, reminderLeadHours: 12);
        var businessB = await InsertBusinessAsync(connection, transaction, "Synthetic T14 Lead Other", "synthetic-t14-lead-other", now, reminderLeadHours: 48);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T14 Lead Class", 60, 8);
        var serviceB = await InsertServiceAsync(connection, transaction, businessB, "Synthetic T14 Lead Other Class", 60, 8);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Lead", "Teacher", "20212121211");
        var workerB = await InsertWorkerAsync(connection, transaction, businessB, "Other", "Teacher", "20212121212");
        const string clientAEmail = "t14.lead.client@example.invalid";
        const string clientBEmail = "t14.lead.other@example.invalid";
        var clientA = await InsertClientAsync(connection, transaction, businessA, "Luisa", "Lead", clientAEmail, now);
        var clientB = await InsertClientAsync(connection, transaction, businessB, "Bruno", "Lead", clientBEmail, now);

        // A 12 h lead: a class 13 h away (outside the window) and one exactly 12 h away (on the inclusive boundary).
        // Both instants derive from the same clock the test hands the service, so the two sides of the boundary
        // differ by the configured lead and nothing else.
        var outsideStart = now.AddHours(13);
        var outsideSession = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, outsideStart.DayOfWeek, TimeOnly.FromDateTime(outsideStart)),
            serviceA, workerA, outsideStart, 60, 8, AppointmentStatus.Scheduled, now);
        var boundaryStartA = now.AddHours(12);
        var boundarySessionA = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, boundaryStartA.DayOfWeek, TimeOnly.FromDateTime(boundaryStartA)),
            serviceA, workerA, boundaryStartA, 60, 8, AppointmentStatus.Scheduled, now);

        // A 48 h lead: a class 47 h away (inside the window) and one exactly 48 h away (on the inclusive boundary).
        var insideStart = now.AddHours(47);
        var insideSession = await InsertClassSessionAsync(connection, transaction, businessB,
            await InsertGridClassAsync(connection, transaction, businessB, serviceB, workerB, insideStart.DayOfWeek, TimeOnly.FromDateTime(insideStart)),
            serviceB, workerB, insideStart, 60, 8, AppointmentStatus.Scheduled, now);
        var boundaryStartB = now.AddHours(48);
        var boundarySessionB = await InsertClassSessionAsync(connection, transaction, businessB,
            await InsertGridClassAsync(connection, transaction, businessB, serviceB, workerB, boundaryStartB.DayOfWeek, TimeOnly.FromDateTime(boundaryStartB)),
            serviceB, workerB, boundaryStartB, 60, 8, AppointmentStatus.Scheduled, now);

        await transaction.CommitAsync();

        return new LeadIds(businessA, businessB, outsideSession, boundarySessionA, insideSession, boundarySessionB,
            clientA, clientB, clientAEmail, clientBEmail);
    }

    private static Task<int> InsertBusinessAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string slug, DateTime now,
        int reminderLeadHours = 24) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, ReminderLeadHours, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @lead, @createdAt)",
            ("@name", name), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@lead", reminderLeadHours), ("@createdAt", now));

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
        AppointmentStatus status, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, @gridClassId, @startsAt, @endsAt, @capacity, NULL, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.ClassSession), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@gridClassId", gridClassId), ("@startsAt", startsAt),
            ("@endsAt", startsAt.AddMinutes(durationMinutes)), ("@capacity", maxCapacity), ("@status", status),
            ("@createdAt", now));

    private static Task<int> InsertIndividualAppointmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DateTime startsAt, int durationMinutes, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.Individual), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(durationMinutes)),
            ("@origin", AppointmentOrigin.Reserved), ("@status", AppointmentStatus.Scheduled), ("@createdAt", now));

    private static Task<int> InsertEnrollmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @createdAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", 1),
            ("@status", EnrollmentStatus.PendingPayment), ("@price", 100.00m), ("@createdAt", now));

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

    private static async Task<BookingSnapshot> ReadBookingAsync(string connectionString, int bookingId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CancelledAt, CreatedAt, CancelledBy FROM Bookings WHERE Id = @id",
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
            reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : (CancelledBy)reader.GetByte(8));
    }

    private static async Task<NotificationSnapshot> ReadNotificationAsync(string connectionString, string dedupKey)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Type, Recipient, Subject, BodyHtml FROM Notifications WHERE DedupKey = @dedupKey",
            connection);
        AddParameter(command, "@dedupKey", dedupKey);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The queued notification was not found.");
        return new NotificationSnapshot(
            (NotificationType)reader.GetByte(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
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

    private sealed record ListIds(
        int BusinessA,
        int BusinessB,
        int ServiceA,
        int WorkerA,
        int FirstClientOfA,
        int SecondClientOfA,
        int SessionAtRangeStart,
        int SessionInside,
        int SessionOfOtherService,
        int SessionAtDayAfterEnd,
        int SessionAtLastInstantOfEndDay,
        int IndividualAppointment,
        int SessionOfOtherBusiness);

    private sealed record ClientIds(int BusinessA, int BusinessB, int LegacyClientOfA);

    private sealed record WriteIds(
        int BusinessA,
        int OpenSession,
        int FullSession,
        int BookedSession,
        int IndividualAppointment,
        int FirstClient,
        int SecondClient,
        int ThirdClient);

    private sealed record RaceIds(int BusinessA, int Session, int[] ClientIds);

    private sealed record RollbackIds(int BusinessA, int Session);

    private sealed record RosterIds(
        int BusinessA,
        int BusinessB,
        int ServiceA,
        int WorkerA,
        int FirstClient,
        int SecondClient,
        int FixedClient,
        int SessionWithoutAnnotations,
        int SessionUpcoming,
        int SessionStarted,
        int IndividualAppointment,
        int SessionOfOtherBusiness,
        int FixedBooking,
        int PendingDropIn,
        int CancelledDropIn,
        int ForeignDropIn);

    private sealed record CancellationIds(
        int BusinessA,
        int BusinessB,
        int Session,
        int Client,
        int DropInBooking,
        int FixedBooking,
        int SuspendedSession,
        int SuspendedDropIn,
        int StartedSession,
        int StartedDropIn,
        int ForeignDropIn);

    private sealed record LeadIds(
        int BusinessA,
        int BusinessB,
        int OutsideSession,
        int BoundarySessionA,
        int InsideSession,
        int BoundarySessionB,
        int ClientA,
        int ClientB,
        string ClientAEmail,
        string ClientBEmail);

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

    private sealed record NotificationSnapshot(NotificationType Type, string Recipient, string Subject, string BodyHtml);
}
