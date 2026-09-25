using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.ViewModels.Shared;

namespace WorkUp.ViewModels.Enrollments;

/// <summary>
/// One filter of the membership list (RF-29) rendered as an option of the filter select, with its Spanish label and
/// its selected state so the same page can show every view without duplicating the vocabulary.
/// </summary>
public sealed record EnrollmentFilterOptionViewModel(EnrollmentListFilter Filter, string Label, bool IsSelected);

/// <summary>
/// One client of the business the owner can enroll. The list comes from the business's own clients, so the picker can
/// never name a client of another tenant.
/// </summary>
public sealed record EnrollmentClientOptionViewModel(int Id, string Name);

/// <summary>
/// One weekly frequency with a configured membership tariff (plan assumption 4). A frequency without a price is not
/// offered when enrolling, so the option carries the tariff the owner reads next to the frequency.
/// </summary>
public sealed record EnrollmentFrequencyOptionViewModel(int Frequency, string Label, decimal Price);

/// <summary>
/// One grid class the owner may pick as a fixed class (RF-05 step 2, D-16): an active grid class of an active
/// class-type service, with the free permanent seats it still has. Only grid classes with a seat left are offered,
/// because a full one can only be rejected; the service is still the guard.
/// </summary>
public sealed record EnrollmentGridClassOptionViewModel(int GridClassId, string Label, int FreeSeats);

/// <summary>
/// One row of the membership list (RF-29). Every state is derived by the service and never persisted (D-23): the row
/// shows the client, the weekly frequency, the derived state as a word plus an icon, the expiry the owner has to
/// resolve, and whether the enrollment is a pending-payment registration. Annulled and terminated enrollments keep
/// their own label and offer no action.
/// <para>
/// <see cref="CanCharge"/>, <see cref="CanAnnul"/> and <see cref="CanTerminate"/> are the courtesy filters of the
/// screen, not the guards: the service still enforces every rule under its own lock.
/// </para>
/// </summary>
public sealed record EnrollmentRowViewModel(EnrollmentListItem Item)
{
    public int EnrollmentId => Item.EnrollmentId;

    /// <summary>The name the owner reads; falls back so a row with an unresolved client never shows an empty label.</summary>
    public string ClientName => string.IsNullOrWhiteSpace(Item.ClientName) ? "Alumna" : Item.ClientName;

    /// <summary><c>3 veces por semana</c>, the phrasing the membership prices screen already uses.</summary>
    public string FrequencyLabel => Item.WeeklyFrequency == 1
        ? "1 vez por semana"
        : $"{Item.WeeklyFrequency} veces por semana";

    public DateOnly? CurrentPeriodEnd => Item.CurrentPeriodEnd;

    public MembershipState MembershipState => Item.MembershipState;

    public bool PendingPayment => Item.PendingPayment;

    public bool ExpiresSoon => Item.ExpiresSoon;

    /// <summary>Whether the membership lapsed: it still holds its places but no period covers the reference day.</summary>
    public bool IsLapsed => Item.Status == EnrollmentStatus.Active && Item.MembershipState == MembershipState.Expired;

    /// <summary>
    /// The state as a word plus an icon (The Word-First Rule); never colour-only meaning. Annulled and terminated
    /// keep their own label; an active membership shows its derived calendar state, so an expired one reads
    /// "Pendiente de renovación" (D-23) and a pending one reads "Pendiente de pago".
    /// </summary>
    public StatusTag StateTag => Item.Status switch
    {
        EnrollmentStatus.Annulled => StatusTag.Annulled,
        EnrollmentStatus.Terminated => new StatusTag("Dada de baja", "tag-cancelled", "cross"),
        EnrollmentStatus.PendingPayment => StatusTag.PendingPayment,
        EnrollmentStatus.Active => Item.MembershipState switch
        {
            MembershipState.Current => StatusTag.For(MembershipState.Current),
            MembershipState.Expired => StatusTag.PendingRenewal,
            _ => StatusTag.For(MembershipState.None)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(Item), Item.Status, null)
    };

