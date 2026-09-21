using Arkana.Domain.Entities;
using FluentAssertions;

namespace Arkana.Domain.Tests.Entities;

public sealed class PolicyTemplateTests
{
    [Fact]
    public void Create_ValidArgs_ReturnsTemplate()
    {
        var config = """{"pii_detection": true, "masking": "full"}""";
        var template = PolicyTemplate.Create("GDPR", "gdpr", "EU data protection", config, isBuiltin: true);

        template.Id.Should().NotBe(Guid.Empty);
        template.Name.Should().Be("GDPR");
        template.Slug.Should().Be("gdpr");
        template.Description.Should().Be("EU data protection");
        template.Config.Should().Be(config);
        template.IsActive.Should().BeTrue();
        template.IsBuiltin.Should().BeTrue();
        template.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_SlugIsLowercased()
    {
        var template = PolicyTemplate.Create("PII Strict", "PII_STRICT", "desc", "{}");
        template.Slug.Should().Be("pii_strict");
    }

    [Fact]
    public void Create_EmptyName_Throws()
    {
        var act = () => PolicyTemplate.Create("", "slug", "desc", "{}");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_EmptySlug_Throws()
    {
        var act = () => PolicyTemplate.Create("Name", "", "desc", "{}");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_EmptyConfig_Throws()
    {
        var act = () => PolicyTemplate.Create("Name", "slug", "desc", "");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateDetails_UpdatesFields()
    {
        var template = PolicyTemplate.Create("Old", "slug", "old desc", "{}");
        template.UpdateDetails("New", "new desc", """{"updated": true}""");

        template.Name.Should().Be("New");
        template.Description.Should().Be("new desc");
        template.Config.Should().Be("""{"updated": true}""");
    }

    [Fact]
    public void UpdateDetails_EmptyName_Throws()
    {
        var template = PolicyTemplate.Create("Old", "slug", "desc", "{}");
        var act = () => template.UpdateDetails("", "desc", "{}");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Deactivate_SetsIsActiveFalse()
    {
        var template = PolicyTemplate.Create("Test", "test", "desc", "{}");
        template.IsActive.Should().BeTrue();

        template.Deactivate();
        template.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_SetsIsActiveTrue()
    {
        var template = PolicyTemplate.Create("Test", "test", "desc", "{}");
        template.Deactivate();
        template.Activate();
        template.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_DefaultBuiltinIsFalse()
    {
        var template = PolicyTemplate.Create("Custom", "custom", "desc", "{}");
        template.IsBuiltin.Should().BeFalse();
    }
}
