using Arkana.Application.Features.Chat.Commands;
using FluentAssertions;

namespace Arkana.Application.Tests.Features.Chat.Commands;

public class SendChatCommandValidatorTests
{
    private readonly SendChatCommandValidator _sut = new();

    [Fact]
    public void ValidCommand_ShouldPassValidation()
    {
        // Arrange
        var command = new SendChatCommand
        {
            Model = "gpt-4",
            Messages = new List<ChatMessageDto>
            {
                new() { Role = "user", Content = "Hello" }
            }
        };

        // Act
        var result = _sut.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyMessagesList_ShouldFailValidation()
    {
        // Arrange
        var command = new SendChatCommand
        {
            Model = "gpt-4",
            Messages = new List<ChatMessageDto>()
        };

        // Act
        var result = _sut.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == "Messages" &&
            e.ErrorMessage == "At least one message is required");
    }

    [Fact]
    public void NoUserMessage_ShouldFailValidation()
    {
        // Arrange
        var command = new SendChatCommand
        {
            Model = "gpt-4",
            Messages = new List<ChatMessageDto>
            {
                new() { Role = "assistant", Content = "Hello" }
            }
        };

        // Act
        var result = _sut.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == "Messages" &&
            e.ErrorMessage == "At least one user message is required");
    }
}
