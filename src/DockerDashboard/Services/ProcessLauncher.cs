using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DockerDashboard.Services;

/// <summary>
/// 子行程建立的唯一入口。所有由本工具啟動、且應隨 App 一起結束的行程都指派到同一個
/// Job Object；該 Job 設了 KILL_ON_JOB_CLOSE，handle 永不主動關閉，因此 App 以任何方式
/// 結束（正常關閉、當機、工作管理員強殺）時，OS 會連帶終止整個行程樹。
///
/// 判定規則：UseShellExecute = true，或刻意要活得比 App 久的行程（自動更新、開瀏覽器）
/// 走 StartDetached 不納管；其餘我方導向串流、由我方負責回收的子行程走 Start。
///
/// 已知殘餘風險：process.Start() 與 AssignProcessToJobObject 之間有極短窗口，若子行程恰好在
/// 這段期間內就 fork 出孫行程，孫行程不會被納入 Job（不會被連帶回收）。正解需改用
/// CREATE_SUSPENDED 搭配自寫 CreateProcess P/Invoke，在 assign 完成後才 Resume，但實作與維護
/// 成本相對這個極窄窗口的實際發生機率不成比例，故此處刻意不採用，僅記錄於此。
/// </summary>
public static class ProcessLauncher
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // 永不關閉：handle 關閉的那一刻就是 Job 內行程被終止的時機，我們要的正是「隨 App 行程結束」
    private static readonly IntPtr _job = CreateAppJob();

    /// <summary>
    /// Job 建立或設定失敗時的原因（含 Win32 錯誤碼）；成功則為 null。
    /// 本工具「App 結束必連帶回收子行程」的保證整個建立在 Job Object 上，Release 組態沒有
    /// Debug.WriteLine 可看，這是唯一能讓 UI 層察覺「保證失效」的管道
    /// </summary>
    internal static string? JobUnavailableReason { get; private set; }

    public static Process Start(ProcessStartInfo psi)
    {
        if (psi.UseShellExecute)
            throw new ArgumentException(
                "UseShellExecute = true 的行程不會納入 Job Object 追蹤，請改用 StartDetached。",
                nameof(psi));

        var process = new Process { StartInfo = psi };
        process.Start();
        TryAssignToJob(process);
        return process;
    }

    /// <summary>啟動刻意要活得比 App 久的行程（自動更新、開瀏覽器），不納入 Job</summary>
    public static Process? StartDetached(ProcessStartInfo psi) => Process.Start(psi);

    internal static bool IsInAppJob(Process process)
    {
        if (_job == IntPtr.Zero) return false;
        try
        {
            return IsProcessInJob(process.Handle, _job, out var result) && result;
        }
        catch
        {
            return false;
        }
    }

    private static void TryAssignToJob(Process process)
    {
        if (_job == IntPtr.Zero) return;
        try
        {
            // 行程可能在指派前就結束（短命指令），失敗屬正常，不影響功能
            if (!AssignProcessToJobObject(_job, process.Handle))
                Debug.WriteLine($"[ProcessLauncher] 指派行程到 Job 失敗: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessLauncher] 指派行程到 Job 發生例外: {ex.Message}");
        }
    }

    private static IntPtr CreateAppJob()
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                var reason = $"建立 Job 失敗（Win32 錯誤碼 {Marshal.GetLastWin32Error()}）";
                Debug.WriteLine($"[ProcessLauncher] {reason}");
                JobUnavailableReason = reason;
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    // 設定失敗時關閉孤兒 handle。此處不違反「Job handle 永不關閉」的約束：
                    // 該約束針對「成功建立且實際在使用」的 job；設定失敗的 handle 是未使用的孤兒，應主動回收
                    var reason = $"設定 Job 失敗（Win32 錯誤碼 {Marshal.GetLastWin32Error()}）";
                    Debug.WriteLine($"[ProcessLauncher] {reason}");
                    JobUnavailableReason = reason;
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return job;
        }
        catch (Exception ex)
        {
            // Job 只是保險機制，不可用時降級為原本的 Kill(entireProcessTree) 行為
            var reason = $"Job 不可用，降級為僅靠 Kill 回收: {ex.Message}";
            Debug.WriteLine($"[ProcessLauncher] {reason}");
            JobUnavailableReason = reason;
            return IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        IntPtr processHandle, IntPtr jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
