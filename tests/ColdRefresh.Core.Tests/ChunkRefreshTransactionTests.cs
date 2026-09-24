using ColdRefresh.Core.Abstractions;
using ColdRefresh.Core.Models;
using ColdRefresh.Core.Transactions;

namespace ColdRefresh.Core.Tests;

public sealed class ChunkRefreshTransactionTests
{
    private static readonly byte[] Original = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task Successful_transaction_requires_verified_target_and_commit()
    {
        var fixture = new Fixture();

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.Committed, result.Outcome);
        Assert.True(result.TargetVerified);
        Assert.Equal(Original, fixture.Target.Bytes);
        Assert.Contains(TransactionState.Prepared, fixture.Journal.States);
        Assert.Contains(TransactionState.TargetVerified, fixture.Journal.States);
        Assert.Equal(TransactionState.Committed, fixture.Journal.States[^1]);
        Assert.True(fixture.Target.FlushCount >= 1);
    }

    [Theory]
    [InlineData(FaultPoint.BeforeJournalWrite)]
    [InlineData(FaultPoint.AfterJournalWrite)]
    [InlineData(FaultPoint.BeforeJournalVerification)]
    public async Task Failure_before_prepared_never_writes_target(FaultPoint point)
    {
        var fixture = new Fixture(point);

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.FailedBeforeTargetWrite, result.Outcome);
        Assert.Equal(0, fixture.Target.WriteCount);
        Assert.DoesNotContain(TransactionState.Prepared, fixture.Journal.States);
    }

    [Fact]
    public async Task Unverified_journal_never_permits_target_write()
    {
        var fixture = new Fixture { CorruptJournalReadback = true };

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.FailedBeforeTargetWrite, result.Outcome);
        Assert.Equal(0, fixture.Target.WriteCount);
    }

    [Fact]
    public async Task Target_hash_mismatch_rolls_back_and_stops_session()
    {
        var fixture = new Fixture { CorruptFirstTargetReadback = true };

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RollbackSucceeded, result.Outcome);
        Assert.True(result.SessionMustStop);
        Assert.Equal(Original, fixture.Target.Bytes);
        Assert.Equal(TransactionState.RollbackSucceeded, fixture.Journal.States[^1]);
    }

    [Theory]
    [InlineData(FaultPoint.AfterPrepared)]
    [InlineData(FaultPoint.DuringTargetWrite)]
    [InlineData(FaultPoint.AfterTargetWrite)]
    [InlineData(FaultPoint.BeforeTargetFlush)]
    [InlineData(FaultPoint.AfterTargetFlush)]
    [InlineData(FaultPoint.BeforeTargetVerification)]
    [InlineData(FaultPoint.AfterTargetVerification)]
    public async Task Failure_after_prepared_rolls_back_and_never_reports_success(FaultPoint point)
    {
        var fixture = new Fixture(point);

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RollbackSucceeded, result.Outcome);
        Assert.False(result.TargetVerified);
        Assert.Equal(Original, fixture.Target.Bytes);
        Assert.Equal(TransactionState.RollbackSucceeded, fixture.Journal.States[^1]);
    }

    [Fact]
    public async Task Failed_rollback_preserves_journal_and_requires_recovery()
    {
        var fixture = new Fixture(FaultPoint.AfterTargetWrite) { FailWritesAfterFirst = true };

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RecoveryRequired, result.Outcome);
        Assert.True(fixture.Journal.Preserved);
        Assert.DoesNotContain(TransactionState.Committed, fixture.Journal.States);
    }

    [Fact]
    public async Task Cancellation_before_start_is_safe_and_writes_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var fixture = new Fixture();

        var result = await fixture.ExecuteAsync(cancelled.Token);

        Assert.Equal(TransactionOutcome.CancelledAtSafeBoundary, result.Outcome);
        Assert.Equal(0, fixture.Target.WriteCount);
        Assert.Empty(fixture.Journal.States);
    }

    [Fact]
    public async Task Cancellation_after_prepared_is_deferred_until_commit()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture(new CancellingInjector(FaultPoint.AfterPrepared, cancellation));

        var result = await fixture.ExecuteAsync(cancellation.Token);

        Assert.Equal(TransactionOutcome.Committed, result.Outcome);
        Assert.Equal(TransactionState.Committed, fixture.Journal.States[^1]);
    }

    [Fact]
    public void Defaults_are_conservative()
    {
        var options = new RefreshOptions();
        options.Validate();
        Assert.Equal(64 * 1024 * 1024, options.ChunkSize);
        Assert.Equal(TimeSpan.FromDays(365), options.ColdAge);
        Assert.Equal(1, options.Concurrency);
        Assert.Equal(VerificationMode.FullFileSha256, options.Verification);
    }

    private sealed class Fixture
    {
        private readonly IFaultInjector _faults;
        public MemoryTarget Target { get; } = new(Original);
        public MemoryJournal Journal { get; } = new();
        public bool CorruptJournalReadback { set => Journal.CorruptReadback = value; }
        public bool CorruptFirstTargetReadback { set => Target.CorruptReadbackOnce = value; }
        public bool FailWritesAfterFirst { set => Target.FailWritesAfterFirst = value; }

        public Fixture(FaultPoint? failure = null) : this(failure is null ? NoFaultInjector.Instance : new ThrowingInjector(failure.Value)) { }
        public Fixture(IFaultInjector faults) => _faults = faults;

        public ValueTask<TransactionResult> ExecuteAsync() => ExecuteAsync(CancellationToken.None);

        public ValueTask<TransactionResult> ExecuteAsync(CancellationToken token)
        {
            var descriptor = new ChunkDescriptor(Guid.NewGuid(), new FileIdentity(123, Guid.NewGuid()), Original.Length, 0, Original.Length);
            return new ChunkRefreshTransaction(Target, Journal, _faults).ExecuteAsync(descriptor, token);
        }
    }

    private sealed class MemoryTarget(byte[] initial) : IChunkSource
    {
        public byte[] Bytes { get; } = initial.ToArray();
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public bool CorruptReadbackOnce { get; set; }
        public bool FailWritesAfterFirst { get; set; }

        public ValueTask ReadExactlyAsync(ChunkDescriptor chunk, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bytes.AsMemory((int)chunk.Offset, chunk.Length).CopyTo(destination);
            if (CorruptReadbackOnce && WriteCount > 0) { destination.Span[0] ^= 0xff; CorruptReadbackOnce = false; }
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteExactlyAsync(ChunkDescriptor chunk, ReadOnlyMemory<byte> source)
        {
            WriteCount++;
            if (FailWritesAfterFirst && WriteCount > 1) throw new IOException("Injected rollback write failure.");
            source.CopyTo(Bytes.AsMemory((int)chunk.Offset, chunk.Length));
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync() { FlushCount++; return ValueTask.CompletedTask; }
    }

    private sealed class MemoryJournal : IRecoveryJournal
    {
        private byte[] _payload = [];
        private ulong _sequence;
        public List<TransactionState> States { get; } = [];
        public bool CorruptReadback { get; set; }
        public bool Preserved { get; private set; }

        public ValueTask WritePayloadAsync(ChunkDescriptor chunk, ReadOnlyMemory<byte> original, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); _payload = original.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask ReadPayloadExactlyAsync(ChunkDescriptor chunk, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _payload.AsMemory().CopyTo(destination);
            if (CorruptReadback) destination.Span[0] ^= 0xff;
            return ValueTask.CompletedTask;
        }
        public ValueTask<JournalRecord> PublishStateAsync(JournalRecord record, TransactionState state)
        { States.Add(state); return ValueTask.FromResult(record with { SequenceNumber = ++_sequence, State = state }); }
        public ValueTask PreserveForRecoveryAsync(JournalRecord record, Exception cause)
        { Preserved = true; States.Add(TransactionState.RecoveryRequired); return ValueTask.CompletedTask; }
    }

    private sealed class ThrowingInjector(FaultPoint failure) : IFaultInjector
    {
        private bool _thrown;
        public ValueTask AtAsync(FaultPoint point)
        {
            if (!_thrown && point == failure) { _thrown = true; throw new IOException($"Injected {point} failure."); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingInjector(FaultPoint at, CancellationTokenSource cancellation) : IFaultInjector
    {
        public ValueTask AtAsync(FaultPoint point) { if (point == at) cancellation.Cancel(); return ValueTask.CompletedTask; }
    }
}
