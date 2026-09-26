using System.ComponentModel.DataAnnotations;

namespace MqttApi.Models;

public static class ModelValidator
{
    public static bool TryValidate<T>(T model, out Dictionary<string, string[]> errors)
    {
        var context = new ValidationContext(model!);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(model!, context, results, validateAllProperties: true))
        {
            errors = [];
            return true;
        }
        errors = results
            .GroupBy(r => r.MemberNames.FirstOrDefault() ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ErrorMessage ?? "Invalid").ToArray());
        return false;
    }

    public static IResult Invalid(Dictionary<string, string[]> errors) => Results.ValidationProblem(errors);
}
