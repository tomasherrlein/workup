using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Reports;
using WorkUp.Data.Reports;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Reports;

namespace WorkUp.Tests.Reports;

// T19 owner side (RF-16, D-20, assumption 7): the report screen. The controller only routes: every rule, every clock
// read and every bucket belongs to ReportsService and is already pinned at the service level (ReportsServiceTests).
// What is pinned here is what the panel adds: the tenant scope that comes from the owner claim, the default period
// (current month through today), the inclusive From/To inputs and the optional comparison pair, the explicit error
// surface for a missing, inverted or not-yet-started period, the partial-period cutoff, the comparison, the labels
// (including the provisional definition of the indicators) and the scoped DI registration. The report is a read: the
// port exposes no write and the screen never opens a transaction.
public sealed class ReportsOwnerControllerTests
{
    private const int BusinessId = 7;
    private const string OwnerUsername = "lucia.demo";

    private readonly FakeReportsRepository _repository = new();
    private readonly FakeClock _clock = new() { Now = new DateTime(2026, 4, 10, 9, 0, 0) };

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // --- Fixtures -----------------------------------------------------------------------------------------------

    private ReportPaymentRow Payment(
        int id,
        decimal amount,
        DateOnly paidOn,
        PaymentConcept concept = PaymentConcept.DropInClass,
        int? serviceId = 10,
        string? serviceName = "Yoga",
        AppointmentKind? kind = AppointmentKind.ClassSession,
        int? appointmentId = 100,
        int? workerId = 7,
        bool annulled = false) => new(
        id, concept, appointmentId, workerId, serviceId, serviceName, kind, amount, paidOn, annulled);

    private ReportRefundRow Refund(int id, int paymentId, decimal amount, DateOnly refundedOn) =>
        new(id, paymentId, amount, refundedOn);

    private ReportExpenseRow Expense(
        int id, decimal amount, DateOnly spentOn, int typeId = 1, string typeName = "Alquiler") =>
        new(id, typeId, typeName, amount, spentOn);

    private ReportAttendanceRow Attendance(
        int appointmentId, int clientId, int workerId, string workerName, BookingStatus status, DateOnly occurredOn) =>
        new(appointmentId, clientId, workerId, workerName, status, occurredOn);

    private void StorePayment(ReportPaymentRow row, int businessId = BusinessId) =>
        _repository.Payments.Add((businessId, row));

    private void StoreRefund(ReportRefundRow row, int businessId = BusinessId) =>
        _repository.Refunds.Add((businessId, row));

    private void StoreExpense(ReportExpenseRow row, int businessId = BusinessId) =>
        _repository.Expenses.Add((businessId, row));

    private void StoreAttendance(ReportAttendanceRow row, int businessId = BusinessId) =>
        _repository.Attendances.Add((businessId, row));

    private ReportsController Create()
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

