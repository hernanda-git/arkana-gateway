using Arkana.Application;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Arkana.Application.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplicationServices_ShouldRegisterMediatR()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddApplicationServices();

        // Assert
        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IMediator));
    }

    [Fact]
    public void AddApplicationServices_ShouldNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddApplicationServices();

        // Assert
        act.Should().NotThrow();
    }
}
