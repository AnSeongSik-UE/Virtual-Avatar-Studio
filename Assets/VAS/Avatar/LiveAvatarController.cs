using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UniGLTF;
using UniJSON;
using UniVRM10;
using UnityEngine;

public enum BroadcastBackgroundMode
{
    BackgroundRemoval = 0,
    SolidColor = 1
}

public static class BroadcastBackgroundSettings
{
    private const string RedKey = "VAS.Broadcast.Background.R";
    private const string GreenKey = "VAS.Broadcast.Background.G";
    private const string BlueKey = "VAS.Broadcast.Background.B";
    private const string ModeKey = "VAS.Broadcast.Background.Mode";

    private static readonly Color DefaultBackgroundColor = new(0.055f, 0.075f, 0.12f, 1f);
    private static bool _loaded;
    private static Color _currentColor;
    private static BroadcastBackgroundMode _currentMode;

    public static Color DefaultColor => DefaultBackgroundColor;

    public static Color CurrentColor
    {
        get
        {
            EnsureLoaded();
            return _currentColor;
        }
    }

    public static BroadcastBackgroundMode CurrentMode
    {
        get
        {
            EnsureLoaded();
            return _currentMode;
        }
    }

    public static void SetCurrentColor(Color color)
    {
        _loaded = true;
        _currentColor = OpaqueClamped(color);
    }

    public static void SetCurrentMode(BroadcastBackgroundMode mode)
    {
        EnsureLoaded();
        _currentMode = mode == BroadcastBackgroundMode.SolidColor
            ? BroadcastBackgroundMode.SolidColor
            : BroadcastBackgroundMode.BackgroundRemoval;
    }

    public static void ApplyTo(Camera camera)
    {
        if (camera == null) return;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = CurrentMode == BroadcastBackgroundMode.BackgroundRemoval
            ? new Color(0f, 0f, 0f, 0f)
            : CurrentColor;
        if (camera.TryGetComponent(out BroadcastOutputController outputController))
            outputController.ApplyPreviewSettings();
    }

    public static void Save()
    {
        EnsureLoaded();
        PlayerPrefs.SetFloat(RedKey, _currentColor.r);
        PlayerPrefs.SetFloat(GreenKey, _currentColor.g);
        PlayerPrefs.SetFloat(BlueKey, _currentColor.b);
        PlayerPrefs.SetInt(ModeKey, (int)_currentMode);
        PlayerPrefs.Save();
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _currentColor = OpaqueClamped(new Color(
            PlayerPrefs.GetFloat(RedKey, DefaultBackgroundColor.r),
            PlayerPrefs.GetFloat(GreenKey, DefaultBackgroundColor.g),
            PlayerPrefs.GetFloat(BlueKey, DefaultBackgroundColor.b),
            1f));
        _currentMode = PlayerPrefs.GetInt(ModeKey, 0) == (int)BroadcastBackgroundMode.SolidColor
            ? BroadcastBackgroundMode.SolidColor
            : BroadcastBackgroundMode.BackgroundRemoval;
    }

    private static Color OpaqueClamped(Color color)
    {
        return new Color(
            Mathf.Clamp01(color.r),
            Mathf.Clamp01(color.g),
            Mathf.Clamp01(color.b),
            1f);
    }
}

public sealed class LiveAvatarController : MonoBehaviour
{
    private sealed class ConfirmedInvalidAvatarException : Exception
    {
        public ConfirmedInvalidAvatarException(string message) : base(message) { }
        public ConfirmedInvalidAvatarException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    private sealed class CandidateAvatar
    {
        public Vrm10Instance Instance;
        public RuntimeGltfInstance GltfInstance;
        public Animator Animator;
        public VRMMapper Mapper;
    }

    [SerializeField] private Vector3 avatarPosition = Vector3.zero;
    [SerializeField] private Vector3 avatarRotation = new(0f, 180f, 0f);

    private static readonly IReadOnlyList<AvatarLibraryStore.AvatarEntry> EmptyEntries =
        Array.Empty<AvatarLibraryStore.AvatarEntry>();