        return new ReportsController(new ReportsService(_repository, _clock), _clock)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
    }

    private static async Task<ReportsIndexViewModel> Get(
        ReportsController controller,
        DateOnly? from = null,
        DateOnly? to = null,
        DateOnly? comparisonFrom = null,
        DateOnly? comparisonTo = null) =>
        Assert.IsType<ReportsIndexViewModel>(
            Assert.IsType<ViewResult>(
                await controller.Index(from, to, comparisonFrom, comparisonTo, CancellationToken.None)).Model);

    // --- Default period -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Index_opens_on_the_current_month_through_today()
    {
        var model = await Get(Create());

        Assert.Equal(D(2026, 4, 1), model.From);
        Assert.Equal(D(2026, 4, 10), model.To);
        Assert.True(model.HasReport);

        // The default is a real period the service computes, not an empty read: one inclusive snapshot was asked for.
        var request = Assert.Single(_repository.LoadedRequests);
        Assert.Equal(D(2026, 4, 1), request.PeriodFrom);
        Assert.Equal(D(2026, 4, 10), request.PeriodTo);
    }

    [Fact]
    public async Task Index_is_tenant_scoped_and_never_counts_another_business()
    {
        StorePayment(Payment(1, 1000m, D(2026, 4, 5)));
        StorePayment(Payment(2, 9999m, D(2026, 4, 6)), businessId: 8);
        StoreRefund(Refund(1, 1, 100m, D(2026, 4, 8)));
        StoreRefund(Refund(2, 2, 7777m, D(2026, 4, 8)), businessId: 8);
        StoreExpense(Expense(1, 200m, D(2026, 4, 7)));
        StoreExpense(Expense(2, 8888m, D(2026, 4, 7)), businessId: 8);

        var model = await Get(Create());

        Assert.Equal(1000m, model.Period!.PaymentsTotal);
        Assert.Equal(100m, model.Period.RefundsTotal);
        Assert.Equal(200m, model.Period.ExpensesTotal);
        Assert.Equal(700m, model.Period.NetResult);
        // The business comes from the owner claim and is the only one ever read.
        Assert.Equal([BusinessId], _repository.LoadedBusinessIds);
    }

    // --- Clipping and cutoff ------------------------------------------------------------------------------------

    [Fact]
    public async Task Index_clips_a_period_that_ends_in_the_future_to_today_and_says_so()
    {
        StorePayment(Payment(1, 1000m, D(2026, 4, 5)));
        // Dated after the business's today: the clip must leave it out instead of inventing a future fact.
        StorePayment(Payment(2, 5000m, D(2026, 4, 20)));

        var model = await Get(Create(), from: D(2026, 4, 1), to: D(2026, 4, 30));

        Assert.True(model.Period!.IsPartial);
        Assert.Equal(D(2026, 4, 30), model.Period.RequestedTo);
        Assert.Equal(D(2026, 4, 10), model.Period.EffectiveTo);
        Assert.Equal(1000m, model.Period.PaymentsTotal);
        Assert.Contains("se calcula hasta hoy", model.Period.CutoffLabel, StringComparison.Ordinal);

        var request = Assert.Single(_repository.LoadedRequests);
        Assert.Equal(D(2026, 4, 10), request.PeriodTo);
    }

    [Fact]
    public async Task Index_refuses_a_period_that_has_not_started()
    {
        var controller = Create();
        var model = await Get(controller, from: D(2026, 5, 1), to: D(2026, 5, 31));

        Assert.False(model.HasReport);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("El período todavía no empezó: no hay nada que calcular.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        // The service refused it before any read, so no snapshot of a period that does not exist was asked for.
        Assert.Equal(0, _repository.LoadCallCount);
    }

    // --- Input validation ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Index_surfaces_a_half_filled_period_instead_of_guessing_the_missing_end()
    {
        var controller = Create();
        var model = await Get(controller, from: D(2026, 4, 1));

        Assert.False(model.HasReport);
        Assert.Equal(D(2026, 4, 1), model.From);
        Assert.Null(model.To);
        Assert.Equal("Indicá las fechas del período.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, _repository.LoadCallCount);
    }

    [Fact]
    public async Task Index_surfaces_an_inverted_period()
    {
        var controller = Create();
        var model = await Get(controller, from: D(2026, 4, 20), to: D(2026, 4, 1));

        Assert.False(model.HasReport);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("La fecha desde no puede ser posterior a la fecha hasta.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, _repository.LoadCallCount);
    }

    [Fact]
    public async Task Index_refuses_a_comparison_with_only_one_end()
    {
        var controller = Create();
        var model = await Get(controller, comparisonFrom: D(2026, 3, 1));

        Assert.False(model.HasReport);
        Assert.Equal("Indicá las dos fechas de la comparación.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, _repository.LoadCallCount);
    }

    // --- Comparison ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Index_computes_the_comparison_period_with_the_same_rules()
    {
        StorePayment(Payment(1, 1000m, D(2026, 4, 5)));
        StorePayment(Payment(2, 400m, D(2026, 3, 5)));
        StoreExpense(Expense(1, 100m, D(2026, 4, 6)));
        StoreExpense(Expense(2, 50m, D(2026, 3, 6)));

        var model = await Get(Create(),
            from: D(2026, 4, 1), to: D(2026, 4, 30),
            comparisonFrom: D(2026, 3, 1), comparisonTo: D(2026, 3, 31));

        Assert.True(model.HasComparison);
        Assert.Equal(D(2026, 3, 1), model.Comparison!.RequestedFrom);
        Assert.Equal(D(2026, 3, 31), model.Comparison.RequestedTo);
        Assert.Equal(350m, model.Comparison.NetResult);

        var line = model.ComparisonLines.Single(item => item.Label == "Resultado del período");
        Assert.Equal(900m, line.PeriodAmount);
        Assert.Equal(350m, line.ComparisonAmount);
        Assert.Equal(550m, line.Difference);

        // A report with a comparison is exactly two reads, never one per row.
        Assert.Equal(2, _repository.LoadCallCount);
    }

    [Fact]
    public async Task Index_without_a_comparison_shows_no_comparison()
    {
        var model = await Get(Create(), from: D(2026, 4, 1), to: D(2026, 4, 10));

        Assert.True(model.HasReport);
        Assert.False(model.HasComparison);
        Assert.Null(model.Comparison);
        Assert.Empty(model.ComparisonLines);
        Assert.Equal(1, _repository.LoadCallCount);
    }

    // --- Buckets and labels -------------------------------------------------------------------------------------

    [Fact]
    public async Task Index_shows_membership_money_as_the_unallocated_abonos_bucket()
    {
        StorePayment(Payment(
            1, 5000m, D(2026, 4, 3),
            concept: PaymentConcept.Membership,
            serviceId: null, serviceName: null, kind: null, appointmentId: null, workerId: null));
        StorePayment(Payment(2, 600m, D(2026, 4, 4)));
        StorePayment(Payment(
            3, 950m, D(2026, 4, 5),
            concept: PaymentConcept.IndividualAppointment,
            serviceId: 20, serviceName: "Masaje", kind: AppointmentKind.Individual,
            appointmentId: 200, workerId: 7));

        var model = await Get(Create());

        Assert.Equal(5000m, model.Period!.MembershipRevenue);
        Assert.Equal("Abonos", model.Period.MembershipLabel);
        Assert.Equal(0m, model.Period.UnallocatedRevenue);
        Assert.Equal(2, model.Period.RevenueLines.Count);
        Assert.Contains(model.Period.RevenueLines, line => line.Label == "Yoga · Clase" && line.Amount == 600m);
        Assert.Contains(model.Period.RevenueLines, line =>
            line.Label == "Masaje · Turno individual" && line.Amount == 950m);
    }

    [Fact]
    public async Task Index_labels_the_result_lines_expenses_by_type_and_worker_production()
    {
        StorePayment(Payment(1, 1000m, D(2026, 4, 5)));
        StoreRefund(Refund(1, 1, 100m, D(2026, 4, 8)));
        StoreExpense(Expense(1, 200m, D(2026, 4, 7), typeId: 1, typeName: "Alquiler"));
        StoreAttendance(Attendance(100, 11, 7, "Lucía", BookingStatus.Attended, D(2026, 4, 5)));

        var model = await Get(Create());

        Assert.Equal(
            ["Cobros no anulados", "Devoluciones del período", "Gastos del período"],
            model.Period!.ResultLines.Select(line => line.Label).ToArray());
        Assert.Equal(1000m, model.Period.ResultLines[0].Amount);
        Assert.Equal(100m, model.Period.ResultLines[1].Amount);
        Assert.Equal(200m, model.Period.ResultLines[2].Amount);
        Assert.Equal("Alquiler", Assert.Single(model.Period.ExpenseTypeLines).Label);

        var worker = Assert.Single(model.Period.WorkerLines);
        Assert.Equal("Lucía", worker.WorkerName);
        Assert.Equal(1, worker.AttendedCount);
        Assert.Equal(1000m, worker.CollectedAmount);
    }

    [Fact]
    public async Task Index_reports_an_explicit_empty_state_when_the_period_has_no_movements()
    {
        var model = await Get(Create());

        Assert.True(model.HasReport);
        Assert.False(model.Period!.HasMovements);
        Assert.Contains("No hay movimientos", model.EmptyMessage, StringComparison.Ordinal);
    }

    // --- Static contract ----------------------------------------------------------------------------------------

    [Fact]
    public void The_reports_port_exposes_no_write()
    {
        // Computing a report can never change a payment, a refund, an expense or an attendance.
        var load = Assert.Single(typeof(IReportsRepository).GetMethods());
        Assert.Equal(nameof(IReportsRepository.LoadAsync), load.Name);
        Assert.Equal(typeof(Task<ReportSnapshot>), load.ReturnType);
    }

    [Fact]
    public void Index_view_labels_every_amount_and_date_and_the_provisional_definition()
    {
        var view = ReadOwnerView("Reports", "Index.cshtml");

        // Each amount and each date of the result has its own label; no bare number is shown. The label strings
        // themselves are pinned at the view-model level (Index_labels_the_result_lines_expenses_by_type_and_worker_production).
        Assert.Contains("Cobros no anulados", view, StringComparison.Ordinal);
        Assert.Contains("Gastos", view, StringComparison.Ordinal);
        Assert.Contains("Resultado del período", view, StringComparison.Ordinal);
        Assert.Contains("Período", view, StringComparison.Ordinal);
        Assert.Contains("ResultLines", view, StringComparison.Ordinal);
        Assert.Contains("ExpenseTypeLines", view, StringComparison.Ordinal);
        Assert.Contains("WorkerLines", view, StringComparison.Ordinal);
        Assert.Contains("MembershipLabel", view, StringComparison.Ordinal);
        Assert.Contains("CutoffLabel", view, StringComparison.Ordinal);
        Assert.Contains("ComparisonLines", view, StringComparison.Ordinal);
        Assert.Contains("EmptyMessage", view, StringComparison.Ordinal);

        // The inclusive inputs and the optional comparison pair have real names the GET binds.
        Assert.Contains("name=\"from\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"to\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"comparisonFrom\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"comparisonTo\"", view, StringComparison.Ordinal);
        Assert.Contains("type=\"date\"", view, StringComparison.Ordinal);

        // The provisional definition of the indicators is said in words, with the 90-day lookback explained.
        Assert.Contains("definición provisoria", ReportLabels.LapsedDefinition, StringComparison.Ordinal);
        Assert.Contains("90 días", ReportLabels.LapsedDefinition, StringComparison.Ordinal);
        Assert.Contains("definición provisoria", ReportLabels.RecurringDefinition, StringComparison.Ordinal);
        Assert.Contains("ReportLabels.LapsedDefinition", view, StringComparison.Ordinal);
        Assert.Contains("ReportLabels.RecurringDefinition", view, StringComparison.Ordinal);

        // Exactly one primary action on the screen.
        Assert.Equal(1, view.Split("btn-primary").Length - 1);
    }

    [Fact]
    public void Reports_repository_and_service_are_registered_as_scoped()
    {
        var services = BuildServices();

        var repository = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IReportsRepository));
        Assert.Equal(ServiceLifetime.Scoped, repository.Lifetime);
        Assert.Equal(typeof(SqlReportsRepository), repository.ImplementationType);

        var service = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ReportsService));
        Assert.Equal(ServiceLifetime.Scoped, service.Lifetime);
    }

    [Fact]
    public void The_reports_section_is_reachable_from_the_owner_navigation()
    {
        var item = Assert.Single(OwnerNavigation.Sections, section => section.Controller == "Reports");
        Assert.Equal("Reportes", item.Label);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------

    private static IServiceCollection BuildServices()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:WorkUp"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT19Reports;Trusted_Connection=True;TrustServerCertificate=True",
            ["WorkUp:PublicBaseUrl"] = "https://reservas.example.com",
            ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        // Registration only: no provider is built, so no key is generated and no database is touched.
        services.AddWorkUp(configuration, new FakeHostEnvironment { EnvironmentName = Environments.Development });
        return services;
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

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
