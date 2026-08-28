// TrackingPipeline.cs
// Main controller for all tracking components
using UnityEngine;
using System.Collections.Generic;

public class TrackingPipeline : MonoBehaviour
{
    private const float CalibrationTrackingWaitSeconds = 15f;
    public struct ArmValidationSample
    {
        public bool PoseTracked;
        public bool Calibrated;
        public float LeftInput;
        public float RightInput;
        public float LeftOutput;
        public float RightOutput;
        public int RejectedInputCount;
    }

    [SerializeField] private UnityEngine.UI.RawImage displayImage;
    [SerializeField] private TMPro.TMP_Text          debugText;
    [SerializeField] private BlazeFaceDetector faceDetector;
    [SerializeField] private BlazeHandDetector handDetector;
    [SerializeField] private HandLandmarkRunner handLandmarker;
    [SerializeField] private BlazePoseDetector poseDetector;
    [SerializeField] private BlazePoseLandmarkRunner poseLandmarker;
    [SerializeField] private ServerStreamSender serverSender;
    [SerializeField] private WebcamManager webcamManager;
    [SerializeField] private LiveAvatarController avatarController;

    [Header("선택 기능")]
    [SerializeField] private bool enableHandTracking;
    [SerializeField] private bool enableNetworkOutput;

    private WebCamTexture _cameraTexture;
    private TrackingPacket _currentPacket;
    private bool _pauseInference;
    private bool _inferenceBusy;
    private bool _shuttingDown;
    private bool _calibrationPoseReady;
    private string _trackingStatusText = "카메라가 꺼져 있습니다. 카메라 시작을 누르세요.";

    private const float StatusRefreshInterval = 0.25f;
    private const float RenderAverageAlpha = 0.02f;
    private const float InferenceAverageAlpha = 0.05f;
    private const int RenderWarmupSamples = 30;
    private const int InferenceWarmupSamples = 5;
    private static readonly Color PrivacyPreviewColor = new(0.018f, 0.03f, 0.05f, 1f);
    private static readonly Color PoseSkeletonColor = new(0.1f, 0.95f, 1f, 1f);
    private const float PoseSkeletonLineWidth = 5f;

    private readonly System.Text.StringBuilder _statusBuilder = new(192);
    private float _renderInstantMs;
    private float _renderAverageMs;
    private float _inferenceInstantMs;
    private float _inferenceAverageMs;
    private float _inferenceUpdateInstantMs;
    private float _inferenceUpdateAverageMs;
    private float _nextStatusRefreshTime;
    private double _lastInferenceCompletionTime;
    private int _renderWarmupRemaining = RenderWarmupSamples;
    private int _inferenceWarmupRemaining = InferenceWarmupSamples;
    private int _inferenceUpdateWarmupRemaining = InferenceWarmupSamples;
    private bool _renderAverageReady;
    private bool _inferenceAverageReady;
    private bool _inferenceUpdateAverageReady;
    private bool _performanceSessionActive;
    private bool _performanceSummaryLogged;
    private UpdateRateMetric _faceDetectorRate;
    private UpdateRateMetric _poseDetectorRate;
    private UpdateRateMetric _poseLandmarkerRate;
    private UpdateRateMetric _poseApplicationRate;

    private struct UpdateRateMetric
    {
        public float InstantIntervalMs;
        public float AverageIntervalMs;
        public double LastCompletionTime;
        public int WarmupRemaining;
        public bool AverageReady;
    }

    // Tracking points storage for persistent rendering
    private Vector2[] _facePoints = new Vector2[6];
    private float _faceTime = -1f;
    private Vector2[] _lHandPoints = new Vector2[21];
    private float _lHandTime = -1f;
    private Vector2[] _rHandPoints = new Vector2[21];
    private float _rHandTime = -1f;
    private Vector2[] _posePoints = new Vector2[33];
    private float _poseTime = -1f;

    // EMA Filters - Increased alpha for better responsiveness
    private EMAFilter _headFilter    = new EMAFilter(0.4f);
    private EMAFilter _lFingerFilter = new EMAFilter(0.3f);
    private EMAFilter _rFingerFilter = new EMAFilter(0.3f);

    private Vector2 _leftArmDirection;
    private Vector2 _rightArmDirection;
    private Vector2 _lastLeftArmRawDirection;
    private Vector2 _lastRightArmRawDirection;
    private bool _leftArmDirectionInitialized;
    private bool _rightArmDirectionInitialized;
    private int _leftArmRejectedFrames;
    private int _rightArmRejectedFrames;
    private int _armInputRejectedCount;

    void Start()
    {
        try 
        {
            ValidateComponents();
            
            // Initialize packet and array
            _currentPacket = new TrackingPacket {
                BlendShapeValues = new float[52],
                LeftFingerCurls  = new float[5],
                RightFingerCurls = new float[5]
            };
            
            InitializeWithoutStarting();
        }
        catch (System.Exception e)
        {
            Debug.LogError("[TrackingPipeline] Start Error: " + e);
            SetTrackingStatus("시작 오류: " + e.Message);
        }
    }

    private void InitializeWithoutStarting()
    {
        try
        {
            bool found = webcamManager.Initialize();
            GetComponent<WebcamControlPanel>()?.RefreshOptions();
            SetTrackingStatus(found
                ? "카메라가 꺼져 있습니다. 카메라 시작을 누르세요."
                : $"웹캠 오류: {webcamManager.LastError}");
            _ = UpdateLoop();
        }
        catch (System.Exception e)
        {
            Debug.LogError("[TrackingPipeline] Camera init error: " + e);
            SetTrackingStatus("카메라 오류: " + e.Message);
        }
    }

