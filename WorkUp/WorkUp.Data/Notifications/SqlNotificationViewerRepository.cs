using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Notifications;

/// <summary>
/// ADO.NET read path of the generated-email viewer (T26, plan assumptions 10 and 11). Every command runs on the request
/// connection and joins the caller's transaction, and every command is a single <c>SELECT</c>: this class can neither
/// create, update nor delete a notice row, and it has no logger, so a body, a recipient or a stored error cannot leave
/// the database through it.
/// <para>
/// <b>Tenant isolation.</b> Both statements filter <c>BusinessId = @businessId</c>. That predicate alone keeps another
/// tenant's notices out and also excludes every row whose <c>BusinessId</c> is NULL — an account verification or a
/// password reset — even when its recipient is the owner's own notice address. Nothing here matches on
/// <c>Recipient</c>, so no address can ever be turned into a tenant, and the two demo businesses sharing one simulated
/// clock (plan assumption 11) changes nothing about which rows are visible.
/// </para>
/// <para>
/// <b>List shape.</b> One <c>TOP (@pageSize)</c> over <c>BusinessId</c> with an optional strict keyset
/// (<c>Id &lt; @beforeId</c>), ordered by <c>Id DESC</c>. There is no <c>OFFSET</c> and no unbounded scan: the page size
/// is clamped by the contract, so the statement can never read more than one bounded page, and the cursor is an
/// identity value rather than a position, so a page never repeats or skips a row. The select list carries only the
/// eight summary columns — never <c>BodyHtml</c>, <c>LastError</c>, <c>DispatchKey</c>, <c>ClaimedAt</c> or
/// <c>NextAttemptAt</c> — and the mapper reads exactly those columns, so an accidental body read would fail the call
/// instead of silently travelling into a list.
/// </para>
/// <para>
/// <b>Neutral denial.</b> A non-positive <c>businessId</c> or <c>notificationId</c> returns the same answer as a
/// missing or foreign row — an empty page or <c>null</c> — and returns it <em>before</em> a command or a connection
/// exists, so such a caller never reaches SQL and cannot distinguish "you did not name it" from "it is not yours".
/// </para>
/// <para>
/// LIMITATION: a repository call is the runtime proof of the SQL shape, but only the opt-in SQL fixture can show it.
/// </para>
/// </summary>
public sealed class SqlNotificationViewerRepository(DbSession session) :
    SqlRepositoryBase(session), INotificationViewerRepository
{
    /// <summary>
    /// The summary columns of one list row. <c>BodyHtml</c>, <c>LastError</c>, <c>DispatchKey</c>, <c>ClaimedAt</c> and
    /// <c>NextAttemptAt</c> are deliberately absent: a queued body can embed a single-use private link or a reset
    /// token, and the claim columns belong to the dispatcher.
    /// </summary>
    private const string ListSql = """
        SELECT TOP (@pageSize) Id, Type, Status, Recipient, Subject, Attempts, CreatedAt, SentAt
          FROM Notifications
         WHERE BusinessId = @businessId
           AND (@beforeId IS NULL OR Id < @beforeId)
         ORDER BY Id DESC;
        """;

    /// <summary>
    /// The one-row detail read. It is the only statement of this class that reads <c>BodyHtml</c>, and the tenant
    /// predicate is what limits that body to the caller's own notice; the same predicate also excludes every row with a
    /// NULL business. There is no lookup by <c>Recipient</c>.
    /// </summary>
    private const string DetailSql = """
        SELECT Id, Type, Status, Recipient, Subject, Attempts, CreatedAt, SentAt, BodyHtml
          FROM Notifications
         WHERE Id = @notificationId
           AND BusinessId = @businessId;
        """;

    public async Task<IReadOnlyList<NotificationViewerListItem>> ListRecentAsync(
        int businessId, int? beforeId, int pageSize, CancellationToken ct = default)
    {
        // Answered like a missing row and answered here: a non-positive tenant is not a tenant, so no command and no
        // connection are created for it.
        if (businessId <= 0)
            return Array.Empty<NotificationViewerListItem>();

        var boundedPageSize = NotificationViewerBounds.ClampPageSize(pageSize);

        var items = await QueryAsync(
            ListSql,
            MapListItem,
            p =>
            {
                p.AddValue("@businessId", businessId)
                    .AddValue("@pageSize", boundedPageSize);
                // Typed as INT even when absent, so the cursor comparison never becomes an implicit string conversion
                // that would defeat the identity ordering. A non-positive cursor matches no row, which is exactly the
                // strict keyset answer to an identifier that no notice can carry.
                p.Add("@beforeId", SqlDbType.Int).Value = (object?)beforeId ?? DBNull.Value;
            },
            ct);

        return items;
    }

    public async Task<NotificationViewerDetail?> GetDetailAsync(
        int businessId, int notificationId, CancellationToken ct = default)
    {
        // Both refusals are the same neutral answer as a missing or foreign row, and they happen before any command is
        // created, so nothing reaches SQL for them and the caller learns nothing.
        if (businessId <= 0 || notificationId <= 0)
            return null;

        return await QuerySingleOrDefaultAsync(
            DetailSql,
            MapDetail,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@notificationId", notificationId),
            ct);
    }

    private static NotificationViewerListItem MapListItem(SqlDataReader reader) => new(
        reader.Get<int>("Id"),
        reader.GetEnum<NotificationType>("Type"),
        reader.GetEnum<NotificationStatus>("Status"),
        reader.Get<string>("Recipient"),
        reader.Get<string>("Subject"),
        reader.Get<int>("Attempts"),
        reader.Get<DateTime>("CreatedAt"),
        reader.GetNullable<DateTime>("SentAt"));

    private static NotificationViewerDetail MapDetail(SqlDataReader reader) => new(
        reader.Get<int>("Id"),
        reader.GetEnum<NotificationType>("Type"),
        reader.GetEnum<NotificationStatus>("Status"),
        reader.Get<string>("Recipient"),
        reader.Get<string>("Subject"),
        reader.Get<int>("Attempts"),
        reader.Get<DateTime>("CreatedAt"),
        reader.GetNullable<DateTime>("SentAt"),
        reader.Get<string>("BodyHtml"));
}
