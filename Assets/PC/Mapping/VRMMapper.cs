using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

[DefaultExecutionOrder(1000)]
public sealed class VRMMapper : MonoBehaviour
{
    public struct ArmMappingConfig
    {
        public bool SwapArms;
        public bool LeftInvert;
        public bool RightInvert;
        public bool TestMode;
        public float LeftNeutralAngle;
        public float RightNeutralAngle;
        public float LeftGain;
        public float RightGain;
        public float MaxAngle;
        public float InputSmoothing;
        public float MaxInputJump;
        public float LeftTestAngle;
        public float RightTestAngle;
    }

    private const string LegacyConfigKeyPrefix = "VAS.ArmMapping.";
    private const string AvatarConfigKeyRoot = "VAS.ArmMapping.Avatar.";

    [SerializeField] private Vrm10Instance vrmInstance;
    [SerializeField] private Animator vrmAnimator;
    [SerializeField, Range(1f, 30f)] private float lerpSpeed = 12f;
    [SerializeField, Range(0.1f, 2f)] private float trackingTimeout = 0.6f;
    [Header("팔 매핑 설정")]
    [SerializeField, Range(-120f, 120f)] private float leftNeutralAngle = -68f;
    [SerializeField, Range(-120f, 120f)] private float rightNeutralAngle = 68f;
    [SerializeField] private bool swapArms;
    [SerializeField] private bool leftInvert;
    [SerializeField] private bool rightInvert;
    [SerializeField, Range(0.25f, 2f)] private float leftGain = 1f;
    [SerializeField, Range(0.25f, 2f)] private float rightGain = 1f;
    [SerializeField, Range(60f, 160f)] private float maxArmAngle = 140f;
    [SerializeField, Range(0.05f, 1f)] private float inputSmoothing = 0.4f;
    [SerializeField, Range(30f, 180f)] private float maxInputJump = 120f;
    [SerializeField] private bool armTestMode;
    [SerializeField, Range(-160f, 160f)] private float leftTestAngle = -68f;
    [SerializeField, Range(-160f, 160f)] private float rightTestAngle = 68f;
    [SerializeField] private bool enableApproximateExpressions;

    private Transform _leftArm;
    private Transform _rightArm;
    private Transform _neck;
    private Quaternion _initialLeftArm;
    private Quaternion _initialRightArm;
    private Quaternion _initialNeck;
    private Quaternion _neutralLeftArm;
    private Quaternion _neutralRightArm;
    private Quaternion _headCalibration = Quaternion.identity;
    private float _leftArmCalibrationAngle;
    private float _rightArmCalibrationAngle;
    private TrackingPacket _latestPacket;
    private bool _initialized;
    private bool _hasPacket;
    private bool _calibrationFrameReady;
    private bool _isCalibrated;
    private float _lastPacketTime;
    private float _leftInputAngle;
    private float _rightInputAngle;
    private float _leftDeltaAngle;
    private float _rightDeltaAngle;
    private float _leftOutputAngle;
    private float _rightOutputAngle;
    private bool _configDirty;
    private float _configSaveTime;
    private string _configKeyPrefix = LegacyConfigKeyPrefix;
    private readonly float[] _currentBlendShapes = new float[52];
    private readonly float[] _neutralBlendShapes = new float[52];

    public bool IsReady => _initialized;
    public bool IsCalibrated => _isCalibrated;
    public bool IsArmTestMode => armTestMode;
    public float LeftInputAngle => _leftInputAngle;
    public float RightInputAngle => _rightInputAngle;
    public float LeftDeltaAngle => _leftDeltaAngle;
    public float RightDeltaAngle => _rightDeltaAngle;
    public float LeftOutputAngle => _leftOutputAngle;
    public float RightOutputAngle => _rightOutputAngle;
    public bool HasTracking => _hasPacket &&
                               (_latestPacket.IsTracking & 5) == 5 &&
                               _calibrationFrameReady &&
                               Time.time - _lastPacketTime <= trackingTimeout;

