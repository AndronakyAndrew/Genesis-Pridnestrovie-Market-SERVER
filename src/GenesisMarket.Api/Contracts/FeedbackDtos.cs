using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>Обращение из формы обратной связи. Доступно анонимам.</summary>
public record CreateFeedbackRequest(
    [Required] FeedbackType Type,
    [MaxLength(200)] string? Name,
    [Required][MaxLength(320)] string Contact,
    [Required][MaxLength(4000)] string Message);

/// <summary>Принятое обращение.</summary>
public record FeedbackResponse(
    Guid Id,
    FeedbackType Type,
    DateTimeOffset CreatedAt);
