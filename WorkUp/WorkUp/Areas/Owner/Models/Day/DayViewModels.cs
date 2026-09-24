using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure;

namespace WorkUp.Areas.Owner.Models.Day;

public sealed record DayIndexViewModel(OwnerDayResult Day, DateTime Now, bool AllowsWalkIns = false,
    WalkInViewModel? WalkIn = null);

public sealed record WalkInOption(int Id, string Name);

public sealed class WalkInViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Elegí un cliente.")]
    public int ClientId { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Elegí un servicio.")]
    public int ServiceId { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Elegí una trabajadora.")]
    public int WorkerId { get; set; }
    [InvariantDecimalRange("0.01", "9999999999.99", ErrorMessage = "Ingresá un precio válido.")]
    [ModelBinder(typeof(InvariantDecimalModelBinder))]
    public decimal? AgreedPrice { get; set; }
    [StringLength(200, ErrorMessage = "El motivo no puede superar 200 caracteres.")]
    public string? PriceReason { get; set; }
    public bool CollectPayment { get; set; }
    [StringLength(1000, ErrorMessage = "La observación no puede superar 1000 caracteres.")]
    public string? NextVisitNote { get; set; }
    public List<WalkInOption> Clients { get; set; } = [];
    public List<WalkInOption> Services { get; set; } = [];
    public List<WalkInOption> Workers { get; set; } = [];
}

/// <summary>
/// Decimal range whose textual bounds are always parsed with <see cref="CultureInfo.InvariantCulture"/>.
/// </summary>
/// <remarks>
/// <see cref="RangeAttribute"/> parses "0.01"/"9999999999.99" with <see cref="CultureInfo.CurrentCulture"/> by
/// default, lazily the first time <see cref="RangeAttribute.IsValid(object?)"/> or
/// <see cref="RangeAttribute.FormatErrorMessage(string)"/> runs. Under comma-decimal cultures (es-AR) the dot is a
/// group separator, so the parse throws and materializing the MVC client validator turns that into an HTTP 500
/// while rendering the walk-in form. Parsing the limits invariantly keeps the exact decimal bounds and the
/// framework's own comparison, client-side range rule and messages.
/// </remarks>
public sealed class InvariantDecimalRangeAttribute : RangeAttribute
{
    public InvariantDecimalRangeAttribute(string minimum, string maximum)
        : base(typeof(decimal), minimum, maximum) => ParseLimitsInInvariantCulture = true;
}
