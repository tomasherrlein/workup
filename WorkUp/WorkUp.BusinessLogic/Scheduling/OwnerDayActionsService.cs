using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Owner-only individual booking actions; the caller supplies the authenticated business ID.</summary>
public sealed class OwnerDayActionsService(IOwnerDayActionsRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<Result> FinishIndividualAsync(int businessId, int bookingId, OwnerDayCharge? charge = null,
        string? nextVisitNote = null, CancellationToken ct = default) =>
        ExecuteAsync(businessId, bookingId, BookingStatus.Attended, charge, nextVisitNote, ct);

    public Task<Result> MarkAbsentAsync(int businessId, int bookingId, CancellationToken ct = default) =>
        ExecuteAsync(businessId, bookingId, BookingStatus.Absent, null, null, ct);

    public Task<Result> ChargeIndividualAsync(int businessId, int bookingId, OwnerDayCharge charge,
        CancellationToken ct = default) =>
        ExecuteAsync(businessId, bookingId, null, charge, null, ct);

    private async Task<Result> ExecuteAsync(int businessId, int bookingId, BookingStatus? status,
        OwnerDayCharge? charge, string? note, CancellationToken ct)
    {
        if (businessId <= 0 || bookingId <= 0)
            return Failure("owner_day_not_found", "No encontramos ese turno.");
        if (charge is not null && (charge.Amount <= 0 || charge.Amount > 9999999999.99m ||
            decimal.Round(charge.Amount, 2) != charge.Amount || charge.PriceReason?.Length > 200))
            return Failure("owner_day_invalid_charge", "Revisá el importe y el motivo del precio.");
        if (note?.Length > 1000)
            return Failure("owner_day_invalid_note", "La observación no puede superar 1000 caracteres.");

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);
        var booking = await repository.LockAsync(businessId, bookingId, ct);
        if (booking is null || booking.BusinessId != businessId || booking.BookingId != bookingId)
            return Failure("owner_day_not_found", "No encontramos ese turno.");
        if (booking.AppointmentKind != AppointmentKind.Individual || booking.MaxCapacity != 1 ||
            booking.Origin is not (AppointmentOrigin.Reserved or AppointmentOrigin.WalkIn) ||
            booking.Modality is not null || booking.EnrollmentId is not null)
            return Failure("owner_day_individual_only", "Las clases y los abonos se gestionan desde la lista de clase.");
        if (booking.AppointmentStatus != AppointmentStatus.Scheduled || booking.CancelledAt is not null ||
            booking.BookingStatus == BookingStatus.Cancelled)
            return Failure("owner_day_unavailable", "Este turno no admite la acción.");
        if (status is not null && (booking.StartsAt > clock.Now ||
            booking.BookingStatus is not (BookingStatus.Pending or BookingStatus.Confirmed) ||
            booking.AttendanceRecordedAt is not null || booking.AbsenceSource is not null ||
            (booking.BookingStatus == BookingStatus.Pending && booking.ConfirmedAt is not null) ||
            (booking.BookingStatus == BookingStatus.Confirmed && booking.ConfirmedAt is null)))
            return Failure("owner_day_unavailable", "Este turno no admite la acción.");
        if (charge is not null)
        {
            if (booking.BookingStatus == BookingStatus.Absent ||
                await repository.HasEffectivePaymentAsync(businessId, bookingId, ct))
                return Failure("owner_day_already_paid", "Este turno ya tiene un cobro vigente o no admite cobro.");
            if (booking.AgreedPrice is { } agreed && charge.Amount != agreed)
                return Failure("owner_day_price_mismatch", "El importe no coincide con el precio acordado.");
        }
        var now = clock.Now;
        if (status is not null && !await repository.SetStatusAsync(businessId, bookingId, status.Value, now, ct))
            return Failure("owner_day_unavailable", "Este turno no admite la acción.");
        if (charge is not null)
            await repository.InsertPaymentAsync(new OwnerDayPayment(businessId, booking.ClientId, bookingId,
                booking.Origin == AppointmentOrigin.WalkIn ? PaymentConcept.WalkIn : PaymentConcept.IndividualAppointment,
                charge.Amount, booking.ListPrice, charge.PriceReason ?? booking.PriceReason, now), ct);
        if (!string.IsNullOrWhiteSpace(note))
            await repository.InsertNoteAsync(businessId, booking.ClientId, booking.AppointmentId, note.Trim(), now, ct);
        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    private static Result Failure(string code, string message) => Result.Failure(code, message);
}
