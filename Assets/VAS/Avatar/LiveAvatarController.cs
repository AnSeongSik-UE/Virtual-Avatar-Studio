using System;
using System.IO;
using UniVRM10;
using UnityEngine;

public sealed class LiveAvatarController : MonoBehaviour
{
    [SerializeField] private string modelRelativePath = "Models/MINI.vrm";
    [SerializeField] private Vector3 avatarPosition = Vector3.zero;
    [SerializeField] private Vector3 avatarRotation = new(0f, 180f, 0f);

    private Vrm10Instance _vrmInstance;
    private VRMMapper _mapper;
    private bool _calibrationPending;
    private float _calibrationEndTime;

    private const float CalibrationCountdownSeconds = 3f;

    public bool IsReady => _mapper != null && _mapper.IsReady;
    public bool CanCalibrate => IsReady && !_calibrationPending;
    public bool IsCalibrating => _calibrationPending;
    public string CalibrationButtonLabel => _calibrationPending
        ? $"캘리브레이션 중... {Mathf.Max(1, Mathf.CeilToInt(_calibrationEndTime - Time.unscaledTime))}"
        : "중립 자세 캘리브레이션 (3초)";
    public string Status { get; private set; } = "아바타 불러오는 중...";
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
            string modelPath = ResolveModelPath();
            if (!File.Exists(modelPath))
            {
                Status = $"아바타 파일을 찾지 못했습니다: {modelRelativePath}";
                Debug.LogError($"[LiveAvatarController] {Status}\n{modelPath}");
                return;
            }

            Status = "MINI.vrm 불러오는 중...";
            _vrmInstance = await Vrm10.LoadPathAsync(modelPath, canLoadVrm0X: true);
            if (this == null || _vrmInstance == null) return;

            _vrmInstance.name = "Runtime Avatar - MINI";
            _vrmInstance.transform.SetPositionAndRotation(avatarPosition, Quaternion.Euler(avatarRotation));

            Vrm10Runtime runtime = _vrmInstance.Runtime;
            Animator animator = runtime.ControlRig?.ControlRigAnimator ?? _vrmInstance.GetComponent<Animator>();
            _mapper = _vrmInstance.gameObject.GetComponent<VRMMapper>();
            if (_mapper == null) _mapper = _vrmInstance.gameObject.AddComponent<VRMMapper>();
            _mapper.Initialize(_vrmInstance, animator);

            if (!_mapper.IsReady)
            {
                Status = "아바타의 휴머노이드 본이 누락되었습니다.";
                return;
            }

            ConfigureStage(animator);
            Status = "아바타 준비 완료 - 편안한 자세로 캘리브레이션하세요.";
            Debug.Log($"[LiveAvatarController] Loaded {modelPath}");
        }
        catch (Exception exception)
        {
            Status = $"아바타 로드 실패: {exception.Message}";
            Debug.LogException(exception);
        }
    }

    public void Apply(TrackingPacket packet, bool calibrationFrameReady)
    {
        if (_mapper != null) _mapper.Apply(packet, calibrationFrameReady);
    }

    public bool BeginCalibration()
    {
        if (_calibrationPending) return false;
        if (_mapper == null || !_mapper.HasTracking)
        {
            Status = "얼굴, 양쪽 어깨와 팔꿈치를 카메라 안에 유지하세요.";
            return false;
        }

        _calibrationPending = true;
        _calibrationEndTime = Time.unscaledTime + CalibrationCountdownSeconds;
        Status = "3초 후 캘리브레이션합니다. 편안한 중립 자세로 돌아오세요.";
        return true;
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
        ? "팔 매퍼 불러오는 중..."
        : $"입력 좌/우: {_mapper.LeftInputAngle:F1} / {_mapper.RightInputAngle:F1}\n" +
          $"변화량 좌/우: {_mapper.LeftDeltaAngle:F1} / {_mapper.RightDeltaAngle:F1}\n" +
          $"출력 좌/우: {_mapper.LeftOutputAngle:F1} / {_mapper.RightOutputAngle:F1}";

    private void Update()
    {
        if (!_calibrationPending) return;

        if (_mapper == null || !_mapper.HasTracking)
        {
            _calibrationPending = false;
            Status = "캘리브레이션 취소: 얼굴, 어깨 또는 팔꿈치를 놓쳤습니다.";
            return;
        }

        float remaining = _calibrationEndTime - Time.unscaledTime;
        if (remaining > 0f)
        {
            Status = $"{Mathf.CeilToInt(remaining)}초 후 캘리브레이션... 중립 자세를 유지하세요.";
            return;
        }

        _calibrationPending = false;
        Status = _mapper.CalibrateNeutralPose()
            ? "캘리브레이션 완료."
            : "캘리브레이션 실패: 얼굴, 어깨와 팔꿈치를 화면에 유지하세요.";
    }

    private string ResolveModelPath()
    {
        string normalized = modelRelativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, normalized));
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
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.055f, 0.075f, 0.12f);
            camera.transform.position = target + new Vector3(0f, 0.03f, -distance);
            camera.transform.LookAt(target);
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
        if (_vrmInstance != null) Destroy(_vrmInstance.gameObject);
    }
}
