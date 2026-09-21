using Arkana.Domain.Services;
using FluentAssertions;

namespace Arkana.Domain.Tests.Services;

public sealed class ApiKeyHasherTests
{
    [Fact]
    public void Hash_Returns64CharLowercaseHexString()
    {
        var hash = ApiKeyHasher.Hash("test-key-123");

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Hash_IsDeterministic()
    {
        const string input = "my-api-key";

        var hash1 = ApiKeyHasher.Hash(input);
        var hash2 = ApiKeyHasher.Hash(input);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Hash_ProducesDifferentOutputForDifferentInputs()
    {
        var hash1 = ApiKeyHasher.Hash("key-one");
        var hash2 = ApiKeyHasher.Hash("key-two");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void GenerateApiKey_ReturnsStringStartingWithArkana()
    {
        var apiKey = ApiKeyHasher.GenerateApiKey();

        apiKey.Should().StartWith("arkana-");
    }

    [Fact]
    public void GenerateApiKey_ReturnsPrefixPlus64HexChars()
    {
        var apiKey = ApiKeyHasher.GenerateApiKey();

        apiKey.Should().MatchRegex("^arkana-[0-9a-f]{64}$");
    }
}
