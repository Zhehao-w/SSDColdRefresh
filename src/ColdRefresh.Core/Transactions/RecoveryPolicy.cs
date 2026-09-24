namespace ColdRefresh.Core.Transactions;

public enum RecoveryAction
{
    NoAction,
    RestoreMetadataThenCommit,
    RestoreMetadataThenMarkAbortedSafeWithoutContentWrite,
    BlockForManualRecovery
}

/// <summary>Pure startup policy. Storage adapters must validate header, payload, identity, and size first.</summary>
public static class RecoveryPolicy
{
    public static RecoveryAction Decide(TransactionState state, bool targetMatchesOriginalHash) => state switch
    {
        TransactionState.Empty or TransactionState.Committed or TransactionState.AbortedSafe or TransactionState.RollbackSucceeded
            => RecoveryAction.NoAction,
        TransactionState.RecoveryRequired => RecoveryAction.BlockForManualRecovery,
        TransactionState.TargetVerified when targetMatchesOriginalHash => RecoveryAction.RestoreMetadataThenCommit,
        TransactionState.Prepared or TransactionState.TargetWritten or TransactionState.RollbackRequired
            when targetMatchesOriginalHash => RecoveryAction.RestoreMetadataThenMarkAbortedSafeWithoutContentWrite,
        _ => RecoveryAction.BlockForManualRecovery
    };
}
