using System.ComponentModel.DataAnnotations;
using WorkUp.BusinessLogic.Scheduling;

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
    [Range(typeof(decimal), "0.01", "9999999999.99", ErrorMessage = "Ingresá un precio válido.")]
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
