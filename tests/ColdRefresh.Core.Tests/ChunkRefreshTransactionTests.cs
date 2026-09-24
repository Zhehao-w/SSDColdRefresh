using ColdRefresh.Core.Abstractions;
using ColdRefresh.Core.Models;
using ColdRefresh.Core.Transactions;

namespace ColdRefresh.Core.Tests;

public sealed class ChunkRefreshTransactionTests
{
    private static readonly byte[] Original = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task Every_publish_uses_latest_authoritative_record()
    {
        var fixture = new Fixture();

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.Committed, result.Outcome);
        Assert.Equal(
            [TransactionState.Prepared, TransactionState.TargetWritten, TransactionState.TargetVerified, TransactionState.Committed],
            fixture.Journal.States);
        Assert.Equal([1UL, 2UL, 3UL, 4UL], fixture.Journal.Sequences);
    }

    [Fact]
    public async Task Strict_journal_rejects_stale_record()
    {
        var fixture = new Fixture();
        var descriptor = fixture.Descriptor;
        var hash = System.Security.Cryptography.SHA256.HashData(Original);
        var initial = new JournalRecord(0, TransactionState.Empty, descriptor, hash, hash);
        var prepared = await fixture.Journal.PublishStateAsync(initial, TransactionState.Prepared);
        var written = await fixture.Journal.PublishStateAsync(prepared, TransactionState.TargetWritten);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Journal.PublishStateAsync(prepared, TransactionState.TargetVerified));
        Assert.Equal(written, fixture.Journal.Authoritative);
    }

    [Fact]
    public async Task Strict_journal_rejects_commit_from_target_written()
    {
        var fixture = new Fixture();
        var hash = System.Security.Cryptography.SHA256.HashData(Original);
        var initial = new JournalRecord(0, TransactionState.Empty, fixture.Descriptor, hash, hash);
        var prepared = await fixture.Journal.PublishStateAsync(initial, TransactionState.Prepared);
        var written = await fixture.Journal.PublishStateAsync(prepared, TransactionState.TargetWritten);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Journal.PublishStateAsync(written, TransactionState.Committed));
    }

    [Theory]
    [InlineData(FaultPoint.BeforeJournalWrite)]
    [InlineData(FaultPoint.AfterJournalWrite)]
    [InlineData(FaultPoint.BeforeJournalVerification)]
    public async Task Failure_before_prepared_never_writes_target(FaultPoint point)
    {
        var fixture = new Fixture(new ThrowingInjector(point));

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.FailedBeforeTargetWrite, result.Outcome);
        Assert.Equal(0, fixture.Target.WriteCount);
        Assert.Empty(fixture.Journal.States);
    }

    [Theory]
    [InlineData(FaultPoint.AfterPrepared)]
    [InlineData(FaultPoint.BeforeTargetWriteAttempt)]
    public async Task Failure_after_prepared_but_before_write_aborts_without_target_write(FaultPoint point)
    {
        var fixture = new Fixture(new ThrowingInjector(point));

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.AbortedSafe, result.Outcome);
        Assert.Equal(0, fixture.Target.WriteCount);
        Assert.Equal(TransactionState.AbortedSafe, fixture.Journal.Authoritative!.State);
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
    public async Task Failure_during_first_target_write_enters_verified_rollback()
    {
        var fixture = new Fixture { FailFirstWrite = true };

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RollbackSucceeded, result.Outcome);
        Assert.Equal(2, fixture.Target.WriteCount);
        Assert.Equal(Original, fixture.Target.Bytes);
        Assert.Equal(TransactionState.RollbackSucceeded, fixture.Journal.Authoritative!.State);
    }

    [Fact]
    public async Task Target_hash_mismatch_rolls_back_and_stops_session()
    {
        var fixture = new Fixture { CorruptFirstTargetReadback = true };

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RollbackSucceeded, result.Outcome);
        Assert.True(result.SessionMustStop);
        Assert.Equal(Original, fixture.Target.Bytes);
    }

    [Theory]
    [InlineData(FaultPoint.AfterTargetWrite)]
    [InlineData(FaultPoint.BeforeTargetFlush)]
    [InlineData(FaultPoint.AfterTargetFlush)]
    [InlineData(FaultPoint.BeforeTargetVerification)]
    [InlineData(FaultPoint.AfterTargetVerification)]
    public async Task Failure_after_write_attempt_rolls_back_from_latest_authoritative_record(FaultPoint point)
    {
        var fixture = new Fixture(new ThrowingInjector(point));

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RollbackSucceeded, result.Outcome);
        Assert.False(result.TargetVerified);
        Assert.Equal(Original, fixture.Target.Bytes);
        Assert.Equal(TransactionState.RollbackSucceeded, fixture.Journal.Authoritative!.State);
    }

    [Fact]
    public async Task Failed_rollback_preserves_latest_record_and_requires_recovery()
    {
        var fixture = new Fixture(new ThrowingInjector(FaultPoint.AfterTargetWrite)) { FailWritesAfterFirst = true };

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RecoveryRequired, result.Outcome);
        Assert.True(fixture.Journal.Preserved);
        Assert.DoesNotContain(TransactionState.Committed, fixture.Journal.States);
    }

    [Fact]
    public async Task Cancellation_before_source_read_is_safe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fixture = new Fixture();

        var result = await fixture.ExecuteAsync(cancellation.Token);

        Assert.Equal(TransactionOutcome.CancelledAtSafeBoundary, result.Outcome);
        Assert.Equal(0, fixture.Target.ReadCount);
        Assert.Equal(0, fixture.Target.WriteCount);
    }

    [Theory]
    [InlineData(CancellationStage.SourceRead)]
    [InlineData(CancellationStage.JournalWrite)]
    [InlineData(CancellationStage.JournalFlush)]
    [InlineData(CancellationStage.JournalReadback)]
    public async Task Cancellation_during_preparation_is_classified_as_safe_cancellation(CancellationStage stage)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture { Cancellation = (stage, cancellation) };

        var result = await fixture.ExecuteAsync(cancellation.Token);

        Assert.Equal(TransactionOutcome.CancelledAtSafeBoundary, result.Outcome);
        Assert.Equal(0, fixture.Target.WriteCount);
    }

    [Fact]
    public async Task Cancellation_after_prepared_before_attempt_aborts_without_write()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture(new CancellingInjector(FaultPoint.AfterPrepared, cancellation));

        var result = await fixture.ExecuteAsync(cancellation.Token);

        Assert.Equal(TransactionOutcome.CancelledAtSafeBoundary, result.Outcome);
        Assert.Equal(0, fixture.Target.WriteCount);
        Assert.Equal(TransactionState.AbortedSafe, fixture.Journal.Authoritative!.State);
    }

    [Fact]
    public async Task Cancellation_after_write_attempt_is_deferred_until_commit()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture(new CancellingInjector(FaultPoint.AfterTargetWrite, cancellation));

        var result = await fixture.ExecuteAsync(cancellation.Token);

        Assert.Equal(TransactionOutcome.Committed, result.Outcome);
        Assert.Equal(TransactionState.Committed, fixture.Journal.Authoritative!.State);
    }

    [Fact]
    public async Task Short_final_chunk_does_not_expose_stale_payload_tail()
    {
        var journal = new StrictMemoryJournal(capacity: 64);
        var longChunk = Fixture.CreateDescriptor(64);
        await journal.WritePayloadAsync(longChunk, Original, TestContext.Current.CancellationToken);
        var shortChunk = Fixture.CreateDescriptor(7);
        var shortBytes = Original.AsMemory(0, 7);
        await journal.WritePayloadAsync(shortChunk, shortBytes, TestContext.Current.CancellationToken);
        var readback = new byte[7];

        await journal.ReadPayloadExactlyAsync(shortChunk, readback, TestContext.Current.CancellationToken);

        Assert.Equal(shortBytes.ToArray(), readback);
        Assert.Equal(7, journal.AuthoritativePayloadLength);
    }

    public enum CancellationStage { SourceRead, JournalWrite, JournalFlush, JournalReadback }

    private sealed class Fixture
    {
        private readonly IFaultInjector _faults;
        public MemoryTarget Target { get; } = new(Original);
        public StrictMemoryJournal Journal { get; } = new(Original.Length);
        public ChunkDescriptor Descriptor { get; } = CreateDescriptor(Original.Length);
        public bool CorruptJournalReadback { set => Journal.CorruptReadback = value; }
        public bool CorruptFirstTargetReadback { set => Target.CorruptReadbackOnce = value; }
        public bool FailFirstWrite { set => Target.FailFirstWrite = value; }
        public bool FailWritesAfterFirst { set => Target.FailWritesAfterFirst = value; }
        public (CancellationStage Stage, CancellationTokenSource Source) Cancellation
        {
            set { Target.Cancellation = value; Journal.Cancellation = value; }
        }

        public Fixture() : this(NoFaultInjector.Instance) { }
        public Fixture(IFaultInjector faults) => _faults = faults;
        public ValueTask<TransactionResult> ExecuteAsync(CancellationToken token) =>
            new ChunkRefreshTransaction(Target, Journal, _faults).ExecuteAsync(Descriptor, token);

        public static ChunkDescriptor CreateDescriptor(int length) => new(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            new FileIdentity(123, new FileId128(Convert.FromHexString("0123456789ABCDEF1032547698BADCFE"))),
            length,
            0,
            length,
            new NtfsBasicMetadata(1, 2, 3, 4, 0x20));
    }

    private sealed class MemoryTarget(byte[] initial) : IChunkSource
    {
        public byte[] Bytes { get; } = initial.ToArray();
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public bool CorruptReadbackOnce { get; set; }
        public bool FailFirstWrite { get; set; }
        public bool FailWritesAfterFirst { get; set; }
        public (CancellationStage Stage, CancellationTokenSource Source)? Cancellation { get; set; }

        public ValueTask ReadExactlyAsync(ChunkDescriptor chunk, Memory<byte> destination, CancellationToken cancellationToken)
        {
            ReadCount++;
            if (ReadCount == 1 && Cancellation?.Stage == CancellationStage.SourceRead) Cancellation.Value.Source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            Bytes.AsMemory((int)chunk.Offset, chunk.Length).CopyTo(destination);
            if (CorruptReadbackOnce && WriteCount > 0) { destination.Span[0] ^= 0xff; CorruptReadbackOnce = false; }
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteExactlyAsync(ChunkDescriptor chunk, ReadOnlyMemory<byte> source)
        {
            WriteCount++;
            if (FailFirstWrite && WriteCount == 1) throw new IOException("Injected target write failure.");
            if (FailWritesAfterFirst && WriteCount > 1) throw new IOException("Injected rollback write failure.");
            source.CopyTo(Bytes.AsMemory((int)chunk.Offset, chunk.Length));
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync() => ValueTask.CompletedTask;
    }

    private sealed class StrictMemoryJournal(int capacity) : IRecoveryJournal
    {
        private readonly byte[] _slot = new byte[capacity];
        public JournalRecord? Authoritative { get; private set; }
        public List<TransactionState> States { get; } = [];
        public List<ulong> Sequences { get; } = [];
        public int AuthoritativePayloadLength { get; private set; }
        public bool CorruptReadback { get; set; }
        public bool Preserved { get; private set; }
        public (CancellationStage Stage, CancellationTokenSource Source)? Cancellation { get; set; }

        public ValueTask WritePayloadAsync(ChunkDescriptor chunk, ReadOnlyMemory<byte> original, CancellationToken cancellationToken)
        {
            if (Cancellation?.Stage == CancellationStage.JournalWrite) Cancellation.Value.Source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Length > _slot.Length || original.Length != chunk.Length) throw new ArgumentOutOfRangeException(nameof(chunk));
            original.CopyTo(_slot);
            AuthoritativePayloadLength = chunk.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            if (Cancellation?.Stage == CancellationStage.JournalFlush) Cancellation.Value.Source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask ReadPayloadExactlyAsync(ChunkDescriptor chunk, Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (Cancellation?.Stage == CancellationStage.JournalReadback) Cancellation.Value.Source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Length != AuthoritativePayloadLength || destination.Length != chunk.Length)
                throw new InvalidDataException("Only the active ChunkLength is authoritative.");
            _slot.AsMemory(0, chunk.Length).CopyTo(destination);
            if (CorruptReadback) destination.Span[0] ^= 0xff;
            return ValueTask.CompletedTask;
        }

        public ValueTask<JournalRecord> PublishStateAsync(JournalRecord supplied, TransactionState next)
        {
            if (Authoritative is null)
            {
                if (supplied.SequenceNumber != 0 || supplied.State != TransactionState.Empty || next != TransactionState.Prepared)
                    throw new InvalidOperationException("Initial publication must be Empty(0) -> Prepared(1).");
            }
            else if (supplied != Authoritative)
            {
                throw new InvalidOperationException("Caller supplied a stale or non-authoritative record.");
            }

            if (!IsLegalTransition(supplied.State, next)) throw new InvalidOperationException($"Illegal transition {supplied.State} -> {next}.");
            var published = supplied with { SequenceNumber = supplied.SequenceNumber + 1, State = next };
            Authoritative = published;
            States.Add(next);
            Sequences.Add(published.SequenceNumber);
            return ValueTask.FromResult(published);
        }

        public ValueTask PreserveForRecoveryAsync(JournalRecord record, Exception cause)
        {
            if (Authoritative is not null && record != Authoritative)
                throw new InvalidOperationException("Recovery preservation must receive the latest authoritative record.");
            Preserved = true;
            return ValueTask.CompletedTask;
        }

        private static bool IsLegalTransition(TransactionState current, TransactionState next) => (current, next) switch
        {
            (TransactionState.Empty, TransactionState.Prepared) => true,
            (TransactionState.Prepared, TransactionState.TargetWritten or TransactionState.RollbackRequired or TransactionState.AbortedSafe) => true,
            (TransactionState.TargetWritten, TransactionState.TargetVerified or TransactionState.RollbackRequired) => true,
            (TransactionState.TargetVerified, TransactionState.Committed or TransactionState.RollbackRequired) => true,
            (TransactionState.RollbackRequired, TransactionState.RollbackSucceeded) => true,
            _ => false
        };
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
