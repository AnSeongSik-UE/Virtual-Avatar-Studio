// TrackingPacket.cs
// UE 비유: FNetSerialize로 직렬화되는 패킷 구조체와 동일
// Android / iPhone / PC 세 프로젝트에서 이 파일 하나를 공유
using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct TrackingPacket
{
  public Vector3 HeadRotation;
  public Vector3 HeadPosition;
  public Vector3 LeftArmRotation;  // 추가: 좌측 팔 회전
  public Vector3 RightArmRotation; // 추가: 우측 팔 회전
  public float   Timestamp;
  public float[] BlendShapeValues;  // 길이 52 고정
  public float[] LeftFingerCurls;   // 추가: [5] (엄지~소지 굽힘도 0~1)
  public float[] RightFingerCurls;  // 추가: [5]

  public byte    IsTracking; // 비트마스크 (0:None, 1:Face, 2:Hand, 4:Pose)

  // ARKit BlendShape 이름 → 배열 인덱스 (iPhone 버전과 동일 키 사용)
  public static readonly Dictionary<string, int> BlendShapeIndex = new()
  {
    { "eyeBlinkLeft",        0 }, { "eyeBlinkRight",       1 },
    { "eyeWideLeft",         2 }, { "eyeWideRight",        3 },
    { "browDownLeft",        4 }, { "browDownRight",       5 },
    { "browInnerUp",         6 }, { "browOuterUpLeft",     7 },
    { "browOuterUpRight",    8 }, { "jawOpen",             9 },
    { "jawLeft",            10 }, { "jawRight",           11 },
    { "mouthSmileLeft",     12 }, { "mouthSmileRight",    13 },
    { "mouthFrownLeft",     14 }, { "mouthFrownRight",    15 },
    { "mouthFunnel",        16 }, { "mouthPucker",        17 },
    { "mouthLeft",          18 }, { "mouthRight",         19 },
    { "mouthRollUpper",     20 }, { "mouthRollLower",     21 },
    { "mouthShrugUpper",    22 }, { "mouthShrugLower",    23 },
    { "mouthOpen",          24 }, { "mouthClose",         25 },
    { "mouthUpperUpLeft",   26 }, { "mouthUpperUpRight",  27 },
    { "mouthLowerDownLeft", 28 }, { "mouthLowerDownRight",29 },
    { "cheekPuffLeft",      30 }, { "cheekPuffRight",     31 },
    { "cheekSquintLeft",    32 }, { "cheekSquintRight",   33 },
    { "noseSneerLeft",      34 }, { "noseSneerRight",     35 },
  };

  // 301 bytes 고정 직렬화
  // 구조: HeadRot(12) + HeadPos(12) + LArm(12) + RArm(12) + Timestamp(4) + BlendShapes(208) + LFingers(20) + RFingers(20) + IsTracking(1) = 301
  public byte[] Serialize()
  {
    var buf = new byte[301];
    int o   = 0;
    WriteV3(buf, ref o, HeadRotation);
    WriteV3(buf, ref o, HeadPosition);
    WriteV3(buf, ref o, LeftArmRotation);
    WriteV3(buf, ref o, RightArmRotation);
    WriteF(buf, ref o, Timestamp);
    
    BlendShapeValues ??= new float[52];
    foreach (float v in BlendShapeValues) WriteF(buf, ref o, v);
    
    LeftFingerCurls ??= new float[5];
    foreach (float v in LeftFingerCurls) WriteF(buf, ref o, v);
    
    RightFingerCurls ??= new float[5];
    foreach (float v in RightFingerCurls) WriteF(buf, ref o, v);
    
    buf[o++] = IsTracking;
    return buf;
  }

  public static TrackingPacket Deserialize(byte[] buf)
  {
    int o = 0;
    var p = new TrackingPacket
    {
      HeadRotation     = ReadV3(buf, ref o),
      HeadPosition     = ReadV3(buf, ref o),
      LeftArmRotation  = ReadV3(buf, ref o),
      RightArmRotation = ReadV3(buf, ref o),
      Timestamp        = ReadF(buf, ref o),
      BlendShapeValues = new float[52],
    };
    for (int i = 0; i < 52; i++) p.BlendShapeValues[i] = ReadF(buf, ref o);
    
    p.LeftFingerCurls = new float[5];
    for (int i = 0; i < 5; i++) p.LeftFingerCurls[i] = ReadF(buf, ref o);
    
    p.RightFingerCurls = new float[5];
    for (int i = 0; i < 5; i++) p.RightFingerCurls[i] = ReadF(buf, ref o);
    
    p.IsTracking = o < buf.Length ? buf[o++] : (byte)0;
    return p;
  }

  private static void WriteF(byte[] b, ref int o, float v)
  { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4); o += 4; }
  private static void WriteV3(byte[] b, ref int o, Vector3 v)
  { WriteF(b, ref o, v.x); WriteF(b, ref o, v.y); WriteF(b, ref o, v.z); }
  private static float ReadF(byte[] b, ref int o)
  { float v = BitConverter.ToSingle(b, o); o += 4; return v; }
  private static Vector3 ReadV3(byte[] b, ref int o)
    => new(ReadF(b, ref o), ReadF(b, ref o), ReadF(b, ref o));
}