    private void SetupLayout()
    {
        if (displayImage == null || _cameraTexture == null) return;
        if (_cameraTexture.width <= 16) return;

        // Privacy invariant: the webcam texture is inference input only.
        // The visible preview is always an opaque surface behind tracking landmarks.
        displayImage.texture = Texture2D.whiteTexture;
        displayImage.color = PrivacyPreviewColor;
        displayImage.enabled = true;
        RectTransform rt = displayImage.rectTransform;

        var fitter = displayImage.GetComponent<UnityEngine.UI.AspectRatioFitter>();
        if (fitter != null) fitter.enabled = false;

        float cw       = _cameraTexture.width;
        float ch       = _cameraTexture.height;
        float rotation = _cameraTexture.videoRotationAngle;
        bool  isPortrait = (int)rotation % 180 != 0;

        const float previewLongEdge = 420f;
        float sourceAspect = cw / ch;
        Vector2 sourceSize = sourceAspect >= 1f
            ? new Vector2(previewLongEdge, previewLongEdge / sourceAspect)
            : new Vector2(previewLongEdge * sourceAspect, previewLongEdge);
        Vector2 visualSize = isPortrait ? new Vector2(sourceSize.y, sourceSize.x) : sourceSize;

        rt.sizeDelta = sourceSize;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(-visualSize.x * 0.5f - 24f, -visualSize.y * 0.5f - 24f);
        rt.localEulerAngles = new Vector3(0, 0, -rotation);

        float scaleX = webcamManager != null && webcamManager.Mirror ? -1f : 1f;
        float scaleY = _cameraTexture.videoVerticallyMirrored ? -1f : 1f;
        
        rt.localScale = new Vector3(scaleX, scaleY, 1f);
    }

    private void EnforceRawCameraOutputBlock()
    {
        if (displayImage == null || _cameraTexture == null) return;
        if (displayImage.texture != _cameraTexture) return;

        displayImage.texture = Texture2D.whiteTexture;
        displayImage.color = PrivacyPreviewColor;
        Debug.LogError("[Privacy] 원본 웹캠의 화면 출력을 감지하여 즉시 차단했습니다.");
    }

    private string _faceStatus = "[얼굴] 꺼짐";
    private string _handStatus = "[손] 꺼짐";
    private string _poseStatus = "[포즈] 꺼짐";

    private Vector3[] _lastPoseJoints; // 포즈 관절 정보 저장용

    private async Awaitable UpdateLoop()
    {
        bool layoutDone = false;
        int frameCounter = 0;
        
        while (!_shuttingDown)
        {
            if (_pauseInference || _cameraTexture == null)
            {
                await Awaitable.NextFrameAsync();
                continue;
            }

            if (!_cameraTexture.isPlaying) { await Awaitable.NextFrameAsync(); continue; }
            if (debugText != null) { debugText.richText = true; debugText.parseCtrlCharacters = true; }

            if (!layoutDone)
            {
                if (_cameraTexture.width > 16) { SetupLayout(); layoutDone = true; }
                else { await Awaitable.NextFrameAsync(); continue; }
            }

            try 
            {
                _inferenceBusy = true;
                int inferenceStartFrame = Time.frameCount;
                float startTime = Time.realtimeSinceStartup;

                if (faceDetector == null || poseDetector == null || poseLandmarker == null)
                {
                    SetTrackingStatus("오류: 얼굴 또는 포즈 추적기가 없습니다!");
                    await Awaitable.NextFrameAsync();
                    continue;
                }

                // 1. Face (Every frame)
                var face = await faceDetector.DetectAsync(_cameraTexture);
                if (_shuttingDown) return;
                RecordUpdateRate(ref _faceDetectorRate);
                UpdateFaceState(face);

                // 2. Hand (Every frame)
                // 포즈 힌트가 있으면 ROI 모드, 없으면 전체 화면 모드
                BlazeHandDetector.HandDetection[] hands = null;
                if (enableHandTracking && handDetector != null && handLandmarker != null &&
                    _lastPoseJoints != null && _lastPoseJoints.Length >= 17)
                {
                    // 손목(15, 16) 주변 ROI 탐색
                    var lWrist = new Unity.Mathematics.float2(_lastPoseJoints[15].x, _lastPoseJoints[15].y);
                    var rWrist = new Unity.Mathematics.float2(_lastPoseJoints[16].x, _lastPoseJoints[16].y);
                    float roiSize = Mathf.Max(_cameraTexture.width, _cameraTexture.height) * 0.35f;

                    var hL = await handDetector.DetectROIAsync(_cameraTexture, lWrist, roiSize);
                    var hR = await handDetector.DetectROIAsync(_cameraTexture, rWrist, roiSize);
                    
                    var list = new List<BlazeHandDetector.HandDetection>();
                    if (hL != null) list.AddRange(hL);
                    if (hR != null) list.AddRange(hR);
                    if (list.Count > 0) hands = list.ToArray();
                }
                
                // ROI 실패 시 또는 포즈 없을 시 전체 화면 스캔
                if (enableHandTracking && handDetector != null && handLandmarker != null)
                {
                    if (hands == null) hands = await handDetector.DetectMultiAsync(_cameraTexture);
                    await UpdateHandState(hands);
                }

                // 3. Pose (Every 2nd frame)
                if (frameCounter % 2 == 0)
                {
                    var pose = await poseDetector.DetectAsync(_cameraTexture);
                    if (_shuttingDown) return;
                    RecordUpdateRate(ref _poseDetectorRate);
                    await UpdatePoseState(pose);
                    if (_shuttingDown) return;
                }
                frameCounter++;

                // Rendering & Network
                RenderAllPoints();
                if (avatarController != null) avatarController.Apply(_currentPacket, _calibrationPoseReady);
                if (enableNetworkOutput && serverSender != null)
                {
                    _currentPacket.Timestamp = Time.time;
                    _ = serverSender.SendAsync(_currentPacket); 
                }

                float duration = (Time.realtimeSinceStartup - startTime) * 1000f;
                RecordInferenceTiming(duration);

                if (Time.frameCount == inferenceStartFrame)
                    await Awaitable.NextFrameAsync();
            }
            catch (System.Exception e)
            {
                if (!_shuttingDown && Application.isPlaying)
                {
                    Debug.LogException(e);
                    await Awaitable.NextFrameAsync();
                }
            }
            finally
            {
                _inferenceBusy = false;
            }
        }
    }

    private void RecordRenderTiming(float milliseconds)
    {
        if (milliseconds <= 0f || float.IsNaN(milliseconds) || float.IsInfinity(milliseconds)) return;

        _renderInstantMs = milliseconds;
        if (_renderWarmupRemaining > 0)
        {
            _renderWarmupRemaining--;
            if (_renderWarmupRemaining == 0)
            {
                _renderAverageMs = milliseconds;
                _renderAverageReady = true;
            }
            return;
        }

        _renderAverageMs += (milliseconds - _renderAverageMs) * RenderAverageAlpha;
    }

