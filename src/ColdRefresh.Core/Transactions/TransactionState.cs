namespace ColdRefresh.Core.Transactions;

public enum TransactionState : uint
{
    Empty = 0,
    Prepared = 1,
    TargetWritten = 2,
    TargetVerified = 3,
    Committed = 4,
    RollbackRequired = 5,
    RollbackSucceeded = 6,
    RecoveryRequired = 7,
    AbortedSafe = 8
}
