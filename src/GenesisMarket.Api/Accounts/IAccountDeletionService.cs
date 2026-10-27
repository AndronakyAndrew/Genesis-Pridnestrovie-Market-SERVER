namespace GenesisMarket.Api.Accounts;

public interface IAccountDeletionService
{
    Task<bool> DeleteAsync(Guid userId, CancellationToken ct);
}
