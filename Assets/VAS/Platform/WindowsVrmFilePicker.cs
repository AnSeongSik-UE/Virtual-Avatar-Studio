using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

public enum VrmFilePickerStatus
{
    Selected,
    Cancelled,
    Busy,
    Unsupported,
    Error
}

public readonly struct VrmFilePickerResult
{
    public VrmFilePickerResult(VrmFilePickerStatus status, string path, string errorMessage)
    {
        Status = status;
        Path = path ?? string.Empty;
        ErrorMessage = errorMessage ?? string.Empty;
    }

    public VrmFilePickerStatus Status { get; }
    public string Path { get; }
    public string ErrorMessage { get; }
    public bool IsSelected => Status == VrmFilePickerStatus.Selected && !string.IsNullOrEmpty(Path);
}

public static class WindowsVrmFilePicker
{
    private static int s_dialogOpen;

    public static Task<VrmFilePickerResult> OpenAsync(string initialDirectory = null)
    {
#if UNITY_EDITOR
        if (Interlocked.CompareExchange(ref s_dialogOpen, 1, 0) != 0)
        {
            return Task.FromResult(BusyResult());
        }

        try
        {
            string selectedPath = UnityEditor.EditorUtility.OpenFilePanel(
                "VRM 아바타 선택",
                initialDirectory ?? string.Empty,
                "vrm");

            return Task.FromResult(string.IsNullOrEmpty(selectedPath)
                ? new VrmFilePickerResult(VrmFilePickerStatus.Cancelled, string.Empty, string.Empty)
                : new VrmFilePickerResult(VrmFilePickerStatus.Selected, selectedPath, string.Empty));
        }
        catch (Exception exception)
        {
            return Task.FromResult(new VrmFilePickerResult(
                VrmFilePickerStatus.Error,
                string.Empty,
                exception.Message));
        }
        finally
        {
            Interlocked.Exchange(ref s_dialogOpen, 0);
        }
#elif UNITY_STANDALONE_WIN
        if (Interlocked.CompareExchange(ref s_dialogOpen, 1, 0) != 0)
        {
            return Task.FromResult(BusyResult());
        }

        var completion = new TaskCompletionSource<VrmFilePickerResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IntPtr ownerWindow = GetActiveWindow();

        try
        {
            var dialogThread = new Thread(() => RunWindowsDialog(
                completion,
                ownerWindow,
                initialDirectory))
            {
                IsBackground = true,
                Name = "VAS VRM File Picker"
            };
            dialogThread.SetApartmentState(ApartmentState.STA);
            dialogThread.Start();
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref s_dialogOpen, 0);
            completion.TrySetResult(new VrmFilePickerResult(
                VrmFilePickerStatus.Error,
                string.Empty,
                exception.Message));
        }

        return completion.Task;
#else
        return Task.FromResult(new VrmFilePickerResult(
            VrmFilePickerStatus.Unsupported,
            string.Empty,
            "VRM 파일 선택은 Unity Editor와 Windows Player에서만 지원합니다."));
#endif
    }

    private static VrmFilePickerResult BusyResult()
    {
        return new VrmFilePickerResult(
            VrmFilePickerStatus.Busy,
            string.Empty,
            "파일 선택 창이 이미 열려 있습니다.");
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private const uint OfnPathMustExist = 0x00000800;
    private const uint OfnFileMustExist = 0x00001000;
    private const uint OfnExplorer = 0x00080000;
    private const uint OfnNoChangeDir = 0x00000008;
    private const uint OfnDontAddToRecent = 0x02000000;
    private const uint OfnEnableSizing = 0x00800000;
    private const int FileBufferCharacterCount = 32768;
    private const int Utf16BytesPerCharacter = 2;

    private static void RunWindowsDialog(
        TaskCompletionSource<VrmFilePickerResult> completion,
        IntPtr ownerWindow,
        string initialDirectory)
    {
        IntPtr fileBuffer = IntPtr.Zero;
        try
        {
            fileBuffer = Marshal.AllocHGlobal(
                FileBufferCharacterCount * Utf16BytesPerCharacter);
            Marshal.WriteInt16(fileBuffer, 0);

            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf(typeof(OpenFileName)),
                OwnerWindow = ownerWindow,
                Filter = "VRM 아바타 (*.vrm)\0*.vrm\0\0",
                FilterIndex = 1,
                File = fileBuffer,
                MaxFile = FileBufferCharacterCount,
                InitialDirectory = string.IsNullOrWhiteSpace(initialDirectory)
                    ? null
                    : initialDirectory,
                Title = "VRM 아바타 선택",
                Flags = OfnExplorer
                    | OfnPathMustExist
                    | OfnFileMustExist
                    | OfnNoChangeDir
                    | OfnDontAddToRecent
                    | OfnEnableSizing,
                DefaultExtension = "vrm"
            };

            if (GetOpenFileName(ref dialog))
            {
                completion.TrySetResult(new VrmFilePickerResult(
                    VrmFilePickerStatus.Selected,
                    Marshal.PtrToStringUni(fileBuffer) ?? string.Empty,
                    string.Empty));
                return;
            }

            uint extendedError = CommDlgExtendedError();
            completion.TrySetResult(extendedError == 0
                ? new VrmFilePickerResult(
                    VrmFilePickerStatus.Cancelled,
                    string.Empty,
                    string.Empty)
                : new VrmFilePickerResult(
                    VrmFilePickerStatus.Error,
                    string.Empty,
                    "Windows 파일 선택 창 오류: 0x" + extendedError.ToString("X8")));
        }
        catch (Exception exception)
        {
            completion.TrySetResult(new VrmFilePickerResult(
                VrmFilePickerStatus.Error,
                string.Empty,
                exception.Message));
        }
        finally
        {
            if (fileBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileBuffer);
            }

            Interlocked.Exchange(ref s_dialogOpen, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr OwnerWindow;
        public IntPtr Instance;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Filter;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string CustomFilter;

        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string InitialDirectory;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Title;

        public uint Flags;
        public ushort FileOffset;
        public ushort FileExtension;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DefaultExtension;

        public IntPtr CustomData;
        public IntPtr Hook;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TemplateName;

        public IntPtr Reserved;
        public uint ReservedValue;
        public uint FlagsEx;
    }

    [DllImport(
        "comdlg32.dll",
        EntryPoint = "GetOpenFileNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName openFileName);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
#endif
}
