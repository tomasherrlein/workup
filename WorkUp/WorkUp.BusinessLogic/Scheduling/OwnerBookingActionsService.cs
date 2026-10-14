using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

public sealed record OwnerBookingForm(
    PublicBookingCatalog Catalog,
    IReadOnlyList<PublicBookingWorkerOption> Workers,
    int? ServiceId,
    int? WorkerId,
    DateOnly Date,
    IReadOnlyList<TimeOnly> AvailableStartTimes,
    DateTime? NearestAlternative = null,
    bool IsPastDate = false);

public sealed record OwnerBookingCancellationState(
    int BookingId,
    int AppointmentId,
    DateTime StartsAt,
    AppointmentKind AppointmentKind,
    AppointmentStatus AppointmentStatus,
    AppointmentOrigin? Origin,
    int MaxCapacity,
    BookingStatus BookingStatus,
    BookingModality? Modality,
    int? EnrollmentId,
    DateTime? ConfirmedAt,
    DateTime? CancelledAt,
    CancelledBy? CancelledBy,
    DateTime? AttendanceRecordedAt,
    AbsenceSource? AbsenceSource);

/// <summary>Persistence boundary for tenant-scoped owner cancellation.</summary>
public interface IOwnerBookingActionsRepository
{
    Task<OwnerBookingCancellationState?> LockOwnerBookingForCancellationAsync(
        int businessId, int bookingId, CancellationToken ct = default);

    Task<bool> CancelOwnerBookingAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default);
}

/// <summary>Owner-assisted individual bookings and pre-start cancellation.</summary>
public sealed class OwnerBookingActionsService(
    PublicBookingBrowseService browseService,
    BookingService bookingService,
    IOwnerBookingActionsRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const string BookingUnavailableCode = "owner_booking_unavailable";
    private const string BookingUnavailableMessage = "No se pudo cancelar este turno. Revisá su estado e intentá de nuevo.";

    public async Task<OwnerBookingForm?> GetBookingFormAsync(
        int businessId,
        string businessSlug,
        int? serviceId,
        int? workerId,
        DateOnly? date,
        CancellationToken ct = default)
    {
        if (businessId <= 0 || string.IsNullOrWhiteSpace(businessSlug))
            return null;

        // Resolve the owner's trusted slug first, then retain only public projections proven to belong to that
        // authenticated business before requesting dependent worker or availability projections.
        var catalog = await browseService.GetCatalogAsync(businessSlug, ct);
        if (catalog is null || catalog.Business.Id != businessId)
            return null;

        var today = DateOnly.FromDateTime(clock.Now);
        var selectedDate = date ?? today;
        var isPastDate = selectedDate < today;
        DateTime? nearestAlternative = null;
        var selectedServiceId = catalog.Services.Any(service => service.Id == serviceId) ? serviceId : null;
        IReadOnlyList<PublicBookingWorkerOption> workers = [];
        int? selectedWorkerId = null;
        IReadOnlyList<TimeOnly> availableStartTimes = [];

        if (selectedServiceId is { } validServiceId)
        {
            var selection = await browseService.GetWorkerSelectionAsync(businessSlug, validServiceId, ct);
            if (selection is { } validSelection && validSelection.Business.Id == businessId &&
                validSelection.Service.Id == validServiceId)
            {
                workers = validSelection.Workers.Where(worker => worker.Id > 0).ToArray();
                selectedWorkerId = workers.Any(worker => worker.Id == workerId) ? workerId : null;
                if (selectedWorkerId is { } validWorkerId && !isPastDate)
                {
                    var availability = await browseService.GetAvailabilityAsync(
                        businessSlug, validServiceId, validWorkerId, selectedDate, ct);
                    if (availability is { } validAvailability &&
                        validAvailability.Business.Id == businessId &&
                        validAvailability.Service.Id == validServiceId &&
                        validAvailability.Worker.Id == validWorkerId &&
                        validAvailability.Date == selectedDate)
                    {
                        availableStartTimes = validAvailability.AvailableStartTimes;
                        if (availableStartTimes.Count == 0)
                            nearestAlternative = validAvailability.NearestFutureAlternative;
                    }
                    else
                        selectedWorkerId = null;
                }
            }
            else
            {
                selectedServiceId = null;
            }
        }

        return new OwnerBookingForm(
            catalog, workers, selectedServiceId, selectedWorkerId, selectedDate, availableStartTimes,
            nearestAlternative, isPastDate);
    }

    /// <summary>Uses the exact public booking transaction, worker lock, availability recheck, link, and outbox path.</summary>
    public Task<PublicBookingAttempt> BookIndividualAsync(
        int businessId,
        BookIndividualAppointmentRequest request,
        CancellationToken ct = default) =>
        bookingService.BookIndividualAsync(businessId, request, ct);

    /// <summary>Cancels only a consistent active individual reservation before its start, preserving payment history.</summary>
    public async Task<Result> CancelIndividualBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        if (businessId <= 0 || bookingId <= 0)
            return BookingNotFound();

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);
        var booking = await repository.LockOwnerBookingForCancellationAsync(businessId, bookingId, ct);
        if (booking is null || booking.BookingId != bookingId)
            return BookingNotFound();

        var now = clock.Now;
        if (!CanCancel(booking, now) ||
            !await repository.CancelOwnerBookingAsync(businessId, bookingId, now, ct))
            return BookingUnavailable();

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public static bool CanCancel(OwnerBookingCancellationState booking, DateTime now) =>
        booking.BookingId > 0 && booking.AppointmentId > 0 &&
        booking.AppointmentKind == AppointmentKind.Individual &&
        booking.AppointmentStatus == AppointmentStatus.Scheduled &&
        booking.Origin == AppointmentOrigin.Reserved && booking.MaxCapacity == 1 &&
        booking.Modality is null && booking.EnrollmentId is null && booking.StartsAt > now &&
        booking.CancelledAt is null && booking.CancelledBy is null &&
        booking.AttendanceRecordedAt is null && booking.AbsenceSource is null &&
        ((booking.BookingStatus == BookingStatus.Pending && booking.ConfirmedAt is null) ||
         (booking.BookingStatus == BookingStatus.Confirmed && booking.ConfirmedAt is not null));

    private static Result BookingNotFound() =>
        Result.Failure("owner_booking_not_found", "No encontramos ese turno.");

    private static Result BookingUnavailable() =>
        Result.Failure(BookingUnavailableCode, BookingUnavailableMessage);
}