    private void Start()
    {
        if (!_initialized) Initialize(vrmInstance, vrmAnimator, string.Empty, false);
    }

    public bool Initialize(
        Vrm10Instance instance,
        Animator animator,
        string avatarId = "",
        bool useLegacyConfigAsInitial = false)
    {
        _initialized = false;
        vrmInstance = instance != null ? instance : GetComponent<Vrm10Instance>();
        vrmAnimator = animator != null ? animator : GetComponent<Animator>();
        if (vrmAnimator == null) return false;

        _leftArm = vrmAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        _rightArm = vrmAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        _neck = vrmAnimator.GetBoneTransform(HumanBodyBones.Neck);
        if (_leftArm == null || _rightArm == null || _neck == null) return false;

        _initialLeftArm = _leftArm.localRotation;
        _initialRightArm = _rightArm.localRotation;
        _initialNeck = _neck.localRotation;
        _configKeyPrefix = BuildAvatarConfigKeyPrefix(avatarId);
        LoadArmConfig(useLegacyConfigAsInitial ? LegacyConfigKeyPrefix : _configKeyPrefix);
        RebuildNeutralArmRotations();
        _initialized = true;
        Debug.Log("[VRMMapper] Avatar bones initialized.");
        return true;
    }

    public ArmMappingConfig GetArmConfig()
    {
        return new ArmMappingConfig
        {
            SwapArms = swapArms,
            LeftInvert = leftInvert,
            RightInvert = rightInvert,
            TestMode = armTestMode,
            LeftNeutralAngle = leftNeutralAngle,
            RightNeutralAngle = rightNeutralAngle,
            LeftGain = leftGain,
            RightGain = rightGain,
            MaxAngle = maxArmAngle,
            InputSmoothing = inputSmoothing,
            MaxInputJump = maxInputJump,
            LeftTestAngle = leftTestAngle,
            RightTestAngle = rightTestAngle
        };
    }

    public void SetArmConfig(ArmMappingConfig config)
    {
        swapArms = config.SwapArms;
        leftInvert = config.LeftInvert;
        rightInvert = config.RightInvert;
        armTestMode = config.TestMode;
        leftNeutralAngle = Mathf.Clamp(config.LeftNeutralAngle, -120f, 120f);
        rightNeutralAngle = Mathf.Clamp(config.RightNeutralAngle, -120f, 120f);
        leftGain = Mathf.Clamp(config.LeftGain, 0.25f, 2f);
        rightGain = Mathf.Clamp(config.RightGain, 0.25f, 2f);
        maxArmAngle = Mathf.Clamp(config.MaxAngle, 60f, 160f);
        inputSmoothing = Mathf.Clamp(config.InputSmoothing, 0.05f, 1f);
        maxInputJump = Mathf.Clamp(config.MaxInputJump, 30f, 180f);
        leftTestAngle = Mathf.Clamp(config.LeftTestAngle, -160f, 160f);
        rightTestAngle = Mathf.Clamp(config.RightTestAngle, -160f, 160f);
        RebuildNeutralArmRotations();
        QueueConfigSave();
    }

    public void ResetArmConfig()
    {
        SetArmConfig(DefaultArmConfig());
    }

    public void PersistCurrentConfig()
    {
        if (!_initialized) return;
        _configDirty = true;
        _configSaveTime = 0f;
        SaveConfigIfDue();
    }

    public void FlushConfig()
    {
        if (!_configDirty) return;
        _configSaveTime = 0f;
        SaveConfigIfDue();
    }

    public static void DeleteSavedConfig(string avatarId)
    {
        string prefix = BuildAvatarConfigKeyPrefix(avatarId);
        foreach (string suffix in ConfigKeySuffixes()) PlayerPrefs.DeleteKey(prefix + suffix);
        PlayerPrefs.Save();
    }

    public void Apply(TrackingPacket packet, bool calibrationFrameReady = true)
    {
        _latestPacket = packet;
        _calibrationFrameReady = calibrationFrameReady;
        _hasPacket = true;
        _lastPacketTime = Time.time;
    }

