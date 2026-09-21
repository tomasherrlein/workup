using WorkUp.BusinessLogic.Catalog;

namespace WorkUp.Infrastructure;

/// <summary>
/// Computes which of <see cref="OwnerNavigation.Sections"/> are visible for the signed-in business, and which of
/// those land in the mobile bottom bar (T02 acceptance: hide Clases and Abonos while the business has no active
/// service with capacity greater than 1). <see cref="OwnerNavigation.Sections"/> stays the single source of the
/// section list and its order; this only filters it and is used by <c>_OwnerLayout.cshtml</c> for the rail, the
/// bottom bar and the nav sheet.
/// </summary>
public sealed class OwnerNavigationProvider(CatalogService catalogService)
{
    private static readonly HashSet<string> GroupOnlyControllers = new(StringComparer.OrdinalIgnoreCase) { "Classes", "Enrollments" };

    public async Task<IReadOnlyList<NavItem>> VisibleSectionsAsync(int businessId, CancellationToken ct = default)
    {
        var hasGroupServices = await catalogService.HasGroupServicesAsync(businessId, ct);
        return hasGroupServices
            ? OwnerNavigation.Sections
            : OwnerNavigation.Sections.Where(s => !GroupOnlyControllers.Contains(s.Controller)).ToList();
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
