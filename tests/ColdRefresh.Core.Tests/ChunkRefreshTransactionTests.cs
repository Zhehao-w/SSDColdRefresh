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
        Assert.Equal(1, fixture.Target.MetadataRestoreCount);
    }

    [Fact]
    public async Task Logical_refresh_over_128MiB_uses_three_transactions_without_sequence_reset()
    {
        var fixture = new Fixture();
        var secondChunk = fixture.Descriptor with { SessionId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f") };
        var thirdChunk = fixture.Descriptor with { SessionId = Guid.Parse("20314253-6475-8697-a8b9-cadbecfd0e1f") };
        var logicalFileSize = (2L * RefreshOptions.DefaultChunkSize) + 1;
        var plannedChunkCount = (logicalFileSize + RefreshOptions.DefaultChunkSize - 1) / RefreshOptions.DefaultChunkSize;

        var first = await fixture.ExecuteAsync(fixture.Descriptor, TestContext.Current.CancellationToken);
        var second = await fixture.ExecuteAsync(secondChunk, TestContext.Current.CancellationToken);
        var third = await fixture.ExecuteAsync(thirdChunk, TestContext.Current.CancellationToken);

        Assert.Equal(3, plannedChunkCount);
        Assert.Equal(TransactionOutcome.Committed, first.Outcome);
        Assert.Equal(TransactionOutcome.Committed, second.Outcome);
        Assert.Equal(TransactionOutcome.Committed, third.Outcome);
        Assert.Equal(
            [
                TransactionState.Prepared, TransactionState.TargetWritten, TransactionState.TargetVerified, TransactionState.Committed,
                TransactionState.Prepared, TransactionState.TargetWritten, TransactionState.TargetVerified, TransactionState.Committed,
                TransactionState.Prepared, TransactionState.TargetWritten, TransactionState.TargetVerified, TransactionState.Committed
            ],
            fixture.Journal.States);
        Assert.Equal([1UL, 2UL, 3UL, 4UL, 5UL, 6UL, 7UL, 8UL, 9UL, 10UL, 11UL, 12UL], fixture.Journal.Sequences);
    }

    [Fact]
    public async Task Reused_payload_before_prepared_does_not_invalidate_terminal_authority()
    {
        var fixture = new Fixture();
        await fixture.ExecuteAsync(TestContext.Current.CancellationToken);
        var committed = fixture.Journal.Authoritative;
        var nextChunk = fixture.Descriptor with { SessionId = Guid.NewGuid() };
        var nextBytes = Original.Reverse().ToArray();

        await fixture.Journal.WritePayloadAsync(nextChunk, nextBytes, TestContext.Current.CancellationToken);
        await fixture.Journal.FlushAsync(TestContext.Current.CancellationToken);
        var readback = new byte[nextBytes.Length];
        await fixture.Journal.ReadPayloadExactlyAsync(nextChunk, readback, TestContext.Current.CancellationToken);
        Assert.Equal(nextBytes, readback);

        Assert.Equal(committed, fixture.Journal.Authoritative);
        Assert.Equal(TransactionState.Committed, fixture.Journal.Authoritative!.State);
        Assert.True(fixture.Journal.IsAuthoritativePayloadValidForStartup());
        Assert.Equal(RecoveryAction.NoAction, RecoveryPolicy.Decide(TransactionState.Committed, targetMatchesOriginalHash: false));
    }

    [Fact]
    public async Task Published_prepared_makes_reused_payload_authoritative()
    {
        var fixture = new Fixture();
        await fixture.ExecuteAsync(TestContext.Current.CancellationToken);
        var nextChunk = fixture.Descriptor with { SessionId = Guid.NewGuid() };
        var nextBytes = Original.Reverse().ToArray();
        await fixture.Journal.WritePayloadAsync(nextChunk, nextBytes, TestContext.Current.CancellationToken);
        await fixture.Journal.FlushAsync(TestContext.Current.CancellationToken);
        var readback = new byte[nextBytes.Length];
        await fixture.Journal.ReadPayloadExactlyAsync(nextChunk, readback, TestContext.Current.CancellationToken);
        var hash = System.Security.Cryptography.SHA256.HashData(readback);

        var prepared = await fixture.Journal.PublishPreparedAsync(nextChunk, hash, hash);

        Assert.Equal(TransactionState.Prepared, prepared.State);
        Assert.Equal(5UL, prepared.SequenceNumber);
        Assert.True(fixture.Journal.IsAuthoritativePayloadValidForStartup());
        fixture.Journal.CorruptPayloadForTest();
        Assert.False(fixture.Journal.IsAuthoritativePayloadValidForStartup());
    }

    [Fact]
    public async Task Torn_prepared_keeps_previous_terminal_authority_and_ignores_new_payload()
    {
        var fixture = new Fixture();
        await fixture.ExecuteAsync(TestContext.Current.CancellationToken);
        var committed = fixture.Journal.Authoritative;
        var nextChunk = fixture.Descriptor with { SessionId = Guid.NewGuid() };
        var nextBytes = Original.Reverse().ToArray();
        await fixture.Journal.WritePayloadAsync(nextChunk, nextBytes, TestContext.Current.CancellationToken);
        await fixture.Journal.FlushAsync(TestContext.Current.CancellationToken);
        var readback = new byte[nextBytes.Length];
        await fixture.Journal.ReadPayloadExactlyAsync(nextChunk, readback, TestContext.Current.CancellationToken);
        var hash = System.Security.Cryptography.SHA256.HashData(readback);
        fixture.Journal.TearNextPreparedPublication = true;

        await Assert.ThrowsAsync<IOException>(async () =>
            await fixture.Journal.PublishPreparedAsync(nextChunk, hash, hash));

        Assert.Equal(committed, fixture.Journal.Authoritative);
        Assert.True(fixture.Journal.IsAuthoritativePayloadValidForStartup());
    }

    [Fact]
    public async Task New_prepared_cannot_supersede_unresolved_transaction()
    {
        var fixture = new Fixture();
        await fixture.Journal.PrepareForTestAsync(fixture.Descriptor, Original, TestContext.Current.CancellationToken);
        var nextChunk = fixture.Descriptor with { SessionId = Guid.NewGuid() };
        var nextBytes = Original.Reverse().ToArray();
        await fixture.Journal.WritePayloadAsync(nextChunk, nextBytes, TestContext.Current.CancellationToken);
        var hash = System.Security.Cryptography.SHA256.HashData(nextBytes);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Journal.PublishPreparedAsync(nextChunk, hash, hash));
        Assert.Equal(TransactionState.Prepared, fixture.Journal.Authoritative!.State);
    }

    [Theory]
    [InlineData(TransactionState.AbortedSafe)]
    [InlineData(TransactionState.RollbackSucceeded)]
    public async Task New_prepared_can_follow_resolved_non_success_terminal(TransactionState terminal)
    {
        var fixture = new Fixture();
        fixture.Journal.TerminalPublicationGuard = () => true;
        var current = await fixture.Journal.PrepareForTestAsync(fixture.Descriptor, Original, TestContext.Current.CancellationToken);
        if (terminal == TransactionState.RollbackSucceeded)
            current = await fixture.Journal.PublishStateAsync(current, TransactionState.RollbackRequired);
        current = await fixture.Journal.PublishStateAsync(current, terminal);
        var nextChunk = fixture.Descriptor with { SessionId = Guid.NewGuid() };
        var nextBytes = Original.Reverse().ToArray();
        await fixture.Journal.WritePayloadAsync(nextChunk, nextBytes, TestContext.Current.CancellationToken);
        var hash = System.Security.Cryptography.SHA256.HashData(nextBytes);

        var prepared = await fixture.Journal.PublishPreparedAsync(nextChunk, hash, hash);

        Assert.Equal(current.SequenceNumber + 1, prepared.SequenceNumber);
        Assert.Equal(TransactionState.Prepared, prepared.State);
    }

    [Fact]
    public async Task Strict_journal_rejects_stale_record()
    {
        var fixture = new Fixture();
        var descriptor = fixture.Descriptor;
        var prepared = await fixture.Journal.PrepareForTestAsync(descriptor, Original, TestContext.Current.CancellationToken);
        var written = await fixture.Journal.PublishStateAsync(prepared, TransactionState.TargetWritten);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Journal.PublishStateAsync(prepared, TransactionState.TargetVerified));
        Assert.Equal(written, fixture.Journal.Authoritative);
    }

    [Fact]
    public async Task Strict_journal_rejects_commit_from_target_written()
    {
        var fixture = new Fixture();
        var prepared = await fixture.Journal.PrepareForTestAsync(fixture.Descriptor, Original, TestContext.Current.CancellationToken);
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
        Assert.False(result.RefreshSuccessfullyCompleted);
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
    public async Task Metadata_failure_cannot_publish_any_terminal_state()
    {
        var fixture = new Fixture { FailMetadataRestore = true };

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RecoveryRequired, result.Outcome);
        Assert.True(fixture.Journal.Preserved);
        Assert.Equal(1, fixture.Target.WriteCount);
        Assert.Equal(TransactionState.TargetVerified, fixture.Journal.Authoritative!.State);
        Assert.DoesNotContain(TransactionState.Committed, fixture.Journal.States);
        Assert.DoesNotContain(TransactionState.AbortedSafe, fixture.Journal.States);
        Assert.DoesNotContain(TransactionState.RollbackSucceeded, fixture.Journal.States);
    }

    [Fact]
    public async Task Final_publication_failure_after_target_verified_never_rewrites_content()
    {
        var fixture = new Fixture { FailPublicationForState = TransactionState.Committed };

        var result = await fixture.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransactionOutcome.RecoveryRequired, result.Outcome);
        Assert.True(fixture.Journal.Preserved);
        Assert.Equal(1, fixture.Target.WriteCount);
        Assert.Equal(TransactionState.TargetVerified, fixture.Journal.Authoritative!.State);
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
        public bool FailMetadataRestore { set => Target.FailMetadataRestore = value; }
        public TransactionState FailPublicationForState { set => Journal.FailPublicationForState = value; }
        public (CancellationStage Stage, CancellationTokenSource Source) Cancellation
        {
            set { Target.Cancellation = value; Journal.Cancellation = value; }
        }

        public Fixture() : this(NoFaultInjector.Instance) { }
        public Fixture(IFaultInjector faults)
        {
            _faults = faults;
            Journal.TerminalPublicationGuard = () => Target.MetadataVerified;
        }
        public ValueTask<TransactionResult> ExecuteAsync(CancellationToken token) =>
            new ChunkRefreshTransaction(Target, Journal, _faults).ExecuteAsync(Descriptor, token);
        public ValueTask<TransactionResult> ExecuteAsync(ChunkDescriptor descriptor, CancellationToken token) =>
            new ChunkRefreshTransaction(Target, Journal, _faults).ExecuteAsync(descriptor, token);

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
        public bool FailMetadataRestore { get; set; }
        public bool MetadataVerified { get; private set; }
        public int MetadataRestoreCount { get; private set; }
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

        public ValueTask RestoreAndVerifyMetadataAsync(ChunkDescriptor chunk)
        {
            MetadataRestoreCount++;
            if (FailMetadataRestore) throw new IOException("Injected metadata restoration failure.");
            MetadataVerified = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StrictMemoryJournal(int capacity) : IRecoveryJournal
    {
        private readonly byte[] _slot = new byte[capacity];
        private ChunkDescriptor? _pendingChunk;
        public JournalRecord? Authoritative { get; private set; }
        public List<TransactionState> States { get; } = [];
        public List<ulong> Sequences { get; } = [];
        public int AuthoritativePayloadLength { get; private set; }
        public bool CorruptReadback { get; set; }
        public bool Preserved { get; private set; }
        public Func<bool>? TerminalPublicationGuard { get; set; }
        public TransactionState? FailPublicationForState { get; set; }
        public bool TearNextPreparedPublication { get; set; }
        public (CancellationStage Stage, CancellationTokenSource Source)? Cancellation { get; set; }

        public ValueTask WritePayloadAsync(ChunkDescriptor chunk, ReadOnlyMemory<byte> original, CancellationToken cancellationToken)
        {
            if (Cancellation?.Stage == CancellationStage.JournalWrite) Cancellation.Value.Source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Length > _slot.Length || original.Length != chunk.Length) throw new ArgumentOutOfRangeException(nameof(chunk));
            original.CopyTo(_slot);
            AuthoritativePayloadLength = chunk.Length;
            _pendingChunk = chunk;
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
            if (next == FailPublicationForState)
                throw new IOException($"Injected {next} publication failure.");
            if ((next is TransactionState.Committed or TransactionState.AbortedSafe or TransactionState.RollbackSucceeded) &&
                TerminalPublicationGuard?.Invoke() != true)
                throw new InvalidOperationException("Terminal state requires verified metadata restoration.");
            if (Authoritative is null || supplied != Authoritative)
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

        public ValueTask<JournalRecord> PublishPreparedAsync(
            ChunkDescriptor chunk,
            ReadOnlyMemory<byte> originalChunkHash,
            ReadOnlyMemory<byte> journalDataHash)
        {
            if (TearNextPreparedPublication)
            {
                TearNextPreparedPublication = false;
                throw new IOException("Injected torn Prepared header.");
            }
            if (Authoritative is not null && Authoritative.State is not
                (TransactionState.Empty or TransactionState.Committed or TransactionState.AbortedSafe or TransactionState.RollbackSucceeded))
                throw new InvalidOperationException("A new transaction cannot supersede unresolved authority.");
            if (_pendingChunk != chunk || AuthoritativePayloadLength != chunk.Length)
                throw new InvalidOperationException("Prepared must reference the verified pending payload.");
            var actualHash = System.Security.Cryptography.SHA256.HashData(_slot.AsSpan(0, chunk.Length));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualHash, originalChunkHash.Span) ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualHash, journalDataHash.Span))
                throw new InvalidDataException("Prepared hashes do not match the pending payload.");
            if (Authoritative?.SequenceNumber == ulong.MaxValue)
                throw new InvalidOperationException("Journal sequence exhausted.");

            var nextSequence = Authoritative is null ? 1UL : Authoritative.SequenceNumber + 1;
            var prepared = new JournalRecord(
                nextSequence,
                TransactionState.Prepared,
                chunk,
                originalChunkHash.ToArray(),
                journalDataHash.ToArray());
            Authoritative = prepared;
            States.Add(TransactionState.Prepared);
            Sequences.Add(nextSequence);
            return ValueTask.FromResult(prepared);
        }

        public async ValueTask<JournalRecord> PrepareForTestAsync(
            ChunkDescriptor chunk,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            await WritePayloadAsync(chunk, bytes, cancellationToken);
            await FlushAsync(cancellationToken);
            var readback = new byte[chunk.Length];
            await ReadPayloadExactlyAsync(chunk, readback, cancellationToken);
            var hash = System.Security.Cryptography.SHA256.HashData(readback);
            return await PublishPreparedAsync(chunk, hash, hash);
        }

        public bool IsAuthoritativePayloadValidForStartup()
        {
            if (Authoritative is null || Authoritative.State is
                TransactionState.Empty or TransactionState.Committed or TransactionState.AbortedSafe or TransactionState.RollbackSucceeded)
                return true;
            if (Authoritative.State == TransactionState.RecoveryRequired)
                return false;
            if (AuthoritativePayloadLength != Authoritative.Chunk.Length)
                return false;
            var hash = System.Security.Cryptography.SHA256.HashData(_slot.AsSpan(0, Authoritative.Chunk.Length));
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hash, Authoritative.JournalDataHash);
        }

        public void CorruptPayloadForTest() => _slot[0] ^= 0xff;

        public ValueTask PreserveForRecoveryAsync(JournalRecord record, Exception cause)
        {
            if (Authoritative is not null && record != Authoritative)
                throw new InvalidOperationException("Recovery preservation must receive the latest authoritative record.");
            Preserved = true;
            return ValueTask.CompletedTask;
        }

        private static bool IsLegalTransition(TransactionState current, TransactionState next) => (current, next) switch
        {
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
