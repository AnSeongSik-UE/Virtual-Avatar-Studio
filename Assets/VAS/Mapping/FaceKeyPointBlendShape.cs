// FaceKeyPointBlendShape.cs
// BlazeFace 의 6 keypoints 만으로 BlendShape 근사 계산
// 랜드마크 468점 불필요 — 복잡도 낮으나 정밀도는 낮음
using UnityEngine;

public static class FaceKeyPointBlendShape
{
  // BlazeFace selectedBoxes 포맷 (정규화 좌표):
  //   [0..3] : cx, cy, w, h  (bbox)
  //   [4..5] : 오른쪽 눈  (x, y)
  //   [6..7] : 왼쪽 눈   (x, y)
  //   [8..9] : 코 끝     (x, y)
  //   [10..11]: 입 중심  (x, y)
  //   [12..13]: 오른쪽 귀 (x, y)
  //   [14..15]: 왼쪽 귀  (x, y)

  public struct Result
  {
    public float BlinkLeft;    // 0=열림, 1=감김
    public float BlinkRight;
    public float MouthOpen;    // 미지원 — 헤드 pose로 대체
  }

  // box: BlazeFace selectedBoxes[0, i]  (length 16, 정규화 좌표)
  public static Result Calculate(float[] box)
  {
    float bboxH   = box[3]; // 정규화 bbox 높이
    float eyeRY   = box[5]; // 오른쪽 눈 Y
    float eyeLY   = box[7]; // 왼쪽 눈 Y
    float centerY = box[1]; // bbox 중심 Y

    // 눈이 중심에서 멀수록 눈이 열림 (bboxH 대비 정규화)
    float blinkR = 1f - Mathf.Clamp01(Mathf.Abs(eyeRY - centerY) / (bboxH * 0.3f));
    float blinkL = 1f - Mathf.Clamp01(Mathf.Abs(eyeLY - centerY) / (bboxH * 0.3f));

    return new Result { BlinkLeft = blinkL, BlinkRight = blinkR, MouthOpen = 0f };
  }
}
