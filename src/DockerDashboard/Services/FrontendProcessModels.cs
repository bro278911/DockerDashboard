using DockerDashboard.Models;

namespace DockerDashboard.Services;

public enum FrontendProcessKind
{
    Dev,
    OneShot
}

public enum FrontendProcessState
{
    Starting,
    Running,
    Stopped,
    Crashed
}

/// <summary>啟動結果。互斥由 manager 判定並回報，是否停舊起新的決策留給 ViewModel（需跳確認框）</summary>
public abstract record StartResult
{
    public sealed record Started : StartResult;
    public sealed record AlreadyRunning : StartResult;
    public sealed record GroupOccupied(FrontendProject Occupant) : StartResult;
    public sealed record Failed(string Message) : StartResult;
}

public sealed class FrontendOutputEventArgs(FrontendProject project, string line) : EventArgs
{
    public FrontendProject Project { get; } = project;
    public string Line { get; } = line;
}

public sealed class FrontendStateEventArgs(
    FrontendProject project, FrontendProcessKind kind, FrontendProcessState state, int exitCode, string label)
    : EventArgs
{
    public FrontendProject Project { get; } = project;
    public FrontendProcessKind Kind { get; } = kind;
    public FrontendProcessState State { get; } = state;
    public int ExitCode { get; } = exitCode;
    public string Label { get; } = label;
}
