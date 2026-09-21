using System.Security.Cryptography;
using Arkana.Domain.Services;
using Arkana.Infrastructure.Security;

namespace Arkana.Gateway.Api.Tests;

/// <summary>
/// Test helper: produces a singleton <see cref="ICredentialVault"/> for the
/// Gateway.Api integration tests. Uses <see cref="EnvelopeCredentialVault"/>
/// with a deterministic 32-byte key.
/// </summary>
internal static class TestVaultFactory
{
    private static readonly byte[] TestKey = new byte[]
    {
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    };

    public static ICredentialVault Create() => new EnvelopeCredentialVault(TestKey);
}
