using Arkana.Domain.ValueObjects;
using FluentAssertions;

namespace Arkana.Domain.Tests.ValueObjects;

public sealed class ToolCallInfoTests
{
    [Fact]
    public void DefaultTypeIsFunction()
    {
        var info = new ToolCallInfo();

        info.Type.Should().Be("function");
    }

    [Fact]
    public void PropertiesCanBeSetViaInit()
    {
        var info = new ToolCallInfo
        {
            Id = "call_123",
            Type = "function",
            FunctionName = "get_weather",
            FunctionArguments = "{\"city\": \"London\"}"
        };

        info.Id.Should().Be("call_123");
        info.Type.Should().Be("function");
        info.FunctionName.Should().Be("get_weather");
        info.FunctionArguments.Should().Be("{\"city\": \"London\"}");
    }
}
