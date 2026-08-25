using System;
using UnityEngine;

public sealed class WebcamManager : MonoBehaviour
{
    private const string DeviceKey = "VAS.Webcam.Device";
    private const string ResolutionKey = "VAS.Webcam.Resolution";
    private const string MirrorKey = "VAS.Webcam.Mirror";

    [Serializable]
    public struct ResolutionPreset
    {
        public string label;
        public int width;
        public int height;
        public int fps;

        public ResolutionPreset(string label, int width, int height, int fps)
        {
            this.label = label;
            this.width = width;
            this.height = height;
            this.fps = fps;
        }
    }

    private readonly ResolutionPreset[] _presets =
    {
        new("640 x 480 @ 30", 640, 480, 30),
        new("1280 x 720 @ 30", 1280, 720, 30)
    };

    private WebCamDevice[] _devices = Array.Empty<WebCamDevice>();
    private WebCamTexture _texture;

    public WebCamTexture Texture => _texture;
    public ResolutionPreset[] Presets => _presets;
    public int SelectedDeviceIndex { get; private set; }
    public int SelectedResolutionIndex { get; private set; }
    public bool Mirror { get; private set; }
    public bool IsReady => _texture != null && _texture.isPlaying && _texture.width > 16;
    public bool IsChanging { get; private set; }
    public string LastError { get; private set; }

    public string[] DeviceNames
    {
        get
        {
            var names = new string[_devices.Length];
            for (int i = 0; i < _devices.Length; i++) names[i] = _devices[i].name;
            return names;
        }
    }

    public void RefreshDevices()
    {
        _devices = WebCamTexture.devices;
        SelectedDeviceIndex = Mathf.Clamp(SelectedDeviceIndex, 0, Mathf.Max(0, _devices.Length - 1));
    }

    public bool Initialize()
    {
        LastError = string.Empty;
        RefreshDevices();
        if (_devices.Length == 0)
        {
            LastError = "사용 가능한 웹캠을 찾지 못했습니다.";
            return false;
        }

        SelectedDeviceIndex = FindDeviceIndex(PlayerPrefs.GetString(DeviceKey, string.Empty));
        SelectedResolutionIndex = Mathf.Clamp(PlayerPrefs.GetInt(ResolutionKey, 0), 0, _presets.Length - 1);
        Mirror = PlayerPrefs.GetInt(MirrorKey, 1) != 0;
        return true;
    }

    public async Awaitable<bool> ChangeCameraAsync(int deviceIndex, int resolutionIndex, bool mirror)
    {
        if (IsChanging) return false;

        IsChanging = true;
        LastError = string.Empty;

        try
        {
            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
                await Application.RequestUserAuthorization(UserAuthorization.WebCam);

            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                LastError = "웹캠 사용 권한이 거부되었습니다.";
                return false;
            }

            RefreshDevices();
            if (_devices.Length == 0)
            {
                LastError = "사용 가능한 웹캠을 찾지 못했습니다.";
                return false;
            }

            deviceIndex = Mathf.Clamp(deviceIndex, 0, _devices.Length - 1);
            resolutionIndex = Mathf.Clamp(resolutionIndex, 0, _presets.Length - 1);
            ResolutionPreset preset = _presets[resolutionIndex];

            bool sameStream = _texture != null &&
                              _texture.isPlaying &&
                              SelectedDeviceIndex == deviceIndex &&
                              SelectedResolutionIndex == resolutionIndex;
            if (sameStream)
            {
                Mirror = mirror;
                SaveSettings();
                return true;
            }

            if (_texture != null)
            {
                _texture.Stop();
                Destroy(_texture);
                _texture = null;
                await Awaitable.NextFrameAsync();
                await Awaitable.NextFrameAsync();
            }

            var nextTexture = new WebCamTexture(
                _devices[deviceIndex].name,
                preset.width,
                preset.height,
                preset.fps);
            nextTexture.Play();

            const int readyTimeoutFrames = 180;
            int frame = 0;
            while (nextTexture.width <= 16 && frame++ < readyTimeoutFrames)
                await Awaitable.NextFrameAsync();

            if (nextTexture.width <= 16)
            {
                nextTexture.Stop();
                Destroy(nextTexture);
                LastError = $"웹캠을 시작하지 못했습니다: {_devices[deviceIndex].name}";
                return false;
            }

            _texture = nextTexture;
            SelectedDeviceIndex = deviceIndex;
            SelectedResolutionIndex = resolutionIndex;
            Mirror = mirror;
            SaveSettings();

            Debug.Log($"[WebcamManager] Started {_texture.deviceName} ({_texture.width}x{_texture.height})");
            return true;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Debug.LogException(exception);
            return false;
        }
        finally
        {
            IsChanging = false;
        }
    }

    public void StopCamera()
    {
        if (_texture == null) return;
        string deviceName = _texture.deviceName;
        _texture.Stop();
        Destroy(_texture);
        _texture = null;
        LastError = string.Empty;
        Debug.Log($"[WebcamManager] Stopped {deviceName}");
    }

    private int FindDeviceIndex(string deviceName)
    {
        if (!string.IsNullOrEmpty(deviceName))
        {
            for (int i = 0; i < _devices.Length; i++)
                if (_devices[i].name == deviceName) return i;
        }

        return 0;
    }

    private void SaveSettings()
    {
        PlayerPrefs.SetString(DeviceKey, _devices[SelectedDeviceIndex].name);
        PlayerPrefs.SetInt(ResolutionKey, SelectedResolutionIndex);
        PlayerPrefs.SetInt(MirrorKey, Mirror ? 1 : 0);
        PlayerPrefs.Save();
    }

    private void OnDestroy()
    {
        StopCamera();
    }
}
