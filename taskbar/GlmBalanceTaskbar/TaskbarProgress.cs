using System.Runtime.InteropServices;

namespace GlmBalanceTaskbar;

/// <summary>
/// 任务栏按钮原生进度条（ITaskbarList3：SetProgressValue / SetProgressState）。
/// </summary>
public static class TaskbarProgress
{
    /// <summary>无进度（清除覆盖层）</summary>
    public const int FlagNoProgress = 0x0;
    /// <summary>正常（绿色）</summary>
    public const int FlagNormal = 0x2;
    /// <summary>错误（红色）</summary>
    public const int FlagError = 0x4;

    static ITaskbarList3? _instance;

    static ITaskbarList3 Instance => _instance ??= (ITaskbarList3)new TaskbarListCoClass();

    /// <summary>在任务栏按钮上叠加进度条：value/total（0-100）。</summary>
    public static void SetValue(IntPtr hwnd, ulong value, ulong total)
    {
        try
        {
            Instance.SetProgressState(hwnd, FlagNormal);
            Instance.SetProgressValue(hwnd, value, total);
        }
        catch
        {
            /* 任务栏不可用时静默 */
        }
    }

    /// <summary>清除任务栏按钮上的进度条。</summary>
    public static void Clear(IntPtr hwnd)
    {
        try
        {
            Instance.SetProgressState(hwnd, FlagNoProgress);
        }
        catch
        {
            /* ignore */
        }
    }

    [ComImport]
    [Guid("56FDF344-FD6D-46d9-958A-006097C9A090")]
    private class TaskbarListCoClass
    {
    }

    /// <summary>仅声明到 SetProgressState（方法顺序必须与 COM 接口一致）。</summary>
    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
    }
}
