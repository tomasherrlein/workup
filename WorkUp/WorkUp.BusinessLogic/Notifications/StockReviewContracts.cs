using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// One still-active supply of a business, reduced to what the stock review (RF-19) shows: the name and the declared
/// current and minimum quantities. Supplier credentials and any client datum are structurally absent from the row, so
/// the review cannot leak them. <see cref="IsActive"/> travels with the row so the service can re-apply the "active
/// only" predicate even against a permissive reader.
/// </summary>
public sealed record StockReviewSupply(
    int SupplyId,
    string Name,
    decimal CurrentStock,
    decimal MinimumStock,
    bool IsActive);

/// <summary>
/// One business the stock review (RF-19) may reach, with everything the notice needs so the core never reads storage
/// twice. <see cref="Recipient"/> is the business notice email and the only destination the review may use: the phone
/// in <c>Businesses.ContactPhone</c> is a contact datum, never a delivery channel (plan assumption 3).
/// <para>
/// <see cref="AnchorCreatedAt"/> is the permanent cadence anchor: the local calendar DATE of
/// <c>Businesses.CreatedAt</c>. <see cref="StockReviewEveryDays"/> is the configured period in days and
/// <see cref="DueDate"/> is the current period bucket the scan computed from the anchor and the supplied instant.
/// <see cref="Supplies"/> holds only the business's current active supplies.
/// </para>
/// </summary>
public sealed record StockReviewCandidate(
    int BusinessId,
    string Recipient,
    string BusinessName,
    DateTime AnchorCreatedAt,
    int StockReviewEveryDays,
    DateOnly DueDate,
    IReadOnlyList<StockReviewSupply> Supplies);

/// <summary>
/// One review to queue together with the facts the guarded enqueue rechecks: <see cref="Notification.CreatedAt"/> is
/// the instant the T23 runner supplied, <see cref="AnchorCreatedAt"/> and <see cref="StockReviewEveryDays"/> are the
/// anchor and cadence the notice was composed from, and <see cref="DueDate"/> is the period bucket the notice belongs
/// to. The scheduled key is
/// <c>stock:business:{BusinessId}:on:{DueDate:yyyyMMdd}</c>, so a retried or repeated tick inside the same period is a
/// no-op while the next period's key differs.
/// </summary>
public sealed record StockReviewNotice(
    int BusinessId,
    DateTime AnchorCreatedAt,
    int StockReviewEveryDays,
    DateOnly DueDate,
    Notification Notification);

/// <summary>
/// Persistence boundary of the stock review (RF-19, D-03). Every call is bounded and the enqueue is the only write.
/// <para>
/// <see cref="ListCandidatesAsync"/> returns at most <paramref name="batchSize"/> businesses whose current period is
/// already due at <paramref name="now"/> — the permanent anchor is the local calendar date of
/// <c>Businesses.CreatedAt</c>, the first review is due after <c>StockReviewEveryDays</c> calendar days and every N
/// days afterwards, for a business with or without registered supplies — whose notice email is present and
/// well-formed, and whose current-bucket key <c>stock:business:{id}:on:{yyyyMMdd}</c> does not exist yet. A business
/// is also skipped when a review for a <b>later</b> bucket already exists, so a clock rewind never backfills an
/// obsolete period. Each candidate carries only its current <b>active</b> supplies, ordered by supply id, and the
/// supplied instant never reaches a bucket that predates the anchor. The scan is tenant-scoped per business but global
/// across tenants so one tick serves every business. A business without a usable notice email is never returned,
/// because RF-19 has no phone or fallback channel (plan assumption 3). Ordered by business id and capped so one tick
/// stays bounded.
/// </para>
/// <para>
/// <see cref="TryEnqueueReviewAsync"/> is <b>one short SQL transaction</b>: it re-reads the business and inserts the
/// notice only when the business still exists, the recipient is still the one the body was composed for, the anchor
/// date and the configured cadence are unchanged, the composed instant is still inside the notice's due bucket
/// (re-derived from the current anchor and cadence at <see cref="Notification.CreatedAt"/>), the type and key are the
/// canonical ones, no earlier-or-equal review shares the key and no later review already exists. The insert is guarded
/// by <c>UQ_Notifications_DedupKey</c>, so a concurrent tick or a retried one is a <c>false</c> no-op rather than a
/// second email. It never writes <see cref="Notification.LastError"/>.
/// </para>
/// <para>
/// The row predicate above is a scan obligation, not a trust boundary: the service independently re-derives it from
/// the candidate before composing, so a reader that over-returns an inactive supply, an ineligible business or a
/// non-canonical bucket cannot leak it into a body.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake cannot prove the atomic guard or the unique-key race; only a SQL fixture can. The fake
/// proves the service contract, never the runtime transaction.
/// </para>
/// </summary>
public interface IStockReviewRepository
{
    /// <summary>
    /// Returns at most <paramref name="batchSize"/> eligible businesses as of <paramref name="now"/>, each with its
    /// current active supplies. The predicate is documented on the interface: current period already due, usable
    /// notice email, no review yet for the current bucket and none for a later one.
    /// </summary>
    Task<IReadOnlyList<StockReviewCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Atomically rechecks the guard described on the interface and inserts
    /// <see cref="StockReviewNotice.Notification"/> when the business is still eligible. Returns <c>true</c> only when
    /// a row was inserted; <c>false</c> when the business vanished, its recipient, anchor or cadence changed after the
    /// scan, the composed instant is outside the notice's bucket, the type or key is not canonical, the bucket key
    /// already exists, or a later bucket was already reviewed.
    /// </summary>
    Task<bool> TryEnqueueReviewAsync(StockReviewNotice notice, CancellationToken ct = default);
}