    private void RecordInferenceTiming(float milliseconds)
    {
        if (milliseconds <= 0f || float.IsNaN(milliseconds) || float.IsInfinity(milliseconds)) return;

        double completionTime = Time.realtimeSinceStartupAsDouble;
        if (_lastInferenceCompletionTime > 0d)
        {
            float updateIntervalMs = (float)((completionTime - _lastInferenceCompletionTime) * 1000d);
            RecordInferenceUpdateInterval(updateIntervalMs);
        }
        _lastInferenceCompletionTime = completionTime;

        _inferenceInstantMs = milliseconds;
        if (_inferenceWarmupRemaining > 0)
        {
            _inferenceWarmupRemaining--;
            if (_inferenceWarmupRemaining == 0)
            {
                _inferenceAverageMs = milliseconds;
                _inferenceAverageReady = true;
            }
            return;
        }

        _inferenceAverageMs += (milliseconds - _inferenceAverageMs) * InferenceAverageAlpha;
    }

    private void RecordInferenceUpdateInterval(float milliseconds)
    {
        if (milliseconds <= 0f || float.IsNaN(milliseconds) || float.IsInfinity(milliseconds)) return;

        _inferenceUpdateInstantMs = milliseconds;
        if (_inferenceUpdateWarmupRemaining > 0)
        {
            _inferenceUpdateWarmupRemaining--;
            if (_inferenceUpdateWarmupRemaining == 0)
            {
                _inferenceUpdateAverageMs = milliseconds;
                _inferenceUpdateAverageReady = true;
            }
            return;
        }

        _inferenceUpdateAverageMs +=
            (milliseconds - _inferenceUpdateAverageMs) * InferenceAverageAlpha;
    }

    private void RecordUpdateRate(ref UpdateRateMetric metric)
    {
        double completionTime = Time.realtimeSinceStartupAsDouble;
        if (metric.LastCompletionTime > 0d)
        {
            float intervalMs = (float)((completionTime - metric.LastCompletionTime) * 1000d);
            if (intervalMs > 0f && !float.IsNaN(intervalMs) && !float.IsInfinity(intervalMs))
            {
                metric.InstantIntervalMs = intervalMs;
                if (metric.WarmupRemaining > 0)
                {
                    metric.WarmupRemaining--;
                    if (metric.WarmupRemaining == 0)
                    {
                        metric.AverageIntervalMs = intervalMs;
                        metric.AverageReady = true;
                    }
                }
                else
                {
                    metric.AverageIntervalMs +=
                        (intervalMs - metric.AverageIntervalMs) * InferenceAverageAlpha;
                }
            }
        }

        metric.LastCompletionTime = completionTime;
    }

    private void RefreshTrackingStatus()
    {
        _statusBuilder.Clear();
        _statusBuilder.AppendLine(_faceStatus);
        _statusBuilder.AppendLine(_handStatus);
        _statusBuilder.AppendLine(_poseStatus);
        AppendFpsTimingLine("렌더", _renderInstantMs, _renderAverageMs, _renderAverageReady);
        AppendMillisecondsTimingLine("추론 처리", _inferenceInstantMs, _inferenceAverageMs, _inferenceAverageReady);
        AppendFpsTimingLine(
            "추론 갱신",
            _inferenceUpdateInstantMs,
            _inferenceUpdateAverageMs,
            _inferenceUpdateAverageReady);
        AppendUpdateRateLine("얼굴 검출", _faceDetectorRate);
        AppendUpdateRateLine("포즈 검출", _poseDetectorRate);
        AppendUpdateRateLine("포즈 랜드마크", _poseLandmarkerRate);
        AppendUpdateRateLine("포즈 적용", _poseApplicationRate);
        SetTrackingStatus(_statusBuilder.ToString());
    }

    private void AppendUpdateRateLine(string label, UpdateRateMetric metric)
    {
        AppendFpsTimingLine(
            label,
            metric.InstantIntervalMs,
            metric.AverageIntervalMs,
            metric.AverageReady);
    }

    private void AppendFpsTimingLine(string label, float instantMs, float averageMs, bool averageReady)
    {
        _statusBuilder.Append('[').Append(label).Append("] 순간 ");
        if (instantMs > 0f)
            AppendTimingValue(instantMs);
        else
            _statusBuilder.Append("측정 중");

        _statusBuilder.Append(" / 평균 ");

        if (averageReady)
            AppendTimingValue(averageMs);
        else
            _statusBuilder.Append("측정 중");

        _statusBuilder.AppendLine();
    }

    private void AppendMillisecondsTimingLine(
        string label,
        float instantMs,
        float averageMs,
        bool averageReady)
    {
        _statusBuilder.Append('[').Append(label).Append("] 순간 ");
        if (instantMs > 0f)
            AppendMillisecondsValue(instantMs);
        else
            _statusBuilder.Append("측정 중");

        _statusBuilder.Append(" / 평균 ");
        if (averageReady)
            AppendMillisecondsValue(averageMs);
        else
            _statusBuilder.Append("측정 중");

        _statusBuilder.AppendLine();
    }

    private void AppendTimingValue(float milliseconds)
    {
        float safeMilliseconds = Mathf.Max(0.01f, milliseconds);
        int tenths = Mathf.RoundToInt(milliseconds * 10f);
        _statusBuilder.Append(Mathf.RoundToInt(1000f / safeMilliseconds)).Append(" FPS (")
            .Append(tenths / 10).Append('.').Append(Mathf.Abs(tenths % 10)).Append(" ms)");
    }

    private void AppendMillisecondsValue(float milliseconds)
    {
        int tenths = Mathf.RoundToInt(milliseconds * 10f);
        _statusBuilder.Append(tenths / 10).Append('.')
            .Append(Mathf.Abs(tenths % 10)).Append(" ms");
    }

    private void ResetPerformanceStatistics()
    {
        _renderInstantMs = 0f;
        _renderAverageMs = 0f;
        _inferenceInstantMs = 0f;
        _inferenceAverageMs = 0f;
        _inferenceUpdateInstantMs = 0f;
        _inferenceUpdateAverageMs = 0f;
        _lastInferenceCompletionTime = 0d;
        _renderWarmupRemaining = RenderWarmupSamples;
        _inferenceWarmupRemaining = InferenceWarmupSamples;
        _inferenceUpdateWarmupRemaining = InferenceWarmupSamples;
        _renderAverageReady = false;
        _inferenceAverageReady = false;
        _inferenceUpdateAverageReady = false;
        ResetUpdateRate(ref _faceDetectorRate);
        ResetUpdateRate(ref _poseDetectorRate);
        ResetUpdateRate(ref _poseLandmarkerRate);
        ResetUpdateRate(ref _poseApplicationRate);
        _nextStatusRefreshTime = 0f;
        _performanceSessionActive = true;
        _performanceSummaryLogged = false;
    }

