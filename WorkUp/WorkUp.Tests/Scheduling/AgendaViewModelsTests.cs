using WorkUp.Areas.Owner.Models.Agenda;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// Covers the query/filter logic behind the agenda's filter form, its removable chips and "Limpiar filtros": every
/// link on that page is built from <see cref="AgendaIndexViewModel.Query"/> and <see cref="AgendaIndexViewModel.HasFilters"/>.
/// </summary>
public sealed class AgendaViewModelsTests
{
    private static readonly DateOnly SelectedDate = new(2026, 9, 22);

    [Fact]
    public void HasFilters_is_false_with_no_worker_status_or_origin_selected()
    {
        var model = CreateModel(workerId: null, status: null, origin: null);

        Assert.False(model.HasFilters);
    }

    [Theory]
    [InlineData(4, null, null)]
    [InlineData(null, BookingStatus.Pending, null)]
    [InlineData(null, null, AppointmentOrigin.WalkIn)]
    public void HasFilters_is_true_when_any_single_filter_is_selected(
        int? workerId, BookingStatus? status, AppointmentOrigin? origin)
    {
        var model = CreateModel(workerId, status, origin);

        Assert.True(model.HasFilters);
    }

    [Fact]
    public void Query_keeps_the_selected_date_period_and_every_active_filter_by_default()
    {
        var model = CreateModel(workerId: 4, status: BookingStatus.Pending, origin: AppointmentOrigin.Reserved);

        var query = model.Query();

        Assert.Equal("2026-09-22", query["date"]);
        Assert.Equal("Day", query["period"]);
        Assert.Equal("4", query["workerId"]);
        Assert.Equal("Pending", query["status"]);
        Assert.Equal("Reserved", query["origin"]);
    }

    [Fact]
    public void Query_overrides_only_the_requested_date_and_period_and_keeps_filters()
    {
        var model = CreateModel(workerId: 4, status: BookingStatus.Pending, origin: AppointmentOrigin.Reserved);
        var nextDate = new DateOnly(2026, 9, 29);

        var query = model.Query(nextDate, OwnerAgendaPeriod.Week);

        Assert.Equal("2026-09-29", query["date"]);
        Assert.Equal("Week", query["period"]);
        Assert.Equal("4", query["workerId"]);
        Assert.Equal("Pending", query["status"]);
        Assert.Equal("Reserved", query["origin"]);
    }

    [Fact]
    public void Removable_worker_chip_drops_only_the_worker_filter()
    {
        var model = CreateModel(workerId: 4, status: BookingStatus.Pending, origin: AppointmentOrigin.Reserved);

        var query = model.Query(keepWorker: false);

        Assert.False(query.ContainsKey("workerId"));
        Assert.Equal("Pending", query["status"]);
        Assert.Equal("Reserved", query["origin"]);
    }

    [Fact]
    public void Removable_status_chip_drops_only_the_status_filter()
    {
        var model = CreateModel(workerId: 4, status: BookingStatus.Pending, origin: AppointmentOrigin.Reserved);

        var query = model.Query(keepStatus: false);

        Assert.Equal("4", query["workerId"]);
        Assert.False(query.ContainsKey("status"));
        Assert.Equal("Reserved", query["origin"]);
    }

    [Fact]
    public void Removable_origin_chip_drops_only_the_origin_filter()
    {
        var model = CreateModel(workerId: 4, status: BookingStatus.Pending, origin: AppointmentOrigin.Reserved);

        var query = model.Query(keepOrigin: false);

        Assert.Equal("4", query["workerId"]);
        Assert.Equal("Pending", query["status"]);
        Assert.False(query.ContainsKey("origin"));
    }

    [Fact]
    public void Limpiar_filtros_drops_every_filter_and_keeps_the_current_date_and_period()
    {
        var model = CreateModel(workerId: 4, status: BookingStatus.Pending, origin: AppointmentOrigin.Reserved);

        var query = model.Query(keepWorker: false, keepStatus: false, keepOrigin: false);

        Assert.Equal("2026-09-22", query["date"]);
        Assert.Equal("Day", query["period"]);
        Assert.False(query.ContainsKey("workerId"));
        Assert.False(query.ContainsKey("status"));
        Assert.False(query.ContainsKey("origin"));
    }

    [Fact]
    public void WorkerName_falls_back_to_a_neutral_label_when_the_filtered_worker_no_longer_matches()
    {
        var agenda = new OwnerAgendaResult(SelectedDate, SelectedDate, SelectedDate.AddDays(1),
            [new OwnerAgendaWorkerOption(4, "Ana Demo")], []);
        var stale = new AgendaIndexViewModel(agenda, OwnerAgendaPeriod.Day, WorkerId: 99, null, null);
        var active = new AgendaIndexViewModel(agenda, OwnerAgendaPeriod.Day, WorkerId: 4, null, null);

        Assert.Equal("Trabajadora no disponible", stale.WorkerName);
        Assert.Equal("Ana Demo", active.WorkerName);
    }

    private static AgendaIndexViewModel CreateModel(int? workerId, BookingStatus? status, AppointmentOrigin? origin)
    {
        var agenda = new OwnerAgendaResult(SelectedDate, SelectedDate, SelectedDate.AddDays(1),
            [new OwnerAgendaWorkerOption(4, "Ana Demo")], []);
        return new AgendaIndexViewModel(agenda, OwnerAgendaPeriod.Day, workerId, status, origin);
    }
}
