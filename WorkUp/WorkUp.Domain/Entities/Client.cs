using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Ficha de cliente. Identified by email inside a business (Regla 7); an account is optional (D-18).</summary>
public class Client : BusinessEntity
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }

    /// <summary>Observaciones de salud. Owner-only: never shown on public pages, links, emails or reports.</summary>
    public string? HealthNotes { get; set; }

    public DateTime CreatedAt { get; set; }

    public string FullName => $"{FirstName} {LastName}";
}