    private void LogPerformanceSummaryOnce(string reason)
    {
        if (!_performanceSessionActive || _performanceSummaryLogged) return;

        _performanceSummaryLogged = true;
        RefreshTrackingStatus();
        Debug.Log(
            $"[TrackingPerformance] 최종 성능 스냅샷 (화면 EMA), reason={reason}\n" +
            _trackingStatusText.TrimEnd());
        _performanceSessionActive = false;
    }

    private void ResetUpdateRate(ref UpdateRateMetric metric)
    {
        metric = new UpdateRateMetric
        {
            WarmupRemaining = InferenceWarmupSamples
        };
    }

    private void UpdateFaceState(BlazeFaceDetector.FaceDetection face)
    {
        if (face.IsValid) _currentPacket.IsTracking |= 1; else _currentPacket.IsTracking &= unchecked((byte)~1);
        _faceStatus = face.IsValid ? "[얼굴] 감지됨" : "[얼굴] 놓침";
        if (face.IsValid && face.RawBoxes != null)
        {
            var bs = FaceKeyPointBlendShape.Calculate(face.RawBoxes); 
            _currentPacket.BlendShapeValues[0] = bs.BlinkLeft;
            _currentPacket.BlendShapeValues[1] = bs.BlinkRight;
            UpdateHeadDataFromFace(face);
            UpdateFacePoints(face);
        }
    }

    private async Awaitable UpdateHandState(BlazeHandDetector.HandDetection[] hands)
    {
        if (hands != null && hands.Length > 0) _currentPacket.IsTracking |= 2; 
        else _currentPacket.IsTracking &= unchecked((byte)~2);

        _handStatus = (hands != null && hands.Length > 0) ? $"[손] {hands.Length}개 감지됨" : "[손] 놓침";
        
        if (hands != null)
        {
            foreach (var h in hands)
            {
                var joints = await handLandmarker.RunAsync(_cameraTexture, h);
                if (joints == null || joints.Length < 21) continue;

                float screenX = joints[0].x / _cameraTexture.width;
                bool isLeft = screenX > 0.5f; 
                
                float[] curls = HandMapper.CalculateCurls(joints);
                if (isLeft) {
                    _currentPacket.LeftFingerCurls = _lFingerFilter.Filter(curls);
                    UpdateLandmarkPoints(joints, _lHandPoints, ref _lHandTime);
                } else {
                    _currentPacket.RightFingerCurls = _rFingerFilter.Filter(curls);
                    UpdateLandmarkPoints(joints, _rHandPoints, ref _rHandTime);
                }
            }
        }
    }

    private async Awaitable UpdatePoseState(BlazePoseDetector.PoseDetection pose)
    {
        if (pose.IsValid) _currentPacket.IsTracking |= 4; else _currentPacket.IsTracking &= unchecked((byte)~4);
        _poseStatus = pose.IsValid ? "[포즈] 감지됨" : "[포즈] 놓침";
        if (pose.IsValid)
        {
            var joints = await poseLandmarker.RunAsync(_cameraTexture, pose);
            RecordUpdateRate(ref _poseLandmarkerRate);
            if (joints != null && joints.Length >= 15)
            {
                _lastPoseJoints = joints; // ROI 힌트용 저장
                _calibrationPoseReady = AreCalibrationJointsVisible(joints);
                UpdatePoseData(joints);
                UpdateLandmarkPoints(joints, _posePoints, ref _poseTime);
                RecordUpdateRate(ref _poseApplicationRate);
            }
            else
            {
                _currentPacket.IsTracking &= unchecked((byte)~4);
                _poseStatus = "[포즈] 놓침";
                _lastPoseJoints = null;
                _calibrationPoseReady = false;
                ResetArmDirectionFilters();
            }
        }
        else
        {
            _lastPoseJoints = null;
            _calibrationPoseReady = false;
            ResetArmDirectionFilters();
        }
    }

    private readonly List<UnityEngine.UI.Image> _pointPool = new();
    private readonly List<UnityEngine.UI.Image> _bonePool = new();
    private int _activePointCount;
    private int _activeBoneCount;

    private void UpdateFacePoints(BlazeFaceDetector.FaceDetection face)
    {
        if (_cameraTexture == null || face.RawBoxes == null || face.RawBoxes.Length == 0) return;
        
        // 첫 번째 얼굴 기준
        // BlazeFace는 6개 키포인트를 가짐 (눈, 코, 입, 귀)
        for (int i=0; i<6; i++)
        {
            var kpTensor = face.AnchorPosition + new Unity.Mathematics.float2(face.RawBoxes[4 + i * 2], face.RawBoxes[4 + i * 2 + 1]);
            var kpTexture = BlazeUtils.mul(face.DetectorMatrix, kpTensor);
            
            _facePoints[i] = new Vector2(kpTexture.x / _cameraTexture.width, kpTexture.y / _cameraTexture.height);
        }
        _faceTime = Time.time;
    }

    private void UpdateHeadDataFromFace(BlazeFaceDetector.FaceDetection face)
    {
        if (face.RawBoxes == null || face.RawBoxes.Length < 16) return;

        Vector2 rightEye = GetFacePoint(face, 0);
        Vector2 leftEye = GetFacePoint(face, 1);
        Vector2 nose = GetFacePoint(face, 2);
        Vector2 mouth = GetFacePoint(face, 3);
        Vector2 rightEar = GetFacePoint(face, 4);
        Vector2 leftEar = GetFacePoint(face, 5);

        Vector2 eyeCenter = (rightEye + leftEye) * 0.5f;
        Vector2 earCenter = (rightEar + leftEar) * 0.5f;
        float eyeDistance = Vector2.Distance(rightEye, leftEye);
        float faceWidth = Mathf.Max(Vector2.Distance(rightEar, leftEar), eyeDistance * 1.8f);
        float faceHeight = Mathf.Max(Vector2.Distance(eyeCenter, mouth), faceWidth * 0.35f);
        if (faceWidth < 1f || faceHeight < 1f) return;

        float yaw = Mathf.Clamp((nose.x - earCenter.x) / faceWidth * 90f, -40f, 40f);
        float pitch = Mathf.Clamp((nose.y - eyeCenter.y) / faceHeight * 35f, -25f, 25f);
        float roll = Mathf.Clamp(
            Mathf.Atan2(leftEye.y - rightEye.y, leftEye.x - rightEye.x) * Mathf.Rad2Deg,
            -25f,
            25f);

        _currentPacket.HeadRotation = _headFilter.Filter(new Vector3(pitch, yaw, roll));
    }

