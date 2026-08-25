using UnityEngine;
using UnityEngine.InputSystem;

public static class KoreanUiFontProvider
{
    private const string KoreanFontResourcePath = "Fonts/NotoSansKR-Regular";

    private static Font _guiFont;
    private static bool _loadAttempted;

    public static Font GuiFont
    {
        get
        {
            if (!_loadAttempted)
            {
                _loadAttempted = true;
                _guiFont = Resources.Load<Font>(KoreanFontResourcePath);
                if (_guiFont == null)
                {
                    Debug.LogError(
                        $"한글 UI 폰트를 불러오지 못했습니다: Resources/{KoreanFontResourcePath}");
                }
            }

            return _guiFont;
        }
    }
}

public sealed class WebcamControlPanel : MonoBehaviour
{
    private WebcamManager _webcamManager;
    private TrackingPipeline _trackingPipeline;
    private Vector2 _panelScroll;
    private Vector2 _deviceScroll;
    private bool _visible = true;
    private bool _changingCamera;
    private int _deviceIndex;
    private int _resolutionIndex;
    private bool _mirror;
    private string[] _deviceNames = System.Array.Empty<string>();
    private string[] _resolutionLabels = System.Array.Empty<string>();
    private GUIStyle _validationOverlayStyle;
    private GUIStyle _validationResultStyle;
    private GUIStyle _trackingStatusStyle;
    private GUISkin _runtimeGuiSkin;
    private readonly GUIContent _trackingStatusContent = new();
    private bool _showArmConfig = true;
    private bool _showDebugTools;
    private bool _armValidationRunning;
    private float _armValidationStartTime;
    private float _armValidationLostTime;
    private float _leftValidationBaseline;
    private float _rightValidationBaseline;
    private float _leftValidationResponse;
    private float _rightValidationResponse;
    private float _previousLeftInput;
    private float _previousRightInput;
    private float _previousLeftOutput;
    private float _previousRightOutput;
    private bool _validationSampleInitialized;
    private int _validationBoundaryCrossings;
    private int _validationUnsafeCrossings;
    private int _validationOutputSpikes;
    private string _armValidationResult;
    private string _validationOverlayMessage;
    private float _validationOverlayEndTime;

    private const float ArmValidationPhaseSeconds = 3f;
    private const float ArmValidationDuration = ArmValidationPhaseSeconds * 4f;
    private const string ArmValidationResultKey = "VAS.ArmValidation.LastResult";

    public void Initialize(WebcamManager webcamManager, TrackingPipeline trackingPipeline)
    {
        _webcamManager = webcamManager;
        _trackingPipeline = trackingPipeline;
        RefreshOptions();
    }

    private void Update()
    {
        if (Keyboard.current?.f1Key.wasPressedThisFrame == true) _visible = !_visible;
        if (Keyboard.current?.cKey.wasPressedThisFrame == true) _trackingPipeline?.CalibrateAvatar();
        UpdateArmValidation();
    }

    private void OnGUI()
    {
        GUISkin previousSkin = GUI.skin;
        try
        {
            ApplyKoreanGuiFont();
            DrawControlPanelGui();
        }
        finally
        {
            GUI.skin = previousSkin;
        }
    }