    public bool CalibrateNeutralPose()
    {
        if (!HasTracking) return false;

        _headCalibration = Quaternion.Inverse(ToHeadTrackingRotation(_latestPacket.HeadRotation));
        _leftArmCalibrationAngle = _latestPacket.LeftArmRotation.z;
        _rightArmCalibrationAngle = _latestPacket.RightArmRotation.z;
        _isCalibrated = true;
        SnapToNeutralPose();
        Debug.Log("[VRMMapper] Neutral pose calibrated.");
        return true;
    }

    private void LateUpdate()
    {
        if (!_initialized) return;

        SaveConfigIfDue();

        if (armTestMode)
        {
            ReturnHeadToNeutral();
            ApplyArmTestPose();
            LerpBlendShapes(_neutralBlendShapes);
            return;
        }

        bool timedOut = !_hasPacket || Time.time - _lastPacketTime > trackingTimeout;
        if (timedOut || !_isCalibrated)
        {
            ReturnToNeutralPose();
            return;
        }

        bool hasFace = (_latestPacket.IsTracking & 1) != 0;
        bool hasPose = (_latestPacket.IsTracking & 4) != 0;
        if (hasFace) ApplyHeadRotation(_latestPacket.HeadRotation);
        else ReturnHeadToNeutral();

        if (hasPose)
            ApplyArmRotations(_latestPacket.LeftArmRotation, _latestPacket.RightArmRotation);
        else ReturnArmsToNeutral();

        if (enableApproximateExpressions && hasFace && _latestPacket.BlendShapeValues != null)
            LerpBlendShapes(_latestPacket.BlendShapeValues);
        else
            LerpBlendShapes(_neutralBlendShapes);
    }

    private void ApplyHeadRotation(Vector3 euler)
    {
        Quaternion trackingRotation = _headCalibration * ToHeadTrackingRotation(euler);
        Quaternion target = _initialNeck * trackingRotation;
        _neck.localRotation = Quaternion.Slerp(_neck.localRotation, target, SmoothingFactor());
    }

    private void ApplyArmRotations(Vector3 leftEuler, Vector3 rightEuler)
    {
        _leftInputAngle = swapArms ? rightEuler.z : leftEuler.z;
        _rightInputAngle = swapArms ? leftEuler.z : rightEuler.z;
        float leftCalibration = swapArms ? _rightArmCalibrationAngle : _leftArmCalibrationAngle;
        float rightCalibration = swapArms ? _leftArmCalibrationAngle : _rightArmCalibrationAngle;

        _leftDeltaAngle = Mathf.DeltaAngle(leftCalibration, _leftInputAngle);
        _rightDeltaAngle = Mathf.DeltaAngle(rightCalibration, _rightInputAngle);
        _leftOutputAngle = CalculateArmOutput(_leftDeltaAngle, leftGain, leftInvert);
        _rightOutputAngle = CalculateArmOutput(_rightDeltaAngle, rightGain, rightInvert);

        Quaternion leftTracking = Quaternion.Euler(0f, 0f, _leftOutputAngle);
        Quaternion rightTracking = Quaternion.Euler(0f, 0f, _rightOutputAngle);
        float t = SmoothingFactor();

        _leftArm.localRotation = Quaternion.Slerp(
            _leftArm.localRotation,
            _neutralLeftArm * leftTracking,
            t);
        _rightArm.localRotation = Quaternion.Slerp(
            _rightArm.localRotation,
            _neutralRightArm * rightTracking,
            t);
    }

    private void ApplyArmTestPose()
    {
        float t = SmoothingFactor();
        _leftArm.localRotation = Quaternion.Slerp(
            _leftArm.localRotation,
            _initialLeftArm * Quaternion.Euler(0f, 0f, leftTestAngle),
            t);
        _rightArm.localRotation = Quaternion.Slerp(
            _rightArm.localRotation,
            _initialRightArm * Quaternion.Euler(0f, 0f, rightTestAngle),
            t);
    }

