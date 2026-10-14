using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Clients;

public sealed record CreateClientRequest(string FirstName, string LastName, string Email, string Phone, string? HealthNotes);

public sealed record UpdateClientRequest(string FirstName, string LastName, string Email, string Phone, string? HealthNotes);

/// <summary>Minimal client identity for public booking flows. It intentionally contains no contact or health information.</summary>
public sealed record ClientReference(int Id);

/// <summary>Ficha de cliente plus its history, for the detail screen (RF-11).</summary>
public sealed record ClientDetail(
    Client Client,
    bool HasAccount,
    List<ClientNote> Notes,
    List<ClientBookingHistoryItem> UpcomingBookings,
    List<ClientBookingHistoryItem> PastBookings,
    List<ClientPaymentHistoryItem> Payments);

/// <summary>
/// Fichas de clientes (RF 1, RF-11). A client is identified by its email inside a business (Regla 7); it never has
/// a password here (RF-27): an account is opt-in, added later by self-registration (T20) or invitation (T22).
/// Health observations (<see cref="Client.HealthNotes"/>) and the note history are owner-only and never reach a
/// public page, email or report.
/// </summary>
public sealed class ClientsService(IClientRepository clients, IClientNoteRepository notes, IClock clock)
{
    public const int MaxHealthNotesLength = 1000;
    public const int MaxNoteTextLength = 1000;

    public Task<List<Client>> SearchAsync(int businessId, string? query, CancellationToken ct = default) =>
        clients.SearchAsync(businessId, query, ct);

    public Task<Client?> GetAsync(int businessId, int id, CancellationToken ct = default) =>
        clients.GetByIdAsync(businessId, id, ct);

    public async Task<Result<Client>> CreateAsync(int businessId, CreateClientRequest request, CancellationToken ct = default)
    {
        var validation = Validate(request.FirstName, request.LastName, request.Email, request.Phone, request.HealthNotes, out var firstName, out var lastName, out var email, out var phone, out var healthNotes);
        if (validation is not null)
            return Result<Client>.Failure(validation);

        if (await clients.EmailExistsAsync(businessId, email, ct: ct))
            return Result<Client>.Failure("client_email_taken", "Ya existe una ficha con ese email.", nameof(request.Email));

        var client = new Client
        {
            BusinessId = businessId,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            Phone = phone,
            HealthNotes = healthNotes,
            CreatedAt = clock.Now
        };
        // The precheck above is only a courtesy check: between it and the insert a competing create can commit the
        // same email. Losing the UQ_Clients_Email race is the same business outcome, never a second ficha.
        var inserted = await clients.InsertAsync(client, ct);
        if (!inserted.IsInserted)
            return Result<Client>.Failure("client_email_taken", "Ya existe una ficha con ese email.", nameof(request.Email));

        client.Id = inserted.ClientId;
        return Result<Client>.Success(client);
    }

    public async Task<Result> UpdateAsync(int businessId, int id, UpdateClientRequest request, CancellationToken ct = default)
    {
        var client = await clients.GetByIdAsync(businessId, id, ct);
        if (client is null)
            return Result.Failure("client_not_found", "No encontramos esa ficha.");

        var validation = Validate(request.FirstName, request.LastName, request.Email, request.Phone, request.HealthNotes, out var firstName, out var lastName, out var email, out var phone, out var healthNotes);
        if (validation is not null)
            return Result.Failure(validation);

        if (await clients.EmailExistsAsync(businessId, email, id, ct))
            return Result.Failure("client_email_taken", "Ya existe una ficha con ese email.", nameof(request.Email));

        client.FirstName = firstName;
        client.LastName = lastName;
        client.Email = email;
        client.Phone = phone;
        client.HealthNotes = healthNotes;
        // Same lost window as the create: the edit must report the existing field error and leave the stored row as it
        // was, never silently repoint the request at another ficha.
        if (!(await clients.UpdateAsync(client, ct)).IsUpdated)
            return Result.Failure("client_email_taken", "Ya existe una ficha con ese email.", nameof(request.Email));

        return Result.Success();
    }

