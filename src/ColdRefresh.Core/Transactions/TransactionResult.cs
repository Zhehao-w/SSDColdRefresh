namespace ColdRefresh.Core.Transactions;

public enum TransactionOutcome
{
    Committed,
    CancelledAtSafeBoundary,
    FailedBeforeTargetWrite,
    RollbackSucceeded,
    RecoveryRequired
}

public sealed record TransactionResult(TransactionOutcome Outcome, Exception? Error = null)
{
    public bool TargetVerified => Outcome == TransactionOutcome.Committed;
    public bool SessionMustStop => Outcome is not TransactionOutcome.Committed;
}
