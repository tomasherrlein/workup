using WorkUp.Infrastructure;

namespace WorkUp.Tests.Infrastructure;

public sealed class OwnerNavigationTests
{
    [Fact]
    public void Solo_business_bottom_bar_uses_clients_when_group_sections_are_hidden()
    {
        var visibleSections = OwnerNavigation.Sections
            .Where(section => section.Controller is not ("Classes" or "Enrollments"))
            .ToList();

        var items = OwnerNavigationProvider.BottomBarItems(visibleSections);

        Assert.Equal(["Jornada", "Agenda", "Clientes"], items.Select(item => item.Label));
        Assert.DoesNotContain(items, item => item.Controller is "Classes" or "Enrollments");
    }

    [Fact]
    public void Group_business_bottom_bar_uses_classes()
    {
        var items = OwnerNavigationProvider.BottomBarItems(OwnerNavigation.Sections);

        Assert.Equal(["Jornada", "Agenda", "Clases"], items.Select(item => item.Label));
    }
}
