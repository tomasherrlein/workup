namespace WorkUp.ViewModels.Classes;

/// <summary>Grilla semanal (RF-04): active classes grouped by weekday, plus archived classes listed separately (never deleted).</summary>
public sealed class ClassesIndexViewModel
{
    /// <summary>The day highlighted by the phone day picker; on desktop every day shows at once.</summary>
    public DayOfWeek SelectedDay { get; init; }

    public Dictionary<DayOfWeek, List<GridClassRowViewModel>> ActiveByDay { get; init; } = new();

    public List<GridClassRowViewModel> Archived { get; init; } = [];

    public bool HasAnyActive => ActiveByDay.Values.Any(day => day.Count > 0);

    public static readonly IReadOnlyList<DayOfWeek> MondayFirst = GridClassFormViewModel.MondayFirst;
}
