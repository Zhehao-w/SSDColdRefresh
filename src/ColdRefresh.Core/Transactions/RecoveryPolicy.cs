namespace ColdRefresh.Core.Transactions;

public enum RecoveryAction
{
    NoAction,
    CommitVerified,
    MarkAbortedSafeWithoutWrite,
    RestoreThenMarkAbortedSafe,
    BlockForManualRecovery
}

/// <summary>Pure startup policy. Storage adapters must validate header, payload, identity, and size first.</summary>
public static class RecoveryPolicy
{
    public static RecoveryAction Decide(TransactionState state, bool targetMatchesOriginalHash) => state switch
    {
        TransactionState.Empty or TransactionState.Committed or TransactionState.AbortedSafe or TransactionState.RollbackSucceeded
            => RecoveryAction.NoAction,
        TransactionState.TargetVerified when targetMatchesOriginalHash => RecoveryAction.CommitVerified,
        TransactionState.TargetVerified => RecoveryAction.RestoreThenMarkAbortedSafe,
        TransactionState.Prepared or TransactionState.TargetWritten or TransactionState.RollbackRequired or TransactionState.RecoveryRequired
            when targetMatchesOriginalHash => RecoveryAction.MarkAbortedSafeWithoutWrite,
        TransactionState.Prepared or TransactionState.TargetWritten or TransactionState.RollbackRequired or TransactionState.RecoveryRequired
            => RecoveryAction.RestoreThenMarkAbortedSafe,
        _ => RecoveryAction.BlockForManualRecovery
    };
}
