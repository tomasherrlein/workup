using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure;

/// <summary>
/// Mode-truthful copy about what the configured email mode really does (T23). The two modes are not the same promise:
/// <see cref="OutboxMode.Retain"/> keeps every notice <c>Pending</c> without sending it, and
/// <see cref="OutboxMode.Deliver"/> only queues it for the scheduled dispatch, which cannot promise that the message
/// reaches the mailbox. Every sentence here is a constant selected by the parsed mode, so no configuration value is
/// ever rendered, and <see cref="ResolveMode"/> uses the same fail-fast parser as the startup.
/// <para>
/// The views call these helpers and let Razor encode the result, so a business name with HTML is escaped once by the
/// view engine and never by this class. The private link of a booking is still only ever sent by email: no sentence
/// here writes it into a public page (RF-07).
/// </para>
/// </summary>
public static class EmailDeliveryCopy
{
    /// <summary>
    /// Parses <c>WorkUp:Email:Mode</c> with the same fail-fast parser as the startup and the controllers, so a view
    /// renders exactly the mode the dispatcher will use and a typo such as <c>Smpt</c> throws instead of silently
    /// reading as Outbox.
    /// </summary>
    public static OutboxMode ResolveMode(string? configuredMode) => OutboxModeParser.Parse(configuredMode);

    /// <summary>
    /// Test-version banner. It no longer promises the T26 viewer page, which is not built yet, and it states what the
    /// mode actually does with a queued notice.
    /// </summary>
    public static string DevToolsBanner(OutboxMode mode) => mode == OutboxMode.Deliver
        ? "Versión de prueba: los emails se encolan para su envío automático; no podemos garantizar que lleguen a la casilla."
        : "Versión de prueba: el envío automático de emails no está activo; los emails quedan retenidos sin enviarse.";

    /// <summary>Success notice of the individual booking confirmation.</summary>
    public static string BookingConfirmationNotice(OutboxMode mode) => mode == OutboxMode.Deliver
        ? "La reserva quedó pendiente. El email con los detalles y el enlace privado de este turno queda en cola para " +
          "su envío automático; no podemos garantizar que llegue a tu casilla."
        : "La reserva quedó pendiente. El email con los detalles y el enlace privado de este turno queda retenido sin " +
          "enviarse: el envío automático de emails no está activo en esta versión.";

    /// <summary>
    /// Hint under the individual booking confirmation. The private link only travels by email, so this states what
    /// really happens to it instead of asking the person to check an inbox that will stay empty in Outbox mode.
    /// </summary>
    public static string BookingConfirmationHint(OutboxMode mode) => mode == OutboxMode.Deliver
        ? "El enlace privado del turno queda en cola para su envío automático; no podemos garantizar que llegue a tu " +
          "casilla, así que guardalo cuando lo recibas."
        : "El enlace privado del turno se entrega por email, pero el envío automático de emails no está activo en esta " +
          "versión, así que todavía no aparece en tu casilla.";

    /// <summary>Success notice of the drop-in class confirmation.</summary>
    public static string DropInConfirmationNotice(OutboxMode mode) => mode == OutboxMode.Deliver
        ? "Tu lugar en la clase quedó reservado. El email con los detalles queda en cola para su envío automático; no " +
          "podemos garantizar que llegue a tu casilla."
        : "Tu lugar en la clase quedó reservado. El email con los detalles queda retenido sin enviarse: el envío " +
          "automático de emails no está activo en esta versión.";

    /// <summary>
    /// Pre-submit explanation of the password recovery form. It keeps the conditional "si tenés una cuenta" (RF-27), so
    /// it never reveals whether an email has an account, and it states what happens to the link per mode.
    /// </summary>
    public static string RecoverPrompt(OutboxMode mode, string? businessName) => mode == OutboxMode.Deliver
        ? $"Escribí tu email y, si tenés una cuenta en {businessName}, el enlace para elegir una contraseña nueva queda " +
          "en cola para su envío automático; no podemos garantizar que llegue a tu casilla."
        : $"Escribí tu email y, si tenés una cuenta en {businessName}, el enlace para elegir una contraseña nueva queda " +
          "retenido sin enviarse: el envío automático de emails no está activo en esta versión.";

    /// <summary>
    /// Pre-submit explanation of the confirmation resend form. It keeps the conditional "Si tu cuenta todavía no está
    /// confirmada" (RF-27), so it never confirms that an account exists, and it states what happens to the link per
    /// mode.
    /// </summary>
    public static string ResendPrompt(OutboxMode mode, string? businessName) => mode == OutboxMode.Deliver
        ? $"Si tu cuenta todavía no está confirmada en {businessName}, otro enlace de confirmación queda en cola para " +
          "su envío automático; no podemos garantizar que llegue a tu casilla."
        : $"Si tu cuenta todavía no está confirmada en {businessName}, otro enlace de confirmación queda retenido sin " +
          "enviarse: el envío automático de emails no está activo en esta versión.";
}
