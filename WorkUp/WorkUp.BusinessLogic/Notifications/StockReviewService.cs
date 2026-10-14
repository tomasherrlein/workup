using System.Globalization;
using System.Net;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// Business core of the periodic stock review (RF-19, D-03, plan assumption 3). It is invoked once per tick with the
/// instant the T23 runner read from <c>IClock</c>; the service never reads a clock of its own, so the real clock and
/// the simulated T26 clock work without changes. The instant arrives as local business time and is used as-is: no
/// timezone conversion happens here.
/// <para>
/// The cadence is anchored permanently to the local calendar DATE of <c>Businesses.CreatedAt</c>: the first review is
/// due after <c>StockReviewEveryDays</c> calendar days and every N days afterwards, with no dependence on when a supply
/// was declared. A business with <b>zero</b> registered supplies is reviewed just the same, because RF-19 asks the owner
/// to keep the declared stock current and an empty inventory is exactly what the owner must fill in. A later supply
/// declaration never restarts the calendar.
/// </para>
/// <para>
/// Every business is served at most once per period. The bucket is <c>CreatedAt + floor(diffDays / N) * N</c>, so a
/// tick that missed the exact due day still issues <b>one</b> review for the current period on a later day, while the
/// periods the host never observed are <b>not</b> backfilled one by one. The once-per-period guarantee is the key
/// <c>stock:business:{BusinessId}:on:{yyyyMMdd}</c> over the bucket date, and the guarded enqueue also refuses a bucket
/// when a later one was already reviewed, so a clock rewind cannot resurrect an obsolete period.
/// </para>
/// <para>
/// The recipient is the business notice email and only it: a missing or malformed email skips the business, and the
/// contact phone is never a fallback (RF-19 speaks of a phone, but WhatsApp/SMS is out of scope; assumption 3 routes
/// owner notices to email). The body lists the current <b>active</b> supplies with their declared current and minimum
/// quantities, encoded for HTML; an inventory with no active supply says so explicitly instead of sending an empty
/// body. Stock is only reported, never deducted (D-03), and supplier credentials or client data are not part of the
/// contract at all. Nothing here logs: no raw body or recipient reaches an error channel, and
/// <see cref="Notification.LastError"/> is never written.
/// </para>
/// <para>
/// The service trusts the scan only for the missing current bucket: it enforces its own eligibility, recipient,
/// bucket and active-supply rules before composing, and the guarded enqueue rechecks the live rows in one short
/// transaction, so a recipient, cadence or anchor edited — or a review already produced — between the scan and the
/// insert is a no-op instead of a stale or duplicate email.
/// </para>
/// </summary>
public sealed class StockReviewService(IStockReviewRepository repository)
{
    /// <summary>Most businesses one tick processes, so a large tenant set never blocks the scheduler loop.</summary>
    public const int BatchSize = 20;

    /// <summary>Subject prefix of the review; the due bucket date is appended.</summary>
    public const string SubjectPrefix = "Revisión de existencias";

