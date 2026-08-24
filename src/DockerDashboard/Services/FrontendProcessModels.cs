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
    FrontendProject project, FrontendProcessKind kind, FrontendProcessState state, int exitCode, string label,
    bool userStopped)
    : EventArgs
{
    public FrontendProject Project { get; } = project;
    public FrontendProcessKind Kind { get; } = kind;
    public FrontendProcessState State { get; } = state;
    public int ExitCode { get; } = exitCode;
    public string Label { get; } = label;

    // 一次性指令一律回報 Stopped（見裁定：測試失敗不是崩潰），此旗標補回「是否使用者主動取消」
    // 這個事實，讓 UI 決定要顯示「已取消」還是依 exit code 顯示成功/失敗訊息
    public bool UserStopped { get; } = userStopped;
}
