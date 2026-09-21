using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Tests.Services;

/// <summary>
/// Tests for the in-memory <see cref="BatchedMeteringQueue"/>
/// (PERF-ARKANA-005). Verifies enqueue/drop semantics, depth
/// reporting, drain ordering, and counter accounting.
/// </summary>
public sealed class BatchedMeteringQueueTests
{
    private static BatchedMeteringQueue NewQueue(
        Action<MeteringQueueOptions>? configure = null)
    {
        var opts = new MeteringQueueOptions();
        configure?.Invoke(opts);
        return new BatchedMeteringQueue(
            Options.Create(opts),
            NullLogger<BatchedMeteringQueue>.Instance);
    }

    private static TokenUsage SampleUsage() =>
        new("opencode", "gpt-4", 100, 50, 0.001m, TimeSpan.FromMilliseconds(250));

    private static RequestLog SampleLog() => new()
    {
        Provider = "opencode",
        Model = "gpt-4",
        Messages = new List<ChatMessage> { new() { Role = "user", Content = "hi" } },
        ResponseContent = "hello",
        InputTokens = 100,
        OutputTokens = 50,
        Cost = 0.001m,
        Duration = TimeSpan.FromMilliseconds(250),
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task EnqueueUsageAsync_OnEmptyQueue_AcceptsAndIncrementsDepth()
    {
        var queue = NewQueue();
        var accepted = await queue.EnqueueUsageAsync(SampleUsage());
        accepted.Should().BeTrue();
        queue.Depth.Should().Be(1);
    }

    [Fact]
    public async Task EnqueueLogAsync_OnEmptyQueue_AcceptsAndIncrementsDepth()
    {
        var queue = NewQueue();
        var accepted = await queue.EnqueueLogAsync(SampleLog());
        accepted.Should().BeTrue();
        queue.Depth.Should().Be(1);
    }

    [Fact]
    public async Task Drain_RespectsMaxBatchSize()
    {
        var queue = NewQueue(o => o.Capacity = 100);
        for (int i = 0; i < 10; i++)
        {
            await queue.EnqueueUsageAsync(SampleUsage());
        }

        var batch = queue.Drain(3);
        batch.Should().HaveCount(3);
        queue.Depth.Should().Be(7);
    }

    [Fact]
    public async Task Drain_PreservesFifoOrder()
    {
        var queue = NewQueue();
        var usages = Enumerable.Range(0, 5).Select(i =>
        {
            // Each usage has a unique cost so we can identify order.
            return new TokenUsage("opencode", $"model-{i}", 1, 1, i, TimeSpan.Zero);
        }).ToArray();

        foreach (var u in usages) await queue.EnqueueUsageAsync(u);

        var batch = queue.Drain(10);
        batch.Should().HaveCount(5);
        for (int i = 0; i < 5; i++)
        {
            var evt = batch[i].Should().BeOfType<MeteringEvent.Usage>().Subject;
            evt.Value.Cost.Should().Be((decimal)i);
        }
    }

    [Fact]
    public async Task Drain_OnEmptyQueue_ReturnsEmpty()
    {
        var queue = NewQueue();
        var batch = queue.Drain(100);
        batch.Should().BeEmpty();
    }

    [Fact]
    public async Task Enqueue_OnFullQueue_BlocksUntilEnqueueTimeout_ThenDrops()
    {
        // Capacity 1, no flusher consuming, so the 2nd enqueue will
        // hit backpressure. Use a tiny timeout so the test is fast.
        var queue = NewQueue(o =>
        {
            o.Capacity = 1;
            o.EnqueueTimeout = TimeSpan.FromMilliseconds(50);
        });

        await queue.EnqueueUsageAsync(SampleUsage());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var accepted = await queue.EnqueueUsageAsync(SampleUsage());
        sw.Stop();

        accepted.Should().BeFalse();
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(40),
            "we should wait at least the EnqueueTimeout before dropping");
    }

    [Fact]
    public async Task Enqueue_OnBackpressure_IncrementsDroppedCounter()
    {
        var queue = NewQueue(o =>
        {
            o.Capacity = 1;
            o.EnqueueTimeout = TimeSpan.FromMilliseconds(20);
        });

        await queue.EnqueueUsageAsync(SampleUsage()); // fills the queue
        await queue.EnqueueUsageAsync(SampleUsage()); // dropped
        await queue.EnqueueLogAsync(SampleLog());     // dropped

        var c = queue.SnapshotAndResetCounters();
        c.UsageDropped.Should().Be(1);
        c.LogDropped.Should().Be(1);
    }

    [Fact]
    public async Task SnapshotAndResetCounters_ResetsToZero()
    {
        var queue = NewQueue();
        await queue.EnqueueUsageAsync(SampleUsage());

        var first = queue.SnapshotAndResetCounters();
        first.UsageEnqueued.Should().Be(1);

        var second = queue.SnapshotAndResetCounters();
        second.UsageEnqueued.Should().Be(0);
        second.LogEnqueued.Should().Be(0);
    }

    [Fact]
    public void RecordFlush_UpdatesBatchesAndEventsCounters()
    {
        var queue = NewQueue();
        queue.RecordFlush(eventCount: 5, TimeSpan.FromMilliseconds(10), success: true);
        queue.RecordFlush(eventCount: 2, TimeSpan.FromMilliseconds(15), success: true);
        queue.RecordFlush(eventCount: 3, TimeSpan.FromMilliseconds(20), success: false);

        var c = queue.SnapshotAndResetCounters();
        c.BatchesFlushed.Should().Be(3);
        c.EventsFlushed.Should().Be(10);
        c.FlushFailures.Should().Be(1);
        c.TotalFlushDuration.Should().Be(TimeSpan.FromMilliseconds(45));
    }

    [Fact]
    public void RecordFlush_OnZeroEvents_IsNoOp()
    {
        var queue = NewQueue();
        queue.RecordFlush(eventCount: 0, TimeSpan.FromMilliseconds(5), success: true);
        var c = queue.SnapshotAndResetCounters();
        c.BatchesFlushed.Should().Be(0);
        c.EventsFlushed.Should().Be(0);
    }
}