    /// <summary>Explicit wording of a business with no active supply, never an empty body.</summary>
    public const string EmptyInventoryText = "todavía no registraste insumos";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Processes the batch emitted for <paramref name="now"/> and returns how many notices were enqueued. A business
    /// whose notice email is blank or malformed is skipped: RF-19 has no phone or fallback channel, so an unusable
    /// recipient must produce nothing rather than the wrong email.
    /// </summary>
    public async Task<int> RunAsync(DateTime now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var candidates = await repository.ListCandidatesAsync(now, BatchSize, ct);
        var enqueued = 0;

        // The cap is enforced here too: a broken or permissive reader must not turn one tick into an unbounded write.
        foreach (var candidate in candidates.Take(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            if (!IsEligible(candidate, now))
                continue;

            if (await repository.TryEnqueueReviewAsync(Compose(candidate, now), ct))
                enqueued++;
        }

        return enqueued;
    }

    /// <summary>
    /// Current due bucket of a business as of <paramref name="now"/>, or <c>null</c> when the business is not yet due.
    /// The anchor is the local calendar date of <paramref name="anchorCreatedAt"/>; the first bucket is the anchor plus
    /// <paramref name="everyDays"/> calendar days and every N days afterwards, so the bucket is
    /// <c>anchor + floor(diffDays / N) * N</c>. A non-positive cadence, an instant before the anchor (a clock rewind or
    /// a future creation) and any instant before the first due day all yield <c>null</c>. The arithmetic stays inside
    /// <see cref="DateOnly"/>: the bucket never passes <paramref name="now"/>, so the last representable day cannot
    /// overflow.
    /// </summary>
    public static DateOnly? CurrentDueDate(DateTime anchorCreatedAt, int everyDays, DateTime now)
    {
        if (everyDays < 1)
            return null;

        var anchor = DateOnly.FromDateTime(anchorCreatedAt);
        var today = DateOnly.FromDateTime(now);
        if (today < anchor)
            return null;

        var diffDays = today.DayNumber - anchor.DayNumber;
        if (diffDays < everyDays)
            return null;

        // diffDays >= everyDays >= 1, so the integer division is a positive floor and the bucket is never after today.
        var periods = diffDays / everyDays;
        return anchor.AddDays(periods * everyDays);
    }

    /// <summary>
    /// Unique scheduled key of one business review for a due bucket: <c>stock:business:{id}:on:{yyyyMMdd}</c>.
    /// </summary>
    public static string DedupKey(int businessId, DateOnly dueDate) =>
        $"stock:business:{businessId}:on:{dueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Whether a candidate is eligible at <paramref name="now"/> without touching storage: a real business with a
    /// usable notice email whose current due bucket equals the bucket the scan reported. This is the same predicate the
    /// scan applies, so the two layers cannot drift apart, and it rejects a permissive reader that over-returns an
    /// ineligible business or a non-canonical bucket.
    /// </summary>
    public static bool IsEligible(StockReviewCandidate candidate, DateTime now)
    {
        if (candidate.BusinessId <= 0)
            return false;
        if (!EmailFormat.IsValid(candidate.Recipient))
            return false;

        var due = CurrentDueDate(candidate.AnchorCreatedAt, candidate.StockReviewEveryDays, now);
        return due is not null && due.Value == candidate.DueDate;
    }

    /// <summary>
    /// The supplies the body may show, re-derived from the candidate so a permissive reader cannot inject an inactive
    /// supply: only active rows are kept, ordered by supply id so the body never depends on the reader's ordering.
    /// </summary>
    public static IReadOnlyList<StockReviewSupply> VisibleSupplies(StockReviewCandidate candidate) =>
        candidate.Supplies
            .Where(supply => supply.IsActive)
            .OrderBy(supply => supply.SupplyId)
            .ToArray();

    /// <summary>
    /// Builds the notice. <see cref="Notification.CreatedAt"/> is the run instant the T23 runner supplied, and the key
    /// is derived from the due bucket, never from the business time, so a late tick inside the same period and a
    /// punctual one share it. The notification carries the anchor and cadence the body was composed from, so the
    /// guarded enqueue can reject an edit made between the scan and the insert.
    /// </summary>
    private StockReviewNotice Compose(StockReviewCandidate candidate, DateTime now)
    {
        var due = CurrentDueDate(candidate.AnchorCreatedAt, candidate.StockReviewEveryDays, now)!.Value;
        return new StockReviewNotice(
            candidate.BusinessId,
            candidate.AnchorCreatedAt,
            candidate.StockReviewEveryDays,
            due,
            new Notification
            {
                BusinessId = candidate.BusinessId,
                Type = NotificationType.StockReview,
                Recipient = candidate.Recipient,
                Subject = $"{SubjectPrefix}: {due.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}",
                BodyHtml = BuildBody(candidate),
                DedupKey = DedupKey(candidate.BusinessId, due),
                CreatedAt = now
            });
    }

    /// <summary>
    /// Renders the owner-facing body from the re-derived active supplies. Every name is HTML-encoded because it is
    /// business data a user can influence; an inventory with no active supply says so explicitly instead of sending an
    /// empty body. Only declared quantities are reported: nothing deducts stock here (D-03).
    /// </summary>
    private static string BuildBody(StockReviewCandidate candidate)
    {
        var visible = VisibleSupplies(candidate);
        var safeBusinessName = WebUtility.HtmlEncode(candidate.BusinessName);
        var lines = visible.Count == 0
            ? $"<p>{EmptyInventoryText}</p>"
            : string.Concat(visible.Select(RenderSupply));

        return $"<p>Es momento de revisar la existencia de tus insumos.</p>{lines}<p>{safeBusinessName}</p>";
    }

    /// <summary>Renders one supply with its declared current and minimum quantities.</summary>
    private static string RenderSupply(StockReviewSupply supply)
    {
        var name = WebUtility.HtmlEncode(supply.Name);
        var current = supply.CurrentStock.ToString("0.##", Spanish);
        var minimum = supply.MinimumStock.ToString("0.##", Spanish);

        return $"<p>{name}: existencia {current}, mínimo {minimum}</p>";
    }
}
