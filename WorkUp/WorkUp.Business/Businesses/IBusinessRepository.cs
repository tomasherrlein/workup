// The entity is aliased: this file lives under the WorkUp.Business.* namespace tree, and the bare name "Business"
// resolves to that enclosing namespace (WorkUp.Business) rather than the entity type (CS0118).
using BusinessEntity = WorkUp.Domain.Entities.Business;

namespace WorkUp.Business.Businesses;

public interface IBusinessRepository
{
    Task<BusinessEntity?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<BusinessEntity?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Slug uniqueness check. Pass the business's own id as <paramref name="excludingId"/> when editing.</summary>
    Task<bool> SlugExistsAsync(string slug, int? excludingId = null, CancellationToken ct = default);

    Task<int> InsertAsync(BusinessEntity business, CancellationToken ct = default);

    Task UpdateAsync(BusinessEntity business, CancellationToken ct = default);
}
