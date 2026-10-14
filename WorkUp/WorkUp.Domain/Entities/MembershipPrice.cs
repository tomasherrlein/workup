using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Monthly membership price per weekly frequency. Plan assumption 4: the SRS does not say where it is set.</summary>
public class MembershipPrice : BusinessEntity
{
    public int WeeklyFrequency { get; set; }
    public decimal Price { get; set; }
}