    private void DrawControlPanelGui()
    {
        DrawTrackingStatusOverlay();
        if (!_visible || _webcamManager == null)
        {
            DrawArmValidationOverlay();
            return;
        }

        DrawCalibrationGuide();

        float panelWidth = Mathf.Min(380f, Screen.width - 24f);
        float panelHeight = Mathf.Min(520f, Screen.height - 24f);
        float contentWidth = Mathf.Max(180f, panelWidth - 32f);
        float contentHeight = Mathf.Max(140f, panelHeight - 24f);

        GUI.Box(new Rect(12f, 12f, panelWidth, panelHeight), GUIContent.none);
        GUILayout.BeginArea(new Rect(28f, 24f, contentWidth, contentHeight));
        _panelScroll = GUILayout.BeginScrollView(_panelScroll, false, false);
        GUILayout.Label("웹캠 설정  (F1: 숨기기)", GUI.skin.box);

        if (_deviceNames.Length == 0)
        {
            GUILayout.Label("사용 가능한 웹캠이 없습니다.");
        }
        else
        {
            GUILayout.Label("카메라 장치");
            float deviceListHeight = Mathf.Clamp(_deviceNames.Length * 25f, 28f, 90f);
            _deviceScroll = GUILayout.BeginScrollView(_deviceScroll, GUILayout.Height(deviceListHeight));
            _deviceIndex = GUILayout.SelectionGrid(_deviceIndex, _deviceNames, 1);
            GUILayout.EndScrollView();

            GUILayout.Space(6);
            GUILayout.Label("해상도");
            _resolutionIndex = GUILayout.SelectionGrid(_resolutionIndex, _resolutionLabels, 1);

            _mirror = GUILayout.Toggle(_mirror, "미리보기 좌우 반전");

            GUI.enabled = !_changingCamera && !_webcamManager.IsChanging;
            string startLabel = _webcamManager.IsReady ? "변경사항 적용" : "카메라 시작";
            if (GUILayout.Button(_changingCamera ? "처리 중..." : startLabel, GUILayout.Height(30)))
                StartOrApplyCamera();

            GUI.enabled = !_changingCamera && !_webcamManager.IsChanging && _webcamManager.IsReady;
            if (GUILayout.Button("카메라 중지", GUILayout.Height(30)))
                StopCamera();
            GUI.enabled = true;
        }

        if (GUILayout.Button("카메라 목록 새로고침")) RefreshOptions();

        if (!string.IsNullOrEmpty(_webcamManager.LastError))
            GUILayout.Label($"오류: {_webcamManager.LastError}");
        else if (_webcamManager.IsReady)
            GUILayout.Label($"사용 중: {_webcamManager.Texture.width} x {_webcamManager.Texture.height}");
        else
            GUILayout.Label("상태: 카메라 꺼짐");

        string privacyStatus = _trackingPipeline == null
            ? "개인정보 보호 상태 확인 중"
            : _trackingPipeline.IsRawCameraPreviewBlocked
                ? "개인정보 보호: 원본 영상 출력 차단"
                : "보호 오류: 원본 영상 출력을 차단하는 중";
        GUILayout.Label(privacyStatus, GUI.skin.box);

        GUILayout.Space(8);
        GUILayout.Label("아바타", GUI.skin.box);
        GUILayout.Label(_trackingPipeline != null ? _trackingPipeline.AvatarStatus : "아바타 컨트롤러가 없습니다.");
        bool canStartCalibration = _trackingPipeline != null && _trackingPipeline.CanCalibrateAvatar;
        GUI.enabled = canStartCalibration;
        string calibrationLabel = _trackingPipeline != null
            ? _trackingPipeline.AvatarCalibrationButtonLabel
            : "중립 자세 캘리브레이션 (3초)";
        if (GUILayout.Button(calibrationLabel, GUILayout.Height(30)) && canStartCalibration)
            _trackingPipeline.CalibrateAvatar();
        GUI.enabled = true;
        GUILayout.Label("단축키: C");

        GUILayout.Space(8);
        _showArmConfig = GUILayout.Toggle(_showArmConfig, "팔 매핑 설정", GUI.skin.button);
        if (_showArmConfig) DrawArmMappingConfig();

        GUILayout.Space(8);
        _showDebugTools = GUILayout.Toggle(_showDebugTools, "디버그 / 테스트", GUI.skin.button);
        if (_showDebugTools) DrawDebugTools();

        GUILayout.EndScrollView();
        GUILayout.EndArea();

        DrawArmValidationOverlay();
    }

