// ServerStreamReceiver.cs (PC 앱)
// Docker 서버에서 전송되는 데이터를 수신하기 위한 클라이언트
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class ServerStreamReceiver : MonoBehaviour
{
  [Header("Docker Cloud Server Settings")]
  [SerializeField] private string serverIpAddress = "127.0.0.1";  // 클라우드 서버IP
  [SerializeField] private int    port            = 49152;
  [SerializeField] private VRMMapper mapper;

  private UdpClient _client;

  void Start()
  {
    _client = new UdpClient();
    _client.Connect(serverIpAddress, port);
    
    // 💡 핵심(Pub/Sub): "SUBSCRIBE"라는 메시지를 보내서 나를 구독자로 등록시킵니다.
    // 안 보내면 Go서버가 내 IP를 몰라 데이터를 안 보내줍니다. (연결 뚫기)
    byte[] initBytes = Encoding.UTF8.GetBytes("SUBSCRIBE");
    _client.Send(initBytes, initBytes.Length);
    
    _ = ReceiveLoopAsync(); // 메인 스레드 블로킹 안함
  }

  private async Awaitable ReceiveLoopAsync()
  {
    while (true)
    {
      var result = await _client.ReceiveAsync();
      
      // 301바이트 미만 패킷은 무시 (Head/Arm Rot + Fingers + BlendShapes + IsTracking)
      if(result.Buffer.Length < 301) continue; 
      
      var packet = TrackingPacket.Deserialize(result.Buffer);
      mapper.Apply(packet); // 메인 렌더링 스레드에서 VRM 캐릭터 변경
    }
  }

  void OnDestroy() => _client?.Dispose();
}
