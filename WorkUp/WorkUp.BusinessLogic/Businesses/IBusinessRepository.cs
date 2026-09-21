using WorkUp.Domain.Entities;
// The entity is aliased: this file lives under the WorkUp.BusinessLogic.* namespace tree, and the bare name "Business"
// resolves to that enclosing namespace (WorkUp.BusinessLogic) rather than the entity type (CS0118).

namespace WorkUp.BusinessLogic.Businesses;

public interface IBusinessRepository
{
    Task<Business?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<Business?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Slug uniqueness check. Pass the business's own id as <paramref name="excludingId"/> when editing.</summary>
    Task<bool> SlugExistsAsync(string slug, int? excludingId = null, CancellationToken ct = default);

    Task<int> InsertAsync(Business business, CancellationToken ct = default);

    Task UpdateAsync(Business business, CancellationToken ct = default);
}
