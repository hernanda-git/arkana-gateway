namespace Arkana.Domain.Entities;

/// <summary>
/// Pre-built compliance policy template (ENT-ARKANA-005).
/// Templates define reusable configurations for PII detection, content filtering,
/// audit retention, and data handling rules.
/// </summary>
public sealed class PolicyTemplate
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;

    /// <summary>JSON-serialized policy rules (PII patterns, retention, engine config).</summary>
    public string Config { get; private set; } = "{}";

    public bool IsActive { get; private set; }
    public bool IsBuiltin { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private PolicyTemplate() { } // EF Core

    public static PolicyTemplate Create(
        string name,
        string slug,
        string description,
        string config,
        bool isBuiltin = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(slug);
        ArgumentException.ThrowIfNullOrEmpty(config);

        return new PolicyTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug.ToLowerInvariant(),
            Description = description,
            Config = config,
            IsActive = true,
            IsBuiltin = isBuiltin,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public void UpdateDetails(string name, string description, string config)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(config);

        Name = name;
        Description = description;
        Config = config;
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}
