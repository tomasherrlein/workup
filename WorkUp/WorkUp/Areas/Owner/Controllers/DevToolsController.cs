using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Jobs;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>
/// Herramientas de prueba → visor de emails generados y ejecución manual de tareas (T26, plan assumptions 10 and 11).
/// The owner-only, read-only page over <see cref="INotificationViewerRepository"/> plus the one write the tools allow:
/// running a single registered scheduled job by its stable key under the singleton <see cref="ScheduledExecutionGate"/>.
/// <para>
/// <b>Fail closed before activation.</b> Every action consults the injected <see cref="DevToolsGate"/> before resolving
/// anything else and answers <see cref="ControllerBase.NotFound()"/> when it is disabled. The collaborators are
/// resolved from the scoped <see cref="IServiceProvider"/> only <em>after</em> that check, so a closed gate activates
/// no viewer, no runner and no execution lease, and a future failure while building the job graph cannot turn the
/// refusal into a 500. The provider is a narrow, deliberate exception to constructor injection, justified by that
/// ordering: injecting the viewer, the runner or the lease would activate them before the controller body runs.
/// </para>
/// <para>
/// <b>Sole tenant source.</b> The business is read from the owner cookie claim (<see cref="OwnerControllerBase.BusinessId"/)
/// and from nowhere else: the only inputs are the keyset cursor, the notice identity and the stable job key, so no
/// request value and no recipient address can widen the read. Invalid, foreign and missing notices are all answered
/// with the same 404 and never distinguished.
/// </para>
/// <para>
/// <b>Manual run.</b> The run action takes only a stable job key: an unknown key is a fixed generic 404 that never runs
/// anything and never echoes the key, a busy lease is a fixed "in progress" message, a failure is a fixed safe message
/// and a success is a fixed completion. All three outcomes are Post/Redirect/Get with fixed, content-free text, so a
/// provider error, a password or a single-use token can never reach the page. Cancellation is not a job failure and is
/// rethrown, and the job graph only opens in Retain mode, so running the <c>outbox</c> key here reaches no provider.
/// </para>
/// <para>
/// <b>No caching, no referrer.</b> A body can embed a single-use private link or a reset token, so both pages are
/// marked <c>no-store</c> and send <c>Referrer-Policy: no-referrer</c>: a shared machine cannot re-show them from
/// history and no downstream request learns which notice was open.
/// </para>
/// </summary>
public sealed class DevToolsController(DevToolsGate gate, IServiceProvider services) : OwnerControllerBase
{
    /// <summary>Fixed, content-free message flashed after a manual run completes.</summary>
    public const string RunCompletedMessage = "La tarea se ejecutó correctamente";

    /// <summary>Fixed, content-free message flashed when the execution lease is already held by another run.</summary>
    public const string RunBusyMessage = "Hay una tarea en curso";

    /// <summary>Fixed, non-echoing message flashed when the manual run failed.</summary>
    public const string RunFailedMessage = "No se pudo ejecutar la tarea";

    /// <summary>Key of the <c>ViewData</c> entry that carries the stable job keys to the list.</summary>
    public const string JobKeysViewDataKey = "JobKeys";

    /// <summary>
    /// One bounded, newest-first keyset page of the business's generated notices. The body is deliberately absent: the
    /// list renders many rows at once, and the body belongs to the one-row detail. Without a cursor it opens on the
    /// newest row; the optional <paramref name="beforeId"/> is the identity of the last row of the previous page.
    /// </summary>
    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Index(int? beforeId, CancellationToken ct)
    {
        SetPrivateHeaders();
        if (!gate.IsEnabled)
            return NotFound();

        ViewData["Title"] = "Avisos generados";

        // Resolved only after the gate accepted the request. The stable keys come straight from the runner, so the
        // manual form can only ever offer a job that is really registered.
        var jobs = services.GetRequiredService<ScheduledJobRunner>();
        ViewData[JobKeysViewDataKey] = jobs.Keys;

        var viewer = services.GetRequiredService<INotificationViewerRepository>();
        var items = await viewer.ListRecentAsync(BusinessId, beforeId, NotificationViewerBounds.MaxPageSize, ct);
        return View(items);
    }

    /// <summary>
    /// The one notice with its body, only when it belongs to the signed-in business. A missing, foreign or non-positive
    /// identity is answered with the same 404, so the page can never confirm that a notice of another business exists.
    /// </summary>
    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        SetPrivateHeaders();
        if (!gate.IsEnabled)
            return NotFound();

