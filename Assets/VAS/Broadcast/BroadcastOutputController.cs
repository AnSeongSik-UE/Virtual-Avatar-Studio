using Klak.Spout;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(1000)]
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera), typeof(SpoutSender))]
public sealed class BroadcastOutputController : MonoBehaviour
{
    private const int OutputWidth = 1280;
    private const int OutputHeight = 720;
    private const string PreviewShaderResource = "PremultipliedPreview";

    private Camera _camera;
    private SpoutSender _spoutSender;
    private RenderTexture _outputTexture;
    private RenderTexture _previousCameraTarget;
    private CaptureMethod _previousCaptureMethod;
    private Camera _previousSourceCamera;
    private Texture _previousSourceTexture;
    private bool _previousKeepAlpha;
    private GameObject _previewRoot;
    private Material _previewMaterial;
    private BroadcastPreviewCameraController _previewCameraController;
    private bool _initialized;
    private bool _hasStarted;

    public RenderTexture OutputTexture => _outputTexture;

    private void Start()
    {
        _hasStarted = true;
        InitializeOutput();
    }

    private void OnEnable()
    {
        Application.quitting -= HandleApplicationQuitting;
        Application.quitting += HandleApplicationQuitting;
        if (_hasStarted) InitializeOutput();
    }

    public void ApplyPreviewSettings()
    {
        if (_previewMaterial == null) return;
        _previewMaterial.SetColor("_BackgroundColor", BroadcastBackgroundSettings.CurrentColor);
    }

    private void InitializeOutput()
    {
        if (_initialized || !Application.isPlaying) return;

        _camera = GetComponent<Camera>();
        _spoutSender = GetComponent<SpoutSender>();
        _previewCameraController = GetComponent<BroadcastPreviewCameraController>();
        if (_previewCameraController == null)
            _previewCameraController = gameObject.AddComponent<BroadcastPreviewCameraController>();
        _previewCameraController.Initialize(_camera);
        _previewCameraController.enabled = true;
        Shader previewShader = Resources.Load<Shader>(PreviewShaderResource);
        if (_camera == null || _spoutSender == null || previewShader == null)
        {
            Debug.LogError("[BroadcastOutput] 알파 송출 구성요소 또는 미리보기 셰이더를 찾지 못했습니다.");
            return;
        }

        _previousCameraTarget = _camera.targetTexture;
        _previousCaptureMethod = _spoutSender.captureMethod;
        _previousSourceCamera = _spoutSender.sourceCamera;
        _previousSourceTexture = _spoutSender.sourceTexture;
        _previousKeepAlpha = _spoutSender.keepAlpha;

        _outputTexture = new RenderTexture(
            OutputWidth,
            OutputHeight,
            24,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Default)
        {
            name = "Virtual Avatar Studio RGBA Output",
            antiAliasing = 1,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
            hideFlags = HideFlags.DontSave
        };
        _outputTexture.Create();

        _previewMaterial = new Material(previewShader)
        {
            name = "Virtual Avatar Studio Premultiplied Preview",
            hideFlags = HideFlags.DontSave
        };

        CreatePreviewCanvas();
        _camera.targetTexture = _outputTexture;
        _spoutSender.captureMethod = CaptureMethod.Texture;
        _spoutSender.sourceCamera = null;
        _spoutSender.sourceTexture = _outputTexture;
        _spoutSender.keepAlpha = true;
        _initialized = true;

        BroadcastBackgroundSettings.ApplyTo(_camera);
    }

    private void CreatePreviewCanvas()
    {
        _previewRoot = new GameObject(
            "Broadcast Local Preview",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster));

        Canvas canvas = _previewRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = short.MinValue;

        CanvasScaler scaler = _previewRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

        GraphicRaycaster raycaster = _previewRoot.GetComponent<GraphicRaycaster>();
        raycaster.enabled = false;

        var imageObject = new GameObject(
            "Premultiplied Avatar Preview",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(RawImage));
        imageObject.transform.SetParent(_previewRoot.transform, false);

        RawImage image = imageObject.GetComponent<RawImage>();
        image.texture = _outputTexture;
        image.material = _previewMaterial;
        image.raycastTarget = false;

        RectTransform rect = image.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private void OnDisable()
    {
        Application.quitting -= HandleApplicationQuitting;
        ReleaseOutput();
    }

    private void OnDestroy()
    {
        Application.quitting -= HandleApplicationQuitting;
        ReleaseOutput();
    }

    private void HandleApplicationQuitting()
    {
        ReleaseOutput();
        RenderTexture.active = null;
    }

    private void ReleaseOutput()
    {
        if (!_initialized) return;
        _initialized = false;

        if (_camera != null && _camera.targetTexture == _outputTexture)
            _camera.targetTexture = _previousCameraTarget;

        if (_spoutSender != null)
        {
            _spoutSender.captureMethod = _previousCaptureMethod;
            _spoutSender.sourceCamera = _previousSourceCamera;
            _spoutSender.sourceTexture = _previousSourceTexture;
            _spoutSender.keepAlpha = _previousKeepAlpha;
        }

        if (_previewRoot != null)
        {
            RawImage previewImage = _previewRoot.GetComponentInChildren<RawImage>();
            if (previewImage != null)
            {
                previewImage.texture = null;
                previewImage.material = null;
            }

            Destroy(_previewRoot);
        }
        if (_previewMaterial != null) Destroy(_previewMaterial);
        if (_previewCameraController != null) _previewCameraController.enabled = false;
        if (_outputTexture != null)
        {
            if (RenderTexture.active == _outputTexture)
                RenderTexture.active = null;

            _outputTexture.Release();
            Destroy(_outputTexture);
        }

        _previewRoot = null;
        _previewMaterial = null;
        _outputTexture = null;
    }
}
