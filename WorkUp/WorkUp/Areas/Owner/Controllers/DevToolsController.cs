using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>
/// Herramientas de prueba → visor de emails generados (T26, plan assumptions 10 and 11). The owner-only, read-only
/// page over <see cref="INotificationViewerRepository"/>: the list of the queued notices of the signed-in business and
/// the one-row detail with its body.
/// <para>
/// <b>Fail closed.</b> Every action consults the injected <see cref="DevToolsGate"/> before anything else and answers
/// <see cref="ControllerBase.NotFound()"/> when it is disabled, without touching the repository, so a direct URL to
/// either page is refused in production, in a real SMTP installation or with the feature flag off. The gate is
/// necessary but not sufficient: the controller inherits the owner cookie requirement from
/// <see cref="OwnerControllerBase"/>, and has no anonymous action.
/// </para>
/// <para>
/// <b>Sole tenant source.</b> The business is read from the owner cookie claim (<see cref="OwnerControllerBase.BusinessId"/>)
/// and from nowhere else: the only inputs are the keyset cursor and the notice identity, so no request value and no
/// recipient address can widen the read. Invalid, foreign and missing notices are all answered with the same 404 and
/// never distinguished.
/// </para>
/// <para>
/// <b>No caching, no referrer.</b> A body can embed a single-use private link or a reset token, so both pages are
/// marked <c>no-store</c> and send <c>Referrer-Policy: no-referrer</c>: a shared machine cannot re-show them from
/// history and no downstream request learns which notice was open. This controller never writes and offers no manual
/// intervention or retry yet.
/// </para>
/// </summary>
public sealed class DevToolsController(DevToolsGate gate, INotificationViewerRepository viewer) : OwnerControllerBase
{
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

        var detail = await viewer.GetDetailAsync(BusinessId, id, ct);
        return detail is null ? NotFound() : View(detail);
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