    private void ReturnToNeutralPose()
    {
        float t = SmoothingFactor();
        _neck.localRotation = Quaternion.Slerp(_neck.localRotation, _initialNeck, t);
        _leftArm.localRotation = Quaternion.Slerp(_leftArm.localRotation, _neutralLeftArm, t);
        _rightArm.localRotation = Quaternion.Slerp(_rightArm.localRotation, _neutralRightArm, t);
        LerpBlendShapes(_neutralBlendShapes);
    }

    private void ReturnHeadToNeutral()
    {
        _neck.localRotation = Quaternion.Slerp(
            _neck.localRotation,
            _initialNeck,
            SmoothingFactor());
    }

    private void ReturnArmsToNeutral()
    {
        float t = SmoothingFactor();
        _leftArm.localRotation = Quaternion.Slerp(_leftArm.localRotation, _neutralLeftArm, t);
        _rightArm.localRotation = Quaternion.Slerp(_rightArm.localRotation, _neutralRightArm, t);
    }

    private void SnapToNeutralPose()
    {
        _neck.localRotation = _initialNeck;
        _leftArm.localRotation = _neutralLeftArm;
        _rightArm.localRotation = _neutralRightArm;
    }

    private static Quaternion ToHeadTrackingRotation(Vector3 euler)
    {
        float pitch = Mathf.Clamp(euler.x, -20f, 20f);
        float yaw = Mathf.Clamp(euler.y, -35f, 35f);
        float roll = Mathf.Clamp(euler.z, -20f, 20f);
        return Quaternion.Euler(-pitch, -yaw, roll);
    }

    private float CalculateArmOutput(float deltaAngle, float gain, bool invert)
    {
        float direction = invert ? -1f : 1f;
        return Mathf.Clamp(deltaAngle * gain * direction, -maxArmAngle, maxArmAngle);
    }

    private void RebuildNeutralArmRotations()
    {
        if (_leftArm == null || _rightArm == null) return;
        _neutralLeftArm = _initialLeftArm * Quaternion.Euler(0f, 0f, leftNeutralAngle);
        _neutralRightArm = _initialRightArm * Quaternion.Euler(0f, 0f, rightNeutralAngle);
    }

    private static ArmMappingConfig DefaultArmConfig()
    {
        return new ArmMappingConfig
        {
            SwapArms = false,
            LeftInvert = false,
            RightInvert = false,
            TestMode = false,
            LeftNeutralAngle = -68f,
            RightNeutralAngle = 68f,
            LeftGain = 1f,
            RightGain = 1f,
            MaxAngle = 140f,
            InputSmoothing = 0.4f,
            MaxInputJump = 120f,
            LeftTestAngle = -68f,
            RightTestAngle = 68f
        };
    }

    private void LoadArmConfig(string prefix)
    {
        ArmMappingConfig defaults = DefaultArmConfig();
        swapArms = PlayerPrefs.GetInt(prefix + "Swap", defaults.SwapArms ? 1 : 0) != 0;
        leftInvert = PlayerPrefs.GetInt(prefix + "LeftInvert", defaults.LeftInvert ? 1 : 0) != 0;
        rightInvert = PlayerPrefs.GetInt(prefix + "RightInvert", defaults.RightInvert ? 1 : 0) != 0;
        armTestMode = false;
        leftNeutralAngle = PlayerPrefs.GetFloat(prefix + "LeftNeutral", defaults.LeftNeutralAngle);
        rightNeutralAngle = PlayerPrefs.GetFloat(prefix + "RightNeutral", defaults.RightNeutralAngle);
        leftGain = PlayerPrefs.GetFloat(prefix + "LeftGain", defaults.LeftGain);
        rightGain = PlayerPrefs.GetFloat(prefix + "RightGain", defaults.RightGain);
        maxArmAngle = PlayerPrefs.GetFloat(prefix + "MaxAngle", defaults.MaxAngle);
        inputSmoothing = PlayerPrefs.GetFloat(prefix + "InputSmoothing", defaults.InputSmoothing);
        maxInputJump = PlayerPrefs.GetFloat(prefix + "MaxInputJump", defaults.MaxInputJump);
        leftTestAngle = PlayerPrefs.GetFloat(prefix + "LeftTest", leftNeutralAngle);
        rightTestAngle = PlayerPrefs.GetFloat(prefix + "RightTest", rightNeutralAngle);
    }

