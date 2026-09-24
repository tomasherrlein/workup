using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.Areas.Owner.Models.Day;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerWalkInControllerTests
{
    [Fact]
    public async Task OwnerWalkIn_get_filters_individual_active_options_and_uses_claim_tenant()
    {
        var (controller, _, business, catalog, staff, clients) = await Create(DefaultClock());
        await catalog.InsertAsync(new Service { BusinessId = 7, Name = "Individual", IsActive = true, MaxCapacity = 1 });
        await catalog.InsertAsync(new Service { BusinessId = 7, Name = "Class", IsActive = true, MaxCapacity = 5 });
        await catalog.InsertAsync(new Service { BusinessId = 8, Name = "Other", IsActive = true, MaxCapacity = 1 });
        await staff.InsertAsync(new Worker { BusinessId = 7, FirstName = "Ana", IsActive = true });
        await staff.InsertAsync(new Worker { BusinessId = 7, FirstName = "Inactive", IsActive = false });
        await clients.InsertAsync(new Client { BusinessId = 7, FirstName = "Pat", HealthNotes = "private" });
        await clients.InsertAsync(new Client { BusinessId = 8, FirstName = "Other" });
        var view = Assert.IsType<ViewResult>(await controller.WalkIn());
        var model = Assert.IsType<WalkInViewModel>(view.Model);
        Assert.Single(model.Services);
        Assert.Single(model.Workers);
        Assert.Single(model.Clients);
        Assert.DoesNotContain("private", model.Clients[0].Name);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        business.All.Last().AllowsWalkIns = false;
        Assert.IsType<NotFoundResult>(await controller.WalkIn());
    }

    [Fact]
    public async Task OwnerWalkIn_post_redirects_to_the_saved_day_with_overlap_warning()
    {
        var (controller, repo, _, _, _, _) = await Create(new FakeClock { Now = new DateTime(2026, 3, 11, 0, 0, 1) });
        repo.Overlap = true;
        var result = await controller.WalkIn(new WalkInViewModel { ClientId = 20, ServiceId = 30, WorkerId = 40, CollectPayment = true });
        Assert.Equal(new DateOnly(2026, 3, 11), Assert.IsType<RedirectToActionResult>(result).RouteValues!["date"]);
        Assert.Equal(7, repo.Inserted!.BusinessId);
        Assert.Contains("superpone", string.Join(" ", controller.TempData.Values.OfType<string>()));
    }

    [Fact]
    public async Task OwnerWalkIn_post_redirects_to_the_saved_start_date_when_the_clock_crosses_midnight()
    {
        var (controller, repo, _, _, _, _) = await Create(new AdvancingClock(new DateTime(2026, 3, 10, 23, 59, 59, 900)));

        var result = await controller.WalkIn(new WalkInViewModel { ClientId = 20, ServiceId = 30, WorkerId = 40 });

        Assert.Equal(new DateTime(2026, 3, 10, 23, 59, 59), repo.Inserted!.StartsAt);
        Assert.Equal(new DateOnly(2026, 3, 10), Assert.IsType<RedirectToActionResult>(result).RouteValues!["date"]);
    }

    [Fact]
    public void Jornada_renders_the_at_arrival_form_inline_behind_the_business_setting()
    {
        // RF-15 note (docs/srs.md): RF-11 must be reachable from the Jornada view without leaving the screen.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var views = Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", "Day");
        var index = File.ReadAllText(Path.Combine(views, "Index.cshtml"));
        var form = File.ReadAllText(Path.Combine(views, "_WalkInForm.cshtml"));
        var page = File.ReadAllText(Path.Combine(views, "WalkIn.cshtml"));

        var gate = index.IndexOf("Model.AllowsWalkIns", StringComparison.Ordinal);
        var inline = index.IndexOf("_WalkInForm", StringComparison.Ordinal);
        Assert.True(gate >= 0, "Jornada must gate the at-arrival form on the business setting.");
        Assert.True(inline > gate, "Jornada must render the at-arrival form inline once the setting allows it.");

        Assert.Contains("asp-action=\"WalkIn\"", form);
        Assert.Contains("method=\"post\"", form);
        Assert.Contains("asp-for=\"ClientId\"", form);
        Assert.Contains("asp-validation-summary", form);
        Assert.DoesNotContain("WalkInViewModel.BusinessId", form);
        Assert.DoesNotContain("asp-for=\"BusinessId\"", form);
        Assert.DoesNotContain("StartsAt", form);

        // The standalone page is a deep link over the same shared form, not a second copy of it.
        Assert.Contains("_WalkInForm", page);
        Assert.DoesNotContain("asp-for=\"ServiceId\"", page);
    }

    [Fact]
    public async Task OwnerWalkIn_post_disabled_or_invalid_preserves_form_and_never_inserts()
    {
        var (controller, repo, business, catalog, staff, clients) = await Create(DefaultClock());
        await catalog.InsertAsync(new Service { BusinessId = 7, Name = "Individual", IsActive = true, MaxCapacity = 1 });
        await staff.InsertAsync(new Worker { BusinessId = 7, FirstName = "Ana", IsActive = true });
        await clients.InsertAsync(new Client { BusinessId = 7, FirstName = "Pat" });
        business.All.Last().AllowsWalkIns = false;
        Assert.IsType<NotFoundResult>(await controller.WalkIn(new WalkInViewModel { ClientId = 20, ServiceId = 30, WorkerId = 40 }));
        Assert.Null(repo.Inserted);
        business.All.Last().AllowsWalkIns = true;
        controller.ModelState.AddModelError("ClientId", "Elegí un cliente.");
        var model = new WalkInViewModel { ClientId = 20, ServiceId = 30, WorkerId = 40, AgreedPrice = 90m };
        var view = Assert.IsType<ViewResult>(await controller.WalkIn(model));
        Assert.Equal("Index", view.ViewName);
        var index = Assert.IsType<DayIndexViewModel>(view.Model);
        Assert.Same(model, index.WalkIn);
        Assert.StartsWith("Elegí un cliente.", controller.ModelState["ClientId"]!.Errors[0].ErrorMessage);
        Assert.Equal(90m, index.WalkIn!.AgreedPrice);
        Assert.Single(index.WalkIn.Services);
        Assert.Null(repo.Inserted);
    }

    [Fact]
    public async Task OwnerWalkIn_index_exposes_the_inline_form_only_when_the_business_allows_walk_ins()
    {
        var (controller, _, business, catalog, staff, clients) = await Create(DefaultClock());
        await catalog.InsertAsync(new Service { BusinessId = 7, Name = "Individual", IsActive = true, MaxCapacity = 1 });
        await catalog.InsertAsync(new Service { BusinessId = 7, Name = "Class", IsActive = true, MaxCapacity = 5 });
        await catalog.InsertAsync(new Service { BusinessId = 7, Name = "Inactive", IsActive = false, MaxCapacity = 1 });
        await staff.InsertAsync(new Worker { BusinessId = 7, FirstName = "Ana", IsActive = true });
        await staff.InsertAsync(new Worker { BusinessId = 7, FirstName = "Inactive", IsActive = false });
        await clients.InsertAsync(new Client { BusinessId = 7, FirstName = "Pat", HealthNotes = "private" });

        var enabled = Assert.IsType<DayIndexViewModel>(Assert.IsType<ViewResult>(await controller.Index()).Model);
        Assert.True(enabled.AllowsWalkIns);
        Assert.NotNull(enabled.WalkIn);
        Assert.Equal("Individual", Assert.Single(enabled.WalkIn!.Services).Name);
        Assert.Equal("Ana", Assert.Single(enabled.WalkIn.Workers).Name.Trim());
        Assert.Equal("Pat", Assert.Single(enabled.WalkIn.Clients).Name.Trim());

        business.All.Last().AllowsWalkIns = false;
        var disabled = Assert.IsType<DayIndexViewModel>(Assert.IsType<ViewResult>(await controller.Index()).Model);
        Assert.False(disabled.AllowsWalkIns);
        Assert.Null(disabled.WalkIn);
    }

    [Fact]
    public async Task OwnerWalkIn_index_keeps_a_historical_selected_date_while_offering_the_at_arrival_form()
    {
        var (controller, _, _, catalog, staff, clients) = await Create(DefaultClock());
        await catalog.InsertAsync(new Service { BusinessId = 7, Name = "Individual", IsActive = true, MaxCapacity = 1 });
        await staff.InsertAsync(new Worker { BusinessId = 7, FirstName = "Ana", IsActive = true });
        await clients.InsertAsync(new Client { BusinessId = 7, FirstName = "Pat" });

        var model = Assert.IsType<DayIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.Index(new DateOnly(2026, 3, 1))).Model);

        Assert.Equal(new DateOnly(2026, 3, 1), model.Day.SelectedDate);
        Assert.NotNull(model.WalkIn);
    }

    [Fact]
    public async Task OwnerWalkIn_post_service_rejection_returns_jornada_with_the_business_error()
    {
        var (controller, repo, _, catalog, staff, clients) = await Create(DefaultClock());
        await catalog.InsertAsync(new Service { BusinessId = 7, Name = "Individual", IsActive = true, MaxCapacity = 1 });
        await staff.InsertAsync(new Worker { BusinessId = 7, FirstName = "Ana", IsActive = true });
        await clients.InsertAsync(new Client { BusinessId = 7, FirstName = "Pat" });

        var view = Assert.IsType<ViewResult>(await controller.WalkIn(
            new WalkInViewModel { ClientId = 21, ServiceId = 30, WorkerId = 40 }));

        Assert.Equal("Index", view.ViewName);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState.Values.SelectMany(v => v.Errors),
            e => e.ErrorMessage.Contains("Revisá el cliente, servicio y trabajadora.", StringComparison.Ordinal));
        Assert.Null(repo.Inserted);
    }

    private static FakeClock DefaultClock() => new() { Now = new DateTime(2026, 3, 10, 10, 0, 0) };

    /// <summary>Advances one second per read so a second <see cref="IClock.Now"/> read crossing midnight is visible.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public DateTime Now => first.AddSeconds(ReadCount++);
        public int ReadCount { get; private set; }
    }

    private static async Task<(DayController, FakeOwnerWalkInRepository, FakeBusinessRepository, FakeServiceRepository, FakeWorkerRepository, FakeClientRepository)> Create(IClock clock)
    {
        var business = new FakeBusinessRepository();
        for (var i = 0; i < 6; i++) await business.InsertAsync(new Business());
        await business.InsertAsync(new Business { AllowsWalkIns = true });
        var catalog = new FakeServiceRepository();
        var staff = new FakeWorkerRepository();
        var clients = new FakeClientRepository();
        var repo = new FakeOwnerWalkInRepository();
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkUpClaims.BusinessId, "7")], "test")) };
        var controller = new DayController(new OwnerDayService(new FakeOwnerDayRepository(), clock),
            new OwnerDayActionsService(new FakeOwnerDayActionsRepository(), new FakeUnitOfWork(), clock), clock,
            new OwnerWalkInService(repo, new FakeUnitOfWork(), clock), new BusinessSettingsService(business),
            new CatalogService(catalog), new StaffService(staff, new FakeUnitOfWork()),
            new ClientsService(clients, new FakeClientNoteRepository(), clock))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
        return (controller, repo, business, catalog, staff, clients);
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