    private void DrawCalibrationGuide()
    {
        if (_trackingPipeline == null || !_trackingPipeline.TryGetWebcamPreviewGuiRect(out Rect previewRect)) return;

        const float border = 2f;
        Color previousColor = GUI.color;
        GUI.color = new Color(0.15f, 0.9f, 1f, 0.9f);
        GUI.DrawTexture(new Rect(previewRect.x, previewRect.y, previewRect.width, border), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(previewRect.x, previewRect.yMax - border, previewRect.width, border), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(previewRect.x, previewRect.y, border, previewRect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(previewRect.xMax - border, previewRect.y, border, previewRect.height), Texture2D.whiteTexture);

        GUI.color = previousColor;
    }

    private void DrawArmMappingConfig()
    {
        if (_trackingPipeline == null || !_trackingPipeline.HasArmMappingConfig)
        {
            GUILayout.Label("팔 매퍼 불러오는 중...");
            return;
        }

        VRMMapper.ArmMappingConfig config = _trackingPipeline.GetArmMappingConfig();
        VRMMapper.ArmMappingConfig changed = config;

        GUILayout.Label(_trackingPipeline.ArmMappingDiagnostics);
        GUILayout.Label("뼈 단독 테스트 (카메라 없이 작동)", GUI.skin.box);
        changed.TestMode = GUILayout.Toggle(changed.TestMode, "뼈 단독 테스트 모드 사용");

        if (changed.TestMode)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("중립 자세"))
            {
                changed.LeftTestAngle = changed.LeftNeutralAngle;
                changed.RightTestAngle = changed.RightNeutralAngle;
            }
            if (GUILayout.Button("T 자세"))
            {
                changed.LeftTestAngle = 0f;
                changed.RightTestAngle = 0f;
            }
            if (GUILayout.Button("양팔 들기"))
            {
                changed.LeftTestAngle = 75f;
                changed.RightTestAngle = -75f;
            }
            GUILayout.EndHorizontal();

            changed.LeftTestAngle = DrawSlider("왼쪽 본 Z축", changed.LeftTestAngle, -160f, 160f);
            changed.RightTestAngle = DrawSlider("오른쪽 본 Z축", changed.RightTestAngle, -160f, 160f);
        }

        GUILayout.Label("웹캠 팔 매핑", GUI.skin.box);
        changed.SwapArms = GUILayout.Toggle(changed.SwapArms, "좌우 입력 서로 바꾸기");
        GUILayout.BeginHorizontal();
        changed.LeftInvert = GUILayout.Toggle(changed.LeftInvert, "왼팔 방향 반전");
        changed.RightInvert = GUILayout.Toggle(changed.RightInvert, "오른팔 방향 반전");
        GUILayout.EndHorizontal();

        changed.LeftNeutralAngle = DrawSlider("왼팔 중립 Z축", changed.LeftNeutralAngle, -120f, 120f);
        changed.RightNeutralAngle = DrawSlider("오른팔 중립 Z축", changed.RightNeutralAngle, -120f, 120f);
        changed.LeftGain = DrawSlider("왼팔 감도", changed.LeftGain, 0.25f, 2f);
        changed.RightGain = DrawSlider("오른팔 감도", changed.RightGain, 0.25f, 2f);
        changed.MaxAngle = DrawSlider("최대 변화 각도", changed.MaxAngle, 60f, 160f);
        changed.InputSmoothing = DrawSlider("입력 반응 속도", changed.InputSmoothing, 0.05f, 1f);
        changed.MaxInputJump = DrawSlider("최대 순간 입력 변화", changed.MaxInputJump, 30f, 180f);

        if (ArmConfigChanged(config, changed))
            _trackingPipeline.SetArmMappingConfig(changed);

        if (GUILayout.Button("팔 설정 기본값 복원"))
            _trackingPipeline.ResetArmMappingConfig();

