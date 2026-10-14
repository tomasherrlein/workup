using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Supplies;

namespace WorkUp.Infrastructure;

/// <summary>
/// Computes which of <see cref="OwnerNavigation.Sections"/> are visible for the signed-in business, and which of
/// those land in the mobile bottom bar. Clases and Abonos stay hidden while there is no active group service; the
/// Insumos item carries the count of active supplies below their declared minimum.
/// </summary>
public sealed class OwnerNavigationProvider(CatalogService catalogService, SupplyService supplyService)
{
    private static readonly HashSet<string> GroupOnlyControllers = new(StringComparer.OrdinalIgnoreCase) { "Classes", "Enrollments" };

    public async Task<IReadOnlyList<NavItem>> VisibleSectionsAsync(int businessId, CancellationToken ct = default)
    {
        var hasGroupServices = await catalogService.HasGroupServicesAsync(businessId, ct);
        var lowStockCount = await supplyService.CountBelowMinimumAsync(businessId, ct);
        var sections = hasGroupServices
            ? OwnerNavigation.Sections
            : OwnerNavigation.Sections.Where(s => !GroupOnlyControllers.Contains(s.Controller)).ToList();

        return sections.Select(section => section.Controller == "Supplies" && lowStockCount > 0
            ? section with { AlertCount = lowStockCount }
            : section).ToList();
    }

    /// <summary>
    /// The three sections shown in the mobile bottom bar (plus "Más"). Normally Jornada, Agenda and Clases; when a
    /// section flagged for the bottom bar is hidden (for example Clases), the next visible section takes its place
    /// so the bar keeps three items.
    /// </summary>
    public static IReadOnlyList<NavItem> BottomBarItems(IReadOnlyList<NavItem> visibleSections)
    {
        var items = visibleSections.Where(s => s.InBottomBar).ToList();
        if (items.Count < 3)
            items.AddRange(visibleSections.Except(items).Take(3 - items.Count));
        return items;
    }
}