    private AvatarLibraryStore _library;
    private Vrm10Instance _vrmInstance;
    private VRMMapper _mapper;
    private CancellationTokenSource _operationCancellation;
    private bool _calibrationPending;
    private bool _calibrationTrackingInterrupted;
    private bool _operationInProgress;
    private bool _destroying;
    private float _calibrationEndTime;
    private float _calibrationTrackingRecoveryEndTime;
    private int _operationGeneration;
    private int _avatarRevision;
    private string _currentAvatarId = string.Empty;
    private string _currentAvatarName = string.Empty;

    private const float CalibrationCountdownSeconds = 3f;
    private const float CalibrationTrackingRecoverySeconds = 5f;

    public event Action AvatarChanged;

    public IReadOnlyList<AvatarLibraryStore.AvatarEntry> Entries => _library != null
        ? _library.Entries
        : EmptyEntries;
    public string CurrentAvatarId => _currentAvatarId;
    public string CurrentAvatarName => _currentAvatarName;
    public int AvatarRevision => _avatarRevision;
    public bool IsBusy => _operationInProgress;
    public bool HasAvatar => _vrmInstance != null && _mapper != null && _mapper.IsReady;
    public bool IsReady => HasAvatar;
    public bool CanCalibrate => IsReady && !_calibrationPending && !_operationInProgress;
    public bool IsCalibrating => _calibrationPending;
    public bool IsCalibrationTrackingInterrupted => _calibrationPending && _calibrationTrackingInterrupted;
    public bool HasCalibrationTracking => _mapper != null && _mapper.HasTracking;
    public int CalibrationCountdown => _calibrationPending
        ? Mathf.Max(1, Mathf.CeilToInt(_calibrationEndTime - Time.unscaledTime))
        : 0;
    public int CalibrationTrackingRecoveryCountdown => IsCalibrationTrackingInterrupted
        ? Mathf.Max(1, Mathf.CeilToInt(_calibrationTrackingRecoveryEndTime - Time.unscaledTime))
        : 0;
    public int CalibrationNoticeRevision { get; private set; }
    public string CalibrationNoticeMessage { get; private set; } = string.Empty;
    public bool CalibrationNoticeIsError { get; private set; }
    public string CalibrationButtonLabel => _calibrationPending
        ? _calibrationTrackingInterrupted
            ? $"인식 복귀 대기... {CalibrationTrackingRecoveryCountdown}"
            : $"캘리브레이션 중... {Mathf.Max(1, Mathf.CeilToInt(_calibrationEndTime - Time.unscaledTime))}"
        : "중립 자세 캘리브레이션 (3초)";
    public string Status { get; private set; } = "아바타 목록 준비 중...";
    public bool HasArmMappingConfig => _mapper != null && _mapper.IsReady;
    public bool IsAvatarCalibrated => _mapper != null && _mapper.IsCalibrated;
    public float LeftArmInputAngle => _mapper != null ? _mapper.LeftInputAngle : 0f;
    public float RightArmInputAngle => _mapper != null ? _mapper.RightInputAngle : 0f;
    public float LeftArmOutputAngle => _mapper != null ? _mapper.LeftOutputAngle : 0f;
    public float RightArmOutputAngle => _mapper != null ? _mapper.RightOutputAngle : 0f;

    private async void Start()
    {
        try
        {
            AvatarLibraryStore library = new AvatarLibraryStore(Application.persistentDataPath);
            library.Initialize();
            _library = library;
        }
        catch (Exception exception)
        {
            _library = null;
            Status = $"아바타 목록 초기화 실패: {exception.Message}";
            Debug.LogException(exception);
            return;
        }

        if (!string.IsNullOrEmpty(_library.LastWarning))
            Debug.LogWarning($"[LiveAvatarController] {_library.LastWarning}");

        foreach (string prunedAvatarId in _library.PrunedAvatarIds)
        {
            if (!CleanupRemovedAvatar(prunedAvatarId, unloadCurrent: false))
                Debug.LogWarning($"[LiveAvatarController] 제거된 아바타의 로컬 설정 일부를 정리하지 못했습니다: {ShortId(prunedAvatarId)}");
        }

        string restoreId = _library.ActiveAvatarId;
        if (string.IsNullOrEmpty(restoreId))
        {
            string guidance = _library.Entries.Count > 0
                ? "등록 목록에서 사용할 아바타를 선택하거나 새 VRM 파일을 추가하세요."
                : "등록된 기본 아바타가 없습니다. VRM 파일을 추가하세요.";
            Status = string.IsNullOrEmpty(_library.LastWarning)
                ? guidance
                : _library.LastWarning + " " + guidance;
            return;
        }

        await ActivateAvatarAsync(restoreId, true);
    }

