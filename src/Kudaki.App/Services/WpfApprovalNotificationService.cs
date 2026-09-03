using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Kudaki.App.Services;

// IApprovalNotificationService の WPF 実装。段階的エスカレーション方式:
//
//   到着時   : ビープ + タスクバー点滅 + タスクバー Paused 表示 (+ 最小化中なら非アクティブ復元)
//   無反応時 : RepeatIntervalSeconds ごとにビープ + 点滅を再発火
//   停止条件 : (a) 承認待ちが全部片付いた → Clear()
//              (b) Kudaki ウィンドウがアクティブになった → 気づいたとみなして再催促だけ停止
//
// フォアグラウンド奪取 (App.BringMainWindowToFront の Topmost トリック) は意図的に使わない。
// あちらは「先生が Kudaki を再起動して明示的に呼び出した」ケースなので奪取が正しいが、
// AI からの提案到着は先生が別作業中に割り込む形なのでキー入力を奪うと事故になる。
public sealed class WpfApprovalNotificationService : IApprovalNotificationService
{
    private readonly Window _owner;
    private readonly IAppSettingsStore _settingsStore;
    private readonly DispatcherTimer _escalationTimer;

    public WpfApprovalNotificationService(Window owner, IAppSettingsStore settingsStore)
    {
        _owner = owner;
        _settingsStore = settingsStore;

        _escalationTimer = new DispatcherTimer();
        _escalationTimer.Tick += (_, _) => Alert(LoadSettings(), restoreIfMinimized: false);

        // 先生がウィンドウを前に出した = 気づいた とみなして再催促を止める。
        // 承認待ち自体はまだ残っているので、タスクバー表示とタブバッジは Clear() まで消さない。
        _owner.Activated += (_, _) => _escalationTimer.Stop();
    }

    public void NotifyPendingArrived()
    {
        var settings = LoadSettings();
        SetTaskbarWaiting(true);
        Alert(settings, restoreIfMinimized: settings.RestoreIfMinimized);

        // 既にアクティブなら先生の目の前に Diff Overlay が出ているので再催促は要らない。
        _escalationTimer.Stop();
        if (!_owner.IsActive && settings.RepeatIntervalSeconds > 0)
        {
            _escalationTimer.Interval = TimeSpan.FromSeconds(settings.RepeatIntervalSeconds);
            _escalationTimer.Start();
        }
    }

    public void Clear()
    {
        _escalationTimer.Stop();
        StopFlash();
        SetTaskbarWaiting(false);
    }

    private ApprovalNotificationSettings LoadSettings()
    {
        // 設定変更を再起動なしで反映させたいので都度読む。呼び出し頻度は
        // 「提案到着」と「数十秒ごとの再催促」だけなので JSON 1 本の read で十分。
        return _settingsStore.Load().ApprovalNotification;
    }

    private void Alert(ApprovalNotificationSettings settings, bool restoreIfMinimized)
    {
        if (settings.Sound)
        {
            // 固定周波数の Console.Beep ではなく OS のサウンドテーマに従わせる。
            // 先生がミュートしていればちゃんと鳴らない。
            System.Media.SystemSounds.Exclamation.Play();
        }
        if (restoreIfMinimized && _owner.WindowState == WindowState.Minimized)
        {
            RestoreWithoutActivating();
        }
        if (settings.FlashTaskbar)
        {
            StartFlash();
        }
    }

    // TaskbarItemInfo.ProgressState = Paused (黄色バー) で「止まって待っている」ことを示す。
    // 点滅が終わった後も残る静的サインなので、後からタスクバーを見ただけで気づける。
    private void SetTaskbarWaiting(bool waiting)
    {
        _owner.TaskbarItemInfo ??= new TaskbarItemInfo();
        if (waiting)
        {
            _owner.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Paused;
            _owner.TaskbarItemInfo.ProgressValue = 1.0;
        }
        else
        {
            _owner.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            _owner.TaskbarItemInfo.ProgressValue = 0.0;
        }
    }

    // WindowState = Normal はアクティブ化してフォーカスを奪うので使えない。
    // SW_SHOWNOACTIVATE なら「見える状態に戻すがフォーカスは渡さない」。
    private void RestoreWithoutActivating()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }

    private void StartFlash()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        var info = NewFlashInfo(hwnd, FLASHW_ALL | FLASHW_TIMERNOFG, uCount: uint.MaxValue);
        FlashWindowEx(ref info);
    }

    private void StopFlash()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        var info = NewFlashInfo(hwnd, FLASHW_STOP, uCount: 0);
        FlashWindowEx(ref info);
    }

    // Window がまだ表示されていない間は IntPtr.Zero が返る。呼び出し側でガードする。
    private IntPtr Handle => new WindowInteropHelper(_owner).Handle;

    private static FLASHWINFO NewFlashInfo(IntPtr hwnd, uint flags, uint uCount) => new()
    {
        cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
        hwnd = hwnd,
        dwFlags = flags,
        uCount = uCount,
        dwTimeout = 0,  // 0 = 既定のカーソル点滅レートに従う
    };

    // ---- user32 P/Invoke ----
    // WPF にタスクバー点滅の標準 API が無いので FlashWindowEx を直接叩く。

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_STOP = 0x00000000;
    private const uint FLASHW_ALL = 0x00000003;       // キャプションとタスクバーの両方
    private const uint FLASHW_TIMERNOFG = 0x0000000C; // フォアグラウンドになるまで点滅継続
    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
