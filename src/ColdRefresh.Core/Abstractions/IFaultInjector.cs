namespace ColdRefresh.Core.Abstractions;

public interface IFaultInjector
{
    ValueTask AtAsync(FaultPoint point);
}

public enum FaultPoint
{
    BeforeJournalWrite,
    AfterJournalWrite,
    BeforeJournalVerification,
    AfterPrepared,
    BeforeTargetWriteAttempt,
    AfterTargetWrite,
    BeforeTargetFlush,
    AfterTargetFlush,
    BeforeTargetVerification,
    AfterTargetVerification,
    DuringStateUpdate,
    DuringRollback,
    AfterRollbackWrite,
    BeforeRollbackVerification,
    DuringMetadataRestore
}

public sealed class NoFaultInjector : IFaultInjector
{
    public static NoFaultInjector Instance { get; } = new();
    private NoFaultInjector() { }
    public ValueTask AtAsync(FaultPoint point) => ValueTask.CompletedTask;
}
