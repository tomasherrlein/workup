namespace WorkUp.ViewModels.Catalog;

/// <summary>Tarifario screen: services grouped as individual turns and classes, plus membership prices per weekly frequency (plan assumption 4).</summary>
public sealed class ServicesPageViewModel
{
    public List<ServiceRowViewModel> Individual { get; init; } = [];
    public List<ServiceRowViewModel> Classes { get; init; } = [];

    /// <summary>Whether the business has at least one active class (drives the membership prices section and the Clases/Abonos navigation).</summary>
    public bool HasGroupServices { get; init; }
    public List<MembershipPriceRowViewModel> MembershipPrices { get; init; } = [];

    /// <summary>Weekly frequencies (1 to 7) that have no price yet, offered in the "add price" form.</summary>
    public List<int> AvailableFrequencies { get; init; } = [];
}