    /// <summary>Charging activates a pending enrollment and renews an active one; an ended one is never charged.</summary>
    public bool CanCharge => Item.Status is EnrollmentStatus.PendingPayment or EnrollmentStatus.Active;

    /// <summary>Only a pending-payment enrollment can be annulled (RF-20).</summary>
    public bool CanAnnul => Item.Status == EnrollmentStatus.PendingPayment;

    /// <summary>A membership that still holds places can be ended from a chosen date (RF-29 step 6).</summary>
    public bool CanTerminate => Item.Status is EnrollmentStatus.PendingPayment or EnrollmentStatus.Active;

    /// <summary>
    /// A membership that still holds places can move one of its fixed classes to another weekly slot (RF-21). It is a
    /// courtesy filter for the list: the service still validates the status, the seat and the effective date under its
    /// own lock.
    /// </summary>
    public bool CanChangeSchedule => Item.Status is EnrollmentStatus.PendingPayment or EnrollmentStatus.Active;
}

/// <summary>
/// One current open fixed class of the enrollment being changed (RF-21): the weekly slot that moves to another class.
/// <see cref="Label"/> already carries the day, the time and the name the owner reads, resolved by the controller.
/// </summary>
public sealed record ChangeScheduleCurrentOptionViewModel(int GridClassId, string Label);

/// <summary>
/// Guided schedule-change form (RF-21, D-19). The owner picks which current fixed class changes, the new weekly class
/// from the grid and the first day the change is valid; everything before that date keeps the old schedule.
/// <para>
/// <see cref="ClientName"/>, <see cref="CurrentClasses"/> and <see cref="GridClasses"/> are filled by the controller
/// only after the enrollment was confirmed as this business's and still changeable, so a stale, missing or foreign id
/// never exposes a name or an option. The seat the picker shows is a courtesy: the service revalidates capacity and
/// status inside its own transaction and is the only guard.
/// </para>
/// </summary>
public sealed class ChangeScheduleFormViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "No pudimos identificar la inscripción.")]
    public int EnrollmentId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Elegí el horario fijo que querés cambiar.")]
    [Display(Name = "Horario fijo actual")]
    public int CurrentGridClassId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Elegí el nuevo horario de la grilla.")]
    [Display(Name = "Nuevo horario fijo")]
    public int NewGridClassId { get; set; }

    [Required(ErrorMessage = "Elegí la fecha de vigencia.")]
    [Display(Name = "Vigente desde")]
    public DateOnly? EffectiveFrom { get; set; }

    /// <summary>The name of the student the enrollment belongs to; empty until the controller resolved it.</summary>
    public string ClientName { get; set; } = "";

    /// <summary>Populated by the controller: the open fixed classes of this enrollment, with day, time and name.</summary>
    public List<ChangeScheduleCurrentOptionViewModel> CurrentClasses { get; set; } = [];

    /// <summary>Populated by the controller: the other grid classes with a free permanent seat, offered as the new slot.</summary>
    public List<EnrollmentGridClassOptionViewModel> GridClasses { get; set; } = [];

    /// <summary>Business day, used as the minimum (and first-render default) of the effective-date input.</summary>
    public DateOnly Today { get; set; }

    public bool HasCurrentClasses => CurrentClasses.Count > 0;

    public bool HasGridClasses => GridClasses.Count > 0;

    /// <summary>Without a current class to move and another slot to move it to, the form can only be rejected.</summary>
    public bool CanChange => HasCurrentClasses && HasGridClasses;
}

/// <summary>
/// Membership list page (RF-29): the active filter, every filter with its label, the rows of the selected view and the
/// business day the screen judges the choosable dates against. Read-only: the page opens no transaction and writes
/// nothing.
/// </summary>
public sealed class EnrollmentIndexViewModel
{
    public EnrollmentListFilter Filter { get; init; }

