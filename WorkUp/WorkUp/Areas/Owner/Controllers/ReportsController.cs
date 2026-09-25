using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Reports;
using WorkUp.Infrastructure;
using WorkUp.ViewModels.Reports;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>
/// Reportes (RF-16, D-20, assumption 7): the result of a period and the business indicators, with an optional
/// comparison. Every rule, every clock read and every bucket belongs to <see cref="ReportsService"/>: this controller
/// routes, resolves the default period, validates the shape of the request and maps the stable error codes to
/// Spanish. The screen is a read, scoped to the signed-in business, so it never opens a transaction and never shows a
/// single row of another business.
/// </summary>
public sealed class ReportsController(ReportsService reports, IClock clock) : OwnerControllerBase
{
    /// <summary>
    /// The report of one inclusive period (RF-16). Without any input it opens on the current month through today; a
    /// half-filled range is an input error that is surfaced instead of guessed. The comparison pair is optional and,
    /// when present, is computed with the same rules and the same clipping. A period that has not started is refused;
    /// one that ends after today is computed up to today and shown as partial. Reading a report writes nothing.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(
        DateOnly? from,
        DateOnly? to,
        DateOnly? comparisonFrom,
        DateOnly? comparisonTo,
        CancellationToken ct)
    {
        ViewData["Title"] = "Reportes";
        SetPrivateHeaders();

        var today = clock.Today;

        // The default period is the current month through today: an inclusive range with both ends, so the form always
        // shows what was computed. A range with only one end is the owner's mistake and is reported, never completed
        // with a date she did not choose.
        var hasPeriodInput = from is not null || to is not null;
        if (hasPeriodInput && (from is null || to is null))
        {
            this.Flash("Indicá las fechas del período.", FlashType.Error);
            return Render(null, null, from, to, comparisonFrom, comparisonTo, today);
        }

        var period = hasPeriodInput
            ? new ReportPeriod(from!.Value, to!.Value)
            : new ReportPeriod(new DateOnly(today.Year, today.Month, 1), today);

        // The comparison is optional; the two ends are a pair, so one without the other is reported.
        ReportPeriod? comparison = null;
        if (comparisonFrom is not null || comparisonTo is not null)
        {
            if (comparisonFrom is null || comparisonTo is null)
            {
                this.Flash("Indicá las dos fechas de la comparación.", FlashType.Error);
                return Render(null, null, from, to, comparisonFrom, comparisonTo, today);
            }

            comparison = new ReportPeriod(comparisonFrom.Value, comparisonTo.Value);
        }

        var result = await reports.GetAsync(BusinessId, new ReportQuery(period, comparison), ct);
        if (!result.Succeeded)
        {
            // An impossible period is rejected by the service before any read; the screen keeps the filters so the
            // owner can correct it and never renders a report of a period that was refused.
            this.Flash(ErrorMessage(result.Error!), FlashType.Error);
            return Render(null, null, from, to, comparisonFrom, comparisonTo, today);
        }

        return Render(
            new ReportsPeriodViewModel(result.Value.Period),
            result.Value.Comparison is null ? null : new ReportsPeriodViewModel(result.Value.Comparison),
            period.From,
            period.To,
            comparisonFrom,
            comparisonTo,
            today);
    }

    /// <summary>
    /// Builds the page. On success the form echoes the resolved period; on a refusal it echoes exactly what the owner
    /// sent, so nothing is silently invented on screen.
    /// </summary>
    private ViewResult Render(
        ReportsPeriodViewModel? period,
        ReportsPeriodViewModel? comparison,
        DateOnly? from,
        DateOnly? to,
        DateOnly? comparisonFrom,
        DateOnly? comparisonTo,
        DateOnly today) => View(new ReportsIndexViewModel
        {
            From = from,
            To = to,
            ComparisonFrom = comparisonFrom,
            ComparisonTo = comparisonTo,
            Period = period,
            Comparison = comparison,
            Today = today
        });

    /// <summary>
    /// Turns the stable code of a refused report into the sentence the owner reads. The inverted range is pinned here
    /// — the missing one never reaches the service — and an unrecognized code falls back to the service's own Spanish
    /// message instead of leaking the code.
    /// </summary>
    private static string ErrorMessage(BusinessError error) => error.Code switch
    {
        ReportsService.InvalidBusinessCode => "No pudimos identificar el negocio.",
        ReportsService.PeriodInFutureCode => "El período todavía no empezó: no hay nada que calcular.",
        ReportsService.InvalidPeriodCode => "La fecha desde no puede ser posterior a la fecha hasta.",
        _ => error.Message
    };

    private void SetPrivateHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
