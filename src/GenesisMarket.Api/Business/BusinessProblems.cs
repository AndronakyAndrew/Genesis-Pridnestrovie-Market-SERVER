using System.Globalization;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace GenesisMarket.Api.Business;

/// <summary>
/// Перевод ошибок бизнес-режима в <c>application/problem+json</c>.
///
/// Форма ответа:
/// <code>
/// { "title": "…", "status": 400, "code": "registration_number_format",
///   "errors": { "registrationNumber": ["Неверный формат …"] },
///   "codes":  { "registrationNumber": "registration_number_format" } }
/// </code>
/// <c>errors</c>/<c>codes</c> есть только у ошибок конкретного поля; <c>code</c> — у всех.
/// </summary>
internal static class BusinessProblems
{
    public static ObjectResult ToProblem(this ControllerBase c, BusinessError error)
    {
        ProblemDetails problem;
        if (error.Field is { } field)
        {
            var state = new ModelStateDictionary();
            state.AddModelError(field, error.Title);
            problem = c.ProblemDetailsFactory.CreateValidationProblemDetails(
                c.HttpContext, state, error.Status, title: error.Title);
            problem.Extensions["codes"] = new Dictionary<string, string> { [field] = error.Code };
        }
        else
        {
            problem = c.ProblemDetailsFactory.CreateProblemDetails(c.HttpContext, error.Status, title: error.Title);
        }

        problem.Extensions["code"] = error.Code;

        if (error.RetryAt is { } retryAt)
        {
            problem.Extensions["retryAt"] = retryAt;
            var seconds = Math.Max(1, (int)Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds));
            c.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return Result(problem, error.Status);
    }

    /// <summary>400 по результату валидатора: все поля сразу, у каждого — текст и код.</summary>
    public static ObjectResult ToProblem(this ControllerBase c, ValidationResult validation)
    {
        var state = new ModelStateDictionary();
        var codes = new Dictionary<string, string>();
        foreach (var e in validation.Errors)
        {
            state.AddModelError(e.PropertyName, e.ErrorMessage);
            codes.TryAdd(e.PropertyName, e.ErrorCode);
        }

        var problem = c.ProblemDetailsFactory.CreateValidationProblemDetails(
            c.HttpContext, state, StatusCodes.Status400BadRequest, title: "Проверьте реквизиты");
        problem.Extensions["code"] = "validation_failed";
        problem.Extensions["codes"] = codes;
        return Result(problem, StatusCodes.Status400BadRequest);
    }

    private static ObjectResult Result(ProblemDetails problem, int status)
    {
        var result = new ObjectResult(problem) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
