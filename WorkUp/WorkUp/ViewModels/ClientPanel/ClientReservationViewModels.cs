namespace WorkUp.ViewModels.ClientPanel;

/// <summary>
/// One active individual service of the authenticated client's own business offered on the reservation screen
/// (T21.5, RF-07). Only the public booking data travels: name, price and duration. It never carries the service's
/// tenant or any administrative flag, because the panel only ever offers the catalogue of the cookie's business.
/// </summary>
public sealed record ClientReservationServiceOption(int Id, string Name, decimal Price, int DurationMinutes);

/// <summary>One active worker of the same business offered for the individual reservation (T21.5).</summary>
public sealed record ClientReservationWorkerOption(int Id, string FullName);

/// <summary>
/// The chosen service, worker, day and time of a client-initiated individual reservation (T21.5, RF-07). These are
/// the only values the POST carries: the tenant and the ficha always come from the client session cookie, so a
/// request can neither retarget another business nor another ficha, and it never carries an email, a name or any
/// other identity field. Every field is optional in the type because a form can arrive empty; the controller adds
/// the safe field messages before it re-renders.
/// </summary>
public sealed class ClientIndividualReservationInput
{
    public int ServiceId { get; set; }

    public int WorkerId { get; set; }

    public DateOnly? Date { get; set; }

    public TimeOnly? StartTime { get; set; }
}

/// <summary>
/// The individual reservation screen (T21.5, RF-07): the active services and workers of the cookie's business, the
/// resolved day and the free fifteen-minute starts the service offers for that worker and day. It carries only the
/// safe public catalogue plus the typed selection; it never carries a private link token, an owner note, a health
/// observation or any client or business identity. The form selection is the typed <see cref="Input"/>, which is the
/// same instance rebuilt with the fresh options after a refused save.
/// </summary>
public sealed record ClientIndividualReservationViewModel(
    string Slug,
    IReadOnlyList<ClientReservationServiceOption> Services,
    IReadOnlyList<ClientReservationWorkerOption> Workers,
    DateOnly Date,
    IReadOnlyList<TimeOnly> AvailableStartTimes,
    ClientIndividualReservationInput Input,
    DateTime? NearestAlternative)
{
    public bool HasServices => Services.Count > 0;

    public bool HasWorkers => Workers.Count > 0;

    public bool HasSlots => AvailableStartTimes.Count > 0;

    public const string NoServicesMessage = "Este negocio no tiene servicios individuales disponibles para reservar.";

    public const string NoWorkersMessage = "No hay trabajadoras disponibles para ese servicio.";

    public const string NoSlotsMessage = "No hay horarios disponibles para ese día.";
}

/// <summary>
/// One bookable drop-in class of the authenticated client's own business (T21.5, RF-22). The business service already
/// dropped the suspended, started and full sessions, so everything here can be booked; the class and teacher names
/// are resolved by the controller from the ids the offer carries, because the panel never receives entities.
/// </summary>
public sealed record ClientReservationClassOption(
    int AppointmentId,
    DateOnly Date,
    TimeOnly StartTime,
    string ServiceName,
    string TeacherName,
    int RemainingPlaces);

/// <summary>
/// The drop-in class screen of the panel (T21.5, RF-22): the future sessions with a free place and their names. The
/// POST carries the appointment id in its route, so this model has no mutable field at all: neither a business nor a
/// client id, an email, a name or any token can be posted through it.
/// </summary>
public sealed record ClientClassReservationViewModel(
    string Slug,
    IReadOnlyList<ClientReservationClassOption> Sessions)
{
    public bool HasSessions => Sessions.Count > 0;

    public const string EmptyMessage = "No hay clases sueltas con lugares disponibles por ahora.";
}
