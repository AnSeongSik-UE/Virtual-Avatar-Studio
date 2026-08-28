using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public sealed class WindowsVrmDropReceiver : MonoBehaviour
{
    private readonly ConcurrentQueue<string> _pendingPaths = new();
    private Func<string, Task<bool>> _importAsync;
    private Task _processingTask;
    private int _lifecycleVersion;
    private volatile bool _acceptingDrops;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private int _hookRetryFrames;
    private const uint WmDropFiles = 0x0233;
    private const int WindowProcedureIndex = -4;
    private const uint QueryFileCount = 0xFFFFFFFF;
    private const uint GetWindowOwner = 4;

    private static readonly object WindowProcedureRootLock = new();
    private static readonly List<WindowProcedureDelegate> RootedWindowProcedures = new();

    private IntPtr _windowHandle;
    private IntPtr _previousWindowProcedure;
    private WindowProcedureDelegate _windowProcedure;
    private bool _hookInstalled;
    private bool _installationBlocked;
#endif

    public static bool IsSupported
    {
        get
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }
    }

    public bool IsHookInstalled
    {
        get
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            return _hookInstalled;
#else
            return false;
#endif
        }
    }

    public string LastError { get; private set; } = string.Empty;

    public void Initialize(Func<string, Task<bool>> importAsync)
    {
        _importAsync = importAsync ?? throw new ArgumentNullException(nameof(importAsync));
        _acceptingDrops = isActiveAndEnabled;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (isActiveAndEnabled)
        {
            TryInstallHook();
        }
#endif
    }

    public bool TryInstallHook()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (_hookInstalled)
        {
            return true;
        }

        if (_installationBlocked)
        {
            LastError = "Windows 드롭 훅을 안전하게 다시 설치할 수 없습니다.";
            return false;
        }

        _windowHandle = FindPlayerWindow();
        if (_windowHandle == IntPtr.Zero)
        {
            LastError = "Unity Player 창을 찾지 못했습니다.";
            return false;
        }

        _windowProcedure ??= WindowProcedure;
        IntPtr replacementProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure);

        SetLastError(0);
        IntPtr existingProcedure = GetWindowLongPointer(_windowHandle, WindowProcedureIndex);
        int readError = Marshal.GetLastWin32Error();
        if (existingProcedure == IntPtr.Zero)
        {
            LastError = "Windows Player 창의 메시지 처리기를 읽지 못했습니다: " + readError;
            _windowHandle = IntPtr.Zero;
            return false;
        }

        _previousWindowProcedure = existingProcedure;
        SetLastError(0);
        IntPtr previousProcedure = SetWindowLongPointer(
            _windowHandle,
            WindowProcedureIndex,
            replacementProcedure);
        int nativeError = Marshal.GetLastWin32Error();

        if (previousProcedure == IntPtr.Zero && nativeError != 0)
        {
            LastError = "Windows 드롭 훅 설치 실패: " + nativeError;
            _windowHandle = IntPtr.Zero;
            _previousWindowProcedure = IntPtr.Zero;
            return false;
        }

        if (previousProcedure == IntPtr.Zero)
        {
            previousProcedure = existingProcedure;
        }

        _previousWindowProcedure = previousProcedure;
        lock (WindowProcedureRootLock)
        {
            if (!RootedWindowProcedures.Contains(_windowProcedure))
            {
                RootedWindowProcedures.Add(_windowProcedure);
            }
        }

        DragAcceptFiles(_windowHandle, true);
        _hookInstalled = true;
        _acceptingDrops = _importAsync != null && isActiveAndEnabled;
        LastError = string.Empty;
        return true;
#else
        LastError = "외부 파일 드롭은 Windows Player에서만 지원합니다.";
        return false;
#endif
    }

    private void OnEnable()
    {
        _lifecycleVersion++;
        _acceptingDrops = _importAsync != null;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        _hookRetryFrames = 0;
#endif
    }

    private void Start()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (_importAsync != null)
        {
            TryInstallHook();
        }
#endif
    }

    private void Update()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!_hookInstalled && !_installationBlocked && _importAsync != null)
        {
            _hookRetryFrames++;
            if (_hookRetryFrames >= 30)
            {
                _hookRetryFrames = 0;
                TryInstallHook();
            }
        }
