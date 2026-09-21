using Microsoft.AspNetCore.DataProtection;
using WorkUp.BusinessLogic.Abstractions;

namespace WorkUp.Infrastructure;

public sealed class DataProtectionLinkTokenProtector(IDataProtectionProvider provider) : ILinkTokenProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("WorkUp.AppointmentLink.v1");

    public string Protect(string token) => _protector.Protect(token);

    public string Unprotect(string protectedToken) => _protector.Unprotect(protectedToken);
}
