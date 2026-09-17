using TimeLens.Domain.Models;
using Orleans;

namespace TimeLens.Validation.Worker.Grains;

public interface IValidationCheckGrain : IGrainWithStringKey
{
    Task<string> ValidateAsync(ValidationCheckMessage message);
}