    /// <summary>
    /// Identifies the client by email (Regla 7) for a public booking or class sign-up that never asks for a
    /// password: returns only a sanitized identity when the email is already registered, or creates a ficha without
    /// a password otherwise. Phone is optional here because public booking does not collect it.
    /// </summary>
    public async Task<Result<ClientReference>> FindOrCreateByEmailAsync(int businessId, string email, string firstName, string lastName, string? phone = null, CancellationToken ct = default)
    {
        var normalizedEmail = Text.NormalizeEmail(email);
        if (!EmailFormat.IsValid(normalizedEmail))
            return Result<ClientReference>.Failure("invalid_client_email", "Ingresá un email válido.", nameof(email));

        var existing = await clients.GetByEmailAsync(businessId, normalizedEmail, ct);
        if (existing is not null)
            return Result<ClientReference>.Success(new ClientReference(existing.Id));

        var normalizedPhone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (normalizedPhone is not null && !PhoneFormat.IsValid(normalizedPhone))
            return Result<ClientReference>.Failure("invalid_client_phone", "Ingresá un teléfono válido.", nameof(phone));

        var normalizedFirstName = firstName.Trim();
        if (normalizedFirstName.Length == 0)
            return Result<ClientReference>.Failure("client_first_name_required", "Ingresá el nombre.", nameof(firstName));

        var normalizedLastName = lastName.Trim();
        if (normalizedLastName.Length == 0)
            return Result<ClientReference>.Failure("client_last_name_required", "Ingresá el apellido.", nameof(lastName));

        var client = new Client
        {
            BusinessId = businessId,
            FirstName = normalizedFirstName,
            LastName = normalizedLastName,
            Email = normalizedEmail,
            Phone = normalizedPhone,
            CreatedAt = clock.Now
        };
        // This public path looks the email up and inserts without a precheck, so it can lose the race too. Returning
        // the competing ficha or creating a duplicate would bind the request to the wrong account: it fails instead.
        var inserted = await clients.InsertAsync(client, ct);
        if (!inserted.IsInserted)
            return Result<ClientReference>.Failure("client_email_taken", "Ya existe una ficha con ese email.", nameof(email));

        client.Id = inserted.ClientId;
        return Result<ClientReference>.Success(new ClientReference(client.Id));
    }

    /// <summary>Observación para la próxima atención (RF-11), owner-only. <paramref name="appointmentId"/> links it to the attention that produced it (T12); a manually added note leaves it null.</summary>
    public async Task<Result<ClientNote>> AddNoteAsync(int businessId, int clientId, string text, int? appointmentId = null, CancellationToken ct = default)
    {
        if (await clients.GetByIdAsync(businessId, clientId, ct) is null)
            return Result<ClientNote>.Failure("client_not_found", "No encontramos esa ficha.");

        var normalizedText = text.Trim();
        if (normalizedText.Length == 0)
            return Result<ClientNote>.Failure("client_note_required", "Ingresá una observación.", nameof(text));
        if (normalizedText.Length > MaxNoteTextLength)
            return Result<ClientNote>.Failure("client_note_too_long", "La observación no puede superar los 1000 caracteres.", nameof(text));

        var note = new ClientNote { ClientId = clientId, AppointmentId = appointmentId, Text = normalizedText, CreatedAt = clock.Now };
        note.Id = await notes.InsertAsync(note, ct);
        return Result<ClientNote>.Success(note);
    }

    /// <summary>
    /// Ficha detail (RF-11): contact data, whether it has a linked account, its note history and its booking and
    /// payment history split into upcoming and past using the current time. Null when the client does not belong to
    /// the business.
    /// </summary>
    public async Task<ClientDetail?> GetDetailAsync(int businessId, int id, CancellationToken ct = default)
    {
        var client = await clients.GetByIdAsync(businessId, id, ct);
        if (client is null)
            return null;

        var bookingHistory = await clients.ListBookingHistoryAsync(businessId, id, ct);
        var upcoming = bookingHistory.Where(item => item.StartsAt >= clock.Now).OrderBy(item => item.StartsAt).ToList();
        var past = bookingHistory.Where(item => item.StartsAt < clock.Now).OrderByDescending(item => item.StartsAt).ToList();
        return new ClientDetail(
            client,
            await clients.HasAccountAsync(businessId, id, ct),
            await notes.ListByClientAsync(id, ct),
            upcoming,
            past,
            await clients.ListPaymentHistoryAsync(businessId, id, ct));
    }

    private static BusinessError? Validate(string firstNameInput, string lastNameInput, string emailInput, string phoneInput, string? healthNotesInput,
        out string firstName, out string lastName, out string email, out string phone, out string? healthNotes)
    {
        firstName = firstNameInput.Trim();
        lastName = lastNameInput.Trim();
        email = Text.NormalizeEmail(emailInput);
        phone = phoneInput.Trim();
        healthNotes = string.IsNullOrWhiteSpace(healthNotesInput) ? null : healthNotesInput.Trim();

        if (firstName.Length == 0)
            return new BusinessError("client_first_name_required", "Ingresá el nombre.", nameof(CreateClientRequest.FirstName));
        if (lastName.Length == 0)
            return new BusinessError("client_last_name_required", "Ingresá el apellido.", nameof(CreateClientRequest.LastName));
        if (email.Length == 0)
            return new BusinessError("client_email_required", "Ingresá el email.", nameof(CreateClientRequest.Email));
        if (!EmailFormat.IsValid(email))
            return new BusinessError("invalid_client_email", "Ingresá un email válido.", nameof(CreateClientRequest.Email));
        if (phone.Length == 0)
            return new BusinessError("client_phone_required", "Ingresá el teléfono.", nameof(CreateClientRequest.Phone));
        if (!PhoneFormat.IsValid(phone))
            return new BusinessError("invalid_client_phone", "Ingresá un teléfono válido.", nameof(CreateClientRequest.Phone));
        if (healthNotes?.Length > MaxHealthNotesLength)
            return new BusinessError("health_notes_too_long", "Las observaciones de salud no pueden superar los 1000 caracteres.", nameof(CreateClientRequest.HealthNotes));

        return null;
    }
}
