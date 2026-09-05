namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Публичная витринная статистика (GET /api/stats) для счётчиков на главной.
/// <paramref name="UsersCount"/> — только рядовые пользователи: персонал площадки
/// (администраторы и модераторы) и удалённые аккаунты не считаются.
/// <paramref name="ListingsCount"/> — опубликованные (Active) объявления.
/// </summary>
public record PublicStatsResponse(long UsersCount, long ListingsCount);