    public IReadOnlyList<EnrollmentFilterOptionViewModel> Filters { get; init; } = [];

    public IReadOnlyList<EnrollmentRowViewModel> Items { get; init; } = [];

    /// <summary>Business day, used as the default effective date of the terminate form of every row.</summary>
    public DateOnly Today { get; init; }

    public bool HasItems => Items.Count > 0;

    /// <summary>Empty-state copy that names the view the owner is looking at.</summary>
    public string EmptyMessage => Filter switch
    {
        EnrollmentListFilter.Current => "No hay abonos vigentes.",
        EnrollmentListFilter.ExpiringSoon => "No hay abonos que vencen en los próximos días.",
        EnrollmentListFilter.Expired => "No hay abonos vencidos.",
        EnrollmentListFilter.PendingPayment => "No hay inscripciones pendientes de pago.",
        _ => "Todavía no hay abonos. Inscribí a la primera alumna para empezar."
    };
}

/// <summary>
/// Create-enrollment form (RF-05 steps 1-3). The weekly frequency and the price defaults come from the configured
/// membership tariffs, and the grid classes are the ones the owner may actually choose: active grid classes of active
/// class-type services that still have a free permanent seat. The effective date is not a field: the fixed classes are
/// valid from the registration day, which the business clock owns.
/// </summary>
public sealed class EnrollmentFormViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Elegí una alumna.")]
    [Display(Name = "Alumna")]
    public int ClientId { get; set; }

    [Range(1, 7, ErrorMessage = "Elegí la frecuencia semanal.")]
    [Display(Name = "Frecuencia semanal")]
    public int WeeklyFrequency { get; set; }

    [ModelBinder(typeof(InvariantDecimalModelBinder))]
    [Display(Name = "Precio acordado (opcional)")]
    public decimal? AgreedPrice { get; set; }

    [StringLength(200, ErrorMessage = "El motivo no puede superar 200 caracteres.")]
    [Display(Name = "Motivo del monto acordado (opcional)")]
    public string? PriceReason { get; set; }

    /// <summary>The fixed classes the owner checked; the service rejects an empty set or more classes than the frequency.</summary>
    public List<int> GridClassIds { get; set; } = [];

    /// <summary>Populated by the controller: the clients of the business, offered in the "Alumna" select.</summary>
    public List<EnrollmentClientOptionViewModel> Clients { get; set; } = [];

    /// <summary>Populated by the controller: the configured tariffs, offered in the "Frecuencia semanal" select.</summary>
    public List<EnrollmentFrequencyOptionViewModel> FrequencyOptions { get; set; } = [];

    /// <summary>Populated by the controller: the grid classes with a free permanent seat, offered as checkboxes.</summary>
    public List<EnrollmentGridClassOptionViewModel> GridClasses { get; set; } = [];

    public bool HasClients => Clients.Count > 0;

    public bool HasTariffs => FrequencyOptions.Count > 0;

    public bool HasGridClasses => GridClasses.Count > 0;

    /// <summary>
    /// The form is offered only when there is something to enroll into. Without a client, a configured tariff or a
    /// grid class with a free seat, every submission could only be rejected, so the page points the owner to what is
    /// missing instead of showing a form that cannot work.
    /// </summary>
    public bool CanEnroll => HasClients && HasTariffs && HasGridClasses;

    /// <summary>What the business is missing before an enrollment can be registered, or <c>null</c> when nothing is.</summary>
    public string? MissingRequirement => !HasTariffs
        ? "Todavía no hay precios de abono configurados. Cargá al menos uno en Configuración · Servicios y precios."
        : !HasClients
            ? "Todavía no cargaste clientes. Creá la ficha de la alumna antes de inscribirla."
            : !HasGridClasses
                ? "No hay clases de la grilla con lugar libre. Agregá clases a la grilla o liberá un lugar."
                : null;
}
