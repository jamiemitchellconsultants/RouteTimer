using Microsoft.AspNetCore.Mvc;
using RouteTimer.Contracts.Errors;
using RouteTimer.Services.Profile;

namespace RouteTimer.Api.Errors;

public static class ApiProblems
{
    public static IResult Create(int status, string code, string detail) =>
        Results.Problem(
            statusCode: status,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult BadRequest(string code, string detail) =>
        Create(StatusCodes.Status400BadRequest, code, detail);

    public static IResult Conflict(string code, string detail) =>
        Create(StatusCodes.Status409Conflict, code, detail);

    public static IResult Forbidden(string code, string detail) =>
        Create(StatusCodes.Status403Forbidden, code, detail);

    public static IResult NotFound(string code, string detail) =>
        Create(StatusCodes.Status404NotFound, code, detail);

    public static IResult PayloadTooLarge(string code, string detail) =>
        Create(StatusCodes.Status413PayloadTooLarge, code, detail);

    public static IResult InvalidProfile(ProfileValidationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (exception.Errors.TryGetValue("riderWeightKg", out var riderWeightErrors))
        {
            errors["riderWeightKg"] = riderWeightErrors;
        }

        if (exception.Errors.TryGetValue("bikeAndEquipmentWeightKg", out var bikeWeightErrors))
        {
            errors["bikeAndEquipmentWeightKg"] = bikeWeightErrors;
        }

        return Results.ValidationProblem(
            errors,
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.InvalidProfile });
    }
}