    private static Vector2 GetFacePoint(BlazeFaceDetector.FaceDetection face, int index)
    {
        var point = face.AnchorPosition + new Unity.Mathematics.float2(
            face.RawBoxes[4 + index * 2],
            face.RawBoxes[5 + index * 2]);
        var texturePoint = BlazeUtils.mul(face.DetectorMatrix, point);
        return new Vector2(texturePoint.x, texturePoint.y);
    }

    private void UpdateLandmarkPoints(Vector3[] joints, Vector2[] outPoints, ref float outTime)
    {
        if (_cameraTexture == null || joints == null) return;
        int count = Mathf.Min(joints.Length, outPoints.Length);
        for (int i = 0; i < count; i++)
        {
            // Normalize texture space (pixels) -> normalized space (0~1)
            outPoints[i] = new Vector2(joints[i].x / _cameraTexture.width, joints[i].y / _cameraTexture.height);
        }
        outTime = Time.time;
    }

    private void RenderAllPoints()
    {
        ClearDebugPoints();
        float now = Time.time;
        float timeout = 0.5f;

        if (now - _faceTime < timeout)
        {
            SetBone(_facePoints[4], _facePoints[0], Color.yellow);
            SetBone(_facePoints[0], _facePoints[2], Color.yellow);
            SetBone(_facePoints[2], _facePoints[1], Color.yellow);
            SetBone(_facePoints[1], _facePoints[5], Color.yellow);
            SetBone(_facePoints[2], _facePoints[3], Color.yellow);
            for (int i = 0; i < 6; i++) SetPoint(_facePoints[i], Color.yellow);
        }
        if (now - _lHandTime < timeout)
        {
            for (int i = 0; i < 21; i++) SetPoint(_lHandPoints[i], Color.green);
        }
        if (now - _rHandTime < timeout)
        {
            for (int i = 0; i < 21; i++) SetPoint(_rHandPoints[i], new Color(0, 0.8f, 0)); // Dark green
        }
        if (now - _poseTime < timeout)
        {
            // Compact upper-body skeleton: shoulders and elbows are sufficient in a narrow room.
            SetBone(_posePoints[11], _posePoints[12], PoseSkeletonColor);
            SetBone(_posePoints[11], _posePoints[13], PoseSkeletonColor);
            SetBone(_posePoints[12], _posePoints[14], PoseSkeletonColor);
            for (int i = 11; i <= 14; i++) SetPoint(_posePoints[i], PoseSkeletonColor);

            if (now - _faceTime < timeout)
            {
                Vector2 shoulderCenter = (_posePoints[11] + _posePoints[12]) * 0.5f;
                SetBone(_facePoints[2], shoulderCenter, PoseSkeletonColor);
            }
        }
    }

    private void SetPoint(Vector2 normPos, Color color)
    {
        if (displayImage == null) return;
        if (normPos.x < 0f || normPos.x > 1f || normPos.y < 0f || normPos.y > 1f) return;
        if (_activePointCount >= 150) return; 

        UnityEngine.UI.Image img;
        if (_activePointCount < _pointPool.Count)
        {
            img = _pointPool[_activePointCount];
        }
        else
        {
            var go = new GameObject("Tracking Point", typeof(UnityEngine.UI.Image));
            go.transform.SetParent(displayImage.transform, false);
            img = go.GetComponent<UnityEngine.UI.Image>();
            img.rectTransform.anchorMin = img.rectTransform.anchorMax = img.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            img.rectTransform.sizeDelta = new Vector2(8, 8);
            img.raycastTarget = false;
            _pointPool.Add(img);
        }

        img.gameObject.SetActive(true);
        img.color = color;
        
        float x = (normPos.x - 0.5f) * displayImage.rectTransform.rect.width;
        float y = (normPos.y - 0.5f) * displayImage.rectTransform.rect.height;
        img.rectTransform.anchoredPosition = new Vector2(x, y);
        _activePointCount++;
    }

