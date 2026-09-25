using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Classes;

/// <summary>
/// SQL Server persistence for the read-only class detail (RF-10, RF-11, RF-12, RF-22, D-15, D-16, D-23). Two
/// tenant-scoped statements answer the read: one for the session and one for every annotation it holds, cancelled
/// ones included, each with its client's name and — for the fixed ones — the enrollment status and the membership
/// periods the derived state needs. Nothing here writes or commits.
/// <para>
/// The <c>Kind = ClassSession</c> discriminant is part of the session statement: an individual turn is also an
/// appointment and would otherwise be detailed as a class. The tenant rides on the session predicate, on the booking
/// rows and on the client and enrollment joins, so a session, a booking or a client of another business is invisible;
/// the membership periods hang off the business-scoped enrollment and inherit its scope. The client join is a
/// <c>LEFT JOIN</c>: an annotation whose client row cannot be resolved still comes back with an empty name and email
/// instead of being dropped from the answer.
/// </para>
/// </summary>
public sealed class SqlClassDetailRepository(DbSession session)
    : SqlRepositoryBase(session), IClassDetailRepository
{
    /// <summary>
    /// The class session of the business, matched by id. The tenant, the id and the kind are the whole predicate, so
    /// a foreign or individual appointment is simply not found. The persisted <c>StartsAt</c> and <c>EndsAt</c> travel
    /// with the row, so the detail never recomputes the end from the tariff of the moment. The suspension reason
    /// travels with the row too: it is set only for a suspended session and never alters the grid or the annotations.
    /// </summary>
    private const string SelectSessionSql = """
        SELECT a.Id AS AppointmentId, a.ServiceId, a.WorkerId, a.StartsAt, a.EndsAt, a.MaxCapacity, a.Kind, a.Status,
               a.SuspensionReason
          FROM Appointments AS a
         WHERE a.Id = @appointmentId
           AND a.BusinessId = @businessId
           AND a.Kind = @classSession
        """;

    /// <summary>
    /// Every annotation of that session, cancelled ones included, with the client's name and email, the enrollment
    /// status and the membership periods joined through the business-scoped enrollment. The period columns are repeated
    /// per paid month, so the reader groups by booking and collects them; a booking without periods (a drop-in, or a
    /// fixed enrollment that never paid) comes back once with null period columns. Ordering by booking and period keeps
    /// the rows deterministic.
    /// </summary>
    private const string SelectAnnotationsSql = """
        SELECT b.Id AS BookingId, b.ClientId, b.Status, b.Modality, b.EnrollmentId,
               c.FirstName, c.LastName, c.Email,
               e.Status AS EnrollmentStatus,
               p.Id AS PeriodId, p.EnrollmentId AS PeriodEnrollmentId, p.CoverageStart, p.ExpiresOn,
               p.PaymentId, p.CreatedAt
          FROM Bookings AS b
          LEFT JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
          LEFT JOIN Enrollments AS e ON e.Id = b.EnrollmentId AND e.BusinessId = b.BusinessId
          LEFT JOIN MembershipPeriods AS p ON p.EnrollmentId = e.Id
         WHERE b.AppointmentId = @appointmentId
           AND b.BusinessId = @businessId
         ORDER BY b.Id, p.CoverageStart, p.Id
        """;

    public async Task<ClassDetailSessionRow?> GetSessionAsync(
        int businessId, int appointmentId, CancellationToken ct = default)
    {
        var session = await QuerySingleOrDefaultAsync(
            SelectSessionSql,
            MapSession,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@appointmentId", appointmentId)
                .AddValue("@classSession", AppointmentKind.ClassSession),
            ct);

        if (session is null)
            return null;

        var annotations = await QueryAsync(
            SelectAnnotationsSql,
            MapAnnotation,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@appointmentId", appointmentId),
            ct);

        // One row per booking and paid period: the group collapses them back into one annotation carrying every
        // period the derived state needs. A session without annotations is returned with an empty list.
        var bookings = annotations
            .GroupBy(annotation => annotation.BookingId)
            .Select(group => new ClassDetailBookingRow(
                group.Key,
                group.First().ClientId,
                group.First().ClientName,
                group.First().ClientEmail,
                group.First().Modality,
                group.First().EnrollmentId,
                group.First().Status,
                group.First().EnrollmentStatus,
                group
                    .Where(annotation => annotation.Period is not null)
                    .Select(annotation => annotation.Period!)
                    .ToArray()))
            .ToArray();

        return new ClassDetailSessionRow(
            session.AppointmentId,
            session.ServiceId,
            session.WorkerId,
            session.StartsAt,
            session.EndsAt,
            session.MaxCapacity,
            session.Kind,
            session.Status,
            session.SuspensionReason,
            bookings);
    }

    private static SessionProjection MapSession(SqlDataReader reader) => new(
        reader.Get<int>("AppointmentId"),
        reader.Get<int>("ServiceId"),
        reader.Get<int>("WorkerId"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("EndsAt"),
        reader.Get<int>("MaxCapacity"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetEnum<AppointmentStatus>("Status"),
        reader.GetNullableString("SuspensionReason"));

    /// <summary>
    /// One annotation row reduced to what the contract keeps. The name and the email are composed from the
    /// <c>LEFT JOIN</c>ed client and stay empty for a row that could not be resolved: the annotation is still counted,
    /// it is the display and contact fields that are missing.
    /// </summary>
    private static BookingProjection MapAnnotation(SqlDataReader reader) => new(
        reader.Get<int>("BookingId"),
        reader.Get<int>("ClientId"),
        ReadClientName(reader),
        reader.GetNullableString("Email") ?? "",
        reader.GetNullableEnum<BookingModality>("Modality"),
        reader.GetNullable<int>("EnrollmentId"),
        reader.GetEnum<BookingStatus>("Status"),
        reader.GetNullableEnum<EnrollmentStatus>("EnrollmentStatus"),
        MapPeriod(reader));

    /// <summary>
    /// One paid month of the annotation's enrollment, or <c>null</c> when the left join found none. Only the coverage
    /// window feeds the derived state, but the whole entity is rebuilt so the contract stays the membership period
    /// and not an ad hoc pair of dates.
    /// </summary>
    private static MembershipPeriod? MapPeriod(SqlDataReader reader)
    {
        if (reader.IsDBNull(reader.GetOrdinal("PeriodId")))
            return null;

        return new MembershipPeriod
        {
            Id = reader.Get<int>("PeriodId"),
            EnrollmentId = reader.Get<int>("PeriodEnrollmentId"),
            CoverageStart = reader.Get<DateOnly>("CoverageStart"),
            ExpiresOn = reader.Get<DateOnly>("ExpiresOn"),
            PaymentId = reader.Get<int>("PaymentId"),
            CreatedAt = reader.Get<DateTime>("CreatedAt")
        };
    }

    private static string ReadClientName(SqlDataReader reader) =>
        $"{reader.GetNullableString("FirstName")} {reader.GetNullableString("LastName")}".Trim();

    private sealed record SessionProjection(
        int AppointmentId,
        int ServiceId,
        int WorkerId,
        DateTime StartsAt,
        DateTime EndsAt,
        int MaxCapacity,
        AppointmentKind Kind,
        AppointmentStatus Status,
        string? SuspensionReason);

    private sealed record BookingProjection(
        int BookingId,
        int ClientId,
        string ClientName,
        string ClientEmail,
        BookingModality? Modality,
        int? EnrollmentId,
        BookingStatus Status,
        EnrollmentStatus? EnrollmentStatus,
        MembershipPeriod? Period);
}
