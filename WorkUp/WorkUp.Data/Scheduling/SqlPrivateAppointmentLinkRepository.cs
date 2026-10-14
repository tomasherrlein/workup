using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

/// <summary>SQL persistence for private-link lookups, guarded actions, and owner token rotation.</summary>
public sealed class SqlPrivateAppointmentLinkRepository(DbSession session)
    : SqlRepositoryBase(session), IPrivateAppointmentLinkRepository, IOwnerPrivateAppointmentRepository
{
    private const string PublicActionFrom = """
        FROM Bookings AS b WITH (UPDLOCK, ROWLOCK)
        JOIN AppointmentLinks AS l WITH (UPDLOCK, ROWLOCK) ON l.BookingId = b.Id
        JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
        JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
        JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
        """;

    private const string PublicLinkValidity = """
        l.TokenHash = @tokenHash
        AND l.RevokedAt IS NULL AND l.ExpiresAt > @now
        AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1 AND a.Status = @scheduled
        AND b.BusinessId = a.BusinessId AND b.Modality IS NULL AND b.EnrollmentId IS NULL
        """;

    private const string ConfirmPendingSql = """
        UPDATE b
           SET Status = @confirmed, ConfirmedAt = @now
        """ + "\n" + PublicActionFrom + "\nWHERE\n" + PublicLinkValidity + "\nAND a.StartsAt > @now " +
        "AND b.Status = @pending AND b.ConfirmedAt IS NULL " +
        "AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL " +
        "AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL";

    private const string ConfirmAlreadyRecordedSql = """
        SELECT CASE WHEN EXISTS (
            SELECT 1
        """ + "\n" + PublicActionFrom + "\nWHERE\n" + PublicLinkValidity + "\nAND a.StartsAt > @now " +
        "AND b.Status = @confirmed AND b.ConfirmedAt IS NOT NULL " +
        "AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL " +
        "AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL " +
        ") THEN 1 ELSE 0 END";

    private const string CancelSql = """
        UPDATE b
           SET Status = @cancelled, CancelledBy = @client, CancelledAt = @now
        """ + "\n" + PublicActionFrom + "\nWHERE\n" + PublicLinkValidity + "\nAND a.StartsAt > @now " +
        "AND b.Status IN (@pending, @confirmed) " +
        "AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL " +
        "AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL " +
        "AND ((b.Status = @pending AND b.ConfirmedAt IS NULL) " +
        "OR (b.Status = @confirmed AND b.ConfirmedAt IS NOT NULL))";

    private const string ReportAbsenceSql = """
        UPDATE b
           SET Status = @absent, AbsenceSource = @clientLink
        """ + "\n" + PublicActionFrom + "\nWHERE\n" + PublicLinkValidity + "\nAND a.StartsAt <= @now " +
        "AND b.Status IN (@pending, @confirmed) " +
        "AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL " +
        "AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL " +
        "AND ((b.Status = @pending AND b.ConfirmedAt IS NULL) " +
        "OR (b.Status = @confirmed AND b.ConfirmedAt IS NOT NULL))";

    public Task<PrivateAppointmentLinkRecord?> GetPublicAppointmentAsync(
        byte[] tokenHash, DateTime now, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            """
            SELECT TOP (1) a.StartsAt, a.EndsAt, l.ExpiresAt, s.Name AS ServiceName,
                   CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName,
                   a.Status AS AppointmentStatus, b.Status AS BookingStatus, b.ConfirmedAt,
                   b.CancelledAt, b.AttendanceRecordedAt, b.AbsenceSource, b.CancelledBy
              FROM AppointmentLinks AS l
              JOIN Bookings AS b ON b.Id = l.BookingId
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
              JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
              JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
             WHERE l.TokenHash = @tokenHash AND l.RevokedAt IS NULL AND l.ExpiresAt > @now
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
            """,
            MapPrivateAppointment,
            p => p.AddValue("@tokenHash", tokenHash).AddValue("@now", now)
                .AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@reserved", AppointmentOrigin.Reserved),
            ct);

    public async Task<bool> ApplyPublicActionAsync(
        byte[] tokenHash, PrivateAppointmentLinkAction action, DateTime now, CancellationToken ct = default)
    {
        var parameters = PublicActionParameters(tokenHash, now);
        switch (action)
        {
            case PrivateAppointmentLinkAction.Confirm:
                if (await ExecuteAsync(ConfirmPendingSql, parameters, ct) > 0)
                    return true;
                return await ScalarAsync<int>(ConfirmAlreadyRecordedSql, parameters, ct) == 1;

            case PrivateAppointmentLinkAction.Cancel:
                return await ExecuteAsync(
                    CancelSql,
                    p =>
                    {
                        parameters(p);
                        p.AddValue("@cancelled", BookingStatus.Cancelled).AddValue("@client", CancelledBy.Client);
                    },
                    ct) > 0;

            case PrivateAppointmentLinkAction.ReportAbsence:
                return await ExecuteAsync(
                    ReportAbsenceSql,
                    p =>
                    {
                        parameters(p);
                        p.AddValue("@absent", BookingStatus.Absent).AddValue("@clientLink", AbsenceSource.ClientLink);
                    },
                    ct) > 0;

            default:
                return false;
        }
    }

    public async Task<bool> LockOwnerBookingAsync(int businessId, int bookingId, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT TOP (1) b.Id
              FROM Bookings AS b WITH (UPDLOCK, ROWLOCK)
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId)
                .AddValue("@individual", AppointmentKind.Individual).AddValue("@reserved", AppointmentOrigin.Reserved),
            ct) > 0;

    public async Task<IReadOnlyList<OwnerPrivateAppointmentSummary>> ListRecentIndividualBookingsAsync(
        int businessId, DateTime now, CancellationToken ct = default) =>
        await QueryAsync(
            """
            SELECT TOP (50) b.Id AS BookingId, a.Id AS AppointmentId, a.StartsAt,
                   s.Name AS ServiceName, CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName,
                   activeLinks.ActiveLinkCount,
                   CASE WHEN activeLinks.ActiveLinkCount = 1 THEN activeLinks.LatestExpiresAt END AS ActiveLinkExpiresAt
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
              JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
              JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
              OUTER APPLY (
                  SELECT COUNT(*) AS ActiveLinkCount, MAX(l.ExpiresAt) AS LatestExpiresAt
                    FROM AppointmentLinks AS l
                   WHERE l.BookingId = b.Id AND l.RevokedAt IS NULL AND l.ExpiresAt > @now
              ) AS activeLinks
             WHERE b.BusinessId = @businessId AND a.BusinessId = @businessId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
             ORDER BY a.StartsAt DESC, b.Id DESC
            """,
            MapOwnerSummary,
            p => p.AddValue("@businessId", businessId).AddValue("@now", now)
                .AddValue("@individual", AppointmentKind.Individual).AddValue("@reserved", AppointmentOrigin.Reserved),
            ct);

    public async Task<IReadOnlyList<OwnerPrivateAppointmentLinkRecord>> GetActiveOwnerLinksAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default) =>
        await QueryAsync(
            """
            SELECT TOP (2) l.Id, l.TokenHash, l.ProtectedToken, l.ExpiresAt
              FROM AppointmentLinks AS l WITH (UPDLOCK, ROWLOCK)
              JOIN Bookings AS b ON b.Id = l.BookingId
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND l.RevokedAt IS NULL AND l.ExpiresAt > @now
             ORDER BY l.Id DESC
            """,
            MapOwnerLink,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId)
                .AddValue("@individual", AppointmentKind.Individual).AddValue("@reserved", AppointmentOrigin.Reserved)
                .AddValue("@now", now),
            ct);

    public async Task<bool> RevokeOwnerLinkAsync(
        int businessId, int bookingId, int linkId, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            """
            UPDATE l
               SET RevokedAt = @now
              FROM AppointmentLinks AS l
              JOIN Bookings AS b ON b.Id = l.BookingId
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId AND l.Id = @linkId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND l.RevokedAt IS NULL AND l.ExpiresAt > @now
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId)
                .AddValue("@linkId", linkId).AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@reserved", AppointmentOrigin.Reserved).AddValue("@now", now),
            ct) == 1;

    public async Task<int> InsertOwnerLinkAsync(int businessId, AppointmentLink link, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            INSERT INTO AppointmentLinks (BookingId, TokenHash, ProtectedToken, ExpiresAt, RevokedAt, CreatedAt)
            OUTPUT INSERTED.Id
            SELECT @bookingId, @tokenHash, @protectedToken, @expiresAt, NULL, @createdAt
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND @expiresAt > @createdAt
               AND NOT EXISTS (
                   SELECT 1 FROM AppointmentLinks AS currentLink
                    WHERE currentLink.BookingId = b.Id AND currentLink.RevokedAt IS NULL
                      AND currentLink.ExpiresAt > @createdAt)
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", link.BookingId)
                .AddValue("@tokenHash", link.TokenHash).AddValue("@protectedToken", link.ProtectedToken)
                .AddValue("@expiresAt", link.ExpiresAt).AddValue("@createdAt", link.CreatedAt)
                .AddValue("@individual", AppointmentKind.Individual).AddValue("@reserved", AppointmentOrigin.Reserved),
            ct);

    private static Action<SqlParameterCollection> PublicActionParameters(byte[] tokenHash, DateTime now) => p =>
        p.AddValue("@tokenHash", tokenHash).AddValue("@now", now)
            .AddValue("@individual", AppointmentKind.Individual).AddValue("@reserved", AppointmentOrigin.Reserved)
            .AddValue("@scheduled", AppointmentStatus.Scheduled)
            .AddValue("@pending", BookingStatus.Pending).AddValue("@confirmed", BookingStatus.Confirmed);

    private static PrivateAppointmentLinkRecord MapPrivateAppointment(SqlDataReader reader) => new(
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("EndsAt"),
        reader.Get<DateTime>("ExpiresAt"),
        reader.Get<string>("ServiceName"),
        reader.Get<string>("WorkerName"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetEnum<BookingStatus>("BookingStatus"),
        reader.GetNullable<DateTime>("ConfirmedAt"),
        reader.GetNullable<DateTime>("CancelledAt"),
        reader.GetNullable<DateTime>("AttendanceRecordedAt"),
        reader.GetNullableEnum<AbsenceSource>("AbsenceSource"),
        reader.GetNullableEnum<CancelledBy>("CancelledBy"));

    private static OwnerPrivateAppointmentSummary MapOwnerSummary(SqlDataReader reader) => new(
        reader.Get<int>("BookingId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<string>("ServiceName"),
        reader.Get<string>("WorkerName"),
        reader.Get<int>("ActiveLinkCount"),
        reader.GetNullable<DateTime>("ActiveLinkExpiresAt"));

    private static OwnerPrivateAppointmentLinkRecord MapOwnerLink(SqlDataReader reader) => new(
        reader.Get<int>("Id"),
        reader.Get<byte[]>("TokenHash"),
        reader.Get<string>("ProtectedToken"),
        reader.Get<DateTime>("ExpiresAt"));
}
