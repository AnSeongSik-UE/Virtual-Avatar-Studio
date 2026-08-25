// ServerStreamSender.cs (Android recommended)
// Relays tracking packets to the Docker Go Server via UDP
using System.Net.Sockets;
using UnityEngine;

public class ServerStreamSender : MonoBehaviour
{
    [Header("Docker Cloud Server Settings")]
    [SerializeField] private string serverIpAddress = "127.0.0.1";
    [SerializeField] private int    port            = 49152;

    private UdpClient _client;

    // Use Awake to initialize before TrackingPipeline.Start()
    void Awake()
    {
        InitializeClient();
    }

    private void InitializeClient()
    {
        if (_client == null)
        {
            _client = new UdpClient();
            Debug.Log($"[ServerStreamSender] UDP Client initialized for {serverIpAddress}:{port}");
        }
    }

    // Unity 6 built-in Awaitable
    public async Awaitable SendAsync(TrackingPacket packet)
    {
        // Safety: ensure client is ready
        if (_client == null) InitializeClient();
        if (_client == null) return;

        try 
        {
            byte[] data = packet.Serialize(); // Fixed 260 bytes
            if (data == null) return;

            // Push UDP packets for maximum speed (latency over reliability)
            await _client.SendAsync(data, data.Length, serverIpAddress, port);
        }
        catch (System.Exception e)
        {
            // Log but don't crash the pipeline
            Debug.LogWarning($"[ServerStreamSender] Send Error: {e.Message}");
        }
    }

    void OnDestroy()
    {
        _client?.Dispose();
        _client = null;
    }
}