        ViewData["Title"] = "Aviso generado";

        // Only the read adapter is resolved: the one-row detail never activates the job graph or the execution lease.
        var viewer = services.GetRequiredService<INotificationViewerRepository>();
        var detail = await viewer.GetDetailAsync(BusinessId, id, ct);
        return detail is null ? NotFound() : View(detail);
    }

    /// <summary>
    /// Runs exactly one registered scheduled job by its stable key, under the singleton execution lease. The gate runs
    /// first, so a disabled tool is a 404 before the runner or the lease is even resolved. An unknown key is the same
    /// fixed generic 404 and never reaches a job or echoes the key; a lease already held by a tick or another manual run
    /// is the fixed "in progress" message; a job failure is the fixed safe message; a success is the fixed completion.
    /// All three outcomes are Post/Redirect/Get with content-free text, so no exception, password or one-use token can
    /// reach the page. Cancellation is an aborted request, not a job failure, and propagates unchanged.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunJob(string key, CancellationToken ct)
    {
        SetPrivateHeaders();
        if (!gate.IsEnabled)
            return NotFound();

        try
        {
            // Resolved only after the gate, and only for this action: the viewer is not part of a manual run, and a
            // resolution failure is reported with the same fixed text as a job failure instead of leaking a message.
            var jobs = services.GetRequiredService<ScheduledJobRunner>();
            var execution = services.GetRequiredService<ScheduledExecutionGate>();
            var outcome = await execution.TryRunOneAsync(jobs, key, ct);
            return outcome switch
            {
                ScheduledRunOutcome.UnknownKey => NotFound(),
                ScheduledRunOutcome.Busy => RedirectWithRunMessage(RunBusyMessage, FlashType.Warning),
                _ => RedirectWithRunMessage(RunCompletedMessage)
            };
        }
        catch (OperationCanceledException)
        {
            // The request was aborted: it is not a job failure and must not be reported as one.
            throw;
        }
        catch (Exception)
        {
            // Only fixed, content-free text: a provider error can carry a password and a job can touch a token.
            return RedirectWithRunMessage(RunFailedMessage, FlashType.Error);
        }
    }

    /// <summary>Flashes one fixed message for the next request and redirects to the list (Post/Redirect/Get).</summary>
    private IActionResult RedirectWithRunMessage(string message, FlashType type = FlashType.Success)
    {
        this.Flash(message, type);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Spanish label of a dispatch status. The labels are presentation, not policy, so they live next to the page that
    /// renders them; an unknown value falls back to its own name instead of guessing a state.
    /// </summary>
    public static string StatusLabel(NotificationStatus status) => status switch
    {
        NotificationStatus.Pending => "Pendiente",
        NotificationStatus.Sent => "Enviado",
        NotificationStatus.Failed => "Falló",
        NotificationStatus.Dispatching => "Enviando",
        NotificationStatus.ReviewRequired => "Requiere revisión",
        _ => status.ToString()
    };

    /// <summary>
    /// Spanish label of a notice type. Same rule as <see cref="StatusLabel"/>: a value without a label shows its own
    /// name rather than being renamed into a type it is not.
    /// </summary>
    public static string TypeLabel(NotificationType type) => type switch
    {
        NotificationType.BookingCreated => "Reserva creada",
        NotificationType.AppointmentReminder => "Recordatorio de turno",
        NotificationType.DropInReminder => "Recordatorio de clase suelta",
        NotificationType.DropInBooked => "Lugar en clase suelta",
        NotificationType.ClassSuspended => "Clase suspendida",
        NotificationType.Rescheduled => "Turno reprogramado",
        NotificationType.MembershipExpiring => "Abono por vencer",
        NotificationType.MembershipExpired => "Abono vencido",
        NotificationType.DailySummary => "Resumen diario",
        NotificationType.StockReview => "Revisión de insumos",
        NotificationType.EmailVerification => "Verificación de email",
        NotificationType.PasswordReset => "Recuperación de contraseña",
        NotificationType.AccountInvitation => "Invitación de cuenta",
        _ => type.ToString()
    };

    /// <summary>
    /// Marks both pages as uncacheable and strips the referrer. It matches the manual headers the other owner read
    /// screens set, backed by the <see cref="ResponseCacheAttribute"/> on each action.
    /// </summary>
    private void SetPrivateHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
