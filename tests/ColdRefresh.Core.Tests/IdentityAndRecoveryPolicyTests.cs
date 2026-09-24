using ColdRefresh.Core.Models;
using ColdRefresh.Core.Transactions;

namespace ColdRefresh.Core.Tests;

public sealed class IdentityAndRecoveryPolicyTests
{
    [Fact]
    public void File_id_128_round_trips_opaque_bytes_exactly()
    {
        var expected = Convert.FromHexString("0123456789ABCDEF1032547698BADCFE");

        var id = FileId128.ReadFrom(expected);
        Span<byte> serialized = stackalloc byte[FileId128.ByteLength];
        id.WriteTo(serialized);

        Assert.Equal(expected, serialized.ToArray());
        Assert.Equal(id, FileId128.ReadFrom(serialized));
        Assert.Equal(id.GetHashCode(), FileId128.ReadFrom(serialized).GetHashCode());
    }

    [Fact]
    public void Session_id_uses_explicit_rfc4122_network_byte_order()
    {
        var session = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Span<byte> bytes = stackalloc byte[JournalBinaryEncoding.SessionIdLength];

        JournalBinaryEncoding.WriteSessionId(session, bytes);

        Assert.Equal("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(bytes));
        Assert.Equal(session, JournalBinaryEncoding.ReadSessionId(bytes));
    }

    [Theory]
    [InlineData(TransactionState.Prepared)]
    [InlineData(TransactionState.TargetWritten)]
    [InlineData(TransactionState.RollbackRequired)]
    [InlineData(TransactionState.RecoveryRequired)]
    public void Matching_content_in_incomplete_state_is_aborted_safe_without_write(TransactionState state) =>
        Assert.Equal(RecoveryAction.MarkAbortedSafeWithoutWrite, RecoveryPolicy.Decide(state, targetMatchesOriginalHash: true));

    [Fact]
    public void Target_written_alone_can_never_imply_refresh_success() =>
        Assert.NotEqual(RecoveryAction.CommitVerified, RecoveryPolicy.Decide(TransactionState.TargetWritten, targetMatchesOriginalHash: true));

    [Fact]
    public void Only_target_verified_evidence_can_reconcile_to_commit() =>
        Assert.Equal(RecoveryAction.CommitVerified, RecoveryPolicy.Decide(TransactionState.TargetVerified, targetMatchesOriginalHash: true));
}
