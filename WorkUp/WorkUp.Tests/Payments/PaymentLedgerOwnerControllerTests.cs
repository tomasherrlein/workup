using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Payments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Payments;

namespace WorkUp.Tests.Payments;

// T18 owner side (RF-11, RF-28, D-20): the Cobros history and its two corrections. The controller only routes: every
// rule, every clock read and every write belongs to PaymentLedgerService and is already pinned at the service level
// (PaymentLedgerServiceTests). What is pinned here is what the panel adds: the tenant scope that comes from the owner
// claim, the annulment's person responsible that comes from the authenticated username and never from the form, the
// filters and the page, the mapping of the stable error codes to Spanish, the POST/redirect/GET with antiforgery, the
// invariant decimal binder of the refund amount, the financial-only copy and the forms that hide each other.
public sealed class PaymentLedgerOwnerControllerTests
{
    private const int BusinessId = 7;
    private const string OwnerUsername = "lucia.demo";

    private readonly FakePaymentLedgerRepository _ledger = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new() { Now = new DateTime(2026, 3, 20, 9, 0, 0) };
    private readonly FakeClientRepository _clients = new();

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // --- Fixtures -----------------------------------------------------------------------------------------------

    private Payment StorePayment(
        int id = 500,
        int? clientId = 11,
        PaymentConcept concept = PaymentConcept.Membership,
        int? enrollmentId = 100,
        int? bookingId = null,
        decimal amount = 5000m,
        DateTime? paidAt = null,
        int businessId = BusinessId)
    {
        var payment = new Payment
        {
            Id = id,
            BusinessId = businessId,
            ClientId = clientId,
            Concept = concept,
            EnrollmentId = enrollmentId,
            BookingId = bookingId,
            Amount = amount,
            PaidAt = paidAt ?? new DateTime(2026, 3, 1, 10, 0, 0),
            RecordedAt = paidAt ?? new DateTime(2026, 3, 1, 10, 0, 0)
        };
        _ledger.Payments.Add(payment);
        return payment;
    }

    private void StoreAnnulment(int paymentId) =>
        _ledger.Annulments.Add(new PaymentAnnulment
        {
            PaymentId = paymentId,
            Reason = "Cobro duplicado",
            AnnulledAt = new DateTime(2026, 3, 15, 9, 0, 0),
            AnnulledBy = "lucia.demo"
        });

    private void StoreRefund(int paymentId, decimal amount, DateOnly refundedOn) =>
        _ledger.Refunds.Add(new Refund
        {
            PaymentId = paymentId,
            Amount = amount,
            RefundedOn = refundedOn,
            Reason = "Devolución acordada",
            RecordedAt = new DateTime(2026, 3, 16, 9, 0, 0)
        });

