using FluentValidation;
using Recommendations.Api.Domain;

namespace Recommendations.Api.Api.Validators;

public class RecommendationRequestValidator : AbstractValidator<RecommendationRequest>
{
    public RecommendationRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.Latitude.HasValue && x.Longitude.HasValue || !string.IsNullOrWhiteSpace(x.Address))
            .WithMessage("Either coordinates (latitude and longitude) or an address must be provided.");

        When(x => x.Latitude.HasValue, () =>
        {
            RuleFor(x => x.Latitude!.Value)
                .InclusiveBetween(-90, 90)
                .WithMessage("Latitude must be between -90 and 90.");
        });

        When(x => x.Longitude.HasValue, () =>
        {
            RuleFor(x => x.Longitude!.Value)
                .InclusiveBetween(-180, 180)
                .WithMessage("Longitude must be between -180 and 180.");
        });

        RuleFor(x => x.MaxResults)
            .InclusiveBetween(1, 20)
            .WithMessage("MaxResults must be between 1 and 20.");

        RuleFor(x => x.RadiusMeters)
            .InclusiveBetween(100, 50000)
            .WithMessage("RadiusMeters must be between 100 and 50000.");
    }
}
