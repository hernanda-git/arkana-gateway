using Arkana.Application.Common.Models;
using FluentAssertions;

namespace Arkana.Application.Tests.Common.Models;

public class ResultTests
{
    [Fact]
    public void Success_ShouldCreateResultWithIsSuccessTrueAndStatusCode200()
    {
        // Arrange
        const string data = "test";

        // Act
        var result = Result<string>.Success(data);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public void Created_ShouldCreateResultWithIsSuccessTrueAndStatusCode201()
    {
        // Arrange
        const string data = "test";

        // Act
        var result = Result<string>.Created(data);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(201);
    }

    [Fact]
    public void Failure_ShouldCreateResultWithErrorMessageAndStatusCode400()
    {
        // Arrange
        const string error = "Something went wrong";

        // Act
        var result = Result<string>.Failure(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public void NotFound_ShouldCreateResultWithStatusCode404()
    {
        // Act
        var result = Result<string>.NotFound();

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(404);
        result.Error.Should().Be("Resource not found");
    }

    [Fact]
    public void Unauthorized_ShouldCreateResultWithStatusCode401()
    {
        // Act
        var result = Result<string>.Unauthorized();

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        result.Error.Should().Be("Unauthorized");
    }

    [Fact]
    public void Success_ShouldStoreDataCorrectly()
    {
        // Arrange
        const int expectedData = 42;

        // Act
        var result = Result<int>.Success(expectedData);

        // Assert
        result.Data.Should().Be(expectedData);
        result.IsSuccess.Should().BeTrue();
    }
}
