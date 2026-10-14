using System.Security.Cryptography;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Provides anonymous private-link actions and business-scoped link copying/replacement.</summary>
public sealed class PrivateAppointmentLinkService(
    IPrivateAppointmentLinkRepository repository,
    IUnitOfWork unitOfWork,
    ILinkTokenProtector linkTokenProtector,
    IPublicAppointmentUrlBuilder appointmentUrlBuilder,
    IClock clock)
{
    private const string PublicUnavailableCode = "private_link_unavailable";
    private const string PublicUnavailableMessage = "Este enlace ya no está disponible. Contactá al negocio.";
    private const string OwnerUnavailableCode = "owner_private_link_unavailable";
    private const string OwnerUnavailableMessage = "No hay un enlace vigente para este turno.";

    /// <summary>Returns only the linked appointment's date/time, service, worker, and currently available actions.</summary>
    public async Task<PrivateAppointmentView?> GetPublicViewAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var now = clock.Now;
        var appointment = await repository.GetPublicAppointmentAsync(SecureToken.Hash(token), now, ct);
        if (appointment is null || appointment.ExpiresAt <= now ||
            string.IsNullOrWhiteSpace(appointment.ServiceName) || string.IsNullOrWhiteSpace(appointment.WorkerName))
            return null;

        var isScheduled = appointment.AppointmentStatus == AppointmentStatus.Scheduled;
        var beforeStart = now < appointment.StartsAt && now < appointment.ExpiresAt;
        var inAbsenceWindow = now >= appointment.StartsAt && now < appointment.ExpiresAt;
        var canCancelOrReportAbsence = HasConsistentPendingState(appointment) || HasConsistentConfirmedState(appointment);

        return new PrivateAppointmentView(
            appointment.StartsAt,
            appointment.EndsAt,
            appointment.ServiceName,
            appointment.WorkerName,
            isScheduled && HasConsistentPendingState(appointment) && beforeStart,
            isScheduled && canCancelOrReportAbsence && beforeStart,
            isScheduled && canCancelOrReportAbsence && inAbsenceWindow);
    }

    /// <summary>Records the client's intention before the start; repeating an existing confirmation is a no-op.</summary>
    public Task<Result> ConfirmIntentionAsync(string? token, CancellationToken ct = default) =>
        ApplyPublicActionAsync(token, PrivateAppointmentLinkAction.Confirm, ct);

    /// <summary>Cancels a pending or confirmed individual booking before its start without deleting history.</summary>
    public Task<Result> CancelFromPrivateLinkAsync(string? token, CancellationToken ct = default) =>
        ApplyPublicActionAsync(token, PrivateAppointmentLinkAction.Cancel, ct);

    /// <summary>Records client-reported absence from the exact start until strictly before link expiry.</summary>
    public Task<Result> ReportAbsenceFromPrivateLinkAsync(string? token, CancellationToken ct = default) =>
        ApplyPublicActionAsync(token, PrivateAppointmentLinkAction.ReportAbsence, ct);

    /// <summary>Reconstructs the current link only for an owner-authorized, tenant-scoped booking.</summary>
    public async Task<Result<OwnerPrivateAppointmentLink>> CopyCurrentLinkAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        if (businessId <= 0 || bookingId <= 0)
            return OwnerUnavailable();

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);
        if (!await repository.LockOwnerBookingAsync(businessId, bookingId, ct))
            return OwnerUnavailable();

        var lookupTime = clock.Now;
        var currentLinks = await repository.GetActiveOwnerLinksAsync(businessId, bookingId, lookupTime, ct);
        var validationTime = clock.Now;
        if (!TryGetSingleActiveLink(currentLinks, validationTime, out var current))
            return OwnerUnavailable();

        string token;
        try
        {
            token = linkTokenProtector.Unprotect(current.ProtectedToken);
        }
        catch (CryptographicException)
        {
            return OwnerUnavailable();
        }

        if (string.IsNullOrWhiteSpace(token) || current.TokenHash.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SecureToken.Hash(token), current.TokenHash))
            return OwnerUnavailable();

        var url = appointmentUrlBuilder.Build(token);
        if (current.ExpiresAt <= clock.Now)
            return OwnerUnavailable();
        await transaction.CommitAsync(ct);
        return Result<OwnerPrivateAppointmentLink>.Success(new OwnerPrivateAppointmentLink(url, current.ExpiresAt));
    }

    /// <summary>Revokes the current active token without creating a replacement.</summary>
    public async Task<Result> RevokeCurrentLinkAsync(int businessId, int bookingId, CancellationToken ct = default)
    {
        if (businessId <= 0 || bookingId <= 0)
            return OwnerUnavailable();

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);
        if (!await repository.LockOwnerBookingAsync(businessId, bookingId, ct))
            return OwnerUnavailable();

        var lookupTime = clock.Now;
        var currentLinks = await repository.GetActiveOwnerLinksAsync(businessId, bookingId, lookupTime, ct);
        var revokeTime = clock.Now;
        if (!TryGetSingleActiveLink(currentLinks, revokeTime, out var current) ||
            !await repository.RevokeOwnerLinkAsync(businessId, bookingId, current.LinkId, revokeTime, ct))
            return OwnerUnavailable();

        if (current.ExpiresAt <= clock.Now)
            return OwnerUnavailable();
        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Revokes the current token and inserts a random replacement atomically, retaining the original expiry. The
    /// repository locks the tenant-owned booking so concurrent replacements serialize on the same SQL row.
    /// </summary>
    public async Task<Result<OwnerPrivateAppointmentLink>> ReplaceCurrentLinkAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        if (businessId <= 0 || bookingId <= 0)
            return OwnerUnavailable();

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);
        if (!await repository.LockOwnerBookingAsync(businessId, bookingId, ct))
            return OwnerUnavailable();

        var lookupTime = clock.Now;
        var currentLinks = await repository.GetActiveOwnerLinksAsync(businessId, bookingId, lookupTime, ct);
        var rotationTime = clock.Now;
        if (!TryGetSingleActiveLink(currentLinks, rotationTime, out var current))
            return OwnerUnavailable();

        var generated = SecureToken.Create();
        var replacement = new AppointmentLink
        {
            BookingId = bookingId,
            TokenHash = generated.Hash,
            ProtectedToken = linkTokenProtector.Protect(generated.Token),
            ExpiresAt = current.ExpiresAt
        };

        if (!await repository.RevokeOwnerLinkAsync(businessId, bookingId, current.LinkId, rotationTime, ct))
            return OwnerUnavailable();

        var createdAt = clock.Now;
        if (current.ExpiresAt <= createdAt)
            return OwnerUnavailable();
        replacement.CreatedAt = createdAt;

        // ScalarAsync returns 0 when the guarded INSERT ... SELECT yields no row. Do not commit: disposing this
        // transaction rolls the revocation back, so a failed replacement cannot strand the booking without a link.
        if (await repository.InsertOwnerLinkAsync(businessId, replacement, ct) <= 0)
            return OwnerUnavailable();

        var url = appointmentUrlBuilder.Build(generated.Token);
        if (current.ExpiresAt <= clock.Now)
            return OwnerUnavailable();
        await transaction.CommitAsync(ct);
        return Result<OwnerPrivateAppointmentLink>.Success(new OwnerPrivateAppointmentLink(url, current.ExpiresAt));
    }

    private async Task<Result> ApplyPublicActionAsync(
        string? token, PrivateAppointmentLinkAction action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return PublicUnavailable();

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);
        var now = clock.Now;
        var applied = await repository.ApplyPublicActionAsync(SecureToken.Hash(token), action, now, ct);
        if (!applied)
            return PublicUnavailable();

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    private static bool TryGetSingleActiveLink(
        IReadOnlyList<OwnerPrivateAppointmentLinkRecord> links,
        DateTime now,
        out OwnerPrivateAppointmentLinkRecord link)
    {
        if (links.Count == 1 && links[0].LinkId > 0 && links[0].ExpiresAt > now &&
            links[0].TokenHash.Length == 32 && !string.IsNullOrWhiteSpace(links[0].ProtectedToken))
        {
            link = links[0];
            return true;
        }

        link = null!;
        return false;
    }

    private static bool HasConsistentPendingState(PrivateAppointmentLinkRecord appointment) =>
        appointment.BookingStatus == BookingStatus.Pending && appointment.ConfirmedAt is null &&
        appointment.CancelledAt is null && appointment.CancelledBy is null &&
        appointment.AttendanceRecordedAt is null && appointment.AbsenceSource is null;

    private static bool HasConsistentConfirmedState(PrivateAppointmentLinkRecord appointment) =>
        appointment.BookingStatus == BookingStatus.Confirmed && appointment.ConfirmedAt is not null &&
        appointment.CancelledAt is null && appointment.CancelledBy is null &&
        appointment.AttendanceRecordedAt is null && appointment.AbsenceSource is null;

    private static Result PublicUnavailable() => Result.Failure(PublicUnavailableCode, PublicUnavailableMessage);

    private static Result<OwnerPrivateAppointmentLink> OwnerUnavailable() =>
        Result<OwnerPrivateAppointmentLink>.Failure(OwnerUnavailableCode, OwnerUnavailableMessage);
}
