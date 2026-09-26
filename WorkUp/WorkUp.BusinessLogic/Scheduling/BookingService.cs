using System.Globalization;
using System.Net;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>
/// Atomically creates an individual reservation and its private-link email. Both entry points share one
/// transactional core — the per-worker update lock, the post-lock availability read, the pending booking, the
/// negotiated price, the private link and the deduplicated notification — and differ only in how the ficha is
/// resolved: the public form by normalized email (creating it when missing), the authenticated client panel by the
/// tenant-scoped <c>ClientId</c> from the cookie.
/// </summary>
public sealed class BookingService(
    IPublicBookingRepository repository,
    IUnitOfWork unitOfWork,
    ILinkTokenProtector linkTokenProtector,
    IPublicAppointmentUrlBuilder appointmentUrlBuilder,
    IClock clock)
{
    private const int AlternativeSearchDays = 30;
    private const int MaxNameLength = 60;
    private const int MaxEmailLength = 254;
    private const string StartTimeField = nameof(BookIndividualAppointmentRequest.StartTime);
    private const string ClientNotFoundCode = "client_booking_not_found";
    private const string ClientNotFoundMessage = "No pudimos completar la reserva.";

    public async Task<PublicBookingAttempt> BookIndividualAsync(
        int businessId,
        BookIndividualAppointmentRequest request,
        CancellationToken ct = default)
    {
        var validation = Validate(businessId, request, clock.Now, out var email, out var firstName, out var lastName, out var startsAt);
        if (validation is not null)
            return Failed(validation);

        return await BookIndividualCoreAsync(
            businessId,
            request.ServiceId,
            request.WorkerId,
            request.Date,
            startsAt,
            async now =>
            {
                var client = await repository.GetClientByEmailAsync(businessId, email, ct);
                if (client is null)
                {
                    client = new Client
                    {
                        BusinessId = businessId,
                        FirstName = firstName,
                        LastName = lastName,
                        Email = email,
                        Phone = null,
                        HealthNotes = null,
                        CreatedAt = now
                    };
                    var clientId = await repository.TryInsertClientAsync(client, ct);
                    if (clientId is null)
                        return ClientResolution.Fail(new BusinessError("booking_retry", "No pudimos completar la reserva. Revisá los datos e intentá de nuevo."));
                    client.Id = clientId.Value;
                }

                // The public form keeps echoing the submitted (normalized) address, exactly as before the panel path.
                return ClientResolution.Ok(client, email);
            },
            ct);
    }

    /// <summary>
    /// Creates the pending individual turn of the client authenticated by the panel cookie. The caller supplies
    /// <c>businessId</c> and <c>clientId</c> straight from the client session; this method never receives an email or
    /// a name, never looks a ficha up by email and never inserts a client. It shares the public transactional core:
    /// the same worker lock, the same post-lock availability read, and the same pending booking, price, private link
    /// and deduplicated notification. A missing or cross-tenant ficha fails with the generic
    /// <c>client_booking_not_found</c> and writes nothing.
    /// </summary>
    public async Task<PublicBookingAttempt> BookIndividualForClientAsync(
        int businessId,
        int clientId,
        int serviceId,
        int workerId,
        DateOnly date,
        TimeOnly startTime,
        CancellationToken ct = default)
    {
        var validation = ValidateClientRequest(businessId, clientId, serviceId, workerId, date, startTime, clock.Now, out var startsAt);
        if (validation is not null)
            return Failed(validation);

        return await BookIndividualCoreAsync(
            businessId,
            serviceId,
            workerId,
            date,
            startsAt,
            async _ =>
            {
                var client = await repository.GetClientByIdAsync(businessId, clientId, ct);
                if (client is null || client.BusinessId != businessId || client.Id != clientId)
                    return ClientResolution.Fail(new BusinessError(ClientNotFoundCode, ClientNotFoundMessage));

                // The address comes from the persisted ficha, never from the cookie or a resubmitted form field.
                return ClientResolution.Ok(client, client.Email);
            },
            ct);
    }

    private async Task<PublicBookingAttempt> BookIndividualCoreAsync(
        int businessId,
        int serviceId,
        int workerId,
        DateOnly date,
        DateTime startsAt,
        Func<DateTime, Task<ClientResolution>> resolveClient,
        CancellationToken ct)
    {
        var searchEndDate = date.AddDays(AlternativeSearchDays);
        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // This worker-row update lock serializes competing reservations before any availability data is read.
        var worker = await repository.LockActiveWorkerAsync(businessId, workerId, ct);
        if (worker is null || !worker.IsActive || worker.BusinessId != businessId || worker.Id != workerId)
            return Failed("worker_unavailable", "No encontramos a esa trabajadora disponible.");

        var service = await repository.GetActiveIndividualServiceAsync(businessId, serviceId, ct);
        if (service is null || !service.IsActive || !service.IsIndividual || service.BusinessId != businessId || service.Id != serviceId)
            return Failed("service_unavailable", "Ese servicio no está disponible para reservar.");

        var business = await repository.GetBusinessSettingsAsync(businessId, ct);
        if (business is null)
            return Failed("business_unavailable", "No pudimos completar la reserva. Probá de nuevo.");

        // Read fresh schedules, breaks, appointments, and bookings only after acquiring the worker lock.
        var snapshot = await repository.GetAvailabilitySnapshotAsync(businessId, worker.Id, date, searchEndDate, ct);
        var now = clock.Now;
        if (startsAt <= now)
            return Failed("booking_start_in_past", "Elegí un horario futuro.", StartTimeField);

        var availableSlots = AvailabilityCalculator.GetAvailableSlots(
            worker.Id, date, service, snapshot.Schedules, snapshot.Breaks, snapshot.Appointments, snapshot.Bookings);
        if (!availableSlots.Contains(startsAt))
        {
            var alternative = NearestSlotSearch.Find(
                worker.Id, date, searchEndDate, startsAt, service, snapshot, slot => slot > now);
            return Failed("slot_unavailable", "Ese horario no está disponible. Elegí otro.", StartTimeField, alternative);
        }

        var resolution = await resolveClient(now);
        if (resolution.Error is not null)
            return Failed(resolution.Error);
        var client = resolution.Client!;

        var appointment = new Appointment
        {
            BusinessId = businessId,
            Kind = AppointmentKind.Individual,
            ServiceId = service.Id,
            WorkerId = worker.Id,
            StartsAt = startsAt,
            EndsAt = startsAt.AddMinutes(service.DurationMinutes),
            MaxCapacity = 1,
            Origin = AppointmentOrigin.Reserved,
            Status = AppointmentStatus.Scheduled,
            CreatedAt = now
        };
        appointment.Id = await repository.InsertAppointmentAsync(appointment, ct);

        var booking = new Booking
        {
            BusinessId = businessId,
            AppointmentId = appointment.Id,
            ClientId = client.Id,
            Modality = null,
            Status = BookingStatus.Pending,
            AgreedPrice = service.Price,
            CreatedAt = now
        };
        booking.Id = await repository.InsertBookingAsync(booking, ct);

        var token = SecureToken.Create();
        var link = new AppointmentLink
        {
            BookingId = booking.Id,
            TokenHash = token.Hash,
            ProtectedToken = linkTokenProtector.Protect(token.Token),
            ExpiresAt = startsAt.AddHours(24),
            CreatedAt = now
        };
        await repository.InsertAppointmentLinkAsync(link, ct);

        var isReminderWindow = startsAt - now <= TimeSpan.FromHours(business.ReminderLeadHours);
        var notificationType = isReminderWindow ? NotificationType.AppointmentReminder : NotificationType.BookingCreated;
        var notification = new Notification
        {
            BusinessId = businessId,
            Type = notificationType,
            Recipient = resolution.Recipient,
            Subject = isReminderWindow ? "Recordatorio de tu turno" : "Tu turno quedó reservado",
            BodyHtml = BuildEmailBody(
                business.Name,
                client.FirstName,
                service.Name,
                worker.FullName,
                startsAt,
                appointmentUrlBuilder.Build(token.Token),
                isReminderWindow),
            DedupKey = $"booking:{booking.Id}",
            CreatedAt = now
        };
        await repository.InsertNotificationAsync(notification, ct);

        await transaction.CommitAsync(ct);
        return new PublicBookingAttempt(Result<PublicBookingConfirmation>.Success(new PublicBookingConfirmation(
            appointment.Id,
            booking.Id,
            appointment.StartsAt,
            appointment.EndsAt,
            service.Name,
            worker.FullName,
            booking.AgreedPrice.Value)));
    }

    private static BusinessError? Validate(
        int businessId,
        BookIndividualAppointmentRequest request,
        DateTime now,
        out string normalizedEmail,
        out string firstName,
        out string lastName,
        out DateTime startsAt)
    {
        normalizedEmail = "";
        firstName = request.FirstName?.Trim() ?? "";
        lastName = request.LastName?.Trim() ?? "";
        startsAt = request.Date.ToDateTime(request.StartTime, DateTimeKind.Unspecified);

        if (businessId <= 0)
            return new BusinessError("business_unavailable", "No pudimos completar la reserva.");
        if (request.ServiceId <= 0)
            return new BusinessError("service_unavailable", "Ese servicio no está disponible para reservar.", nameof(request.ServiceId));
        if (request.WorkerId <= 0)
            return new BusinessError("worker_unavailable", "No encontramos a esa trabajadora disponible.", nameof(request.WorkerId));
        if (request.Date.DayNumber >= DateOnly.MaxValue.DayNumber - AlternativeSearchDays)
            return new BusinessError("booking_date_out_of_range", "Elegí otra fecha para buscar horarios disponibles.", nameof(request.Date));
        if (firstName.Length == 0)
            return new BusinessError("client_first_name_required", "Ingresá tu nombre.", nameof(request.FirstName));
        if (firstName.Length > MaxNameLength)
            return new BusinessError("client_first_name_too_long", "El nombre no puede superar los 60 caracteres.", nameof(request.FirstName));
        if (lastName.Length == 0)
            return new BusinessError("client_last_name_required", "Ingresá tu apellido.", nameof(request.LastName));
        if (lastName.Length > MaxNameLength)
            return new BusinessError("client_last_name_too_long", "El apellido no puede superar los 60 caracteres.", nameof(request.LastName));

        normalizedEmail = Text.NormalizeEmail(request.Email ?? "");
        if (normalizedEmail.Length == 0)
            return new BusinessError("client_email_required", "Ingresá tu email.", nameof(request.Email));
        if (normalizedEmail.Length > MaxEmailLength || !EmailFormat.IsValid(normalizedEmail))
            return new BusinessError("invalid_client_email", "Ingresá un email válido.", nameof(request.Email));
        if (request.StartTime.Ticks % TimeSpan.TicksPerMinute != 0 || request.StartTime.Minute % 15 != 0)
            return new BusinessError("invalid_booking_grid", "Elegí un horario en la grilla de 15 minutos.", nameof(request.StartTime));
        if (startsAt <= now)
            return new BusinessError("booking_start_in_past", "Elegí un horario futuro.", nameof(request.StartTime));

        return null;
    }

    private static BusinessError? ValidateClientRequest(
        int businessId,
        int clientId,
        int serviceId,
        int workerId,
        DateOnly date,
        TimeOnly startTime,
        DateTime now,
        out DateTime startsAt)
    {
        startsAt = date.ToDateTime(startTime, DateTimeKind.Unspecified);

        // A missing, foreign or invalid ficha is reported the same generic way and never reaches persistence.
        if (businessId <= 0 || clientId <= 0)
            return new BusinessError(ClientNotFoundCode, ClientNotFoundMessage);
        if (serviceId <= 0)
            return new BusinessError("service_unavailable", "Ese servicio no está disponible para reservar.", nameof(BookIndividualAppointmentRequest.ServiceId));
        if (workerId <= 0)
            return new BusinessError("worker_unavailable", "No encontramos a esa trabajadora disponible.", nameof(BookIndividualAppointmentRequest.WorkerId));
        if (date.DayNumber >= DateOnly.MaxValue.DayNumber - AlternativeSearchDays)
            return new BusinessError("booking_date_out_of_range", "Elegí otra fecha para buscar horarios disponibles.", nameof(BookIndividualAppointmentRequest.Date));
        if (startTime.Ticks % TimeSpan.TicksPerMinute != 0 || startTime.Minute % 15 != 0)
            return new BusinessError("invalid_booking_grid", "Elegí un horario en la grilla de 15 minutos.", StartTimeField);
        if (startsAt <= now)
            return new BusinessError("booking_start_in_past", "Elegí un horario futuro.", StartTimeField);

        return null;
    }

    /// <summary>The ficha resolved inside the transaction plus the address its notification must go to.</summary>
    private sealed record ClientResolution(Client? Client, string Recipient, BusinessError? Error)
    {
        public static ClientResolution Ok(Client client, string recipient) => new(client, recipient, null);
        public static ClientResolution Fail(BusinessError error) => new(null, "", error);
    }

    private static string BuildEmailBody(
        string businessName,
        string clientFirstName,
        string serviceName,
        string workerName,
        DateTime startsAt,
        string appointmentUrl,
        bool isReminder)
    {
        var safeBusinessName = WebUtility.HtmlEncode(businessName);
        var safeFirstName = WebUtility.HtmlEncode(clientFirstName);
        var safeServiceName = WebUtility.HtmlEncode(serviceName);
        var safeWorkerName = WebUtility.HtmlEncode(workerName);
        var safeUrl = WebUtility.HtmlEncode(appointmentUrl);
        var when = WebUtility.HtmlEncode(startsAt.ToString("dddd d 'de' MMMM 'a las' HH:mm", CultureInfo.GetCultureInfo("es-AR")));
        var lead = isReminder
            ? "Este es el recordatorio de tu turno. Confirmá tu intención de asistir desde el enlace privado."
            : "Tu reserva quedó pendiente. Desde el enlace privado podés consultar y gestionar este turno.";

        return $"<p>Hola {safeFirstName},</p><p>{lead}</p><p>{safeServiceName} con {safeWorkerName}, {when}.</p>" +
               $"<p><a href=\"{safeUrl}\">Ver mi turno</a></p><p>{safeBusinessName}</p>";
    }

    private static PublicBookingAttempt Failed(string code, string message, string? field = null, DateTime? alternativeStart = null) =>
        new(Result<PublicBookingConfirmation>.Failure(code, message, field), alternativeStart);

    private static PublicBookingAttempt Failed(BusinessError error) =>
        new(Result<PublicBookingConfirmation>.Failure(error));
}
