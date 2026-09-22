using WorkUp.BusinessLogic.Clients;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Clients;

public class ClientsServiceTests
{
    private readonly FakeClientRepository _clients = new();
    private readonly FakeClientNoteRepository _notes = new();
    private readonly FakeClock _clock = new() { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
    private readonly ClientsService _service;

    public ClientsServiceTests()
    {
        _service = new ClientsService(_clients, _notes, _clock);
    }

    private static CreateClientRequest ValidRequest() =>
        new("Marina", "Gómez", "marina@peluqueria.com", "1122334455", null);

    [Fact]
    public async Task Creates_a_client_with_trimmed_contact_data_and_a_normalized_email()
    {
        var result = await _service.CreateAsync(1, new CreateClientRequest(
            " Marina ", " Gómez ", " MARINA@PELUQUERIA.COM ", " 11 2233-4455 ", " Alergia a la tintura "));

        Assert.True(result.Succeeded);
        Assert.Equal("Marina", result.Value.FirstName);
        Assert.Equal("Gómez", result.Value.LastName);
        Assert.Equal("marina@peluqueria.com", result.Value.Email);
        Assert.Equal("11 2233-4455", result.Value.Phone);
        Assert.Equal("Alergia a la tintura", result.Value.HealthNotes);
        Assert.Equal(_clock.Now, result.Value.CreatedAt);
    }

    [Theory]
    [InlineData("FirstName", "client_first_name_required")]
    [InlineData("LastName", "client_last_name_required")]
    [InlineData("Phone", "client_phone_required")]
    [InlineData("Email", "client_email_required")]
    public async Task Requires_every_contact_field(string field, string code)
    {
        var request = ValidRequest() with { FirstName = field == "FirstName" ? " " : "Marina", LastName = field == "LastName" ? " " : "Gómez", Phone = field == "Phone" ? " " : "1122334455", Email = field == "Email" ? " " : "marina@peluqueria.com" };

        var result = await _service.CreateAsync(1, request);

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
        Assert.Empty(_clients.All);
    }

    [Fact]
    public async Task Rejects_invalid_email_phone_and_excessively_long_health_observations()
    {
        var invalidEmail = await _service.CreateAsync(1, ValidRequest() with { Email = "no-es-email" });
        var invalidPhone = await _service.CreateAsync(1, ValidRequest() with { Phone = "abc" });
        var tooLongNotes = await _service.CreateAsync(1, ValidRequest() with { HealthNotes = new string('a', ClientsService.MaxHealthNotesLength + 1) });

        Assert.Equal("invalid_client_email", invalidEmail.Error!.Code);
        Assert.Equal("invalid_client_phone", invalidPhone.Error!.Code);
        Assert.Equal("health_notes_too_long", tooLongNotes.Error!.Code);
    }

    [Fact]
    public async Task Keeps_normalized_email_unique_per_business_but_allows_it_in_another_business()
    {
        await _service.CreateAsync(1, ValidRequest());

        var duplicate = await _service.CreateAsync(1, ValidRequest() with { FirstName = "Otra", Email = " MARINA@PELUQUERIA.COM " });
        var otherBusiness = await _service.CreateAsync(2, ValidRequest());

        Assert.Equal("client_email_taken", duplicate.Error!.Code);
        Assert.True(otherBusiness.Succeeded);
    }

    [Fact]
    public async Task Updates_only_a_client_from_its_own_business_and_keeps_its_own_email_available()
    {
        var created = await _service.CreateAsync(1, ValidRequest());

        var update = await _service.UpdateAsync(1, created.Value.Id,
            new UpdateClientRequest("Marina", "Pérez", "MARINA@PELUQUERIA.COM", "1199887766", "Actualizada"));
        var crossBusiness = await _service.UpdateAsync(2, created.Value.Id,
            new UpdateClientRequest("Otra", "Persona", "otra@correo.com", "1122334455", null));

        Assert.True(update.Succeeded);
        Assert.Equal("Pérez", created.Value.LastName);
        Assert.Equal("marina@peluqueria.com", created.Value.Email);
        Assert.Equal("Actualizada", created.Value.HealthNotes);
        Assert.Equal("client_not_found", crossBusiness.Error!.Code);
    }

    [Fact]
    public async Task Public_find_or_create_returns_a_sanitized_identity_without_health_data()
    {
        var existing = await _service.CreateAsync(1, ValidRequest() with { HealthNotes = "Privada" });

        var found = await _service.FindOrCreateByEmailAsync(1, " MARINA@PELUQUERIA.COM ", "Otro", "Nombre", "1199998888");
        var created = await _service.FindOrCreateByEmailAsync(1, "lucia@yoga.com", "Lucía", "Paz");

        Assert.IsType<ClientReference>(found.Value);
        Assert.Equal(existing.Value.Id, found.Value.Id);
        Assert.True(created.Succeeded);
        Assert.DoesNotContain(typeof(ClientReference).GetProperties(), property => property.Name == "HealthNotes");
    }

    [Fact]
    public async Task Searches_only_the_current_business_by_contact_data()
    {
        await _service.CreateAsync(1, ValidRequest());
        await _service.CreateAsync(1, ValidRequest() with { FirstName = "Lucía", LastName = "Paz", Email = "lucia@yoga.com" });
        await _service.CreateAsync(2, ValidRequest() with { FirstName = "Otra", Email = "marina@peluqueria.com" });

        var results = await _service.SearchAsync(1, "marina@");

        var client = Assert.Single(results);
        Assert.Equal("Marina", client.FirstName);
    }

    [Fact]
    public async Task Adds_owner_only_notes_to_an_existing_client_and_rejects_cross_business_access()
    {
        var created = await _service.CreateAsync(1, ValidRequest());

        var added = await _service.AddNoteAsync(1, created.Value.Id, " Usó tono 7.1 ");
        var crossBusiness = await _service.AddNoteAsync(2, created.Value.Id, "No corresponde");

        Assert.True(added.Succeeded);
        Assert.Equal("Usó tono 7.1", added.Value.Text);
        Assert.Equal(created.Value.Id, added.Value.ClientId);
        Assert.Equal(_clock.Now, added.Value.CreatedAt);
        Assert.Equal("client_not_found", crossBusiness.Error!.Code);
    }

    [Fact]
    public async Task Detail_splits_booking_history_using_the_clock_and_keeps_health_data_in_the_owner_service()
    {
        var created = await _service.CreateAsync(1, ValidRequest() with { HealthNotes = "Alergia" });
        await _service.AddNoteAsync(1, created.Value.Id, "Nota previa");
        _clients.SetHasAccount(1, created.Value.Id);
        _clients.AddBooking(new ClientBookingHistoryItem(1, _clock.Now.AddDays(-1), _clock.Now.AddDays(-1).AddHours(1), "Corte", "Ana", BookingStatus.Attended));
        _clients.AddBooking(new ClientBookingHistoryItem(2, _clock.Now.AddDays(1), _clock.Now.AddDays(1).AddHours(1), "Color", "Ana", BookingStatus.Pending));
        _clients.AddPayment(new ClientPaymentHistoryItem(1, _clock.Now.AddDays(-1), PaymentConcept.IndividualAppointment, 12000m, false));

        var detail = await _service.GetDetailAsync(1, created.Value.Id);
        var crossBusiness = await _service.GetDetailAsync(2, created.Value.Id);

        Assert.NotNull(detail);
        Assert.Equal("Alergia", detail!.Client.HealthNotes);
        Assert.True(detail.HasAccount);
        Assert.Single(detail.Notes);
        Assert.Single(detail.UpcomingBookings);
        Assert.Single(detail.PastBookings);
        Assert.Single(detail.Payments);
        Assert.Null(crossBusiness);
    }
}
