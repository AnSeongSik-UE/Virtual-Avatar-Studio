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
    private enum SettingsPage
    {
        Camera,
        Avatar,
        ArmMapping,
        Debug
    }

    private WebcamManager _webcamManager;
    private TrackingPipeline _trackingPipeline;
    private Vector2 _cameraPageScroll;
    private Vector2 _avatarPageScroll;
    private Vector2 _armPageScroll;
    private Vector2 _debugPageScroll;
    private Vector2 _deviceScroll;
    private Vector2 _avatarListScroll;
    private SettingsPage _settingsPage = SettingsPage.Avatar;
    private bool _visible = true;
    private bool _changingCamera;
    private bool _avatarFilePickerOpen;
    private int _deviceIndex;
    private int _resolutionIndex;
    private bool _mirror;
    private string[] _deviceNames = System.Array.Empty<string>();
    private string[] _resolutionLabels = System.Array.Empty<string>();
    private GUIStyle _validationOverlayStyle;
    private GUIStyle _validationResultStyle;
    private GUIStyle _trackingStatusStyle;
    private GUIStyle _trackingOverlayHeaderStyle;
    private GUIStyle _trackingOverlayToggleStyle;
    private GUIStyle _debugTrackingStatusStyle;
    private GUIStyle _avatarActionButtonStyle;
    private GUIStyle _paletteButtonStyle;
    private GUIStyle _avatarNoticeStyle;
    private GUIStyle _calibrationTitleStyle;
    private GUIStyle _calibrationDetailStyle;
    private GUISkin _runtimeGuiSkin;
    private readonly GUIContent _trackingStatusContent = new();
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
    private int _observedAvatarRevision = -1;
    private string _validationAvatarId = string.Empty;
    private string _pendingAvatarRemovalId = string.Empty;
    private string _avatarInputMessage = string.Empty;
    private string _avatarNoticeMessage = string.Empty;
    private float _avatarNoticeEndTime;
    private bool _avatarNoticeIsError;
    private bool _calibrationRequestRunning;
    private int _observedCalibrationNoticeRevision;
    private WindowsVrmDropReceiver _vrmDropReceiver;
    private Rect _trackingOverlayRect;
    private Vector2 _trackingOverlayDragOffset;
    private bool _trackingOverlayCollapsed = true;
    private bool _trackingOverlayDragging;
    private bool _trackingOverlayPreferencesLoaded;
    private float _trackingOverlayNormalizedX;
    private float _trackingOverlayNormalizedY = 1f;
    private string _trackingSummarySource = string.Empty;
    private string _trackingSummary = string.Empty;
    private Color _broadcastBackgroundColor;
    private bool _broadcastBackgroundDirty;

    private const float ArmValidationPhaseSeconds = 3f;
    private const float ArmValidationDuration = ArmValidationPhaseSeconds * 4f;
    private const float TrackingOverlayHeaderHeight = 36f;
    private const float TrackingOverlayMargin = 14f;
    private const float TrackingOverlayPanelGap = 8f;
    private const string LegacyArmValidationResultKey = "VAS.ArmValidation.LastResult";
    private const string AvatarArmValidationKeyRoot = "VAS.ArmValidation.Avatar.";
    private const string TrackingOverlayXKey = "VAS.UI.TrackingOverlay.X";
    private const string TrackingOverlayYKey = "VAS.UI.TrackingOverlay.Y";
    private const string TrackingOverlayCollapsedKey = "VAS.UI.TrackingOverlay.Collapsed";

    public void Initialize(WebcamManager webcamManager, TrackingPipeline trackingPipeline)
    {
        _webcamManager = webcamManager;
        _trackingPipeline = trackingPipeline;
        _vrmDropReceiver = GetComponent<WindowsVrmDropReceiver>();
        if (_vrmDropReceiver == null) _vrmDropReceiver = gameObject.AddComponent<WindowsVrmDropReceiver>();
        _vrmDropReceiver.Initialize(RegisterAvatarWithNoticeAsync);
        _observedCalibrationNoticeRevision = _trackingPipeline != null
            ? _trackingPipeline.CalibrationNoticeRevision
            : 0;
        _broadcastBackgroundColor = BroadcastBackgroundSettings.CurrentColor;
        BroadcastBackgroundSettings.ApplyTo(Camera.main);
        LoadTrackingOverlayPreferences();
        SyncAvatarValidationContext();
        RefreshOptions();
    }

    private void Update()
    {
        if (Keyboard.current?.f1Key.wasPressedThisFrame == true) _visible = !_visible;
        if (Keyboard.current?.cKey.wasPressedThisFrame == true) RequestCalibration();
        SyncAvatarValidationContext();
        SyncCalibrationNotice();
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
            DrawAvatarNoticeOverlay();
            DrawCalibrationProcessOverlay();
            DrawArmValidationOverlay();
            return;
        }

        DrawCalibrationGuide();

        float panelWidth = Mathf.Min(380f, Screen.width - 24f);
        float availablePanelHeight = Screen.height - 24f - GetTrackingOverlayReservedHeight();
        float panelHeight = Mathf.Min(680f, Mathf.Max(140f, availablePanelHeight));
        float innerWidth = Mathf.Max(180f, panelWidth - 32f);
        float innerHeight = Mathf.Max(140f, panelHeight - 24f);
        const float paletteWidth = 42f;
        const float paletteGap = 6f;
        float pageWidth = Mathf.Max(132f, innerWidth - paletteWidth - paletteGap);

        GUI.Box(new Rect(12f, 12f, panelWidth, panelHeight), GUIContent.none);
        GUILayout.BeginArea(new Rect(28f, 24f, pageWidth, innerHeight));
        GUILayout.Label("버추얼 아바타 스튜디오  (F1)", GUI.skin.box);
        string privacyStatus = _trackingPipeline == null
            ? "보호 상태 확인 중"
            : _trackingPipeline.IsRawCameraPreviewBlocked
                ? "원본 영상 출력 차단"
                : "원본 영상 차단 확인 중";
        GUILayout.Label(privacyStatus);
        DrawSelectedSettingsPage();
        GUILayout.EndArea();

        GUILayout.BeginArea(new Rect(28f + pageWidth + paletteGap, 24f, paletteWidth, innerHeight));
        DrawSettingsPalette();
        GUILayout.EndArea();

        DrawAvatarNoticeOverlay();
        DrawCalibrationProcessOverlay();
        DrawArmValidationOverlay();
    }

    private void DrawSelectedSettingsPage()
    {
        switch (_settingsPage)
        {
            case SettingsPage.Camera:
                GUILayout.Label("카메라", GUI.skin.box);
                _cameraPageScroll = GUILayout.BeginScrollView(_cameraPageScroll, false, false);
                DrawCameraSettings();
                GUILayout.EndScrollView();
                break;
            case SettingsPage.Avatar:
                GUILayout.Label("아바타", GUI.skin.box);
                _avatarPageScroll = GUILayout.BeginScrollView(_avatarPageScroll, false, false);
                DrawAvatarSettings();
                GUILayout.EndScrollView();
                break;
            case SettingsPage.ArmMapping:
                GUILayout.Label("팔 매핑", GUI.skin.box);
                _armPageScroll = GUILayout.BeginScrollView(_armPageScroll, false, false);
                DrawArmMappingConfig();
                GUILayout.EndScrollView();
                break;
            case SettingsPage.Debug:
                GUILayout.Label("디버그 / 테스트", GUI.skin.box);
                _debugPageScroll = GUILayout.BeginScrollView(_debugPageScroll, false, false);
                DrawDebugTools();
                GUILayout.EndScrollView();
                break;
        }
    }

    private void DrawCameraSettings()
    {
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
            if (GUILayout.Button(_changingCamera ? "처리 중..." : startLabel, GUILayout.Height(30f)))
                StartOrApplyCamera();

            GUI.enabled = !_changingCamera && !_webcamManager.IsChanging && _webcamManager.IsReady;
            if (GUILayout.Button("카메라 중지", GUILayout.Height(30f))) StopCamera();
            GUI.enabled = true;
        }

        if (GUILayout.Button("카메라 목록 새로고침")) RefreshOptions();

        if (!string.IsNullOrEmpty(_webcamManager.LastError))
            GUILayout.Label($"오류: {_webcamManager.LastError}");
        else if (_webcamManager.IsReady)
            GUILayout.Label($"사용 중: {_webcamManager.Texture.width} x {_webcamManager.Texture.height}");
        else
            GUILayout.Label("상태: 카메라 꺼짐");
    }

    private void DrawAvatarSettings()
    {
        DrawAvatarLibrary();
        if (_trackingPipeline == null || _trackingPipeline.IsAvatarBusy)
            GUILayout.Label(_trackingPipeline != null ? _trackingPipeline.AvatarStatus : "아바타 컨트롤러가 없습니다.");

        bool canRequestCalibration = _trackingPipeline != null && !_calibrationRequestRunning;
        GUI.enabled = canRequestCalibration;
        string calibrationLabel = _trackingPipeline != null
            ? _trackingPipeline.AvatarCalibrationButtonLabel
            : "중립 자세 캘리브레이션 (3초)";
        if (GUILayout.Button(calibrationLabel, GUILayout.Height(30f)) && canRequestCalibration)
            RequestCalibration();
        GUI.enabled = true;
        GUILayout.Label("단축키: C");

        DrawBroadcastBackgroundSettings();
    }

    private void DrawBroadcastBackgroundSettings()
    {
        GUILayout.Space(8f);
        GUILayout.Label("송출 배경색", GUI.skin.box);
        GUILayout.Label("OBS 크로마 키에 사용할 단색 배경입니다.");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("기본색")) SetBroadcastBackgroundColor(BroadcastBackgroundSettings.DefaultColor, true);
        if (GUILayout.Button("녹색")) SetBroadcastBackgroundColor(Color.green, true);
        if (GUILayout.Button("파란색")) SetBroadcastBackgroundColor(Color.blue, true);
        GUILayout.EndHorizontal();

        Rect previewRect = GUILayoutUtility.GetRect(0f, 26f, GUILayout.ExpandWidth(true));
        Color previousGuiColor = GUI.color;
        GUI.color = _broadcastBackgroundColor;
        GUI.DrawTexture(previewRect, Texture2D.whiteTexture);
        GUI.color = previousGuiColor;
        GUI.Label(previewRect, "#" + ColorUtility.ToHtmlStringRGB(_broadcastBackgroundColor));

        float red = DrawBackgroundColorChannel("R", _broadcastBackgroundColor.r);
        float green = DrawBackgroundColorChannel("G", _broadcastBackgroundColor.g);
        float blue = DrawBackgroundColorChannel("B", _broadcastBackgroundColor.b);
        if (!Mathf.Approximately(red, _broadcastBackgroundColor.r) ||
            !Mathf.Approximately(green, _broadcastBackgroundColor.g) ||
            !Mathf.Approximately(blue, _broadcastBackgroundColor.b))
        {
            SetBroadcastBackgroundColor(new Color(red, green, blue, 1f), false);
        }

        EventType eventType = Event.current.rawType;
        if (_broadcastBackgroundDirty && (eventType == EventType.MouseUp || eventType == EventType.KeyUp))
            SaveBroadcastBackgroundColor();
    }

    private static float DrawBackgroundColorChannel(string label, float value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(18f));
        float result = GUILayout.HorizontalSlider(value, 0f, 1f);
        GUILayout.Label(Mathf.RoundToInt(result * 255f).ToString(), GUILayout.Width(32f));
        GUILayout.EndHorizontal();
        return result;
    }

    private void SetBroadcastBackgroundColor(Color color, bool saveImmediately)
    {
        _broadcastBackgroundColor = new Color(
            Mathf.Clamp01(color.r),
            Mathf.Clamp01(color.g),
            Mathf.Clamp01(color.b),
            1f);
        BroadcastBackgroundSettings.SetCurrentColor(_broadcastBackgroundColor);
        BroadcastBackgroundSettings.ApplyTo(Camera.main);
        _broadcastBackgroundDirty = true;
        if (saveImmediately) SaveBroadcastBackgroundColor();
    }

    private void SaveBroadcastBackgroundColor()
    {
        if (!_broadcastBackgroundDirty) return;
        BroadcastBackgroundSettings.Save();
        _broadcastBackgroundDirty = false;
    }

    private void DrawSettingsPalette()
    {
        DrawPaletteButton(SettingsPage.Camera, "캠", "카메라 설정");
        DrawPaletteButton(SettingsPage.Avatar, "아", "아바타 설정");
        DrawPaletteButton(SettingsPage.ArmMapping, "팔", "팔 매핑 설정");
        DrawPaletteButton(SettingsPage.Debug, "테", "디버그 / 테스트");
        GUILayout.FlexibleSpace();
    }

    private void DrawPaletteButton(SettingsPage page, string label, string tooltip)
    {
        Color previousBackground = GUI.backgroundColor;
        if (_settingsPage == page) GUI.backgroundColor = new Color(0.2f, 0.9f, 1f);
        GUIStyle paletteButtonStyle = _paletteButtonStyle ?? GUI.skin.button;
        if (GUILayout.Button(
                new GUIContent(label, tooltip),
                paletteButtonStyle,
                GUILayout.Width(38f),
                GUILayout.Height(38f)))
        {
            _settingsPage = page;
        }

        GUI.backgroundColor = previousBackground;
    }

    private void DrawAvatarLibrary()
    {
        if (_trackingPipeline == null)
        {
            GUILayout.Label("아바타 컨트롤러가 없습니다.");
            return;
        }

        bool avatarBusy = _trackingPipeline.IsAvatarBusy;
        bool previousEnabled = GUI.enabled;
        GUIStyle actionButtonStyle = _avatarActionButtonStyle ?? GUI.skin.button;
        GUI.enabled = previousEnabled && !avatarBusy && !_avatarFilePickerOpen;
        string addLabel = _avatarFilePickerOpen
            ? "파일 선택 창 여는 중..."
            : avatarBusy
                ? "아바타 처리 중..."
                : "VRM 파일 추가";
        if (GUILayout.Button(addLabel, GUILayout.Height(32f))) OpenAvatarFilePicker();
        GUI.enabled = previousEnabled;

        if (!string.IsNullOrEmpty(_avatarInputMessage)) GUILayout.Label(_avatarInputMessage);
        if (_vrmDropReceiver != null &&
            WindowsVrmDropReceiver.IsSupported &&
            !string.IsNullOrEmpty(_vrmDropReceiver.LastError))
        {
            GUILayout.Label("외부 드롭 오류: " + _vrmDropReceiver.LastError);
        }

        System.Collections.Generic.IReadOnlyList<AvatarLibraryStore.AvatarEntry> entries =
            _trackingPipeline.RegisteredAvatars;
        if (entries.Count == 0)
        {
            _pendingAvatarRemovalId = string.Empty;
            GUILayout.Label("등록된 아바타가 없습니다.");
            return;
        }

        bool hasPendingRemoval = !string.IsNullOrEmpty(_pendingAvatarRemovalId);
        float listHeight = Mathf.Clamp(entries.Count * 76f + (hasPendingRemoval ? 44f : 0f), 76f, 220f);
        _avatarListScroll = GUILayout.BeginScrollView(_avatarListScroll, GUILayout.Height(listHeight));
        bool pendingEntryStillExists = false;
        string removalRequestId = string.Empty;
        foreach (AvatarLibraryStore.AvatarEntry entry in entries)
        {
            if (entry == null) continue;
            bool current = string.Equals(
                entry.Id,
                _trackingPipeline.CurrentAvatarId,
                System.StringComparison.OrdinalIgnoreCase);
            bool confirmingRemoval = string.Equals(
                entry.Id,
                _pendingAvatarRemovalId,
                System.StringComparison.OrdinalIgnoreCase);
            if (confirmingRemoval) pendingEntryStillExists = true;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(current ? entry.DisplayName + "  [사용 중]" : entry.DisplayName);
            if (confirmingRemoval)
            {
                GUILayout.Label("앱 등록과 캐시만 제거합니다. 원본 VRM은 유지됩니다.");
                Rect buttonRow = GUILayoutUtility.GetRect(0f, 28f, GUILayout.ExpandWidth(true));
                const float buttonGap = 6f;
                float buttonWidth = Mathf.Max(0f, (buttonRow.width - buttonGap) * 0.5f);
                Rect leftButtonRect = new(buttonRow.x, buttonRow.y, buttonWidth, buttonRow.height);
                Rect rightButtonRect = new(buttonRow.x + buttonWidth + buttonGap, buttonRow.y, buttonWidth, buttonRow.height);
                GUI.enabled = previousEnabled && !avatarBusy;
                if (GUI.Button(leftButtonRect, "제거 확인", actionButtonStyle))
                {
                    removalRequestId = entry.Id;
                }
                GUI.enabled = previousEnabled;
                if (GUI.Button(rightButtonRect, "취소", actionButtonStyle))
                {
                    _pendingAvatarRemovalId = string.Empty;
                }
            }
            else
            {
                Rect buttonRow = GUILayoutUtility.GetRect(0f, 28f, GUILayout.ExpandWidth(true));
                const float buttonGap = 6f;
                float buttonWidth = Mathf.Max(0f, (buttonRow.width - buttonGap) * 0.5f);
                Rect leftButtonRect = new(buttonRow.x, buttonRow.y, buttonWidth, buttonRow.height);
                Rect rightButtonRect = new(buttonRow.x + buttonWidth + buttonGap, buttonRow.y, buttonWidth, buttonRow.height);
                GUI.enabled = previousEnabled && !avatarBusy && !current;
                if (GUI.Button(leftButtonRect, current ? "사용 중" : "이 아바타 사용", actionButtonStyle))
                {
                    ActivateAvatar(entry.Id);
                }
                GUI.enabled = previousEnabled && !avatarBusy;
                if (GUI.Button(rightButtonRect, "등록 제거", actionButtonStyle))
                {
                    _pendingAvatarRemovalId = entry.Id;
                    pendingEntryStillExists = true;
                }
                GUI.enabled = previousEnabled;
            }
            GUILayout.EndVertical();
        }
        GUILayout.EndScrollView();

        if (!pendingEntryStillExists) _pendingAvatarRemovalId = string.Empty;
        if (!string.IsNullOrEmpty(removalRequestId)) RemoveAvatar(removalRequestId);
        GUI.enabled = previousEnabled;
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
        GUILayout.Label("추적 상태 / 성능", GUI.skin.box);
        if (_trackingPipeline == null || string.IsNullOrEmpty(_trackingPipeline.TrackingStatusText))
        {
            GUILayout.Label("추적 정보를 기다리는 중입니다.");
        }
        else
        {
            _debugTrackingStatusStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                wordWrap = true
            };
            _debugTrackingStatusStyle.font = KoreanUiFontProvider.GuiFont;
            _debugTrackingStatusStyle.normal.textColor = Color.white;
            GUILayout.Label(_trackingPipeline.TrackingStatusText, _debugTrackingStatusStyle);
        }

        GUILayout.Space(8f);
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
            : LoadArmValidationResult();
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

        string resultKey = ArmValidationResultKey(_validationAvatarId);
        if (!string.IsNullOrEmpty(resultKey))
        {
            PlayerPrefs.SetString(resultKey, _armValidationResult);
            PlayerPrefs.Save();
        }
    }

    private void CancelArmValidation(string message = "팔 동작 검증을 취소했습니다.")
    {
        _armValidationRunning = false;
        _armValidationResult = message;
        _validationOverlayMessage = message;
        _validationOverlayEndTime = Time.unscaledTime + 3f;
    }

    private void SyncAvatarValidationContext()
    {
        if (_trackingPipeline == null) return;
        int revision = _trackingPipeline.AvatarRevision;
        if (revision == _observedAvatarRevision) return;

        if (_armValidationRunning)
            CancelArmValidation("아바타가 변경되어 팔 동작 검증을 취소했습니다.");

        _observedAvatarRevision = revision;
        _validationAvatarId = _trackingPipeline.CurrentAvatarId ?? string.Empty;
        _armValidationResult = string.Empty;

        string avatarKey = ArmValidationResultKey(_validationAvatarId);
        if (!string.IsNullOrEmpty(avatarKey) &&
            !PlayerPrefs.HasKey(avatarKey) &&
            PlayerPrefs.HasKey(LegacyArmValidationResultKey))
        {
            PlayerPrefs.SetString(
                avatarKey,
                PlayerPrefs.GetString(LegacyArmValidationResultKey, string.Empty));
            PlayerPrefs.DeleteKey(LegacyArmValidationResultKey);
            PlayerPrefs.Save();
        }
    }

    private string LoadArmValidationResult()
    {
        string resultKey = ArmValidationResultKey(_validationAvatarId);
        return string.IsNullOrEmpty(resultKey)
            ? "아바타를 먼저 불러오세요."
            : PlayerPrefs.GetString(resultKey, "아직 검증 결과가 없습니다.");
    }

    private static string ArmValidationResultKey(string avatarId)
    {
        return string.IsNullOrEmpty(avatarId)
            ? string.Empty
            : AvatarArmValidationKeyRoot + avatarId.ToLowerInvariant() + ".LastResult";
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

        _avatarActionButtonStyle ??= new GUIStyle(_runtimeGuiSkin.button)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            stretchWidth = true,
            wordWrap = false
        };
        _paletteButtonStyle ??= new GUIStyle(_runtimeGuiSkin.button)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            stretchWidth = false,
            wordWrap = false
        };
        _avatarNoticeStyle ??= new GUIStyle(_runtimeGuiSkin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };
        _avatarNoticeStyle.normal.textColor = Color.white;

        GUI.skin = _runtimeGuiSkin;
    }

    private void OnDisable()
    {
        SaveBroadcastBackgroundColor();

        if (_runtimeGuiSkin != null)
        {
            DestroyImmediate(_runtimeGuiSkin);
            _runtimeGuiSkin = null;
        }

        _validationOverlayStyle = null;
        _validationResultStyle = null;
        _trackingStatusStyle = null;
        _trackingOverlayHeaderStyle = null;
        _trackingOverlayToggleStyle = null;
        _debugTrackingStatusStyle = null;
        _avatarActionButtonStyle = null;
        _paletteButtonStyle = null;
        _avatarNoticeStyle = null;
        _calibrationTitleStyle = null;
        _calibrationDetailStyle = null;
    }

    private void DrawTrackingStatusOverlay()
    {
        if (_trackingPipeline == null || string.IsNullOrEmpty(_trackingPipeline.TrackingStatusText)) return;

        LoadTrackingOverlayPreferences();
        string status = _trackingPipeline.TrackingStatusText;
        float fontSize = Mathf.Clamp(Screen.height * 0.026f, 17f, 24f);
        float width = Mathf.Min(620f, Mathf.Max(1f, Screen.width - TrackingOverlayMargin * 2f));

        _trackingStatusStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperLeft,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };
        _trackingStatusStyle.font = KoreanUiFontProvider.GuiFont;
        _trackingStatusStyle.fontSize = Mathf.RoundToInt(fontSize);
        _trackingStatusStyle.normal.textColor = Color.white;

        float height = TrackingOverlayHeaderHeight;
        if (!_trackingOverlayCollapsed)
        {
            _trackingStatusContent.text = status;
            float contentWidth = width - 20f;
            float contentHeight = _trackingStatusStyle.CalcHeight(_trackingStatusContent, contentWidth);
            height += contentHeight + 18f;
        }

        UpdateTrackingOverlayRect(width, height);
        HandleTrackingOverlayDragging();
        Rect backgroundRect = _trackingOverlayRect;

        int previousDepth = GUI.depth;
        Color previousColor = GUI.color;
        GUI.depth = -850;
        GUI.color = new Color(0.02f, 0.03f, 0.06f, 0.78f);
        GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
        GUI.color = new Color(0.04f, 0.12f, 0.16f, 0.96f);
        GUI.DrawTexture(
            new Rect(backgroundRect.x, backgroundRect.y, backgroundRect.width, TrackingOverlayHeaderHeight),
            Texture2D.whiteTexture);
        GUI.color = previousColor;

        _trackingOverlayHeaderStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold,
            fontSize = 14,
            clipping = TextClipping.Clip,
            wordWrap = false
        };
        _trackingOverlayHeaderStyle.font = KoreanUiFontProvider.GuiFont;
        _trackingOverlayHeaderStyle.normal.textColor = Color.white;

        _trackingOverlayToggleStyle ??= new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            wordWrap = false
        };
        _trackingOverlayToggleStyle.font = KoreanUiFontProvider.GuiFont;

        const float toggleWidth = 58f;
        Rect toggleRect = new(
            backgroundRect.xMax - toggleWidth - 5f,
            backgroundRect.y + 4f,
            toggleWidth,
            TrackingOverlayHeaderHeight - 8f);
        Rect headerLabelRect = new(
            backgroundRect.x + 10f,
            backgroundRect.y,
            backgroundRect.width - toggleWidth - 20f,
            TrackingOverlayHeaderHeight);
        GUI.Label(
            headerLabelRect,
            _trackingOverlayCollapsed ? GetTrackingSummary(status) : "추적 상태 / 성능  ·  제목을 드래그해 이동",
            _trackingOverlayHeaderStyle);

        if (GUI.Button(
                toggleRect,
                _trackingOverlayCollapsed ? "펼치기" : "접기",
                _trackingOverlayToggleStyle))
        {
            _trackingOverlayCollapsed = !_trackingOverlayCollapsed;
            PlayerPrefs.SetInt(TrackingOverlayCollapsedKey, _trackingOverlayCollapsed ? 1 : 0);
            PlayerPrefs.Save();
        }

        if (!_trackingOverlayCollapsed)
        {
            GUI.Label(
                new Rect(
                    backgroundRect.x + 10f,
                    backgroundRect.y + TrackingOverlayHeaderHeight + 7f,
                    backgroundRect.width - 20f,
                    backgroundRect.height - TrackingOverlayHeaderHeight - 14f),
                _trackingStatusContent,
                _trackingStatusStyle);
        }

        GUI.color = previousColor;
        GUI.depth = previousDepth;
    }

    private float GetTrackingOverlayReservedHeight()
    {
        LoadTrackingOverlayPreferences();
        bool hasStatus = _trackingPipeline != null &&
                         !string.IsNullOrEmpty(_trackingPipeline.TrackingStatusText);
        return hasStatus && _trackingOverlayCollapsed
            ? TrackingOverlayHeaderHeight + TrackingOverlayPanelGap
            : 0f;
    }

    private void LoadTrackingOverlayPreferences()
    {
        if (_trackingOverlayPreferencesLoaded) return;

        _trackingOverlayPreferencesLoaded = true;
        _trackingOverlayCollapsed = PlayerPrefs.GetInt(TrackingOverlayCollapsedKey, 1) != 0;
        _trackingOverlayNormalizedX = Mathf.Clamp01(PlayerPrefs.GetFloat(TrackingOverlayXKey, 0f));
        _trackingOverlayNormalizedY = Mathf.Clamp01(PlayerPrefs.GetFloat(TrackingOverlayYKey, 1f));
    }

    private void UpdateTrackingOverlayRect(float width, float height)
    {
        float maxX = Mathf.Max(0f, Screen.width - width);
        float maxY = Mathf.Max(0f, Screen.height - height);
        if (_trackingOverlayRect.width <= 0f || _trackingOverlayRect.height <= 0f)
        {
            _trackingOverlayRect = new Rect(
                _trackingOverlayNormalizedX * maxX,
                _trackingOverlayNormalizedY * maxY,
                width,
                height);
        }
        else
        {
            _trackingOverlayRect.width = width;
            _trackingOverlayRect.height = height;
        }

        _trackingOverlayRect.x = Mathf.Clamp(_trackingOverlayRect.x, 0f, maxX);
        _trackingOverlayRect.y = Mathf.Clamp(_trackingOverlayRect.y, 0f, maxY);
    }

    private void HandleTrackingOverlayDragging()
    {
        Event currentEvent = Event.current;
        if (currentEvent == null) return;

        const float toggleWidth = 58f;
        Rect dragRect = new(
            _trackingOverlayRect.x,
            _trackingOverlayRect.y,
            Mathf.Max(0f, _trackingOverlayRect.width - toggleWidth - 10f),
            TrackingOverlayHeaderHeight);

        if (currentEvent.type == EventType.MouseDown &&
            currentEvent.button == 0 &&
            dragRect.Contains(currentEvent.mousePosition))
        {
            _trackingOverlayDragging = true;
            _trackingOverlayDragOffset = currentEvent.mousePosition - _trackingOverlayRect.position;
            currentEvent.Use();
        }
        else if (currentEvent.type == EventType.MouseDrag && _trackingOverlayDragging)
        {
            Vector2 target = currentEvent.mousePosition - _trackingOverlayDragOffset;
            _trackingOverlayRect.position = ClampTrackingOverlayPosition(target);
            currentEvent.Use();
        }
        else if (currentEvent.type == EventType.MouseUp && _trackingOverlayDragging)
        {
            _trackingOverlayDragging = false;
            SaveTrackingOverlayPosition();
            currentEvent.Use();
        }
    }

    private Vector2 ClampTrackingOverlayPosition(Vector2 position)
    {
        float maxX = Mathf.Max(0f, Screen.width - _trackingOverlayRect.width);
        float maxY = Mathf.Max(0f, Screen.height - _trackingOverlayRect.height);
        return new Vector2(
            Mathf.Clamp(position.x, 0f, maxX),
            Mathf.Clamp(position.y, 0f, maxY));
    }

    private void SaveTrackingOverlayPosition()
    {
        float maxX = Mathf.Max(0f, Screen.width - _trackingOverlayRect.width);
        float maxY = Mathf.Max(0f, Screen.height - _trackingOverlayRect.height);
        _trackingOverlayNormalizedX = maxX > 0f ? _trackingOverlayRect.x / maxX : 0f;
        _trackingOverlayNormalizedY = maxY > 0f ? _trackingOverlayRect.y / maxY : 0f;
        PlayerPrefs.SetFloat(TrackingOverlayXKey, _trackingOverlayNormalizedX);
        PlayerPrefs.SetFloat(TrackingOverlayYKey, _trackingOverlayNormalizedY);
        PlayerPrefs.Save();
    }

    private string GetTrackingSummary(string status)
    {
        if (string.Equals(_trackingSummarySource, status, System.StringComparison.Ordinal))
            return _trackingSummary;

        _trackingSummarySource = status;
        string face = GetTrackingStatusValue(status, "얼굴");
        string pose = GetTrackingStatusValue(status, "포즈");
        string inferenceFps = GetTrackingAverageFps(status, "추론 갱신");
        string poseFps = GetTrackingAverageFps(status, "포즈 적용");
        _trackingSummary =
            $"얼굴 {face} · 포즈 {pose} · 추론 {inferenceFps} · 적용 {poseFps}";
        return _trackingSummary;
    }

    private static string GetTrackingStatusValue(string status, string label)
    {
        string marker = "[" + label + "] ";
        int start = status.IndexOf(marker, System.StringComparison.Ordinal);
        if (start < 0) return "측정 중";

        start += marker.Length;
        int end = status.IndexOf('\n', start);
        if (end < 0) end = status.Length;
        return status.Substring(start, end - start).TrimEnd('\r');
    }

    private static string GetTrackingAverageFps(string status, string label)
    {
        string marker = "[" + label + "]";
        int lineStart = status.IndexOf(marker, System.StringComparison.Ordinal);
        if (lineStart < 0) return "측정 중";

        const string averageMarker = "/ 평균 ";
        int valueStart = status.IndexOf(averageMarker, lineStart, System.StringComparison.Ordinal);
        if (valueStart < 0) return "측정 중";

        valueStart += averageMarker.Length;
        int fpsEnd = status.IndexOf(" FPS", valueStart, System.StringComparison.Ordinal);
        if (fpsEnd < 0) return "측정 중";
        return status.Substring(valueStart, fpsEnd - valueStart) + " FPS";
    }

    private void DrawAvatarNoticeOverlay()
    {
        if (string.IsNullOrEmpty(_avatarNoticeMessage) || Time.unscaledTime >= _avatarNoticeEndTime) return;

        const float fadeSeconds = 1.5f;
        float remaining = _avatarNoticeEndTime - Time.unscaledTime;
        float alpha = Mathf.Clamp01(remaining / fadeSeconds);
        float width = Mathf.Min(620f, Screen.width - 32f);
        Rect backgroundRect = new((Screen.width - width) * 0.5f, 24f, width, 74f);

        int previousDepth = GUI.depth;
        Color previousColor = GUI.color;
        GUI.depth = -900;
        Color noticeColor = _avatarNoticeIsError
            ? new Color(0.55f, 0.08f, 0.08f, 0.94f * alpha)
            : new Color(0.02f, 0.3f, 0.36f, 0.94f * alpha);
        GUI.color = noticeColor;
        GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.Label(
            new Rect(backgroundRect.x + 14f, backgroundRect.y + 8f, backgroundRect.width - 28f, backgroundRect.height - 16f),
            _avatarNoticeMessage,
            _avatarNoticeStyle ?? GUI.skin.label);
        GUI.color = previousColor;
        GUI.depth = previousDepth;
    }

    private void ShowAvatarNotice(string message, bool isError)
    {
        _avatarNoticeMessage = string.IsNullOrWhiteSpace(message)
            ? (isError ? "아바타 작업에 실패했습니다." : "아바타 작업을 완료했습니다.")
            : message;
        _avatarNoticeIsError = isError;
        _avatarNoticeEndTime = Time.unscaledTime + 5f;
    }

    private void DrawCalibrationProcessOverlay()
    {
        if (_trackingPipeline == null) return;
        bool preparing = _trackingPipeline.IsPreparingCalibration;
        bool counting = _trackingPipeline.IsCalibratingAvatar;
        bool interrupted = _trackingPipeline.IsCalibrationTrackingInterrupted;
        if (!preparing && !counting) return;

        string title = interrupted
            ? $"인식 범위 이탈  {_trackingPipeline.CalibrationTrackingRecoveryCountdown}"
            : counting
            ? $"캘리브레이션  {_trackingPipeline.AvatarCalibrationCountdown}"
            : "캘리브레이션 준비";
        string detail = interrupted
            ? "얼굴과 양쪽 어깨·팔꿈치가 보이도록 위치를 조정하세요"
            : counting
            ? "편안한 중립 자세를 유지하세요"
            : _trackingPipeline.CalibrationPreparationMessage;
        float progress = interrupted
            ? Mathf.Clamp01(_trackingPipeline.CalibrationTrackingRecoveryCountdown / 5f)
            : counting
            ? Mathf.Clamp01((_trackingPipeline.AvatarCalibrationCountdown - 1f) / 3f)
            : 1f;

        float width = Mathf.Min(560f, Screen.width - 32f);
        Rect box = new((Screen.width - width) * 0.5f, Mathf.Max(110f, Screen.height * 0.18f), width, 150f);
        int previousDepth = GUI.depth;
        Color previousColor = GUI.color;
        GUI.depth = -950;
        GUI.color = interrupted
            ? new Color(0.36f, 0.035f, 0.035f, 0.96f)
            : new Color(0.015f, 0.035f, 0.055f, 0.94f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = Color.white;

        _calibrationTitleStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold
        };
        _calibrationTitleStyle.fontSize = counting ? 40 : 28;
        _calibrationDetailStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            wordWrap = true
        };
        GUI.Label(new Rect(box.x + 16f, box.y + 12f, box.width - 32f, 58f), title, _calibrationTitleStyle);
        GUI.Label(new Rect(box.x + 16f, box.y + 70f, box.width - 32f, 38f), detail, _calibrationDetailStyle);
        Rect track = new(box.x + 28f, box.yMax - 25f, box.width - 56f, 8f);
        GUI.color = new Color(1f, 1f, 1f, 0.2f);
        GUI.DrawTexture(track, Texture2D.whiteTexture);
        GUI.color = interrupted
            ? new Color(1f, 0.48f, 0.18f, 1f)
            : new Color(0.15f, 0.9f, 1f, 1f);
        GUI.DrawTexture(new Rect(track.x, track.y, track.width * progress, track.height), Texture2D.whiteTexture);
        GUI.color = previousColor;
        GUI.depth = previousDepth;
    }

    private async void RequestCalibration()
    {
        if (_trackingPipeline == null || _calibrationRequestRunning) return;
        _calibrationRequestRunning = true;
        try
        {
            string error = await _trackingPipeline.RequestAvatarCalibrationAsync();
            if (this != null && !string.IsNullOrEmpty(error)) ShowAvatarNotice(error, isError: true);
        }
        catch (System.Exception exception)
        {
            if (this != null)
            {
                ShowAvatarNotice("캘리브레이션 준비 오류: " + exception.Message, isError: true);
                Debug.LogException(exception, this);
            }
        }
        finally
        {
            if (this != null) _calibrationRequestRunning = false;
        }
    }

    private void SyncCalibrationNotice()
    {
        if (_trackingPipeline == null) return;
        int revision = _trackingPipeline.CalibrationNoticeRevision;
        if (revision == _observedCalibrationNoticeRevision) return;

        _observedCalibrationNoticeRevision = revision;
        if (revision > 0)
        {
            ShowAvatarNotice(
                _trackingPipeline.CalibrationNoticeMessage,
                _trackingPipeline.CalibrationNoticeIsError);
        }
    }

    private async System.Threading.Tasks.Task<bool> RegisterAvatarWithNoticeAsync(string path)
    {
        if (_trackingPipeline == null) return false;
        bool succeeded = await _trackingPipeline.RegisterAndActivateAvatarAsync(path);
        if (this != null) ShowAvatarNotice(_trackingPipeline.AvatarStatus, !succeeded);
        return succeeded;
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

    private async void OpenAvatarFilePicker()
    {
        if (_trackingPipeline == null || _avatarFilePickerOpen || _trackingPipeline.IsAvatarBusy) return;

        _avatarFilePickerOpen = true;
        _avatarInputMessage = string.Empty;
        try
        {
            VrmFilePickerResult result = await WindowsVrmFilePicker.OpenAsync();
            if (this == null || _trackingPipeline == null) return;

            if (result.IsSelected)
            {
                await RegisterAvatarWithNoticeAsync(result.Path);
            }
            else if (result.Status != VrmFilePickerStatus.Cancelled)
            {
                _avatarInputMessage = string.IsNullOrEmpty(result.ErrorMessage)
                    ? "VRM 파일 선택을 시작하지 못했습니다."
                    : result.ErrorMessage;
            }
        }
        catch (System.Exception exception)
        {
            if (this != null)
            {
                _avatarInputMessage = "VRM 파일 선택 오류: " + exception.Message;
                Debug.LogException(exception, this);
            }
        }
        finally
        {
            if (this != null) _avatarFilePickerOpen = false;
        }
    }

    private async void ActivateAvatar(string avatarId)
    {
        if (_trackingPipeline == null || _trackingPipeline.IsAvatarBusy) return;

        _avatarInputMessage = string.Empty;
        try
        {
            await _trackingPipeline.ActivateAvatarAsync(avatarId);
        }
        catch (System.Exception exception)
        {
            if (this != null)
            {
                _avatarInputMessage = "아바타 전환 오류: " + exception.Message;
                Debug.LogException(exception, this);
            }
        }
    }

    private async void RemoveAvatar(string avatarId)
    {
        if (_trackingPipeline == null || _trackingPipeline.IsAvatarBusy) return;

        _avatarInputMessage = string.Empty;
        try
        {
            bool removed = await _trackingPipeline.RemoveAvatarAsync(avatarId);
            if (this != null)
            {
                if (removed) _pendingAvatarRemovalId = string.Empty;
                ShowAvatarNotice(_trackingPipeline.AvatarStatus, !removed);
            }
        }
        catch (System.Exception exception)
        {
            if (this != null)
            {
                _avatarInputMessage = "아바타 제거 오류: " + exception.Message;
                ShowAvatarNotice(_avatarInputMessage, isError: true);
                Debug.LogException(exception, this);
            }
        }
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
