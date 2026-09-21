using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Cuenta única de acceso al panel de gestión del negocio (D-06, Restricción 5).</summary>
public class OwnerAccount : BusinessEntity
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
