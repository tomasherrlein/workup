using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Enrollments;

namespace WorkUp.Tests.Enrollments;

// T15 owner side (RF-05, RF-20, RF-29): the membership list with its filters and the create, charge, annul and
// terminate actions of the Abonos panel. The controller only routes: every rule, every date and every permanent seat
// belongs to EnrollmentService and is already tested at the service level (EnrollmentServiceTests). What is pinned
// here is what the panel adds: the derived states rendered for each filter, the global-classes picker with its free
// seats, the tariff defaults, the POST/redirect/GET of every action, the mapping of the stable error codes to Spanish
// and that a rejected action never writes.
public sealed class EnrollmentControllerTests
{
    private const int BusinessId = 7;

    private readonly FakeEnrollmentRepository _repository = new();
    private readonly FakeMembershipPriceRepository _prices = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();
    private readonly FakeGridClassRepository _gridClasses = new();
    private readonly FakeServiceRepository _services = new();
    private readonly FakeWorkerRepository _workers = new();
    private readonly FakeClientRepository _clients = new();

    public EnrollmentControllerTests()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // --- Fixtures -----------------------------------------------------------------------------------------------

    private void AddListedClient(int clientId, string fullName, int businessId = BusinessId) =>
        _repository.Clients.Add(new FakeEnrollmentClient(businessId, new EnrollmentClient(clientId, fullName)));

