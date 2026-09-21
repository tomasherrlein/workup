namespace WorkUp.Business.Abstractions;

/// <summary>
/// Reversible protection for private link tokens, so the owner can copy the current "Ver mi turno" link again
/// (RF-23) without storing it in clear. Implemented with ASP.NET Core Data Protection.
/// </summary>
public interface ILinkTokenProtector
{
    string Protect(string token);
    string Unprotect(string protectedToken);
}