    private void QueueConfigSave()
    {
        _configDirty = true;
        _configSaveTime = Time.unscaledTime + 0.5f;
    }

    private void SaveConfigIfDue()
    {
        if (!_configDirty || Time.unscaledTime < _configSaveTime) return;

        PlayerPrefs.SetInt(_configKeyPrefix + "Swap", swapArms ? 1 : 0);
        PlayerPrefs.SetInt(_configKeyPrefix + "LeftInvert", leftInvert ? 1 : 0);
        PlayerPrefs.SetInt(_configKeyPrefix + "RightInvert", rightInvert ? 1 : 0);
        PlayerPrefs.SetFloat(_configKeyPrefix + "LeftNeutral", leftNeutralAngle);
        PlayerPrefs.SetFloat(_configKeyPrefix + "RightNeutral", rightNeutralAngle);
        PlayerPrefs.SetFloat(_configKeyPrefix + "LeftGain", leftGain);
        PlayerPrefs.SetFloat(_configKeyPrefix + "RightGain", rightGain);
        PlayerPrefs.SetFloat(_configKeyPrefix + "MaxAngle", maxArmAngle);
        PlayerPrefs.SetFloat(_configKeyPrefix + "InputSmoothing", inputSmoothing);
        PlayerPrefs.SetFloat(_configKeyPrefix + "MaxInputJump", maxInputJump);
        PlayerPrefs.SetFloat(_configKeyPrefix + "LeftTest", leftTestAngle);
        PlayerPrefs.SetFloat(_configKeyPrefix + "RightTest", rightTestAngle);
        PlayerPrefs.Save();
        _configDirty = false;
    }

    private static string BuildAvatarConfigKeyPrefix(string avatarId)
    {
        if (string.IsNullOrEmpty(avatarId)) return LegacyConfigKeyPrefix;
        return AvatarConfigKeyRoot + avatarId.ToLowerInvariant() + ".";
    }

    private static IEnumerable<string> ConfigKeySuffixes()
    {
        yield return "Swap";
        yield return "LeftInvert";
        yield return "RightInvert";
        yield return "LeftNeutral";
        yield return "RightNeutral";
        yield return "LeftGain";
        yield return "RightGain";
        yield return "MaxAngle";
        yield return "InputSmoothing";
        yield return "MaxInputJump";
        yield return "LeftTest";
        yield return "RightTest";
    }

    private void OnApplicationQuit()
    {
        FlushConfig();
    }

    private void OnDisable() => FlushConfig();

    private float SmoothingFactor()
    {
        return 1f - Mathf.Exp(-lerpSpeed * Time.deltaTime);
    }

    private void LerpBlendShapes(float[] targetValues)
    {
        if (targetValues == null || targetValues.Length < 18) return;

        float t = SmoothingFactor();
        int count = Mathf.Min(_currentBlendShapes.Length, targetValues.Length);
        for (int i = 0; i < count; i++)
            _currentBlendShapes[i] = Mathf.Lerp(_currentBlendShapes[i], targetValues[i], t);

        Set(ExpressionPreset.blinkLeft, Get(_currentBlendShapes, "eyeBlinkLeft"));
        Set(ExpressionPreset.blinkRight, Get(_currentBlendShapes, "eyeBlinkRight"));
    }

    private void Set(ExpressionPreset preset, float value)
    {
        if (vrmInstance != null && vrmInstance.Runtime != null)
            vrmInstance.Runtime.Expression.SetWeight(
                ExpressionKey.CreateFromPreset(preset),
                Mathf.Clamp01(value));
    }

    private static float Get(float[] values, string name)
    {
        return TrackingPacket.BlendShapeIndex.TryGetValue(name, out int index) ? values[index] : 0f;
    }
}
