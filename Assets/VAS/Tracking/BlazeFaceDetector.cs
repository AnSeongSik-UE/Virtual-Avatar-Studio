// BlazeFaceDetector.cs
using System;
using Unity.Mathematics;
using Unity.InferenceEngine;
using UnityEngine;

public class BlazeFaceDetector : MonoBehaviour
{
    [Header("Model Settings")]
    [SerializeField] private ModelAsset blazeFaceModelAsset;
    [SerializeField] private TextAsset  faceAnchorsCSV;

    [Header("Detection Parameters")]
    [SerializeField, Range(0f, 1f)] private float iouThreshold = 0.3f;
    [SerializeField, Range(0f, 1f)] private float scoreThreshold = 0.5f;

    private Worker        _worker;
    private Tensor<float> _inputTensor;
    private float[]       _anchors1D; // 메모리 접근 최적화용 1차원 배열
    private float[]       _reusableRawValues = new float[16]; // GC 최적화
    private bool          _disposed;

    private const int INPUT_SIZE    = 128;
    private const int NUM_ANCHORS   = 896;

    public struct FaceDetection
    {
        public bool     IsValid;
        public Rect     BoundingBox;
        public float    Score;
        public float2   AnchorPosition;
        public float[]  RawBoxes;
        public float2x3 DetectorMatrix;
    }

    void Start()
    {
        if (blazeFaceModelAsset == null) { Debug.LogWarning("[BlazeFaceDetector] ModelAsset is null"); return; }

        var baseModel = ModelLoader.Load(blazeFaceModelAsset);
        var graph     = new FunctionalGraph();
        var input     = graph.AddInput(baseModel, 0);
        var outputs   = Functional.Forward(baseModel, 2f * input - 1f); // 혹은 지난번 보정처럼 input 그대로 사용을 원하시면 모델 특성에 맞게 수정
        var rawBoxes  = outputs[0];
        var rawScores = outputs[1];

        var anchorsData = new float[NUM_ANCHORS * 4];
        var anchorsCSV  = faceAnchorsCSV != null ? BlazeUtils.LoadAnchors(faceAnchorsCSV.text, NUM_ANCHORS) : BlazeUtils.LoadAnchors("Data/face_anchors");
        
        // 1차원 배열 변환
        _anchors1D = new float[NUM_ANCHORS * 2];
        for (int i = 0; i < NUM_ANCHORS; i++)
        {
            _anchors1D[i * 2] = anchorsCSV[i, 0];
            _anchors1D[i * 2 + 1] = anchorsCSV[i, 1];
        }

        Buffer.BlockCopy(anchorsCSV, 0, anchorsData, 0, anchorsData.Length * sizeof(float));

        var anchorsTensor = Functional.Constant(new TensorShape(NUM_ANCHORS, 4), anchorsData);
        var nmsResult     = BlazeUtils.NMSFiltering(rawBoxes, rawScores, anchorsTensor, INPUT_SIZE, iouThreshold, scoreThreshold);
        var nmsModel      = graph.Compile(nmsResult.Item1, nmsResult.Item2, nmsResult.Item3);

        _worker      = new Worker(nmsModel, BackendType.GPUCompute);
        _inputTensor = new Tensor<float>(new TensorShape(1, INPUT_SIZE, INPUT_SIZE, 3));
    }

    public async Awaitable<FaceDetection> DetectAsync(WebCamTexture texture)
    {
        // Early Return: 카메라 텍스처 무결성 검증 실패 시 즉시 종료 (오류 방어 로직)
        if (texture == null || texture.width < 16 || texture.height < 16 || _worker == null || _inputTensor == null || _anchors1D == null)
            return new FaceDetection { IsValid = false };

        var size  = Mathf.Max(texture.width, texture.height);
        var scale = size / (float)INPUT_SIZE;
        
        // 치명적 버그 수정: Sentis 공식 샘플 기준, X축 반전( -scale )이 들어가면 
        // 화면과 모델의 좌우 인식이 달라져 얼굴 박스가 0개가 나오는 현상 해결.
        // new Vector2(-scale, scale) -> new Vector2(scale, -scale) 
        var M = BlazeUtils.mul(
            BlazeUtils.TranslationMatrix(0.5f * (new Vector2(texture.width, texture.height) + new Vector2(-size, size))),
            BlazeUtils.ScaleMatrix(new Vector2(scale, -scale)) 
        );

        BlazeUtils.SampleImageAffine(texture, _inputTensor, M);
        _worker.Schedule(_inputTensor);

        var indicesAw = (_worker.PeekOutput(0) as Tensor<int>)?.ReadbackAndCloneAsync();
        var scoresAw  = (_worker.PeekOutput(1) as Tensor<float>)?.ReadbackAndCloneAsync();
        var boxesAw   = (_worker.PeekOutput(2) as Tensor<float>)?.ReadbackAndCloneAsync();

        if (indicesAw == null || scoresAw == null || boxesAw == null) return new FaceDetection { IsValid = false };

        // 텐서 자원 누수(Leak) 방어 로직: try-finally 블록을 통한 메모리 할당 영구해제 보장
        Tensor<int> outputIndices = null;
        Tensor<float> outputScores = null;
        Tensor<float> outputBoxes = null;

        try
        {
            outputIndices = await indicesAw;
            outputScores  = await scoresAw;
            outputBoxes   = await boxesAw;

            if (outputIndices == null || outputScores == null || outputBoxes == null || outputIndices.shape.length == 0)
                return new FaceDetection { IsValid = false };

            int anchorIdx = outputIndices[0];
            if (anchorIdx < 0 || anchorIdx >= NUM_ANCHORS) return new FaceDetection { IsValid = false };

            var anchorPosition = INPUT_SIZE * new float2(_anchors1D[anchorIdx * 2], _anchors1D[anchorIdx * 2 + 1]);

            float cx = (anchorPosition.x + outputBoxes[0, 0, 0]) / INPUT_SIZE;
            float cy = (anchorPosition.y + outputBoxes[0, 0, 1]) / INPUT_SIZE;
            float w  = outputBoxes[0, 0, 2] / INPUT_SIZE;
            float h  = outputBoxes[0, 0, 3] / INPUT_SIZE;

            // GC 메모리 할당 최적화: 클래스 레벨에서 재사용되는 _reusableRawValues 배열 사용
            for (int i = 0; i < 16; i++) _reusableRawValues[i] = outputBoxes[0, 0, i];

            return new FaceDetection
            {
                IsValid        = true,
                BoundingBox    = new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h),
                Score          = outputScores[0, 0, 0],
                AnchorPosition = new float2(_anchors1D[anchorIdx * 2], _anchors1D[anchorIdx * 2 + 1]) * INPUT_SIZE,
                RawBoxes       = _reusableRawValues,
                DetectorMatrix = M
            };
        }
        catch (NullReferenceException) when (_disposed || !Application.isPlaying)
        {
            return new FaceDetection { IsValid = false };
        }
        finally
        {
            outputIndices?.Dispose();
            outputScores?.Dispose();
            outputBoxes?.Dispose();
        }
    }

    void OnDestroy()
    {
        _disposed = true;
        _worker?.Dispose();
        _inputTensor?.Dispose();
    }
}
