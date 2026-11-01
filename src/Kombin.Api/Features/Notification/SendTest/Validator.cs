using FluentValidation;

namespace Kombin.Api.Features.Notification.SendTest;

internal sealed class SendTestValidator : AbstractValidator<SendTestRequest>
{
    public SendTestValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.Title).MaximumLength(Copy.MaxTitleLength);
        RuleFor(request => request.Body).MaximumLength(Copy.MaxBodyLength);
    }
}