    private void SetBone(Vector2 startNorm, Vector2 endNorm, Color color)
    {
        if (displayImage == null) return;
        if (!IsNormalizedPointVisible(startNorm) || !IsNormalizedPointVisible(endNorm)) return;
        if (_activeBoneCount >= 32) return;

        UnityEngine.UI.Image bone;
        if (_activeBoneCount < _bonePool.Count)
        {
            bone = _bonePool[_activeBoneCount];
        }
        else
        {
            var go = new GameObject("Tracking Bone", typeof(UnityEngine.UI.Image));
            go.transform.SetParent(displayImage.transform, false);
            go.transform.SetAsFirstSibling();
            bone = go.GetComponent<UnityEngine.UI.Image>();
            bone.rectTransform.anchorMin = bone.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            bone.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            bone.raycastTarget = false;
            bone.gameObject.SetActive(false);
            _bonePool.Add(bone);
        }

        Vector2 start = ToPreviewLocalPosition(startNorm);
        Vector2 end = ToPreviewLocalPosition(endNorm);
        Vector2 direction = end - start;
        float length = direction.magnitude;
        if (length < 1f)
        {
            bone.gameObject.SetActive(false);
            return;
        }

        bone.gameObject.SetActive(true);
        bone.color = color;
        bone.rectTransform.sizeDelta = new Vector2(length, PoseSkeletonLineWidth);
        bone.rectTransform.anchoredPosition = (start + end) * 0.5f;
        bone.rectTransform.localRotation = Quaternion.Euler(
            0f,
            0f,
            Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
        _activeBoneCount++;
    }

    private static bool IsNormalizedPointVisible(Vector2 point)
    {
        return point.x >= 0f && point.x <= 1f && point.y >= 0f && point.y <= 1f;
    }

    private Vector2 ToPreviewLocalPosition(Vector2 normPos)
    {
        Rect previewRect = displayImage.rectTransform.rect;
        return new Vector2(
            (normPos.x - 0.5f) * previewRect.width,
            (normPos.y - 0.5f) * previewRect.height);
    }

    private void ClearDebugPoints()
    {
        for (int i = 0; i < _pointPool.Count; i++) _pointPool[i].gameObject.SetActive(false);
        for (int i = 0; i < _bonePool.Count; i++) _bonePool[i].gameObject.SetActive(false);
        _activePointCount = 0;
        _activeBoneCount = 0;
    }

    private void UpdatePoseData(Vector3[] joints)
    {
        var leftShoulder   = joints[11];
        var rightShoulder  = joints[12];
        var lElbow         = joints[13];
        var rElbow         = joints[14];

        Vector2 leftArmVector = new Vector2(lElbow.x - leftShoulder.x, lElbow.y - leftShoulder.y);
        Vector2 rightArmVector = new Vector2(rElbow.x - rightShoulder.x, rElbow.y - rightShoulder.y);
        GetArmInputFilterSettings(out float smoothing, out float maxInputJump);

        if (TryFilterArmDirection(
                leftArmVector,
                smoothing,
                maxInputJump,
                ref _leftArmDirection,
                ref _lastLeftArmRawDirection,
                ref _leftArmDirectionInitialized,
                ref _leftArmRejectedFrames,
                out float leftAngle))
        {
            _currentPacket.LeftArmRotation = new Vector3(0f, 0f, leftAngle);
        }

        if (TryFilterArmDirection(
                rightArmVector,
                smoothing,
                maxInputJump,
                ref _rightArmDirection,
                ref _lastRightArmRawDirection,
                ref _rightArmDirectionInitialized,
                ref _rightArmRejectedFrames,
                out float rightAngle))
        {
            _currentPacket.RightArmRotation = new Vector3(0f, 0f, rightAngle);
        }
    }

    private void Update()
    {
        if (_shuttingDown) return;

        EnforceRawCameraOutputBlock();
        RecordRenderTiming(Time.unscaledDeltaTime * 1000f);
        if (_cameraTexture == null || !_cameraTexture.isPlaying) return;
        if (Time.unscaledTime < _nextStatusRefreshTime) return;

        _nextStatusRefreshTime = Time.unscaledTime + StatusRefreshInterval;
        RefreshTrackingStatus();
    }

    private void GetArmInputFilterSettings(out float smoothing, out float maxInputJump)
    {
        smoothing = 0.4f;
        maxInputJump = 120f;
        if (avatarController == null || !avatarController.HasArmMappingConfig) return;

        VRMMapper.ArmMappingConfig config = avatarController.GetArmMappingConfig();
        smoothing = Mathf.Clamp(config.InputSmoothing, 0.05f, 1f);
        maxInputJump = Mathf.Clamp(config.MaxInputJump, 30f, 180f);
    }

    private bool TryFilterArmDirection(
        Vector2 armVector,
        float smoothing,
        float maxInputJump,
        ref Vector2 filteredDirection,
        ref Vector2 lastRawDirection,
        ref bool initialized,
        ref int rejectedFrames,
        out float angle)
    {
        angle = 0f;
        float minimumLength = _cameraTexture != null
            ? Mathf.Min(_cameraTexture.width, _cameraTexture.height) * 0.025f
            : 8f;
        if (armVector.sqrMagnitude < minimumLength * minimumLength)
        {
            if (!initialized) return false;
            angle = DirectionToAngle(filteredDirection);
            return true;
        }

        Vector2 rawDirection = armVector.normalized;
        if (!initialized)
        {
            filteredDirection = rawDirection;
            lastRawDirection = rawDirection;
            initialized = true;
            rejectedFrames = 0;
            angle = DirectionToAngle(filteredDirection);
            return true;
        }

        float inputJump = Vector2.Angle(lastRawDirection, rawDirection);
        if (inputJump > maxInputJump && rejectedFrames < 2)
        {
            rejectedFrames++;
            _armInputRejectedCount++;
            angle = DirectionToAngle(filteredDirection);
            return true;
        }

        if (inputJump > maxInputJump)
            filteredDirection = rawDirection;
        else
            filteredDirection = Vector2.Lerp(filteredDirection, rawDirection, smoothing);

        if (filteredDirection.sqrMagnitude < 0.0001f)
        {
            angle = DirectionToAngle(lastRawDirection);
            return true;
        }

        filteredDirection.Normalize();
        lastRawDirection = rawDirection;
        rejectedFrames = 0;
        angle = DirectionToAngle(filteredDirection);
        return true;
    }

    private static float DirectionToAngle(Vector2 direction)
    {
        return Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
    }

    private void ResetArmDirectionFilters()
    {
        _leftArmDirectionInitialized = false;
        _rightArmDirectionInitialized = false;
        _leftArmRejectedFrames = 0;
        _rightArmRejectedFrames = 0;
    }

    private bool AreCalibrationJointsVisible(Vector3[] joints)
    {
        if (_cameraTexture == null || joints == null || joints.Length < 15) return false;

        const float margin = 0.02f;
        float minX = _cameraTexture.width * margin;
        float maxX = _cameraTexture.width * (1f - margin);
        float minY = _cameraTexture.height * margin;
        float maxY = _cameraTexture.height * (1f - margin);

        int[] required = { 11, 12, 13, 14 };
        foreach (int index in required)
        {
            Vector3 point = joints[index];
            if (point.x < minX || point.x > maxX || point.y < minY || point.y > maxY)
                return false;
        }

        return Vector3.Distance(joints[11], joints[12]) > _cameraTexture.width * 0.08f;
    }

    private void ValidateComponents()
    {
        EnsureRuntimeUI();
        if (faceDetector == null)   faceDetector = GetComponent<BlazeFaceDetector>();
        if (handDetector == null)   handDetector = GetComponent<BlazeHandDetector>();
        if (handLandmarker == null) handLandmarker = GetComponent<HandLandmarkRunner>();
        if (poseDetector == null)   poseDetector = GetComponent<BlazePoseDetector>();
        if (poseLandmarker == null) poseLandmarker = GetComponent<BlazePoseLandmarkRunner>();
        if (serverSender == null)   serverSender = GetComponent<ServerStreamSender>();
        if (webcamManager == null)  webcamManager = GetComponent<WebcamManager>();
        if (webcamManager == null)  webcamManager = gameObject.AddComponent<WebcamManager>();
        if (avatarController == null) avatarController = GetComponent<LiveAvatarController>();
        if (avatarController == null) avatarController = gameObject.AddComponent<LiveAvatarController>();

        var controlPanel = GetComponent<WebcamControlPanel>();
        if (controlPanel == null) controlPanel = gameObject.AddComponent<WebcamControlPanel>();
        controlPanel.Initialize(webcamManager, this);
    }

    private void EnsureRuntimeUI()
    {
        if (displayImage == null)
            displayImage = FindFirstObjectByType<UnityEngine.UI.RawImage>();
        if (debugText == null)
            debugText = FindFirstObjectByType<TMPro.TMP_Text>();

        UnityEngine.Canvas canvas = FindFirstObjectByType<UnityEngine.Canvas>();
        if (canvas == null)
        {
            var canvasObject = new GameObject(
                "Runtime Canvas",
                typeof(RectTransform),
                typeof(UnityEngine.Canvas),
                typeof(UnityEngine.UI.CanvasScaler),
                typeof(UnityEngine.UI.GraphicRaycaster));
            canvas = canvasObject.GetComponent<UnityEngine.Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        if (displayImage == null)
        {
            var previewObject = new GameObject(
                "Privacy Tracking Preview",
                typeof(RectTransform),
                typeof(UnityEngine.CanvasRenderer),
                typeof(UnityEngine.UI.RawImage));
            previewObject.transform.SetParent(canvas.transform, false);
            previewObject.transform.SetAsFirstSibling();

            displayImage = previewObject.GetComponent<UnityEngine.UI.RawImage>();
            displayImage.raycastTarget = false;
            displayImage.color = PrivacyPreviewColor;

            RectTransform previewRect = displayImage.rectTransform;
            previewRect.anchorMin = Vector2.zero;
            previewRect.anchorMax = Vector2.one;
            previewRect.offsetMin = Vector2.zero;
            previewRect.offsetMax = Vector2.zero;
        }

        if (displayImage != null && _cameraTexture == null)
        {
            displayImage.texture = null;
            displayImage.enabled = false;
        }

        if (debugText == null)
        {
            var textObject = new GameObject(
                "Tracking Status",
                typeof(RectTransform),
                typeof(UnityEngine.CanvasRenderer),
                typeof(TMPro.TextMeshProUGUI));
            textObject.transform.SetParent(canvas.transform, false);

            debugText = textObject.GetComponent<TMPro.TextMeshProUGUI>();
            debugText.fontSize = 24f;
            debugText.color = Color.white;
            debugText.richText = true;
            debugText.raycastTarget = false;
            debugText.alignment = TMPro.TextAlignmentOptions.BottomLeft;

            RectTransform textRect = debugText.rectTransform;
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 0.28f);
            textRect.offsetMin = new Vector2(24f, 20f);
            textRect.offsetMax = new Vector2(-24f, -20f);
        }

        if (debugText != null)
        {
            debugText.text = string.Empty;
            debugText.enabled = false;
        }
    }

    private void SetTrackingStatus(string status)
    {
        _trackingStatusText = status ?? string.Empty;
        if (debugText != null && debugText.enabled)
        {
            debugText.text = string.Empty;
            debugText.enabled = false;
        }
    }

    private void OnDisable()
    {
        LogPerformanceSummaryOnce("pipeline-disable");
        _shuttingDown = true;
        _pauseInference = true;
        _cameraTexture = null;
    }

    private void OnDestroy() => OnDisable();

    private void OnApplicationQuit() => LogPerformanceSummaryOnce("application-quit");

    public async Awaitable<bool> ChangeCameraAsync(int deviceIndex, int resolutionIndex, bool mirror)
    {
        if (webcamManager == null || _shuttingDown) return false;

        _pauseInference = true;
        while (_inferenceBusy && !_shuttingDown)
            await Awaitable.NextFrameAsync();

        bool changed = await webcamManager.ChangeCameraAsync(deviceIndex, resolutionIndex, mirror);
        if (changed)
        {
            LogPerformanceSummaryOnce("camera-change");
            _cameraTexture = webcamManager.Texture;
            ResetArmDirectionFilters();
            ResetPerformanceStatistics();
            SetupLayout();
        }

        _pauseInference = false;
        return changed;
    }

    public async Awaitable StopCameraAsync()
    {
        if (webcamManager == null || _shuttingDown) return;

        _pauseInference = true;
        while (_inferenceBusy && !_shuttingDown)
            await Awaitable.NextFrameAsync();

        LogPerformanceSummaryOnce("camera-stop");
        webcamManager.StopCamera();
        _cameraTexture = null;
        _lastPoseJoints = null;
        _calibrationPoseReady = false;
        ResetArmDirectionFilters();
        ClearDebugPoints();

        if (displayImage != null)
        {
            displayImage.texture = null;
            displayImage.enabled = false;
        }
        SetTrackingStatus("카메라가 꺼져 있습니다. 카메라 시작을 누르세요.");

        _pauseInference = false;
    }

    public string AvatarStatus => avatarController != null ? avatarController.Status : "아바타 컨트롤러가 없습니다.";
    public IReadOnlyList<AvatarLibraryStore.AvatarEntry> RegisteredAvatars => avatarController != null
        ? avatarController.Entries
        : System.Array.Empty<AvatarLibraryStore.AvatarEntry>();
    public string CurrentAvatarId => avatarController != null ? avatarController.CurrentAvatarId : string.Empty;
    public string CurrentAvatarName => avatarController != null ? avatarController.CurrentAvatarName : string.Empty;
    public int AvatarRevision => avatarController != null ? avatarController.AvatarRevision : 0;
    public bool IsAvatarBusy => avatarController != null && avatarController.IsBusy;
    public bool HasAvatar => avatarController != null && avatarController.HasAvatar;
    public string TrackingStatusText => _trackingStatusText;
    public bool IsRawCameraPreviewBlocked =>
        _cameraTexture == null || displayImage == null || displayImage.texture != _cameraTexture;
    public bool CanCalibrateAvatar => avatarController != null && avatarController.CanCalibrate;
    public bool IsCalibratingAvatar => avatarController != null && avatarController.IsCalibrating;
    public bool IsCalibrationTrackingInterrupted =>
        avatarController != null && avatarController.IsCalibrationTrackingInterrupted;
    public bool IsPreparingCalibration { get; private set; }
    public string CalibrationPreparationMessage { get; private set; } = string.Empty;
    public int AvatarCalibrationCountdown => avatarController != null
        ? avatarController.CalibrationCountdown
        : 0;
    public int CalibrationTrackingRecoveryCountdown => avatarController != null
        ? avatarController.CalibrationTrackingRecoveryCountdown
        : 0;
    public int CalibrationNoticeRevision => avatarController != null
        ? avatarController.CalibrationNoticeRevision
        : 0;
    public string CalibrationNoticeMessage => avatarController != null
        ? avatarController.CalibrationNoticeMessage
        : string.Empty;
    public bool CalibrationNoticeIsError =>
        avatarController != null && avatarController.CalibrationNoticeIsError;
    public string AvatarCalibrationButtonLabel => avatarController != null
        ? avatarController.CalibrationButtonLabel
        : "중립 자세 캘리브레이션 (3초)";

    public bool CalibrateAvatar()
    {
        return avatarController != null && avatarController.BeginCalibration();
    }

    public async Awaitable<string> RequestAvatarCalibrationAsync()
    {
        if (IsPreparingCalibration) return "캘리브레이션 준비가 이미 진행 중입니다.";
        if (avatarController == null) return "아바타 컨트롤러가 준비되지 않았습니다.";

        string reason = avatarController.GetCalibrationBlockReason(requireTracking: false);
        if (!string.IsNullOrEmpty(reason)) return reason;

        IsPreparingCalibration = true;
        try
        {
            if (webcamManager == null) return "카메라 관리자가 준비되지 않았습니다.";
            if (!webcamManager.IsReady)
            {
                CalibrationPreparationMessage = "카메라를 자동으로 시작하는 중...";
                bool started = await ChangeCameraAsync(
                    webcamManager.SelectedDeviceIndex,
                    webcamManager.SelectedResolutionIndex,
                    webcamManager.Mirror);
                if (!started)
                {
                    return string.IsNullOrWhiteSpace(webcamManager.LastError)
                        ? "카메라를 시작하지 못했습니다. 카메라 설정을 확인하세요."
                        : webcamManager.LastError;
                }
            }

            CalibrationPreparationMessage = "얼굴과 양쪽 어깨·팔꿈치를 찾는 중...";
            float deadline = Time.unscaledTime + CalibrationTrackingWaitSeconds;
            while (!_shuttingDown && !avatarController.HasCalibrationTracking && Time.unscaledTime < deadline)
            {
                reason = avatarController.GetCalibrationBlockReason(requireTracking: false);
                if (!string.IsNullOrEmpty(reason)) return reason;
                await Awaitable.NextFrameAsync();
            }

            if (_shuttingDown) return "프로그램 종료 중에는 캘리브레이션할 수 없습니다.";
            reason = avatarController.GetCalibrationBlockReason(requireTracking: true);
            if (!string.IsNullOrEmpty(reason)) return reason;
            return avatarController.BeginCalibration() ? string.Empty : avatarController.Status;
        }
        finally
        {
            IsPreparingCalibration = false;
            CalibrationPreparationMessage = string.Empty;
        }
    }

    public System.Threading.Tasks.Task<bool> RegisterAndActivateAvatarAsync(string sourcePath)
    {
        return avatarController != null
            ? avatarController.RegisterAndActivateAsync(sourcePath)
            : System.Threading.Tasks.Task.FromResult(false);
    }

    public System.Threading.Tasks.Task<bool> ActivateAvatarAsync(string avatarId)
    {
        return avatarController != null
            ? avatarController.ActivateAvatarAsync(avatarId)
            : System.Threading.Tasks.Task.FromResult(false);
    }

    public System.Threading.Tasks.Task<bool> RemoveAvatarAsync(string avatarId)
    {
        return avatarController != null
            ? avatarController.RemoveAvatarAsync(avatarId)
            : System.Threading.Tasks.Task.FromResult(false);
    }

    public bool HasArmMappingConfig => avatarController != null && avatarController.HasArmMappingConfig;
    public string ArmMappingDiagnostics => avatarController != null
        ? avatarController.ArmMappingDiagnostics
        : "팔 매퍼가 없습니다.";

    public VRMMapper.ArmMappingConfig GetArmMappingConfig()
    {
        return avatarController != null ? avatarController.GetArmMappingConfig() : default;
    }

    public void SetArmMappingConfig(VRMMapper.ArmMappingConfig config)
    {
        if (avatarController != null) avatarController.SetArmMappingConfig(config);
    }

    public void ResetArmMappingConfig()
    {
        if (avatarController != null) avatarController.ResetArmMappingConfig();
    }

    public bool TryGetArmValidationSample(out ArmValidationSample sample)
    {
        sample = default;
        if (avatarController == null || !avatarController.HasArmMappingConfig) return false;

        sample.PoseTracked = (_currentPacket.IsTracking & 4) != 0 && _calibrationPoseReady;
        sample.Calibrated = avatarController.IsAvatarCalibrated;
        sample.LeftInput = avatarController.LeftArmInputAngle;
        sample.RightInput = avatarController.RightArmInputAngle;
        sample.LeftOutput = avatarController.LeftArmOutputAngle;
        sample.RightOutput = avatarController.RightArmOutputAngle;
        sample.RejectedInputCount = _armInputRejectedCount;
        return true;
    }

    public void ResetArmValidationDiagnostics()
    {
        _armInputRejectedCount = 0;
    }

    public bool TryGetWebcamPreviewGuiRect(out Rect guiRect)
    {
        guiRect = default;
        if (displayImage == null || webcamManager == null || !webcamManager.IsReady) return false;

        var corners = new Vector3[4];
        displayImage.rectTransform.GetWorldCorners(corners);
        float minX = Mathf.Min(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        float maxX = Mathf.Max(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        float minY = Mathf.Min(corners[0].y, corners[1].y, corners[2].y, corners[3].y);
        float maxY = Mathf.Max(corners[0].y, corners[1].y, corners[2].y, corners[3].y);

        guiRect = new Rect(minX, Screen.height - maxY, maxX - minX, maxY - minY);
        return guiRect.width > 1f && guiRect.height > 1f;
    }
}