        GUILayout.Label("변경사항은 자동으로 저장됩니다.");
    }

    private void DrawDebugTools()
    {
        GUILayout.Label("팔 동작 검증", GUI.skin.box);
        GUILayout.Label("캘리브레이션 후 실행하세요. 어깨와 팔꿈치는 보여야 하며 손목은 선택 사항입니다.");

        if (_armValidationRunning)
        {
            GUILayout.Label(GetArmValidationInstruction());
            float progress = Mathf.Clamp01((Time.unscaledTime - _armValidationStartTime) / ArmValidationDuration);
            GUILayout.HorizontalSlider(progress, 0f, 1f);
            if (GUILayout.Button("검증 취소")) CancelArmValidation();
        }
        else
        {
            bool canValidate = CanStartArmValidation(out string reason);
            GUI.enabled = canValidate;
            if (GUILayout.Button("팔 동작 검증 시작 (12초)", GUILayout.Height(30)))
                BeginArmValidation();
            GUI.enabled = true;
            if (!canValidate) GUILayout.Label(reason);
        }

        string result = !string.IsNullOrEmpty(_armValidationResult)
            ? _armValidationResult
            : PlayerPrefs.GetString(ArmValidationResultKey, "아직 검증 결과가 없습니다.");
        GUILayout.Label(result);
    }

    private bool CanStartArmValidation(out string reason)
    {
        reason = string.Empty;
        if (_trackingPipeline == null || !_trackingPipeline.TryGetArmValidationSample(out TrackingPipeline.ArmValidationSample sample))
        {
            reason = "팔 매퍼가 아직 준비되지 않았습니다.";
            return false;
        }

        if (!sample.Calibrated)
        {
            reason = "먼저 캘리브레이션을 완료하세요.";
            return false;
        }

        if (!sample.PoseTracked)
        {
            reason = "얼굴, 양쪽 어깨와 팔꿈치를 화면에 유지하세요.";
            return false;
        }

        if (_trackingPipeline.GetArmMappingConfig().TestMode)
        {
            reason = "먼저 뼈 단독 테스트 모드를 끄세요.";
            return false;
        }

        return true;
    }

    private void BeginArmValidation()
    {
        if (!CanStartArmValidation(out _)) return;

        _trackingPipeline.ResetArmValidationDiagnostics();
        _armValidationRunning = true;
        _armValidationStartTime = Time.unscaledTime;
        _armValidationLostTime = 0f;
        _leftValidationResponse = 0f;
        _rightValidationResponse = 0f;
        _validationBoundaryCrossings = 0;
        _validationUnsafeCrossings = 0;
        _validationOutputSpikes = 0;
        _validationSampleInitialized = false;
        _armValidationResult = string.Empty;
    }

    private void UpdateArmValidation()
    {
        if (!_armValidationRunning || _trackingPipeline == null) return;

        float elapsed = Time.unscaledTime - _armValidationStartTime;
        if (!_trackingPipeline.TryGetArmValidationSample(out TrackingPipeline.ArmValidationSample sample))
        {
            CancelArmValidation("검증 취소: 팔 매퍼 연결이 끊어졌습니다.");
            return;
        }

        if (!sample.PoseTracked)
        {
            _armValidationLostTime += Time.unscaledDeltaTime;
        }
        else
        {
            CollectArmValidationSample(sample, elapsed);
        }

        if (elapsed >= ArmValidationDuration)
            CompleteArmValidation(sample.RejectedInputCount);
    }

    private void CollectArmValidationSample(TrackingPipeline.ArmValidationSample sample, float elapsed)
    {
        if (!_validationSampleInitialized)
        {
            _leftValidationBaseline = sample.LeftOutput;
            _rightValidationBaseline = sample.RightOutput;
            _previousLeftInput = sample.LeftInput;
            _previousRightInput = sample.RightInput;
            _previousLeftOutput = sample.LeftOutput;
            _previousRightOutput = sample.RightOutput;
            _validationSampleInitialized = true;
            return;
        }

        int phase = Mathf.Clamp(Mathf.FloorToInt(elapsed / ArmValidationPhaseSeconds), 0, 3);
        if (phase == 0 || phase == 2)
        {
            _leftValidationBaseline = Mathf.LerpAngle(_leftValidationBaseline, sample.LeftOutput, 0.08f);
            _rightValidationBaseline = Mathf.LerpAngle(_rightValidationBaseline, sample.RightOutput, 0.08f);
        }
        else if (phase == 1)
        {
            _leftValidationResponse = Mathf.Max(
                _leftValidationResponse,
                Mathf.Abs(Mathf.DeltaAngle(_leftValidationBaseline, sample.LeftOutput)));
        }
        else
        {
            _rightValidationResponse = Mathf.Max(
                _rightValidationResponse,
                Mathf.Abs(Mathf.DeltaAngle(_rightValidationBaseline, sample.RightOutput)));
        }

        CheckAngleBoundary(sample.LeftInput, sample.LeftOutput, _previousLeftInput, _previousLeftOutput);
        CheckAngleBoundary(sample.RightInput, sample.RightOutput, _previousRightInput, _previousRightOutput);

        if (Mathf.Abs(Mathf.DeltaAngle(_previousLeftOutput, sample.LeftOutput)) > 55f)
            _validationOutputSpikes++;
        if (Mathf.Abs(Mathf.DeltaAngle(_previousRightOutput, sample.RightOutput)) > 55f)
            _validationOutputSpikes++;

        _previousLeftInput = sample.LeftInput;
        _previousRightInput = sample.RightInput;
        _previousLeftOutput = sample.LeftOutput;
        _previousRightOutput = sample.RightOutput;
    }

    private void CheckAngleBoundary(float input, float output, float previousInput, float previousOutput)
    {
        if (Mathf.Abs(input - previousInput) <= 300f) return;

        _validationBoundaryCrossings++;
        if (Mathf.Abs(Mathf.DeltaAngle(previousOutput, output)) > 30f)
            _validationUnsafeCrossings++;
    }

    private string GetArmValidationInstruction()
    {
        float elapsed = Time.unscaledTime - _armValidationStartTime;
        int phase = Mathf.Clamp(Mathf.FloorToInt(elapsed / ArmValidationPhaseSeconds), 0, 3);
        float remaining = ArmValidationPhaseSeconds - elapsed % ArmValidationPhaseSeconds;
        string instruction = phase switch
        {
            0 => "1/4 편안한 중립 자세를 유지하세요",
            1 => "2/4 왼쪽 팔꿈치를 들어주세요",
            2 => "3/4 다시 중립 자세로 돌아오세요",
            _ => "4/4 오른쪽 팔꿈치를 들어주세요"
        };
        return $"{instruction}  ({Mathf.Max(1, Mathf.CeilToInt(remaining))}초)";
    }

    private void CompleteArmValidation(int rejectedInputCount)
    {
        _armValidationRunning = false;
        bool leftResponded = _leftValidationResponse >= 20f;
        bool rightResponded = _rightValidationResponse >= 20f;
        bool stable = _validationUnsafeCrossings == 0 && _validationOutputSpikes <= 1;
        bool trackingStable = _armValidationLostTime < 1f;
        bool passed = leftResponded && rightResponded && stable && trackingStable;

        string status = passed ? "검증 통과" : "확인 필요";
        _armValidationResult =
            $"[{status}] {System.DateTime.Now:yyyy.MM.dd HH:mm}\n" +
            $"반응 각도 좌/우: {_leftValidationResponse:F1} / {_rightValidationResponse:F1}\n" +
            $"경계 통과: {_validationBoundaryCrossings}회 (불안정 {_validationUnsafeCrossings}회)\n" +
            $"출력 급변: {_validationOutputSpikes}회, 입력 거부: {rejectedInputCount}회\n" +
            $"포즈 유실: {_armValidationLostTime:F1}초";

        _validationOverlayMessage = passed ? "팔 동작 검증 통과" : "팔 동작 검증: 확인 필요";
        _validationOverlayEndTime = Time.unscaledTime + 5f;

        PlayerPrefs.SetString(ArmValidationResultKey, _armValidationResult);
        PlayerPrefs.Save();
    }

    private void CancelArmValidation(string message = "팔 동작 검증을 취소했습니다.")
    {
        _armValidationRunning = false;
        _armValidationResult = message;
        _validationOverlayMessage = message;
        _validationOverlayEndTime = Time.unscaledTime + 3f;
    }

    private void ApplyKoreanGuiFont()
    {
        Font koreanFont = KoreanUiFontProvider.GuiFont;
        if (koreanFont == null) return;

        if (_runtimeGuiSkin == null)
        {
            _runtimeGuiSkin = Instantiate(GUI.skin);
            _runtimeGuiSkin.name = "런타임 한글 GUI 스킨";
            _runtimeGuiSkin.hideFlags = HideFlags.HideAndDontSave;
            _runtimeGuiSkin.font = koreanFont;
            _runtimeGuiSkin.label.fontSize = 16;
            _runtimeGuiSkin.label.wordWrap = true;
            _runtimeGuiSkin.button.fontSize = 16;
            _runtimeGuiSkin.toggle.fontSize = 16;
            _runtimeGuiSkin.box.fontSize = 16;
        }

        GUI.skin = _runtimeGuiSkin;
    }

    private void OnDisable()
    {
        if (_runtimeGuiSkin != null)
        {
            DestroyImmediate(_runtimeGuiSkin);
            _runtimeGuiSkin = null;
        }

        _validationOverlayStyle = null;
        _validationResultStyle = null;
        _trackingStatusStyle = null;
    }

    private void DrawTrackingStatusOverlay()
    {
        if (_trackingPipeline == null || string.IsNullOrEmpty(_trackingPipeline.TrackingStatusText)) return;

        string status = _trackingPipeline.TrackingStatusText;
        float fontSize = Mathf.Clamp(Screen.height * 0.026f, 17f, 24f);
        float width = Mathf.Min(620f, Screen.width - 28f);

        _trackingStatusStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperLeft,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };
        _trackingStatusStyle.font = KoreanUiFontProvider.GuiFont;
        _trackingStatusStyle.fontSize = Mathf.RoundToInt(fontSize);
        _trackingStatusStyle.normal.textColor = Color.white;

        _trackingStatusContent.text = status;
        float contentWidth = width - 20f;
        float contentHeight = _trackingStatusStyle.CalcHeight(_trackingStatusContent, contentWidth);
        float height = contentHeight + 18f;
        Rect backgroundRect = new Rect(14f, Screen.height - height - 14f, width, height);

        Color previousColor = GUI.color;
        GUI.color = new Color(0.02f, 0.03f, 0.06f, 0.78f);
        GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
        GUI.color = previousColor;

        GUI.Label(new Rect(backgroundRect.x + 10f, backgroundRect.y + 7f, backgroundRect.width - 20f, backgroundRect.height - 14f),
            _trackingStatusContent,
            _trackingStatusStyle);
    }

    private void DrawArmValidationOverlay()
    {
        bool showResult = !string.IsNullOrEmpty(_validationOverlayMessage) &&
                          Time.unscaledTime < _validationOverlayEndTime;
        if (!_armValidationRunning && !showResult) return;

        int previousDepth = GUI.depth;
        GUI.depth = -1000;

        float width = Mathf.Min(760f, Screen.width - 32f);
        float height = _armValidationRunning ? 132f : 104f;
        Rect backgroundRect = new Rect((Screen.width - width) * 0.5f, 24f, width, height);

        Color previousColor = GUI.color;
        GUI.color = new Color(0.02f, 0.03f, 0.06f, 0.9f);
        GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
        GUI.color = previousColor;

        int mainFontSize = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.05f, 28f, 52f));
        _validationOverlayStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };
        _validationOverlayStyle.font = KoreanUiFontProvider.GuiFont;
        _validationOverlayStyle.fontSize = mainFontSize;
        _validationOverlayStyle.normal.textColor = Color.white;

        _validationResultStyle ??= new GUIStyle(_validationOverlayStyle);
        _validationResultStyle.font = KoreanUiFontProvider.GuiFont;
        _validationResultStyle.fontSize = mainFontSize;
        _validationResultStyle.normal.textColor = new Color(0.2f, 0.95f, 1f);

        if (_armValidationRunning)
        {
            GUI.Label(new Rect(backgroundRect.x + 12f, backgroundRect.y + 8f, backgroundRect.width - 24f, 82f),
                GetArmValidationInstruction(),
                _validationOverlayStyle);

            float progress = Mathf.Clamp01((Time.unscaledTime - _armValidationStartTime) / ArmValidationDuration);
            Rect progressBack = new Rect(backgroundRect.x + 28f, backgroundRect.yMax - 28f, backgroundRect.width - 56f, 12f);
            GUI.color = new Color(1f, 1f, 1f, 0.22f);
            GUI.DrawTexture(progressBack, Texture2D.whiteTexture);
            GUI.color = new Color(0.15f, 0.9f, 1f, 1f);
            GUI.DrawTexture(new Rect(progressBack.x, progressBack.y, progressBack.width * progress, progressBack.height), Texture2D.whiteTexture);
            GUI.color = previousColor;
        }
        else
        {
            GUI.Label(backgroundRect, _validationOverlayMessage, _validationResultStyle);
        }

        GUI.depth = previousDepth;
    }

    private static float DrawSlider(string label, float value, float min, float max)
    {
        GUILayout.Label($"{label}: {value:F1}");
        return GUILayout.HorizontalSlider(value, min, max);
    }

    private static bool ArmConfigChanged(VRMMapper.ArmMappingConfig before, VRMMapper.ArmMappingConfig after)
    {
        return before.SwapArms != after.SwapArms ||
               before.LeftInvert != after.LeftInvert ||
               before.RightInvert != after.RightInvert ||
               before.TestMode != after.TestMode ||
               !Mathf.Approximately(before.LeftNeutralAngle, after.LeftNeutralAngle) ||
               !Mathf.Approximately(before.RightNeutralAngle, after.RightNeutralAngle) ||
               !Mathf.Approximately(before.LeftGain, after.LeftGain) ||
               !Mathf.Approximately(before.RightGain, after.RightGain) ||
               !Mathf.Approximately(before.MaxAngle, after.MaxAngle) ||
               !Mathf.Approximately(before.InputSmoothing, after.InputSmoothing) ||
               !Mathf.Approximately(before.MaxInputJump, after.MaxInputJump) ||
               !Mathf.Approximately(before.LeftTestAngle, after.LeftTestAngle) ||
               !Mathf.Approximately(before.RightTestAngle, after.RightTestAngle);
    }

    private async void StartOrApplyCamera()
    {
        if (_trackingPipeline == null || _changingCamera) return;

        _changingCamera = true;
        try
        {
            await _trackingPipeline.ChangeCameraAsync(_deviceIndex, _resolutionIndex, _mirror);
            RefreshOptions();
        }
        finally
        {
            _changingCamera = false;
        }
    }

    private async void StopCamera()
    {
        if (_trackingPipeline == null || _changingCamera) return;

        _changingCamera = true;
        try
        {
            await _trackingPipeline.StopCameraAsync();
            RefreshOptions();
        }
        finally
        {
            _changingCamera = false;
        }
    }

    private void SyncSelection()
    {
        if (_webcamManager == null) return;
        _deviceIndex = _webcamManager.SelectedDeviceIndex;
        _resolutionIndex = _webcamManager.SelectedResolutionIndex;
        _mirror = _webcamManager.Mirror;
    }

    public void RefreshOptions()
    {
        if (_webcamManager == null) return;

        _webcamManager.RefreshDevices();
        _deviceNames = _webcamManager.DeviceNames;

        var presets = _webcamManager.Presets;
        _resolutionLabels = new string[presets.Length];
        for (int i = 0; i < presets.Length; i++) _resolutionLabels[i] = presets[i].label;

        SyncSelection();
    }
}
