// BlazePoseLandmarkRunner.cs
// pose_landmark_heavy.onnx 로 전신 33관절 좌표 추출
// BlazePose 33관절 인덱스: 0=코, 7=좌귀, 8=우귀, 11=좌어깨, 12=우어깨, 23=좌엉덩이, 24=우엉덩이
using Unity.Mathematics;
using Unity.InferenceEngine;
using UnityEngine;

public class BlazePoseLandmarkRunner : MonoBehaviour
{
    [SerializeField] private ModelAsset poseLandmarkAsset;

    private Worker        _worker;
    private Tensor<float> _inputTensor;
    private bool          _disposed;

    private const int LANDMARKER_SIZE = 256;

    // 공개 결과 캐시 (TrackingPipeline에서 직접 접근 가능)
    public Vector3[] Joints { get; private set; } = new Vector3[33];

    void Start()
    {
        if (poseLandmarkAsset == null) return;
        var model    = ModelLoader.Load(poseLandmarkAsset);
        _worker      = new Worker(model, BackendType.GPUCompute);
        _inputTensor = new Tensor<float>(new TensorShape(1, LANDMARKER_SIZE, LANDMARKER_SIZE, 3));
    }

    public async Awaitable<Vector3[]> RunAsync(
        WebCamTexture texture,
        BlazePoseDetector.PoseDetection detection)
    {
        if (_worker == null || _inputTensor == null) return Joints;

        // kp1(엉덩이), kp2(어깨) 두 점으로 회전 정렬 크롭 행렬 계산
        var kp1 = BlazeUtils.mul(detection.DetectorMatrix, detection.Keypoint1);
        var kp2 = BlazeUtils.mul(detection.DetectorMatrix, detection.Keypoint2);

        var   delta  = kp2 - kp1;
        float radius = 1.25f * math.length(delta);
        float theta  = math.atan2(delta.y, delta.x);

        var   origin2 = new float2(0.5f * LANDMARKER_SIZE, 0.5f * LANDMARKER_SIZE);
        float scale2  = radius / (0.5f * LANDMARKER_SIZE);

        var M2 = BlazeUtils.mul(
            BlazeUtils.mul(
                BlazeUtils.mul(
                    BlazeUtils.TranslationMatrix(kp1),
                    BlazeUtils.ScaleMatrix(new float2(scale2, -scale2))
                ),
                BlazeUtils.RotationMatrix(0.5f * Mathf.PI - theta)
            ),
            BlazeUtils.TranslationMatrix(-origin2)
        );

        BlazeUtils.SampleImageAffine(texture, _inputTensor, M2);
        _worker.Schedule(_inputTensor);

        // 출력: 주요 텐서는 output(0) = world_landmarks or pose_landmarks
        // shape: (1, 33, 5) 또는 (1, 165) — 모델에 따라 다름
        var t0 = _worker.PeekOutput(0);
        if (t0 == null) return Joints;

        try
        {
            using var output = await t0.ReadbackAndCloneAsync() as Tensor<float>;
            if (output == null) return Joints;

            var joints = new Vector3[33];
            int rank   = output.shape.rank;

            if (rank == 2)
            {
                // shape: (1, 165) — 33관절 × 5값 (x,y,z,visibility,presence)
                for (int i = 0; i < 33; i++)
                {
                    var pos = BlazeUtils.mul(M2, new float2(output[0, i * 5], output[0, i * 5 + 1]));
                    joints[i] = new Vector3(pos.x, pos.y, output[0, i * 5 + 2]);
                }
            }
            else if (rank == 3)
            {
                // shape: (1, 33, 5)
                for (int i = 0; i < 33; i++)
                {
                    var pos = BlazeUtils.mul(M2, new float2(output[0, i, 0], output[0, i, 1]));
                    joints[i] = new Vector3(pos.x, pos.y, output[0, i, 2]);
                }
            }

            Joints = joints;
            return joints;
        }
        catch (System.NullReferenceException) when (_disposed || !Application.isPlaying)
        {
            return Joints;
        }
    }

    void OnDestroy()
    {
        _disposed = true;
        _worker?.Dispose();
        _inputTensor?.Dispose();
    }
}
