using System.ComponentModel.DataAnnotations;

namespace GenesisMarket.Api.Contracts;

public record VerifyPhoneRequest(
    [Required] string Code);

/// <summary>Ответ на запрос кода: когда код истечёт (сам код не возвращается).</summary>
public record SendCodeResponse(string Message, DateTimeOffset ExpiresAt);
