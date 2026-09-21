using System.Net;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

/// <summary>
/// Test-only access to the ChatGptCodexChatService usage-capture internals
/// (the capture path is private in the production class). Uses NSubstitute
/// stubs for everything the constructor requires.
/// </summary>
public static class ChatGptCodexChatServiceTestsHarness
{
    public static ChatGptCodexChatService Create(IAccountUsageSnapshotRepository? repo)
    {
        return new ChatGptCodexChatService(
            new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new ChatGptAccountPool(
                Substitute.For<IProviderCatalog>(),
                Substitute.For<IOAuthFlowService>(),
                NullLogger<ChatGptAccountPool>.Instance),
            NullLogger<ChatGptCodexChatService>.Instance,
            repo);
    }

    /// <summary>Invokes the private CaptureUsageSnapshot via reflection (fire-and-forget persist).</summary>
    public static void CaptureForTest(
        this ChatGptCodexChatService service, ChatGptAccount account, object report)
    {
        var method = typeof(ChatGptCodexChatService).GetMethod(
            "CaptureUsageSnapshot",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method!.Invoke(service, [account, report]);
    }
}