    public async Task<bool> RegisterAndActivateAsync(string sourcePath)
    {
        if (_library == null)
        {
            Status = "아바타 목록이 아직 준비되지 않았습니다.";
            return false;
        }

        if (!TryBeginOperation(out int generation, out CancellationTokenSource cancellation))
            return false;

        AvatarLibraryStore.PendingImport pending = null;
        CandidateAvatar candidate = null;
        try
        {
            Status = "VRM 파일을 안전하게 복사하고 확인하는 중...";
            pending = await Task.Run(
                () => _library.StageImport(sourcePath, cancellation.Token),
                cancellation.Token);
            EnsureCurrentOperation(generation, cancellation.Token);

            string avatarId = pending.Id;
            string displayName = pending.DisplayName;
            if (_library.TryGetEntry(avatarId, out AvatarLibraryStore.AvatarEntry existingEntry))
            {
                Status = $"이미 등록된 아바타입니다: {existingEntry.DisplayName}";
                return true;
            }

            bool migrateLegacySettings = ShouldMigrateLegacySettings(avatarId, includePendingAvatar: true);
            Status = $"{displayName} 아바타를 검증하는 중...";
            candidate = await LoadCandidateAsync(
                pending.TemporaryPath,
                avatarId,
                displayName,
                migrateLegacySettings,
                cancellation.Token);
            EnsureCurrentOperation(generation, cancellation.Token);

            _mapper?.FlushConfig();
            if (migrateLegacySettings) candidate.Mapper.PersistCurrentConfig();
            candidate.GltfInstance.ShowMeshes();
            _library.CommitImportAndActivate(pending, migrateLegacySettings);
            pending = null;

            CommitCandidate(candidate, avatarId, displayName);
            candidate = null;

            Status = $"{displayName} 준비 완료 - 편안한 자세로 캘리브레이션하세요.";
            Debug.Log($"[LiveAvatarController] 아바타 등록 및 활성화 완료: {ShortId(avatarId)}");
            NotifyAvatarChanged();
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!_destroying) Status = "아바타 등록을 취소했습니다.";
            return false;
        }
        catch (Exception) when (OperationWasCancelled(generation, cancellation))
        {
            if (!_destroying) Status = "아바타 등록을 취소했습니다.";
            return false;
        }
        catch (Exception exception) when (IsConfirmedInvalid(exception))
        {
            Status = $"사용할 수 없는 VRM입니다: {UserMessage(exception)}";
            Debug.LogWarning($"[LiveAvatarController] VRM 등록 거부: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
        catch (OutOfMemoryException exception)
        {
            Status = "메모리가 부족해 새 아바타를 불러오지 못했습니다. 기존 아바타는 유지됩니다.";
            Debug.LogException(exception);
            return false;
        }
        catch (Exception exception)
        {
            Status = $"일시적인 오류로 아바타를 등록하지 못했습니다: {exception.Message}";
            Debug.LogException(exception);
            return false;
        }
        finally
        {
            pending?.Dispose();
            DestroyCandidate(candidate);
            EndOperation(generation, cancellation);
        }
    }

    public Task<bool> ActivateAvatarAsync(string avatarId)
    {
        return ActivateAvatarAsync(avatarId, false);
    }

    public Task<bool> RemoveAvatarAsync(string avatarId)
    {
        if (_library == null)
        {
            Status = "아바타 목록이 아직 준비되지 않았습니다.";
            return Task.FromResult(false);
        }

        if (!TryBeginOperation(out int generation, out CancellationTokenSource cancellation))
            return Task.FromResult(false);

        try
        {
            if (!_library.TryGetEntry(avatarId, out AvatarLibraryStore.AvatarEntry entry))
            {
                Status = "이미 제거되었거나 등록되지 않은 아바타입니다.";
                return Task.FromResult(false);
            }

            bool removingCurrent = string.Equals(
                _currentAvatarId,
                entry.Id,
                StringComparison.OrdinalIgnoreCase);

            if (!_library.Remove(entry.Id))
            {
                Status = "아바타를 목록에서 제거하지 못했습니다.";
                return Task.FromResult(false);
            }

            bool cleanupComplete = CleanupRemovedAvatar(entry.Id, removingCurrent);
            Status = cleanupComplete
                ? $"{entry.DisplayName} 등록을 제거했습니다. 원본 VRM 파일은 유지됩니다."
                : $"{entry.DisplayName} 등록은 제거했지만 일부 로컬 설정을 정리하지 못했습니다. 원본 VRM 파일은 유지됩니다.";
            if (removingCurrent) NotifyAvatarChanged();
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            Status = $"아바타 제거 실패: {exception.Message}";
            Debug.LogException(exception);
            return Task.FromResult(false);
        }
        finally
        {
            EndOperation(generation, cancellation);
        }
    }

    public void Apply(TrackingPacket packet, bool calibrationFrameReady)
    {
        if (_mapper != null) _mapper.Apply(packet, calibrationFrameReady);
    }

    public bool BeginCalibration()
    {
        if (_calibrationPending || _operationInProgress) return false;
        if (_mapper == null || !_mapper.HasTracking)
        {
            Status = HasAvatar
                ? "얼굴, 양쪽 어깨와 팔꿈치를 카메라 안에 유지하세요."
                : "아바타를 먼저 불러오세요.";
            return false;
        }

        _calibrationPending = true;
        _calibrationTrackingInterrupted = false;
        _calibrationEndTime = Time.unscaledTime + CalibrationCountdownSeconds;
        _calibrationTrackingRecoveryEndTime = 0f;
        Status = "3초 후 캘리브레이션합니다. 편안한 중립 자세로 돌아오세요.";
        return true;
    }

    public string GetCalibrationBlockReason(bool requireTracking)
    {
        if (_operationInProgress) return "아바타 등록 또는 전환이 끝난 뒤 다시 시도하세요.";
        if (_calibrationPending) return "캘리브레이션이 이미 진행 중입니다.";
        if (!HasAvatar) return "캘리브레이션할 아바타를 먼저 등록하거나 선택하세요.";
        if (requireTracking && !HasCalibrationTracking)
            return "얼굴과 양쪽 어깨·팔꿈치가 카메라에 보이도록 위치를 조정하세요.";
        return string.Empty;
    }

    public VRMMapper.ArmMappingConfig GetArmMappingConfig()
    {
        return _mapper != null ? _mapper.GetArmConfig() : default;
    }

    public void SetArmMappingConfig(VRMMapper.ArmMappingConfig config)
    {
        if (_mapper != null) _mapper.SetArmConfig(config);
    }

    public void ResetArmMappingConfig()
    {
        if (_mapper != null) _mapper.ResetArmConfig();
    }

    public string ArmMappingDiagnostics => _mapper == null
        ? "아바타를 먼저 불러오세요."
        : $"입력 좌/우: {_mapper.LeftInputAngle:F1} / {_mapper.RightInputAngle:F1}\n" +
          $"변화량 좌/우: {_mapper.LeftDeltaAngle:F1} / {_mapper.RightDeltaAngle:F1}\n" +
          $"출력 좌/우: {_mapper.LeftOutputAngle:F1} / {_mapper.RightOutputAngle:F1}";

    private async Task<bool> ActivateAvatarAsync(string avatarId, bool startupRestore)
    {
        if (_library == null)
        {
            Status = "아바타 목록이 아직 준비되지 않았습니다.";
            return false;
        }

        if (!TryBeginOperation(out int generation, out CancellationTokenSource cancellation))
            return false;

        CandidateAvatar candidate = null;
        try
        {
            if (!_library.TryGetEntry(avatarId, out AvatarLibraryStore.AvatarEntry entry))
            {
                Status = "등록되지 않은 아바타입니다.";
                return false;
            }

            Status = startupRestore
                ? $"마지막 아바타 {entry.DisplayName} 복원 중..."
                : $"{entry.DisplayName} 아바타를 검증하는 중...";
            string cachePath = await Task.Run(
                () => _library.ValidateRegisteredCache(entry.Id, cancellation.Token),
                cancellation.Token);
            EnsureCurrentOperation(generation, cancellation.Token);

            bool migrateLegacySettings = ShouldMigrateLegacySettings(entry.Id, includePendingAvatar: false);
            candidate = await LoadCandidateAsync(
                cachePath,
                entry.Id,
                entry.DisplayName,
                migrateLegacySettings,
                cancellation.Token);
            EnsureCurrentOperation(generation, cancellation.Token);

            _mapper?.FlushConfig();
            if (migrateLegacySettings) candidate.Mapper.PersistCurrentConfig();
            candidate.GltfInstance.ShowMeshes();
            _library.Activate(entry.Id, migrateLegacySettings);

            CommitCandidate(candidate, entry.Id, entry.DisplayName);
            candidate = null;

            Status = $"{entry.DisplayName} 준비 완료 - 편안한 자세로 캘리브레이션하세요.";
            Debug.Log($"[LiveAvatarController] 아바타 활성화 완료: {ShortId(entry.Id)}");
            NotifyAvatarChanged();
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!_destroying) Status = "아바타 전환을 취소했습니다.";
            return false;
        }
        catch (Exception) when (OperationWasCancelled(generation, cancellation))
        {
            if (!_destroying) Status = "아바타 전환을 취소했습니다.";
            return false;
        }
        catch (Exception exception) when (IsConfirmedInvalid(exception))
        {
            HandleConfirmedInvalidRegistration(avatarId, exception);
            return false;
        }
        catch (OutOfMemoryException exception)
        {
            Status = "메모리가 부족해 아바타를 전환하지 못했습니다. 기존 아바타는 유지됩니다.";
            Debug.LogException(exception);
            return false;
        }
        catch (Exception exception)
        {
            Status = $"일시적인 오류로 아바타를 불러오지 못했습니다. 등록은 유지됩니다: {exception.Message}";
            Debug.LogException(exception);
            return false;
        }
        finally
        {
            DestroyCandidate(candidate);
            EndOperation(generation, cancellation);
        }
    }

