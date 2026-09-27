using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// One row of the generated-email viewer (T26, plan assumptions 10 and 11). This is the <em>list</em> projection: it
/// answers what happened to a queued notice, never what the notice says.
/// <para>
/// The type is deliberately unable to carry the notice body, the stored error, the dispatcher's claim
/// (<c>ClaimedAt</c>, <c>DispatchKey</c>), the retry gate or the dedup key. A queued body can embed a single-use
/// private appointment link or a password-reset token, and the claim fields belong to the dispatcher's internal
/// concurrency control; none of them may travel in a page that renders many rows at once. The body is reachable only
/// through <see cref="NotificationViewerDetail"/>, read one row at a time for the caller's own business.
/// </para>
/// </summary>
public sealed record NotificationViewerListItem(
    int Id,
    NotificationType Type,
    NotificationStatus Status,
    string Recipient,
    string Subject,
    int Attempts,
    DateTime CreatedAt,
    DateTime? SentAt);

/// <summary>
/// One notice of the generated-email viewer with its body. It is the <em>detail</em> projection and it is only reached
/// for a row of the caller's own business, which is why the body may live here: it is exactly what the owner asked to
/// inspect. It still carries no stored error, no dispatch claim and no dedup key — the viewer shows the notice, it is
/// not a window into dispatch internals.
/// </summary>
public sealed record NotificationViewerDetail(
    int Id,
    NotificationType Type,
    NotificationStatus Status,
    string Recipient,
    string Subject,
    int Attempts,
    DateTime CreatedAt,
    DateTime? SentAt,
    string BodyHtml);

/// <summary>
/// The fixed limits of the viewer (T26). They live with the contract so the SQL adapter, the in-memory fake used by the
/// controller tests and any future caller cannot disagree about them.
/// </summary>
public static class NotificationViewerBounds
{
    /// <summary>
    /// Hard upper bound of one page. The viewer is a convenience for inspecting a handful of generated emails, never a
    /// sweeping export of the outbox, so no request can ask for more than this.
    /// </summary>
    public const int MaxPageSize = 50;

    /// <summary>
    /// Clamps a requested page size into <c>[1, <see cref="MaxPageSize"/>]</c>. A nonsensical value is clamped rather
    /// than refused, because it can never widen the read: the viewer always returns at least one row at most and always
    /// at most one bounded page, and a non-positive value can never reach a storage engine as an unbounded or negative
    /// <c>TOP</c>.
    /// </summary>
    public static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);
}

/// <summary>
/// Read-only persistence boundary of the generated-email viewer (T26, plan assumptions 10 and 11). It reads the same
/// <c>Notifications</c> table the dispatcher writes, and it only ever reads: no statement here may create, update or
/// delete a row, and nothing here may log a body, a recipient or a stored error.
/// <para>
/// <b>Tenant isolation.</b> Every read is scoped by <c>Notifications.BusinessId = businessId</c>, the business the
/// authenticated owner carries in its claims. A row with <c>BusinessId</c> NULL is therefore never returned, not even
/// when its recipient is the owner's own notice address: an account-verification or password-reset notice belongs to no
/// business, and the tenant must never be inferred from the addressee. The two demo businesses share one simulated
/// clock (plan assumption 11), so isolation cannot rest on time, on an unguessable identifier or on the recipient
/// address; it rests on this column alone.
/// </para>
/// <para>
/// <b>Neutral denial.</b> A non-positive <paramref name="businessId"/> or <paramref name="notificationId"/> is answered
/// exactly like a missing or foreign row: an empty page or <c>null</c>, and without issuing any query at all. The
/// caller therefore cannot tell "you did not name it" from "it is not yours", and a broken or unauthenticated caller
/// never reaches storage.
/// </para>
/// <para>
/// LIMITATION: the in-memory fake proves this contract for the controller tests; only the opt-in SQL fixture can prove
/// what SQL Server does with the tenant predicate, the keyset cursor and the bounded <c>TOP</c>.
/// </para>
/// </summary>
public interface INotificationViewerRepository
{
    /// <summary>
    /// Returns one bounded page of the business's notices, newest first by <c>Notification.Id</c> descending, or an
    /// empty list when <paramref name="businessId"/> is not positive.
    /// <para>
    /// The cursor is a strict keyset: <paramref name="beforeId"/> is the <c>Id</c> of the last row of the previous page
    /// and only strictly older rows are returned, so pages never repeat or skip a row and the read is never an offset
    /// scan. A <c>null</c> cursor starts at the newest row; a non-positive cursor matches no row and yields an empty
    /// page, because no notice can carry such an identity. <paramref name="pageSize"/> is clamped into
    /// <c>[1, <see cref="NotificationViewerBounds.MaxPageSize"/>]</c>.
    /// </para>
    /// <para>
    /// Each row carries the notice type, the dispatch status, the recipient, the subject, the attempt count and the
    /// creation and sent instants. The row carries no body, no stored error, no retry gate and no dispatch claim: the
    /// projection has no member for them and the read never selects them.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<NotificationViewerListItem>> ListRecentAsync(
        int businessId, int? beforeId, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Returns the notice with its body when it belongs to <paramref name="businessId"/>, or <c>null</c> when it does
    /// not exist, belongs to another business, has no business at all or when either identifier is not positive. The
    /// lookup is by identity and tenant, never by recipient address, so a notice that merely shares the owner's notice
    /// address is never returned and the two outcomes cannot be distinguished by the caller.
    /// </summary>
    Task<NotificationViewerDetail?> GetDetailAsync(
        int businessId, int notificationId, CancellationToken ct = default);
}