    private async Task<int> SeedClient(string firstName, string lastName, int businessId = BusinessId)
    {
        var clientId = (await _clients.InsertAsync(new Client
        {
            BusinessId = businessId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}@example.com",
            Phone = "1145667788",
            CreatedAt = _clock.Now
        })).ClientId;
        // The service validates the client through its own port: the same id must be reachable from both fakes.
        _repository.Clients.Add(new FakeEnrollmentClient(
            businessId, new EnrollmentClient(clientId, $"{firstName} {lastName}")));
        return clientId;
    }

    private Task<int> SeedTariff(int weeklyFrequency, decimal price, int businessId = BusinessId) =>
        _prices.InsertAsync(new MembershipPrice
        {
            BusinessId = businessId,
            WeeklyFrequency = weeklyFrequency,
            Price = price
        });

    private async Task<EnrollmentGridClassRow> SeedGridClass(
        string serviceName,
        int maxCapacity = 8,
        bool gridActive = true,
        bool serviceActive = true,
        DayOfWeek dayOfWeek = DayOfWeek.Monday,
        int hour = 10,
        int businessId = BusinessId)
    {
        var serviceId = await _services.InsertAsync(new Service
        {
            BusinessId = businessId,
            Name = serviceName,
            Price = 3000m,
            DurationMinutes = 60,
            MaxCapacity = maxCapacity,
            IsActive = serviceActive
        });
        var workerId = await _workers.InsertAsync(new Worker
        {
            BusinessId = businessId,
            FirstName = "Ana",
            LastName = "Díaz",
            IsActive = true
        });
        var startTime = new TimeOnly(hour, 0);
        var gridClassId = await _gridClasses.InsertAsync(new GridClass
        {
            BusinessId = businessId,
            ServiceId = serviceId,
            WorkerId = workerId,
            DayOfWeek = dayOfWeek,
            StartTime = startTime,
            IsActive = gridActive
        });

        var row = new EnrollmentGridClassRow(
            gridClassId, serviceId, workerId, dayOfWeek, startTime, maxCapacity, gridActive);
        _repository.GridClasses.Add(new FakeEnrollmentGridClass(businessId, row));
        return row;
    }

    private Enrollment StoreEnrollment(
        int clientId = 100,
        EnrollmentStatus status = EnrollmentStatus.PendingPayment,
        int weeklyFrequency = 2,
        decimal agreedPrice = 8000m,
        int? referenceDay = null,
        DateTime? activatedAt = null,
        string? priceReason = null,
        int businessId = BusinessId)
    {
        var enrollment = new Enrollment
        {
            Id = _repository.NextEnrollmentId++,
            BusinessId = businessId,
            ClientId = clientId,
            WeeklyFrequency = weeklyFrequency,
            Status = status,
            AgreedPrice = agreedPrice,
            PriceReason = priceReason,
            ReferenceDay = referenceDay,
            CreatedAt = _clock.Now,
            ActivatedAt = activatedAt
        };
        _repository.Enrollments.Add(enrollment);
        return enrollment;
    }

    private void StoreFixedClass(Enrollment enrollment, int gridClassId, DateOnly validFrom, DateOnly? validTo = null) =>
        _repository.FixedClasses.Add(new EnrollmentFixedClass
        {
            EnrollmentId = enrollment.Id,
            GridClassId = gridClassId,
            ValidFrom = validFrom,
            ValidTo = validTo
        });

    private void StorePeriod(Enrollment enrollment, DateOnly coverageStart, DateOnly expiresOn) =>
        _repository.Periods.Add(new MembershipPeriod
        {
            EnrollmentId = enrollment.Id,
            CoverageStart = coverageStart,
            ExpiresOn = expiresOn,
            PaymentId = 0,
            CreatedAt = _clock.Now
        });

    private Enrollment StoreActiveMembership(
        DateOnly coverageStart, DateOnly expiresOn, int referenceDay, int clientId, int weeklyFrequency = 2)
    {
        var enrollment = StoreEnrollment(
            clientId: clientId,
            status: EnrollmentStatus.Active,
            weeklyFrequency: weeklyFrequency,
            referenceDay: referenceDay,
            activatedAt: _clock.Now);
        StorePeriod(enrollment, coverageStart, expiresOn);
        return enrollment;
    }

    // --- List (RF-29) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Index_shows_every_derived_state_and_the_ended_memberships_in_the_all_view()
    {
        AddListedClient(100, "Ana Vigente");
        AddListedClient(200, "Bruno Pronto");
        AddListedClient(300, "Carla Vencida");
        AddListedClient(400, "Diego Pendiente");
        AddListedClient(500, "Elena Anulada");
        AddListedClient(600, "Fabio Baja");

        StoreActiveMembership(D(2026, 3, 1), D(2026, 4, 1), referenceDay: 1, clientId: 100);
        StoreActiveMembership(D(2026, 2, 13), D(2026, 3, 13), referenceDay: 13, clientId: 200);
        StoreActiveMembership(D(2026, 2, 5), D(2026, 3, 5), referenceDay: 5, clientId: 300);
        StoreEnrollment(clientId: 400);
        StoreEnrollment(clientId: 500, status: EnrollmentStatus.Annulled);
        StoreEnrollment(clientId: 600, status: EnrollmentStatus.Terminated);

        var controller = Create();
        var model = Assert.IsType<EnrollmentIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.Index(null, CancellationToken.None)).Model);

        Assert.Equal(EnrollmentListFilter.All, model.Filter);
        Assert.Equal(
            new[] { "Ana Vigente", "Bruno Pronto", "Carla Vencida", "Diego Pendiente", "Elena Anulada", "Fabio Baja" },
            model.Items.Select(item => item.ClientName).ToArray());

        // Every row carries its state as a word (The Word-First Rule), never as colour alone.
        Assert.Equal("Vigente", model.Items[0].StateTag.Label);
        Assert.Equal("Vigente", model.Items[1].StateTag.Label);
        Assert.Equal("Pendiente de renovación", model.Items[2].StateTag.Label);
        Assert.Equal("Pendiente de pago", model.Items[3].StateTag.Label);
        Assert.Equal("Anulado", model.Items[4].StateTag.Label);
        Assert.Equal("Dada de baja", model.Items[5].StateTag.Label);

        // The expiry the owner has to resolve is shown when there is one.
        Assert.Equal(D(2026, 4, 1), model.Items[0].CurrentPeriodEnd);
        Assert.Equal(D(2026, 3, 13), model.Items[1].CurrentPeriodEnd);
        Assert.True(model.Items[1].ExpiresSoon);
        Assert.Equal(D(2026, 3, 5), model.Items[2].CurrentPeriodEnd);
        Assert.True(model.Items[2].IsLapsed);
        Assert.Null(model.Items[3].CurrentPeriodEnd);
        Assert.True(model.Items[3].PendingPayment);

        // Annulled and terminated memberships hold no place: the screen offers them no action.
        Assert.False(model.Items[4].CanCharge);
        Assert.False(model.Items[4].CanAnnul);
        Assert.False(model.Items[4].CanTerminate);
        Assert.False(model.Items[5].CanCharge);
        Assert.False(model.Items[5].CanAnnul);
        Assert.False(model.Items[5].CanTerminate);

        // Annul is a courtesy gate for the pending one; terminate is offered while a place is still held.
        Assert.True(model.Items[3].CanAnnul);
        Assert.True(model.Items[3].CanTerminate);
        Assert.False(model.Items[0].CanAnnul);
        Assert.True(model.Items[0].CanTerminate);

        Assert.Equal(D(2026, 3, 10), model.Today);
    }

    [Fact]
    public async Task Index_filter_hides_the_ended_memberships_outside_the_all_view()
    {
        AddListedClient(100, "Ana Vigente");
        AddListedClient(300, "Carla Vencida");
        AddListedClient(400, "Diego Pendiente");
        AddListedClient(500, "Elena Anulada");
        AddListedClient(600, "Fabio Baja");
        StoreActiveMembership(D(2026, 3, 1), D(2026, 4, 1), referenceDay: 1, clientId: 100);
        StoreActiveMembership(D(2026, 2, 5), D(2026, 3, 5), referenceDay: 5, clientId: 300);
        StoreEnrollment(clientId: 400);
        StoreEnrollment(clientId: 500, status: EnrollmentStatus.Annulled);
        StoreEnrollment(clientId: 600, status: EnrollmentStatus.Terminated);

        var controller = Create();

        var current = await Listed(controller, "Current");
        Assert.Single(current.Items);
        Assert.Equal("Ana Vigente", current.Items[0].ClientName);

        var expired = await Listed(controller, "Expired");
        Assert.Single(expired.Items);
        Assert.Equal("Carla Vencida", expired.Items[0].ClientName);

        var pending = await Listed(controller, "PendingPayment");
        Assert.Single(pending.Items);
        Assert.Equal("Diego Pendiente", pending.Items[0].ClientName);

        // No view but "all" ever shows the annulled or terminated rows.
        Assert.DoesNotContain(current.Items.Concat(expired.Items).Concat(pending.Items),
            item => item.ClientName is "Elena Anulada" or "Fabio Baja");
    }

    [Fact]
    public async Task Index_unknown_filter_falls_back_to_the_all_view()
    {
        AddListedClient(100, "Ana Vigente");
        StoreActiveMembership(D(2026, 3, 1), D(2026, 4, 1), referenceDay: 1, clientId: 100);

        var controller = Create();

        var unknown = await Listed(controller, "no-existe");
        Assert.Equal(EnrollmentListFilter.All, unknown.Filter);
        Assert.Single(unknown.Items);
        Assert.Equal(EnrollmentListFilter.All, unknown.Filters.Single(option => option.IsSelected).Filter);

        // A numeric value that is not a defined member of the enum is unknown too: it must not reach the service.
        var undefined = await Listed(controller, "99");
        Assert.Equal(EnrollmentListFilter.All, undefined.Filter);
        Assert.Single(undefined.Items);

        var empty = await Listed(controller, "");
        Assert.Equal(EnrollmentListFilter.All, empty.Filter);
    }

    [Fact]
    public async Task Index_offers_every_filter_with_its_spanish_label()
    {
        var controller = Create();
        var model = await Listed(controller, "ExpiringSoon");

        Assert.Equal(
            new[] { "Todos", "Vigentes", "Vencen pronto", "Vencidos", "Pendientes de pago" },
            model.Filters.Select(option => option.Label).ToArray());
        Assert.Equal(EnrollmentListFilter.ExpiringSoon, model.Filter);
        Assert.Equal(EnrollmentListFilter.ExpiringSoon, model.Filters.Single(option => option.IsSelected).Filter);
    }

    [Fact]
    public async Task Index_is_a_read_and_opens_no_transaction()
    {
        AddListedClient(100, "Ana Vigente");
        StoreActiveMembership(D(2026, 3, 1), D(2026, 4, 1), referenceDay: 1, clientId: 100);

        var controller = Create();
        await controller.Index(null, CancellationToken.None);

        Assert.Equal(1, _repository.ListEnrollmentsCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    // --- Create form (RF-05 steps 1-3) --------------------------------------------------------------------------

    [Fact]
    public async Task Create_form_offers_only_the_active_class_type_grid_classes_with_a_free_seat()
    {
        var free = await SeedGridClass("Hatha", maxCapacity: 3);
        await SeedGridClass("Corte", maxCapacity: 1); // individual service: never a fixed class
        await SeedGridClass("Yoga archivada", gridActive: false);
        await SeedGridClass("Pilates", serviceActive: false);
        var full = await SeedGridClass("Vinyasa", maxCapacity: 2);
        // Two active fixed classes fill the grid class: no permanent seat is left.
        var first = StoreEnrollment(clientId: 901, status: EnrollmentStatus.Active);
        StoreFixedClass(first, full.GridClassId, D(2026, 1, 1));
        var second = StoreEnrollment(clientId: 902, status: EnrollmentStatus.Active);
        StoreFixedClass(second, full.GridClassId, D(2026, 1, 1));

        await SeedTariff(2, 8000m);
        await SeedClient("Ana", "Pérez");

        var model = await GetCreateForm();

        var option = Assert.Single(model.GridClasses);
        Assert.Equal(free.GridClassId, option.GridClassId);
        Assert.Equal(3, option.FreeSeats);
        Assert.Contains("Hatha", option.Label);
        Assert.Contains("Ana Díaz", option.Label);
        Assert.DoesNotContain(model.GridClasses, candidate => candidate.GridClassId == full.GridClassId);
    }

    [Fact]
    public async Task Create_form_defaults_the_price_to_the_configured_tariff_of_the_lowest_frequency()
    {
        await SeedTariff(3, 10000m);
        await SeedTariff(2, 8000m);
        await SeedGridClass("Hatha");
        await SeedClient("Ana", "Pérez");

        var model = await GetCreateForm();

        Assert.Equal(2, model.WeeklyFrequency);
        Assert.Equal(8000m, model.AgreedPrice);
        Assert.Equal(
            new[] { 2, 3 },
            model.FrequencyOptions.Select(option => option.Frequency).ToArray());
        Assert.Equal(8000m, model.FrequencyOptions[0].Price);
        Assert.True(model.CanEnroll);
    }

    [Fact]
    public async Task Create_form_lists_only_the_clients_of_the_business()
    {
        await SeedTariff(2, 8000m);
        await SeedGridClass("Hatha");
        await SeedClient("Ana", "Pérez");
        await SeedClient("Ajena", "De Otro Negocio", businessId: 8);

        var model = await GetCreateForm();

        var option = Assert.Single(model.Clients);
        Assert.Equal("Ana Pérez", option.Name);
    }

    [Fact]
    public async Task Create_form_without_a_tariff_or_a_seat_does_not_offer_the_form()
    {
        await SeedClient("Ana", "Pérez");

        var model = await GetCreateForm();

        Assert.False(model.CanEnroll);
        Assert.False(model.HasTariffs);
        Assert.False(model.HasGridClasses);
        Assert.NotNull(model.MissingRequirement);
    }

    [Fact]
    public async Task Create_post_writes_once_with_the_business_from_the_claim_and_redirects_with_a_flash()
    {
        var clientId = await SeedClient("Ana", "Pérez");
        await SeedTariff(2, 8000m);
        var gridClass = await SeedGridClass("Hatha", maxCapacity: 2);

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Create(
            new EnrollmentFormViewModel
            {
                ClientId = clientId,
                WeeklyFrequency = 2,
                GridClassIds = [gridClass.GridClassId]
            },
            CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Inscripción registrada. Cobrá el primer abono para activarla.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        var stored = Assert.Single(_repository.Enrollments);
        Assert.Equal(BusinessId, stored.BusinessId);
        Assert.Equal(clientId, stored.ClientId);
        Assert.Equal(EnrollmentStatus.PendingPayment, stored.Status);
        Assert.Equal(8000m, stored.AgreedPrice); // the configured tariff, because no amount was typed
        Assert.Equal(1, _repository.TryInsertCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Create_post_re_renders_the_form_keeping_the_typed_values_when_the_service_rejects()
    {
        var clientId = await SeedClient("Ana", "Pérez");
        await SeedTariff(2, 8000m);
        var gridClass = await SeedGridClass("Hatha", maxCapacity: 2);

        var controller = Create();
        var view = Assert.IsType<ViewResult>(await controller.Create(
            new EnrollmentFormViewModel
            {
                ClientId = clientId,
                WeeklyFrequency = 2,
                AgreedPrice = 7000m, // differs from the tariff and carries no reason, so the create is rejected
                PriceReason = null,
                GridClassIds = [gridClass.GridClassId]
            },
            CancellationToken.None));

        Assert.Equal("Form", view.ViewName);
        var model = Assert.IsType<EnrollmentFormViewModel>(view.Model);
        Assert.Equal(clientId, model.ClientId);
        Assert.Equal(2, model.WeeklyFrequency);
        Assert.Equal(7000m, model.AgreedPrice);
        Assert.Equal(new[] { gridClass.GridClassId }, model.GridClassIds);
        Assert.True(model.CanEnroll);

        var error = Assert.Single(controller.ModelState["PriceReason"]!.Errors).ErrorMessage;
        Assert.Equal("Indicá el motivo del monto acordado.", error);
        Assert.DoesNotContain("enrollment_", error);

        // The rejection rolls back: nothing is written.
        Assert.Equal(0, _repository.TryInsertCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    // --- Charge (RF-05 step 6, RF-29) ---------------------------------------------------------------------------

    [Fact]
    public async Task Charge_post_activates_the_pending_membership_and_flashes_the_new_expiry()
    {
        var enrollment = StoreEnrollment(clientId: 100, status: EnrollmentStatus.PendingPayment, weeklyFrequency: 2, agreedPrice: 8000m);

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Charge(
            enrollment.Id, null, null, CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Inscripción activada. El abono vence el 10/04/2026.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        Assert.Equal(enrollment.Id, _repository.LockedEnrollmentId);
        Assert.Equal(EnrollmentStatus.Active, _repository.Enrollments.Single().Status);
        Assert.Equal(1, _repository.InsertPaymentCallCount);
        Assert.Equal(1, _repository.InsertPeriodCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Charge_post_flashes_the_mapped_message_of_an_ended_membership_and_writes_nothing()
    {
        var enrollment = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Annulled);

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Charge(
            enrollment.Id, null, null, CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Esa inscripción está anulada o dada de baja: no se puede cobrar.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, _repository.InsertPaymentCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Charge_post_with_an_invalid_amount_never_reaches_the_service()
    {
        var enrollment = StoreEnrollment(clientId: 100);

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Charge(
            enrollment.Id, -5m, null, CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Ingresá un monto válido para cobrar.", Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // --- Annul (RF-20) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Annul_post_annuls_the_pending_membership_with_the_business_from_the_claim()
    {
        var enrollment = StoreEnrollment(clientId: 100, status: EnrollmentStatus.PendingPayment);
        var gridClass = await SeedGridClass("Hatha");
        StoreFixedClass(enrollment, gridClass.GridClassId, D(2026, 3, 1));

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Annul(
            enrollment.Id, CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Inscripción anulada. Sus lugares fijos quedaron libres.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(enrollment.Id, _repository.LockedEnrollmentId);
        Assert.Equal(EnrollmentStatus.Annulled, _repository.Enrollments.Single().Status);
        Assert.Equal((enrollment.Id, DateOnly.FromDateTime(_clock.Now)), _repository.LastCloseRequest);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Annul_post_flashes_the_mapped_message_of_a_non_pending_membership()
    {
        var enrollment = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Active, referenceDay: 10);
        StorePeriod(enrollment, D(2026, 3, 1), D(2026, 4, 1));

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Annul(
            enrollment.Id, CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Sólo se puede anular una inscripción pendiente de pago.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(EnrollmentStatus.Active, _repository.Enrollments.Single().Status);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    // --- Terminate (RF-29 step 6) -------------------------------------------------------------------------------

    [Fact]
    public async Task Terminate_post_ends_the_membership_from_the_chosen_date()
    {
        var enrollment = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Active, referenceDay: 10);
        var gridClass = await SeedGridClass("Hatha");
        StoreFixedClass(enrollment, gridClass.GridClassId, D(2026, 3, 1));
        StorePeriod(enrollment, D(2026, 3, 1), D(2026, 4, 1));

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Terminate(
            enrollment.Id, D(2026, 4, 1), CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Abono dado de baja desde el 01/04/2026. Sus lugares quedaron libres.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(enrollment.Id, _repository.LockedEnrollmentId);
        Assert.Equal(EnrollmentStatus.Terminated, _repository.Enrollments.Single().Status);
        Assert.Equal((enrollment.Id, D(2026, 4, 1)), _repository.LastCloseRequest);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Terminate_post_flashes_the_mapped_message_of_an_ended_membership()
    {
        var enrollment = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Terminated);

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Terminate(
            enrollment.Id, D(2026, 4, 1), CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Esa inscripción ya está anulada o dada de baja.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Terminate_post_without_a_date_flashes_and_never_reaches_the_service()
    {
        var enrollment = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Active, referenceDay: 10);

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Terminate(
            enrollment.Id, null, CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Elegí la fecha desde la que dar de baja el abono.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(EnrollmentStatus.Active, _repository.Enrollments.Single().Status);
    }

    [Fact]
    public async Task Charge_annul_and_terminate_never_reach_an_enrollment_of_another_business()
    {
        var foreign = StoreEnrollment(clientId: 100, status: EnrollmentStatus.PendingPayment, businessId: 8);

        var controller = Create();

        var charge = Assert.IsType<RedirectToActionResult>(
            await controller.Charge(foreign.Id, null, null, CancellationToken.None));
        Assert.Equal("Index", charge.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No encontramos ese abono.", Assert.IsType<string>(controller.TempData["FlashMessage"]));

        await controller.Annul(foreign.Id, CancellationToken.None);
        Assert.Equal("No encontramos esa inscripción.", Assert.IsType<string>(controller.TempData["FlashMessage"]));

        await controller.Terminate(foreign.Id, D(2026, 4, 1), CancellationToken.None);
        Assert.Equal("No encontramos esa inscripción.", Assert.IsType<string>(controller.TempData["FlashMessage"]));

        // The claim business scopes every write: the foreign enrollment is untouched and nothing commits.
        Assert.Equal(EnrollmentStatus.PendingPayment, _repository.Enrollments.Single().Status);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    // --- Views --------------------------------------------------------------------------------------------------

    [Fact]
    public void Index_view_keeps_one_primary_action_and_gates_the_courtesy_actions()
    {
        var index = ReadEnrollmentsView("Index.cshtml");

        // Exactly one primary action on the whole screen: "Nueva inscripción".
        Assert.Equal(1, index.Split("btn-primary").Length - 1);

        Assert.True(index.IndexOf("item.CanCharge", StringComparison.Ordinal) <
            index.IndexOf("asp-action=\"Charge\"", StringComparison.Ordinal));
        Assert.True(index.IndexOf("item.CanAnnul", StringComparison.Ordinal) <
            index.IndexOf("asp-action=\"Annul\"", StringComparison.Ordinal));
        Assert.True(index.IndexOf("item.CanTerminate", StringComparison.Ordinal) <
            index.IndexOf("asp-action=\"Terminate\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Form_view_keeps_one_primary_action_and_gates_on_the_missing_requirements()
    {
        var form = ReadEnrollmentsView("Form.cshtml");

        Assert.Equal(1, form.Split("btn-primary").Length - 1);
        Assert.True(form.IndexOf("Model.CanEnroll", StringComparison.Ordinal) <
            form.IndexOf("asp-action=\"Create\"", StringComparison.Ordinal));
    }

    // --- Harness ------------------------------------------------------------------------------------------------

    private async Task<EnrollmentIndexViewModel> Listed(EnrollmentsController controller, string? filter) =>
        Assert.IsType<EnrollmentIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.Index(filter, CancellationToken.None)).Model);

    private async Task<EnrollmentFormViewModel> GetCreateForm()
    {
        var controller = Create();
        return Assert.IsType<EnrollmentFormViewModel>(
            Assert.IsType<ViewResult>(await controller.Create(CancellationToken.None)).Model);
    }

    private EnrollmentsController Create()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkUpClaims.BusinessId, BusinessId.ToString())], "test"))
        };

        var controller = new EnrollmentsController(
            new EnrollmentService(_repository, _prices, _unitOfWork, _clock),
            _clock,
            _repository,
            new ClassesService(_gridClasses, _services, _workers, _clock),
            new CatalogService(_services),
            new StaffService(_workers, _unitOfWork),
            new ClientsService(_clients, new FakeClientNoteRepository(), _clock),
            _prices)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
        return controller;
    }

    private static string ReadEnrollmentsView(string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", "Enrollments", fileName));
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
