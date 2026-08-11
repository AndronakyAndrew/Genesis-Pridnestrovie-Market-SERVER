namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Результат добавления объявления в избранное. Идемпотентно: и первое добавление,
/// и повтор возвращают одно и то же состояние (200), без дубля на уровне БД.
/// </summary>
/// <param name="IsFavorite">Всегда true после успешного POST.</param>
/// <param name="FavoritesCount">Актуальный денормализованный счётчик объявления.</param>
public record FavoriteResponse(bool IsFavorite, int FavoritesCount);
