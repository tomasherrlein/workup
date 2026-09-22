namespace WorkUp.Infrastructure;

public sealed record NavItem(string Label, string Controller, string Icon, bool InBottomBar = false, int AlertCount = 0);

/// <summary>
/// Owner panel sections, in menu order. Controller names are fixed: each task creates the controller for its
/// section with an <c>Index</c> action. The first three items also appear in the mobile bottom bar.
/// </summary>
public static class OwnerNavigation
{
    public static readonly IReadOnlyList<NavItem> Sections =
    [
        new("Jornada", "Day", "sun", InBottomBar: true),
        new("Agenda", "Agenda", "calendar", InBottomBar: true),
        new("Clases", "Classes", "grid", InBottomBar: true),
        new("Abonos", "Enrollments", "repeat"),
        new("Clientes", "Clients", "users"),
        new("Cobros", "Payments", "coin"),
        new("Gastos", "Expenses", "receipt"),
        new("Insumos", "Supplies", "box"),
        new("Reportes", "Reports", "chart"),
        new("Configuración", "Settings", "sliders"),
    ];

    public static readonly NavItem DevTools = new("Herramientas de prueba", "DevTools", "flask");
}