#endif

        if (_processingTask != null && _processingTask.IsCompleted)
        {
            _processingTask = null;
        }

        if (_processingTask == null && !_pendingPaths.IsEmpty && _importAsync != null)
        {
            _processingTask = ProcessPendingPathsAsync();
        }
    }

    private async Task ProcessPendingPathsAsync()
    {
        int lifecycleVersion = _lifecycleVersion;
        while (_acceptingDrops
            && lifecycleVersion == _lifecycleVersion
            && _pendingPaths.TryDequeue(out string path))
        {
            if (!string.Equals(Path.GetExtension(path), ".vrm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Func<string, Task<bool>> importAsync = _importAsync;
            if (importAsync == null)
            {
                break;
            }

            try
            {
                await importAsync(path);
            }
            catch (Exception exception)
            {
                if (lifecycleVersion == _lifecycleVersion)
                {
                    Debug.LogException(exception, this);
                }
            }
        }
    }

    private void OnDisable()
    {
        _lifecycleVersion++;
        _acceptingDrops = false;
        ClearPendingPaths();
        UninstallHook();
    }

    private void OnApplicationQuit()
    {
        _acceptingDrops = false;
        UninstallHook();
    }

    private void OnDestroy()
    {
        _lifecycleVersion++;
        _acceptingDrops = false;
        _importAsync = null;
        ClearPendingPaths();
        UninstallHook();
    }

    private void ClearPendingPaths()
    {
        while (_pendingPaths.TryDequeue(out _))
        {
        }
    }

    private void UninstallHook()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!_hookInstalled || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        DragAcceptFiles(_windowHandle, false);
        _hookInstalled = false;

        if (!IsWindow(_windowHandle))
        {
            _windowHandle = IntPtr.Zero;
            _previousWindowProcedure = IntPtr.Zero;
            return;
        }

        IntPtr replacementProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure);
        IntPtr currentProcedure = GetWindowLongPointer(_windowHandle, WindowProcedureIndex);

        if (currentProcedure != replacementProcedure)
        {
            _installationBlocked = true;
            LastError = "다른 메시지 훅이 뒤에 설치되어 기존 드롭 훅을 보존했습니다.";
            return;
        }

        SetLastError(0);
        IntPtr restoreResult = SetWindowLongPointer(
            _windowHandle,
            WindowProcedureIndex,
            _previousWindowProcedure);
        int nativeError = Marshal.GetLastWin32Error();
        if (restoreResult == IntPtr.Zero && nativeError != 0)
        {
            _installationBlocked = true;
            LastError = "Windows 드롭 훅 해제 실패: " + nativeError;
            return;
        }

        _windowHandle = IntPtr.Zero;
        _previousWindowProcedure = IntPtr.Zero;
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDropFiles)
        {
            CaptureDroppedPaths(wParam);
            return IntPtr.Zero;
        }

        return _previousWindowProcedure != IntPtr.Zero
            ? CallWindowProc(_previousWindowProcedure, window, message, wParam, lParam)
            : DefWindowProc(window, message, wParam, lParam);
    }

    private void CaptureDroppedPaths(IntPtr dropHandle)
    {
        try
        {
            if (!_acceptingDrops)
            {
                return;
            }

            uint fileCount = DragQueryFile(dropHandle, QueryFileCount, null, 0);
            for (uint index = 0; index < fileCount; index++)
            {
                uint pathLength = DragQueryFile(dropHandle, index, null, 0);
                if (pathLength == 0 || pathLength > 32767)
                {
                    continue;
                }

                var pathBuffer = new StringBuilder((int)pathLength + 1);
                uint copiedLength = DragQueryFile(
                    dropHandle,
                    index,
                    pathBuffer,
                    pathBuffer.Capacity);
                if (copiedLength > 0)
                {
                    _pendingPaths.Enqueue(pathBuffer.ToString());
                }
            }
        }
        catch
        {
            // 예외를 네이티브 Windows 메시지 경계 밖으로 전달하지 않는다.
        }
        finally
        {
            DragFinish(dropHandle);
        }
    }

    private static IntPtr FindPlayerWindow()
    {
        uint currentProcessId = GetCurrentProcessId();

        IntPtr activeWindow = GetActiveWindow();
        if (BelongsToProcess(activeWindow, currentProcessId))
        {
            return activeWindow;
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        if (BelongsToProcess(foregroundWindow, currentProcessId))
        {
            return foregroundWindow;
        }

        IntPtr discoveredWindow = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (BelongsToProcess(window, currentProcessId)
                && IsWindowVisible(window)
                && GetWindow(window, GetWindowOwner) == IntPtr.Zero)
            {
                discoveredWindow = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return discoveredWindow;
    }

    private static bool BelongsToProcess(IntPtr window, uint processId)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
        {
            return false;
        }

        GetWindowThreadProcessId(window, out uint ownerProcessId);
        return ownerProcessId == processId;
    }

    private static IntPtr SetWindowLongPointer(IntPtr window, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
    }

    private static IntPtr GetWindowLongPointer(IntPtr window, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedureDelegate(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsDelegate(IntPtr window, IntPtr lParam);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(
        IntPtr dropHandle,
        uint fileIndex,
        StringBuilder filePath,
        int characterCount);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr dropHandle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProc(
        IntPtr previousProcedure,
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);
#endif
}
