namespace GenesisMarket.Domain.Common;

/// <summary>
/// Ресурс, у которого есть владелец. Единая точка для проверки владения
/// в авторизации (ResourceOwnerRequirement/Handler). Реализуют сущности,
/// доступ к изменению которых имеет только владелец: Listing (и позже
/// Review, SavedSearch — им достаточно реализовать этот интерфейс).
/// </summary>
public interface IOwnedResource
{
    Guid OwnerId { get; }
}