    private async Task<CandidateAvatar> LoadCandidateAsync(
        string path,
        string avatarId,
        string displayName,
        bool useLegacyConfig,
        CancellationToken cancellationToken)
    {
        Vrm10Instance instance = null;
        try
        {
            instance = await Vrm10.LoadPathAsync(
                path,
                canLoadVrm0X: true,
                showMeshes: false,
                materialGenerator: new UrpVrm10MaterialDescriptorGenerator(),
                ct: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (instance == null)
                throw new InvalidOperationException("UniVRM이 아바타 인스턴스를 생성하지 못했습니다.");

            instance.name = $"Runtime Avatar - {displayName}";
            instance.transform.SetPositionAndRotation(avatarPosition, Quaternion.Euler(avatarRotation));

            RuntimeGltfInstance gltfInstance = instance.GetComponent<RuntimeGltfInstance>();
            if (gltfInstance == null)
                throw new ConfirmedInvalidAvatarException("런타임 VRM 구성요소가 누락되었습니다.");

            Vrm10Runtime runtime = instance.Runtime;
            Animator animator = runtime != null
                ? runtime.ControlRig?.ControlRigAnimator ?? instance.GetComponent<Animator>()
                : instance.GetComponent<Animator>();
            if (animator == null)
                throw new ConfirmedInvalidAvatarException("휴머노이드 Animator가 없습니다.");

            VRMMapper mapper = instance.gameObject.GetComponent<VRMMapper>();
            if (mapper == null) mapper = instance.gameObject.AddComponent<VRMMapper>();
            if (!mapper.Initialize(instance, animator, avatarId, useLegacyConfig))
                throw new ConfirmedInvalidAvatarException("목, 왼팔 또는 오른팔 휴머노이드 본이 누락되었습니다.");

            mapper.enabled = false;
            return new CandidateAvatar
            {
                Instance = instance,
                GltfInstance = gltfInstance,
                Animator = animator,
                Mapper = mapper
            };
        }
        catch (Exception exception) when (
            exception is UniGLTFException ||
            exception is Vrm10Exception ||
            exception is TreeValueException ||
            IsKnownUniVrmLoadFailure(exception))
        {
            DisposeAndDestroyVrm(instance);

            throw new ConfirmedInvalidAvatarException("VRM 구조를 해석할 수 없습니다.", exception);
        }
        catch
        {
            DisposeAndDestroyVrm(instance);

            throw;
        }
    }

    private void CommitCandidate(CandidateAvatar candidate, string avatarId, string displayName)
    {
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));

