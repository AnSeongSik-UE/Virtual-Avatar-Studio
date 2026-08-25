// BlazePoseDetector.cs
using System;
using Unity.Mathematics;
using Unity.InferenceEngine;
using UnityEngine;

public class BlazePoseDetector : MonoBehaviour
{
    [SerializeField] private ModelAsset poseDetectorAsset;
    [SerializeField] private TextAsset  poseAnchorsCSV;

    private Worker        _worker;
    private Tensor<float> _inputTensor;
    private float[,]      _anchors;
    private bool          _disposed;

    private const int DETECTOR_SIZE = 224;
    private const int NUM_ANCHORS   = 2254;
    private const float SCORE_THRESH = 0.5f;

    public struct PoseDetection
    {
        public bool     IsValid;
        public float    Score;
        public float2   Keypoint1;
        public float2   Keypoint2;
        public float2x3 DetectorMatrix;
    }

    void Start()
    {
        if (poseDetectorAsset == null) { Debug.LogWarning("[BlazePoseDetector] ModelAsset is null"); return; }

        var baseModel = ModelLoader.Load(poseDetectorAsset);
        var graph     = new FunctionalGraph();
        var input     = graph.AddInput(baseModel, 0);
        var outputs   = Functional.Forward(baseModel, input);
        var rawBoxes  = outputs[0];
        var rawScores = outputs[1];

        var argmaxResult = BlazeUtils.ArgMaxFiltering(rawBoxes, rawScores);
        var argmaxModel  = graph.Compile(argmaxResult.Item1, argmaxResult.Item2, argmaxResult.Item3);

        _worker      = new Worker(argmaxModel, BackendType.GPUCompute);
        _inputTensor = new Tensor<float>(new TensorShape(1, DETECTOR_SIZE, DETECTOR_SIZE, 3));

        _anchors = poseAnchorsCSV != null ? BlazeUtils.LoadAnchors(poseAnchorsCSV.text, NUM_ANCHORS) : BlazeUtils.LoadAnchors("Data/pose_anchors");
    }

    public async Awaitable<PoseDetection> DetectAsync(WebCamTexture texture)
    {
        if (_worker == null || _inputTensor == null || _anchors == null)
            return new PoseDetection { IsValid = false };

        var size  = Mathf.Max(texture.width, texture.height);
        var scale = size / (float)DETECTOR_SIZE;
        var M = BlazeUtils.mul(
            BlazeUtils.TranslationMatrix(0.5f * (new Vector2(texture.width, texture.height) + new Vector2(-size, size))),
            BlazeUtils.ScaleMatrix(new Vector2(scale, -scale))
        );
        BlazeUtils.SampleImageAffine(texture, _inputTensor, M);
        _worker.Schedule(_inputTensor);

        var t0 = _worker.PeekOutput(0) as Tensor<int>;
        var t1 = _worker.PeekOutput(1) as Tensor<float>;
        var t2 = _worker.PeekOutput(2) as Tensor<float>;

        if (t0 == null || t1 == null || t2 == null) return new PoseDetection { IsValid = false };

        try
        {
            using var outputIdx   = await t0.ReadbackAndCloneAsync();
            using var outputScore = await t1.ReadbackAndCloneAsync();
            using var outputBox   = await t2.ReadbackAndCloneAsync();

            if (outputIdx == null || outputScore == null || outputBox == null || outputScore.shape.length == 0)
                return new PoseDetection { IsValid = false };

            if (outputScore[0] < SCORE_THRESH) return new PoseDetection { IsValid = false };

            int idx = outputIdx[0];
            if (idx < 0 || idx >= _anchors.GetLength(0)) return new PoseDetection { IsValid = false };

            var anchorPosition = DETECTOR_SIZE * new float2(_anchors[idx, 0], _anchors[idx, 1]);

            return new PoseDetection
            {
                IsValid        = true,
                Score          = outputScore[0],
                Keypoint1      = anchorPosition + new float2(outputBox[0, 0, 4], outputBox[0, 0, 5]),
                Keypoint2      = anchorPosition + new float2(outputBox[0, 0, 6], outputBox[0, 0, 7]),
                DetectorMatrix = M,
            };
        }
        catch (NullReferenceException) when (_disposed || !Application.isPlaying)
        {
            return new PoseDetection { IsValid = false };
        }
    }

    void OnDestroy()
    {
        _disposed = true;
        _worker?.Dispose();
        _inputTensor?.Dispose();
    }
}
