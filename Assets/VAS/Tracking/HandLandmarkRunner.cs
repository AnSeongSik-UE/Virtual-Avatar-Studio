// HandLandmarkRunner.cs
// hand_landmarks_detector.onnx 를 사용하여 21개 관절 추출
using Unity.Mathematics;

using UnityEngine;

public class HandLandmarkRunner : MonoBehaviour
{
  [SerializeField] private Unity.InferenceEngine.ModelAsset handLandmarkAsset;
  private Unity.InferenceEngine.Worker        _worker;
  private Unity.InferenceEngine.Tensor<float> _inputTensor;

  private const int LANDMARKER_SIZE = 224;

  void Start()
  {
    if (handLandmarkAsset == null) return;
    var model = Unity.InferenceEngine.ModelLoader.Load(handLandmarkAsset);
    _worker      = new Unity.InferenceEngine.Worker(model, Unity.InferenceEngine.BackendType.GPUCompute);
    _inputTensor = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, LANDMARKER_SIZE, LANDMARKER_SIZE, 3));
  }

  public async Awaitable<Vector3[]> RunAsync(WebCamTexture texture, BlazeHandDetector.HandDetection detection)
  {
    if (_worker == null || _inputTensor == null) return new Vector3[0];
    var origin2 = new float2(0.5f * LANDMARKER_SIZE, 0.5f * LANDMARKER_SIZE);
    var scale2  = detection.SizeTensorSpace / LANDMARKER_SIZE;

    // 회전 정렬 크롭 행렬 (M2): Pose와 동일한 방식의 affine 변환
    var M2 = BlazeUtils.mul(detection.DetectorMatrix, BlazeUtils.mul(
      BlazeUtils.mul(
        BlazeUtils.mul(
          BlazeUtils.TranslationMatrix(detection.CenterTensorSpace),
          BlazeUtils.ScaleMatrix(new float2(scale2, -scale2))
        ),
        BlazeUtils.RotationMatrix(detection.Rotation)
      ),
      BlazeUtils.TranslationMatrix(-origin2)
    ));

    BlazeUtils.SampleImageAffine(texture, _inputTensor, M2);
    _worker.Schedule(_inputTensor);

    var t0 = _worker.PeekOutput(0);
    if (t0 == null) return new Vector3[0];

    using var output = await t0.ReadbackAndCloneAsync() as Unity.InferenceEngine.Tensor<float>;
    if (output == null || output.shape.length < 63) return new Vector3[21];

    var joints = new Vector3[21];
    for (int i = 0; i < 21; i++)
    {
       var pos = BlazeUtils.mul(M2, new float2(output[0, i * 3], output[0, i * 3 + 1]));
       joints[i] = new Vector3(pos.x, pos.y, output[0, i * 3 + 2]);
    }
    return joints;
  }

  void OnDestroy() { _worker?.Dispose(); _inputTensor?.Dispose(); }
}
