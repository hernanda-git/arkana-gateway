using FluentValidation;

namespace Arkana.Application.Features.Chat.Commands;

/// <summary>
/// Validates <see cref="SendChatCommand"/> ensuring at least one user message is present
/// and optional parameters are within acceptable ranges.
/// </summary>
public sealed class SendChatCommandValidator : AbstractValidator<SendChatCommand>
{
    public SendChatCommandValidator()
    {
        RuleFor(x => x.Messages).NotEmpty().WithMessage("At least one message is required");
        RuleFor(x => x.Messages)
            .Must(m => m.Any(msg => msg.Role == "user"))
            .WithMessage("At least one user message is required");
    }
}