        CancelCalibration();

        Vrm10Instance previousInstance = _vrmInstance;
        if (previousInstance != null) previousInstance.gameObject.SetActive(false);

        _vrmInstance = candidate.Instance;
        _mapper = candidate.Mapper;
        _currentAvatarId = avatarId;
        _currentAvatarName = displayName;
        _mapper.enabled = true;
        try
        {
            ConfigureStage(candidate.Animator);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[LiveAvatarController] 무대 카메라 자동 배치 실패: {exception.Message}");
        }

        DisposeAndDestroyVrm(previousInstance);
    }

    private bool ShouldMigrateLegacySettings(string avatarId, bool includePendingAvatar)
    {
        if (_library.LegacyArmSettingsMigrated) return false;

        if (_library.TryGetEntry(avatarId, out AvatarLibraryStore.AvatarEntry existing))
        {
            return _library.Entries.Count == 1 &&
                   string.Equals(existing.Id, avatarId, StringComparison.OrdinalIgnoreCase);
        }

        return includePendingAvatar && _library.Entries.Count == 0;
    }

    private void HandleConfirmedInvalidRegistration(string avatarId, Exception exception)
    {
        string displayName = "등록된 아바타";
        if (_library.TryGetEntry(avatarId, out AvatarLibraryStore.AvatarEntry entry))
            displayName = entry.DisplayName;

        bool current = string.Equals(_currentAvatarId, avatarId, StringComparison.OrdinalIgnoreCase);
        bool removed;
        try
        {
            removed = _library.Remove(avatarId);
        }
        catch (Exception removalException)
        {
            Status = $"{displayName}은(는) 사용할 수 없지만 목록 제거에 실패했습니다. 등록은 유지됩니다.";
            Debug.LogException(removalException);
            Debug.LogWarning($"[LiveAvatarController] 확정 오류: {exception.GetType().Name}: {exception.Message}");
            return;
        }

        if (removed)
        {
            bool cleanupComplete = CleanupRemovedAvatar(avatarId, current);
            if (current) NotifyAvatarChanged();
            Status = cleanupComplete
                ? $"{displayName}은(는) 사용할 수 없어 등록 목록에서 제거했습니다: {UserMessage(exception)}"
                : $"{displayName} 등록은 제거했지만 일부 로컬 설정을 정리하지 못했습니다: {UserMessage(exception)}";
        }
        else
        {
            Status = $"{displayName}은(는) 사용할 수 없습니다: {UserMessage(exception)}";
        }

        Debug.LogWarning($"[LiveAvatarController] 확정 오류: {exception.GetType().Name}: {exception.Message}");
    }

