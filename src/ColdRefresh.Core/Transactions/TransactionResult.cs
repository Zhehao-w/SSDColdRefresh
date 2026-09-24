namespace ColdRefresh.Core.Transactions;

public enum TransactionOutcome
{
    Committed,
    CancelledAtSafeBoundary,
    FailedBeforeTargetWrite,
    AbortedSafe,
    RollbackSucceeded,
    RecoveryRequired
}

public sealed record TransactionResult(TransactionOutcome Outcome, Exception? Error = null)
{
    public bool RefreshSuccessfullyCompleted => Outcome == TransactionOutcome.Committed;
    public bool SessionMustStop => Outcome is not TransactionOutcome.Committed;
}