    private async Task<int> StoreClient(string firstName, string lastName, int businessId = BusinessId) =>
        await _clients.InsertAsync(new Client
        {
            BusinessId = businessId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}@example.com",
            Phone = "1145667788",
            CreatedAt = _clock.Now
        });

    private PaymentsController Create()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(WorkUpClaims.BusinessId, BusinessId.ToString()),
                new Claim(WorkUpClaims.BusinessName, "Estudio Lucía"),
                new Claim(ClaimTypes.Name, OwnerUsername)
            ], "test"))
        };

        return new PaymentsController(
            new PaymentLedgerService(_ledger, _unitOfWork, _clock),
            new ClientsService(_clients, new FakeClientNoteRepository(), _clock),
            _clock)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
    }

    private static async Task<PaymentLedgerIndexViewModel> Listed(
        PaymentsController controller,
        DateOnly? from = null,
        DateOnly? to = null,
        int? clientId = null,
        string? concept = null,
        int? page = null) =>
        Assert.IsType<PaymentLedgerIndexViewModel>(
            Assert.IsType<ViewResult>(
                await controller.Index(from, to, clientId, concept, page, CancellationToken.None)).Model);

    // --- List (RF-28) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Index_shows_the_rows_with_their_states_and_corrections()
    {
        await StoreClient("Ana", "Pérez");
        StorePayment(id: 1, clientId: 11, paidAt: new DateTime(2026, 3, 1, 10, 0, 0));
        StorePayment(id: 2, clientId: 11, paidAt: new DateTime(2026, 3, 2, 10, 0, 0));
        StoreAnnulment(2);
        StorePayment(id: 3, clientId: 11, paidAt: new DateTime(2026, 3, 3, 10, 0, 0));
        StoreRefund(3, 1000m, D(2026, 3, 16));
        _ledger.ClientNames[11] = "Ana Pérez";

        var model = await Listed(Create());

        Assert.True(model.HasItems);
        Assert.Equal(3, model.TotalCount);
        Assert.Equal([3, 2, 1], model.Items.Select(item => item.PaymentId).ToArray());

        var paid = model.Items.Single(item => item.PaymentId == 1);
        Assert.Equal("Cobrado", paid.StateTag.Label);
        Assert.Equal(5000m, paid.Amount);
        Assert.Equal("Ana Pérez", paid.ClientName);
        Assert.Equal("Abono", paid.ConceptLabel);
        Assert.Equal("Inscripción #100", paid.ReferenceLabel);
        Assert.True(paid.CanAnnul);
        Assert.True(paid.CanRefund);
        Assert.False(paid.HasRefund);

        var annulled = model.Items.Single(item => item.PaymentId == 2);
        Assert.Equal("Anulado", annulled.StateTag.Label);
        Assert.True(annulled.Annulled);
        Assert.Equal("Cobro duplicado", annulled.AnnulmentReason);
        Assert.Equal("lucia.demo", annulled.AnnulledBy);
        Assert.Equal(new DateTime(2026, 3, 15, 9, 0, 0), annulled.AnnulledAt);
        // The two corrections never coexist: an annulled row offers neither form.
        Assert.False(annulled.CanAnnul);
        Assert.False(annulled.CanRefund);

        var refunded = model.Items.Single(item => item.PaymentId == 3);
        Assert.Equal("Devuelto", refunded.StateTag.Label);
        Assert.Equal(1000m, refunded.RefundedAmount);
        Assert.Equal(4000m, refunded.RemainingAmount);
        // A row with a refund offers no annulment, but a partial one can still be refunded again.
        Assert.False(refunded.CanAnnul);
        Assert.True(refunded.CanRefund);
    }

    [Fact]
    public async Task Index_forwards_the_period_client_and_concept_filters()
    {
        await StoreClient("Ana", "Pérez");
        await StoreClient("Bruno", "Gómez");
        await StoreClient("Ajena", "De Otro Negocio", businessId: 8);
        StorePayment(id: 1, clientId: 11, concept: PaymentConcept.Membership, paidAt: new DateTime(2026, 3, 1, 10, 0, 0));
        StorePayment(id: 2, clientId: 12, concept: PaymentConcept.IndividualAppointment,
            enrollmentId: null, bookingId: 70, paidAt: new DateTime(2026, 3, 5, 10, 0, 0));
        StorePayment(id: 3, clientId: 11, concept: PaymentConcept.DropInClass,
            enrollmentId: null, bookingId: 71, paidAt: new DateTime(2026, 3, 10, 10, 0, 0));

        var controller = Create();

        var byPeriod = await Listed(controller, from: D(2026, 3, 1), to: D(2026, 3, 4));
        Assert.Equal(1, Assert.Single(byPeriod.Items).PaymentId);

        var byClient = await Listed(controller, clientId: 12);
        Assert.Equal(2, Assert.Single(byClient.Items).PaymentId);

        var byConcept = await Listed(controller, concept: "DropInClass");
        Assert.Equal(3, Assert.Single(byConcept.Items).PaymentId);
        Assert.Equal(PaymentConcept.DropInClass, byConcept.Concept);

        // An unknown filter falls back to the unfiltered view instead of failing the page, and only the clients of
        // the business populate the picker.
        var unknown = await Listed(controller, concept: "no-existe");
        Assert.Null(unknown.Concept);
        Assert.Equal(3, unknown.Items.Count);
        Assert.Equal(["Bruno Gómez", "Ana Pérez"], unknown.Clients.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task Index_pages_with_the_total_and_keeps_the_service_page_size_bound()
    {
        for (var day = 1; day <= 25; day++)
            StorePayment(id: day, paidAt: new DateTime(2026, 3, 1, 10, 0, 0).AddMinutes(day));

        var controller = Create();

        var first = await Listed(controller, page: 1);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(25, first.TotalCount);
        Assert.Equal(1, first.Page);
        Assert.Equal(PaymentLedgerService.DefaultPageSize, first.PageSize);
        Assert.Equal(2, first.TotalPages);
        Assert.False(first.HasPrevious);
        Assert.True(first.HasNext);

        var second = await Listed(controller, page: 2);
        Assert.Equal(5, second.Items.Count);
        Assert.True(second.HasPrevious);
        Assert.False(second.HasNext);

        // Page 0 and a negative page are clamped to the first page; the controller exposes no page size to request.
        Assert.Equal(1, (await Listed(controller, page: 0)).Page);
        Assert.Equal(1, (await Listed(controller, page: -3)).Page);
    }

    [Fact]
    public async Task Index_surfaces_an_inverted_period_instead_of_showing_an_empty_history()
    {
        StorePayment();

        var controller = Create();
        var model = await Listed(controller, from: D(2026, 3, 10), to: D(2026, 3, 1));

        Assert.False(model.HasItems);
        Assert.True(model.HasFilters);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("La fecha desde no puede ser posterior a la fecha hasta.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        // The service rejected it before the read, so the impossible range never reached storage.
        Assert.Equal(0, _ledger.ListCallCount);
    }

    [Fact]
    public async Task Index_is_a_read_and_opens_no_transaction()
    {
        StorePayment();

        await Listed(Create());

        Assert.Equal(1, _ledger.ListCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    // --- Annul (RF-28) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Annul_takes_the_tenant_from_the_claim_and_the_actor_from_the_authenticated_owner()
    {
        StorePayment();

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.Annul(500, "  Cobro duplicado  ", CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Cobro anulado.", Assert.IsType<string>(controller.TempData["FlashMessage"]));

        var annulment = Assert.Single(_ledger.Annulments);
        Assert.Equal(500, annulment.PaymentId);
        Assert.Equal("Cobro duplicado", annulment.Reason);
        // The person responsible is the signed-in owner, never a field of the request body.
        Assert.Equal(OwnerUsername, annulment.AnnulledBy);
        Assert.Equal(_clock.Now, annulment.AnnulledAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Annul_is_refused_for_a_foreign_or_missing_payment()
    {
        StorePayment(businessId: 8);

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.Annul(500, "Motivo", CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No encontramos ese cobro.", Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Empty(_ledger.Annulments);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Annul_flashes_the_mapped_message_of_an_already_annulled_payment()
    {
        StorePayment();
        StoreAnnulment(500);

        var controller = Create();
        await controller.Annul(500, "Otro motivo", CancellationToken.None);

        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Ese cobro ya está anulado.", Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Single(_ledger.Annulments);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Annul_refuses_a_missing_or_too_long_reason_before_the_service()
    {
        StorePayment();

        var controller = Create();

        await controller.Annul(500, "   ", CancellationToken.None);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Indicá el motivo de la anulación (hasta 300 caracteres).",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        await controller.Annul(500, new string('x', 301), CancellationToken.None);
        Assert.Equal("Error", controller.TempData["FlashType"]);

        Assert.Empty(_ledger.Annulments);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // --- Refund (D-20) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refund_registers_a_partial_refund_and_redirects_with_a_flash()
    {
        StorePayment();

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.Refund(500, 1500m, D(2026, 3, 18), "Devolución acordada", CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Devolución registrada.", Assert.IsType<string>(controller.TempData["FlashMessage"]));

        var refund = Assert.Single(_ledger.Refunds);
        Assert.Equal(500, refund.PaymentId);
        Assert.Equal(1500m, refund.Amount);
        Assert.Equal(D(2026, 3, 18), refund.RefundedOn);
        Assert.Equal("Devolución acordada", refund.Reason);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Refund_is_refused_when_the_amount_passes_the_remaining_cap()
    {
        StorePayment(amount: 5000m);
        StoreRefund(500, 4000m, D(2026, 3, 15));

        var controller = Create();
        await controller.Refund(500, 2000m, D(2026, 3, 18), "Motivo", CancellationToken.None);

        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Las devoluciones no pueden superar el importe cobrado.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Single(_ledger.Refunds);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Refund_is_refused_on_an_annulled_payment()
    {
        StorePayment();
        StoreAnnulment(500);

        var controller = Create();
        await controller.Refund(500, 1000m, D(2026, 3, 18), "Motivo", CancellationToken.None);

        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Ese cobro está anulado: no se puede devolver.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Empty(_ledger.Refunds);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Refund_refuses_a_missing_amount_date_or_reason_before_the_service()
    {
        StorePayment();

        var controller = Create();

        await controller.Refund(500, null, D(2026, 3, 18), "Motivo", CancellationToken.None);
        Assert.Equal("Ingresá un importe válido para la devolución.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        await controller.Refund(500, 1000m, null, "Motivo", CancellationToken.None);
        Assert.Equal("Indicá la fecha de la devolución.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        await controller.Refund(500, 1000m, D(2026, 3, 18), "  ", CancellationToken.None);
        Assert.Equal("Indicá el motivo de la devolución (hasta 300 caracteres).",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        Assert.Empty(_ledger.Refunds);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // F1 from the verifier: a refund dated after the business's today has its own stable code, and the panel maps it
    // explicitly instead of falling back to the service sentence, so the contract survives a change of that message.
    [Fact]
    public async Task Refund_is_refused_with_its_own_message_when_the_date_is_in_the_future()
    {
        StorePayment(paidAt: new DateTime(2026, 3, 1, 10, 0, 0));

        // The controller clock says today is 2026-03-20; the posted day is after it.
        var controller = Create();
        await controller.Refund(500, 1000m, D(2026, 3, 25), "Motivo", CancellationToken.None);

        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("La devolución no puede tener una fecha posterior a hoy.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Empty(_ledger.Refunds);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    // --- Static contract ----------------------------------------------------------------------------------------

    [Fact]
    public void Every_write_action_requires_antiforgery_and_the_list_does_not()
    {
        foreach (var name in new[] { nameof(PaymentsController.Annul), nameof(PaymentsController.Refund) })
        {
            var method = typeof(PaymentsController).GetMethod(name, [.. ParameterTypes(name)]);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        }

        var index = typeof(PaymentsController).GetMethod(nameof(PaymentsController.Index),
            [.. ParameterTypes(nameof(PaymentsController.Index))]);
        Assert.NotNull(index);
        Assert.Null(index!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(index.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public void The_refund_amount_is_bound_with_the_invariant_decimal_binder()
    {
        var refund = typeof(PaymentsController).GetMethod(nameof(PaymentsController.Refund),
            [.. ParameterTypes(nameof(PaymentsController.Refund))]);
        Assert.NotNull(refund);

        var amount = refund!.GetParameters().Single(parameter => parameter.Name == "amount");
        var binder = amount.GetCustomAttribute<ModelBinderAttribute>();
        Assert.NotNull(binder);
        Assert.Equal(typeof(InvariantDecimalModelBinder), binder!.BinderType);

        // The real refund date is owner input, and the reason is a bounded string: neither is invented here.
        Assert.Equal(typeof(DateOnly?), refund.GetParameters().Single(p => p.Name == "refundedOn").ParameterType);
        Assert.Equal(typeof(string), refund.GetParameters().Single(p => p.Name == "reason").ParameterType);
        // The actor is never a request parameter of the annulment: it comes from the authenticated owner.
        var annul = typeof(PaymentsController).GetMethod(nameof(PaymentsController.Annul),
            [.. ParameterTypes(nameof(PaymentsController.Annul))]);
        Assert.DoesNotContain(annul!.GetParameters(), parameter => parameter.Name is "actor" or "annulledBy");
    }

    [Fact]
    public void Index_view_gates_the_forms_on_the_row_flags_and_keeps_one_primary_action()
    {
        var view = ReadOwnerView("Payments", "Index.cshtml");

        AssertGated(view, "item.CanAnnul", "asp-action=\"Annul\"");
        AssertGated(view, "item.CanRefund", "asp-action=\"Refund\"");
        Assert.Equal(1, view.Split("btn-primary").Length - 1);

        // The financial-only copy: an annulment reverses no service and no money is transferred automatically.
        Assert.Contains("no revierte", view, StringComparison.Ordinal);
        Assert.Contains("no transfiere", view, StringComparison.Ordinal);
        Assert.DoesNotContain("transferencia automática", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("revertimos", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devolvemos", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_payments_section_is_reachable_from_the_owner_navigation()
    {
        var item = Assert.Single(OwnerNavigation.Sections, section => section.Controller == "Payments");
        Assert.Equal("Cobros", item.Label);
    }

    private static Type[] ParameterTypes(string methodName) =>
        typeof(PaymentsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == methodName)
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

    private static void AssertGated(string view, string gate, string form)
    {
        var gateIndex = view.IndexOf(gate, StringComparison.Ordinal);
        var formIndex = view.IndexOf(form, StringComparison.Ordinal);
        Assert.True(gateIndex >= 0, $"La vista debe mirar {gate}.");
        Assert.True(formIndex > gateIndex, $"{form} debe seguir al gate {gate}.");
    }

    private static string ReadOwnerView(string folder, string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", folder, fileName));
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