    private bool TryBeginOperation(out int generation, out CancellationTokenSource cancellation)
    {
        generation = _operationGeneration;
        cancellation = null;

        if (_destroying) return false;
        if (_operationInProgress)
        {
            Status = "다른 아바타 작업이 진행 중입니다. 잠시 후 다시 시도하세요.";
            return false;
        }

        _operationInProgress = true;
        generation = ++_operationGeneration;
        cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        return true;
    }

    private void EnsureCurrentOperation(int generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_destroying || generation != _operationGeneration)
            throw new OperationCanceledException(cancellationToken);
    }

    private bool OperationWasCancelled(int generation, CancellationTokenSource cancellation)
    {
        return _destroying ||
               cancellation == null ||
               cancellation.IsCancellationRequested ||
               generation != _operationGeneration;
    }

    private void EndOperation(int generation, CancellationTokenSource cancellation)
    {
        if (cancellation == null) return;
        if (generation == _operationGeneration && ReferenceEquals(_operationCancellation, cancellation))
        {
            _operationCancellation = null;
            _operationInProgress = false;
        }

        cancellation.Dispose();
    }

    private void Update()
    {
        if (!_calibrationPending) return;

        if (_mapper == null || !_mapper.HasTracking)
        {
            if (!_calibrationTrackingInterrupted)
            {
                _calibrationTrackingInterrupted = true;
                _calibrationTrackingRecoveryEndTime = Time.unscaledTime + CalibrationTrackingRecoverySeconds;
            }

            float recoveryRemaining = _calibrationTrackingRecoveryEndTime - Time.unscaledTime;
            if (recoveryRemaining > 0f)
            {
                Status = $"인식 범위 이탈: {Mathf.CeilToInt(recoveryRemaining)}초 안에 얼굴과 양쪽 어깨·팔꿈치를 다시 보여주세요.";
                return;
            }

            _calibrationPending = false;
            _calibrationTrackingInterrupted = false;
            Status = "캘리브레이션 취소: 얼굴과 양쪽 어깨·팔꿈치를 5초 안에 다시 인식하지 못했습니다.";
            PublishCalibrationNotice(Status, isError: true);
            return;
        }

        if (_calibrationTrackingInterrupted)
        {
            _calibrationTrackingInterrupted = false;
            _calibrationTrackingRecoveryEndTime = 0f;
            _calibrationEndTime = Time.unscaledTime + CalibrationCountdownSeconds;
            Status = "인식이 복구되었습니다. 3초 카운트다운을 다시 시작합니다.";
        }

        float remaining = _calibrationEndTime - Time.unscaledTime;
        if (remaining > 0f)
        {
            Status = $"{Mathf.CeilToInt(remaining)}초 후 캘리브레이션... 중립 자세를 유지하세요.";
            return;
        }

        _calibrationPending = false;
        bool succeeded = _mapper.CalibrateNeutralPose();
        Status = succeeded
            ? "캘리브레이션 완료."
            : "캘리브레이션 실패: 얼굴과 양쪽 어깨·팔꿈치를 화면에 유지하세요.";
        PublishCalibrationNotice(Status, isError: !succeeded);
    }

    private void PublishCalibrationNotice(string message, bool isError)
    {
        CalibrationNoticeMessage = message ?? string.Empty;
        CalibrationNoticeIsError = isError;
        CalibrationNoticeRevision++;
    }

    private void CancelCalibration()
    {
        _calibrationPending = false;
        _calibrationTrackingInterrupted = false;
        _calibrationEndTime = 0f;
        _calibrationTrackingRecoveryEndTime = 0f;
    }

    private void UnloadCurrentAvatar(bool flushConfig = true)
    {
        CancelCalibration();
        if (flushConfig) _mapper?.FlushConfig();
        if (_vrmInstance != null)
        {
            DisposeAndDestroyVrm(_vrmInstance);
        }

        _vrmInstance = null;
        _mapper = null;
        _currentAvatarId = string.Empty;
        _currentAvatarName = string.Empty;
    }

    private bool CleanupRemovedAvatar(string avatarId, bool unloadCurrent)
    {
        bool cleanupComplete = true;
        if (unloadCurrent)
        {
            try
            {
                UnloadCurrentAvatar(flushConfig: false);
            }
            catch (Exception exception)
            {
                cleanupComplete = false;
                _vrmInstance = null;
                _mapper = null;
                _currentAvatarId = string.Empty;
                _currentAvatarName = string.Empty;
                Debug.LogException(exception);
            }
        }

        try
        {
            VRMMapper.DeleteSavedConfig(avatarId);
        }
        catch (Exception exception)
        {
            cleanupComplete = false;
            Debug.LogException(exception);
        }

        try
        {
            DeleteArmValidationResult(avatarId);
        }
        catch (Exception exception)
        {
            cleanupComplete = false;
            Debug.LogException(exception);
        }

        return cleanupComplete;
    }

    private void NotifyAvatarChanged()
    {
        _avatarRevision++;
        try
        {
            AvatarChanged?.Invoke();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    private static void DestroyCandidate(CandidateAvatar candidate)
    {
        if (candidate?.Instance == null) return;
        DisposeAndDestroyVrm(candidate.Instance);
    }

    private static void DisposeAndDestroyVrm(Vrm10Instance instance)
    {
        if (instance == null) return;

        try
        {
            instance.DisposeRuntime();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            try
            {
                instance.gameObject.SetActive(false);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                if (instance != null) Destroy(instance.gameObject);
            }
        }
    }

    private static bool IsConfirmedInvalid(Exception exception)
    {
        return exception is ConfirmedInvalidAvatarException ||
               exception is FileNotFoundException ||
               exception is DirectoryNotFoundException ||
               exception is InvalidDataException ||
               exception is EndOfStreamException ||
               exception is UniGLTFException ||
               exception is Vrm10Exception ||
               exception is TreeValueException;
    }

    private static bool IsKnownUniVrmLoadFailure(Exception exception)
    {
        if (exception == null || exception.GetType() != typeof(Exception)) return false;
        return string.Equals(exception.Message, "Failed to load", StringComparison.Ordinal) ||
               string.Equals(exception.Message, "Failed to load as VRM 1.0", StringComparison.Ordinal);
    }

    private static string UserMessage(Exception exception)
    {
        if (exception is ConfirmedInvalidAvatarException && exception.InnerException != null)
            return exception.Message;
        return string.IsNullOrWhiteSpace(exception.Message) ? "파일 구조가 올바르지 않습니다." : exception.Message;
    }

    private static string ShortId(string id)
    {
        return !string.IsNullOrEmpty(id) && id.Length >= 8 ? id.Substring(0, 8) : "unknown";
    }

    private static string ArmValidationResultKey(string avatarId)
    {
        return "VAS.ArmValidation.Avatar." + avatarId.ToLowerInvariant() + ".LastResult";
    }

    private static void DeleteArmValidationResult(string avatarId)
    {
        if (string.IsNullOrEmpty(avatarId)) return;
        PlayerPrefs.DeleteKey(ArmValidationResultKey(avatarId));
        PlayerPrefs.Save();
    }

    private static void ConfigureStage(Animator animator)
    {
        Camera camera = Camera.main;
        if (camera != null)
        {
            Transform head = animator != null ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            Transform hips = animator != null ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;

            Vector3 headPosition = head != null ? head.position : new Vector3(0f, 1.55f, 0f);
            Vector3 hipsPosition = hips != null ? hips.position : new Vector3(0f, 0.85f, 0f);
            Vector3 target = Vector3.Lerp(hipsPosition, headPosition, 0.62f);
            float avatarHeight = Mathf.Max(1.2f, headPosition.y);
            float distance = Mathf.Clamp(avatarHeight * 1.75f, 2.3f, 3.5f);

            camera.orthographic = false;
            camera.fieldOfView = 32f;
            BroadcastBackgroundSettings.ApplyTo(camera);
            camera.transform.position = target + new Vector3(0f, 0.03f, -distance);
            camera.transform.LookAt(target);

            BroadcastPreviewCameraController previewController =
                camera.GetComponent<BroadcastPreviewCameraController>();
            if (previewController == null)
                previewController = camera.gameObject.AddComponent<BroadcastPreviewCameraController>();
            previewController.Initialize(camera);
            previewController.SetHomePose(camera.transform.position, target, camera.fieldOfView);
        }

        if (FindFirstObjectByType<Light>() == null)
        {
            var lightObject = new GameObject("Avatar Key Light", typeof(Light));
            Light keyLight = lightObject.GetComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.color = new Color(1f, 0.95f, 0.9f);
            keyLight.intensity = 1.15f;
            lightObject.transform.rotation = Quaternion.Euler(35f, -30f, 0f);
        }
    }

    private void OnDestroy()
    {
        _destroying = true;
        _operationGeneration++;
        _operationCancellation?.Cancel();
        try
        {
            _mapper?.FlushConfig();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            DisposeAndDestroyVrm(_vrmInstance);
        }
    }
}
