// BlazeHandDetector.cs
using System;
using Unity.Mathematics;
using Unity.InferenceEngine;
using UnityEngine;

public class BlazeHandDetector : MonoBehaviour
{
    [SerializeField] private ModelAsset handDetectorAsset;
    [SerializeField] private TextAsset  handAnchorsCSV;

    private Worker        _worker;
    private Tensor<float> _inputTensor;
    private float[,]      _anchors;

    private const int DETECTOR_SIZE = 192;
    private const int NUM_ANCHORS   = 2016;
    private const float SCORE_THRESH = 0.5f;

    public struct HandDetection
    {
        public bool     IsValid;
        public float2   CenterTensorSpace;
        public float    SizeTensorSpace;
        public float    Rotation;
        public float2x3 DetectorMatrix;
    }

    void Start()
    {
        if (handDetectorAsset == null) { Debug.LogWarning("[BlazeHandDetector] ModelAsset is null"); return; }

        var baseModel = ModelLoader.Load(handDetectorAsset);
        var graph     = new FunctionalGraph();
        var input     = graph.AddInput(baseModel, 0);
        var outputs   = Functional.Forward(baseModel, input);
        var rawBoxes  = outputs[0];
        var rawScores = outputs[1];

        var argmaxResult = BlazeUtils.ArgMaxFiltering(rawBoxes, rawScores);
        var argmaxModel  = graph.Compile(argmaxResult.Item1, argmaxResult.Item2, argmaxResult.Item3);

        _worker      = new Worker(argmaxModel, BackendType.GPUCompute);
        _inputTensor = new Tensor<float>(new TensorShape(1, DETECTOR_SIZE, DETECTOR_SIZE, 3));

        _anchors = handAnchorsCSV != null ? BlazeUtils.LoadAnchors(handAnchorsCSV.text, NUM_ANCHORS) : BlazeUtils.LoadAnchors("Data/hand_anchors");
    }

    public async Awaitable<HandDetection[]> DetectMultiAsync(WebCamTexture texture)
    {
        var size  = Mathf.Max(texture.width, texture.height);
        var scale = size / (float)DETECTOR_SIZE;
        var M = BlazeUtils.mul(
            BlazeUtils.TranslationMatrix(0.5f * (new Vector2(texture.width, texture.height) + new Vector2(-size, size))),
            BlazeUtils.ScaleMatrix(new Vector2(scale, -scale))
        );
        return await RunWorkerAsync(texture, M);
    }

    public async Awaitable<HandDetection[]> DetectROIAsync(WebCamTexture texture, float2 center, float size)
    {
        var scale = size / (float)DETECTOR_SIZE;
        var M = BlazeUtils.mul(
            BlazeUtils.TranslationMatrix(center - new float2(size * 0.5f)),
            BlazeUtils.ScaleMatrix(new Vector2(scale, -scale))
        );
        return await RunWorkerAsync(texture, M);
    }

    private async Awaitable<HandDetection[]> RunWorkerAsync(WebCamTexture texture, float2x3 M)
    {
        if (_worker == null || _inputTensor == null || _anchors == null) return null;

        BlazeUtils.SampleImageAffine(texture, _inputTensor, M);
        _worker.Schedule(_inputTensor);

        var t0 = _worker.PeekOutput(0) as Tensor<int>;
        var t1 = _worker.PeekOutput(1) as Tensor<float>;
        var t2 = _worker.PeekOutput(2) as Tensor<float>;
        if (t0 == null || t1 == null || t2 == null) return null;

        using var outputIdx   = await t0.ReadbackAndCloneAsync();
        using var outputScore = await t1.ReadbackAndCloneAsync();
        using var outputBox   = await t2.ReadbackAndCloneAsync();

        if (outputIdx == null || outputScore == null || outputBox == null || outputScore.shape.length == 0) return null;

        var results = new System.Collections.Generic.List<HandDetection>();
        for (int i = 0; i < outputIdx.shape.length && i < 2; i++)
        {
            if (outputScore[i] < SCORE_THRESH) break;
            int idx = outputIdx[i];
            if (idx < 0 || idx >= NUM_ANCHORS) continue;

            var anchorPos = DETECTOR_SIZE * new float2(_anchors[idx, 0], _anchors[idx, 1]);
            var boxCentre = anchorPos + new float2(outputBox[0, i, 0], outputBox[0, i, 1]);
            var boxSize   = math.max(outputBox[0, i, 2], outputBox[0, i, 3]);

            var kp0 = anchorPos + new float2(outputBox[0, i, 4], outputBox[0, i, 5]);
            var kp2 = anchorPos + new float2(outputBox[0, i, 8], outputBox[0, i, 9]);
            
            var delta = kp2 - kp0;
            var up    = delta / (math.length(delta) + 0.0001f);
            var rotation = 0.5f * Mathf.PI - math.atan2(delta.y, delta.x);

            var center  = boxCentre + 0.5f * boxSize * up;
            var sizeOut = boxSize * 2.6f;

            results.Add(new HandDetection {
                IsValid = true, CenterTensorSpace = center, SizeTensorSpace = sizeOut,
                Rotation = rotation, DetectorMatrix = M
            });
        }
        return results.Count > 0 ? results.ToArray() : null;
    }

    public async Awaitable<HandDetection> DetectAsync(WebCamTexture texture)
    {
        var all = await DetectMultiAsync(texture);
        return (all != null && all.Length > 0) ? all[0] : new HandDetection { IsValid = false };
    }

    void OnDestroy() { _worker?.Dispose(); _inputTensor?.Dispose(); }
}
