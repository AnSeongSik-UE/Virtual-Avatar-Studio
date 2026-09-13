using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class BroadcastPreviewCameraController : MonoBehaviour
{
    private enum DragMode
    {
        None,
        Pan,
        Orbit
    }

    private const float OrbitDegreesPerPixel = 0.18f;
    private const float MinimumPitch = -75f;
    private const float MaximumPitch = 75f;
    private const float ZoomFactorPerWheelStep = 0.88f;
    private const float MinimumDistanceRatio = 0.35f;
    private const float MaximumDistanceRatio = 3f;

    private Camera _camera;
    private WebcamControlPanel _controlPanel;
    private DragMode _dragMode;
    private Vector3 _pivot;
    private Vector3 _homePosition;
    private Vector3 _homePivot;
    private Quaternion _homeRotation;
    private float _homeFieldOfView;
    private float _homeDistance;
    private bool _hasHomePose;

    public bool HasHomePose => _hasHomePose;

    public void Initialize(Camera targetCamera)
    {
        _camera = targetCamera != null ? targetCamera : GetComponent<Camera>();
        if (_controlPanel == null) _controlPanel = FindFirstObjectByType<WebcamControlPanel>();
        if (_camera == null || _hasHomePose) return;

        const float fallbackDistance = 3f;
        SetHomePose(
            _camera.transform.position,
            _camera.transform.position + _camera.transform.forward * fallbackDistance,
            _camera.fieldOfView);
    }

    public void SetHomePose(Vector3 position, Vector3 pivot, float fieldOfView)
    {
        if (_camera == null) _camera = GetComponent<Camera>();
        if (_camera == null) return;

        _homePosition = position;
        _homePivot = pivot;
        _homeRotation = Quaternion.LookRotation(pivot - position, Vector3.up);
        _homeFieldOfView = fieldOfView;
        _homeDistance = Mathf.Max(0.01f, Vector3.Distance(position, pivot));
        _hasHomePose = true;
        ApplyPose(_homePosition, _homePivot, _homeRotation, _homeFieldOfView);
    }

    public void ResetView()
    {
        if (!_hasHomePose) return;
        _dragMode = DragMode.None;
        ApplyPose(_homePosition, _homePivot, _homeRotation, _homeFieldOfView);
    }

    private void Update()
    {
        if (_camera == null) Initialize(null);
        Mouse mouse = Mouse.current;
        if (_camera == null || mouse == null || !_hasHomePose || !Application.isFocused)
        {
            _dragMode = DragMode.None;
            return;
        }

        if (_controlPanel == null) _controlPanel = FindFirstObjectByType<WebcamControlPanel>();
        Vector2 pointerPosition = mouse.position.ReadValue();
        bool pointerOverUi = _controlPanel != null &&
                             _controlPanel.IsPointerOverControlUi(pointerPosition);

        if (mouse.leftButton.wasPressedThisFrame && !pointerOverUi)
            _dragMode = DragMode.Pan;
        if (mouse.rightButton.wasPressedThisFrame && !pointerOverUi)
            _dragMode = DragMode.Orbit;

        if ((_dragMode == DragMode.Pan && !mouse.leftButton.isPressed) ||
            (_dragMode == DragMode.Orbit && !mouse.rightButton.isPressed))
        {
            _dragMode = DragMode.None;
        }

        Vector2 pointerDelta = mouse.delta.ReadValue();
        if (_dragMode == DragMode.Pan && pointerDelta.sqrMagnitude > 0f)
            Pan(pointerDelta);
        else if (_dragMode == DragMode.Orbit && pointerDelta.sqrMagnitude > 0f)
            Orbit(pointerDelta);

        float wheelDelta = mouse.scroll.ReadValue().y;
        if (!pointerOverUi && Mathf.Abs(wheelDelta) > 0.01f)
            Zoom(wheelDelta);
    }

    private void Pan(Vector2 pointerDelta)
    {
        float distance = Mathf.Max(0.01f, Vector3.Distance(_camera.transform.position, _pivot));
        float worldHeight = 2f * distance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float worldPerPixel = worldHeight / Mathf.Max(1f, Screen.height);
        Vector3 translation =
            -_camera.transform.right * pointerDelta.x * worldPerPixel -
            _camera.transform.up * pointerDelta.y * worldPerPixel;
        _pivot += translation;
        _camera.transform.position += translation;
    }

    private void Orbit(Vector2 pointerDelta)
    {
        Vector3 offset = _camera.transform.position - _pivot;
        float distance = offset.magnitude;
        if (distance < 0.01f) return;

        float yaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
        float pitch = Mathf.Asin(Mathf.Clamp(offset.y / distance, -1f, 1f)) * Mathf.Rad2Deg;
        yaw += pointerDelta.x * OrbitDegreesPerPixel;
        pitch = Mathf.Clamp(
            pitch - pointerDelta.y * OrbitDegreesPerPixel,
            MinimumPitch,
            MaximumPitch);

        float pitchRadians = pitch * Mathf.Deg2Rad;
        float yawRadians = yaw * Mathf.Deg2Rad;
        float horizontalDistance = Mathf.Cos(pitchRadians) * distance;
        Vector3 newOffset = new(
            Mathf.Sin(yawRadians) * horizontalDistance,
            Mathf.Sin(pitchRadians) * distance,
            Mathf.Cos(yawRadians) * horizontalDistance);
        _camera.transform.position = _pivot + newOffset;
        _camera.transform.LookAt(_pivot, Vector3.up);
    }

    private void Zoom(float wheelSteps)
    {
        Vector3 offset = _camera.transform.position - _pivot;
        float distance = offset.magnitude;
        if (distance < 0.01f) return;

        float minimumDistance = Mathf.Max(0.25f, _homeDistance * MinimumDistanceRatio);
        float maximumDistance = Mathf.Max(minimumDistance, _homeDistance * MaximumDistanceRatio);
        float targetDistance = Mathf.Clamp(
            distance * Mathf.Pow(ZoomFactorPerWheelStep, wheelSteps),
            minimumDistance,
            maximumDistance);
        _camera.transform.position = _pivot + offset.normalized * targetDistance;
        _camera.transform.LookAt(_pivot, Vector3.up);
    }

    private void ApplyPose(
        Vector3 position,
        Vector3 pivot,
        Quaternion rotation,
        float fieldOfView)
    {
        _pivot = pivot;
        _camera.transform.SetPositionAndRotation(position, rotation);
        _camera.fieldOfView = fieldOfView;
    }
}
