# Third-Party Notices

Virtual Avatar Studio uses third-party software, models, and fonts. Each component remains subject to its own license and attribution requirements. Inclusion does not imply endorsement by the component's authors or licensors.

## Runtime components

| Component | Version | License | Source |
| --- | --- | --- | --- |
| Unity / Sentis | Unity 6000.3.10f1 / Sentis 2.5.0 | Unity terms and package notices | [Unity](https://unity.com/), [Sentis documentation](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.5/manual/index.html) |
| UniVRM / UniGLTF | 0.131.0 | MIT | [vrm-c/UniVRM](https://github.com/vrm-c/UniVRM) |
| UniTask | 2.5.10 | MIT | [Cysharp/UniTask](https://github.com/Cysharp/UniTask) |
| KlakSpout | 2.0.6 | Unlicense | [keijiro/KlakSpout](https://github.com/keijiro/KlakSpout) |
| Noto Sans KR Regular | Local font asset | SIL Open Font License 1.1 | [notofonts/noto-cjk](https://github.com/notofonts/noto-cjk) |
| Newtonsoft.Json | Unity package dependency | MIT | [JamesNK/Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) |

The Windows distribution contains copies of the relevant license texts and Unity package third-party notices in its `ThirdPartyNotices` directory.

## Blaze inference models

The following ONNX files used by the Windows distribution were obtained from Unity Technologies' Sentis Blaze Detection Sample:

- `blaze_face_short_range.onnx`
- `hand_detector.onnx`
- `hand_landmarks_detector.onnx`
- `pose_detection.onnx`
- `pose_landmarks_detector_full.onnx`

Unity's sample documentation identifies the Blaze models as Google Research / MediaPipe models and explains that the supplied pose models were converted from TFLite to ONNX. Review the source terms before reuse or redistribution:

- [Unity Technologies Sentis Samples](https://github.com/Unity-Technologies/sentis-samples)
- [Blaze Detection Sample](https://github.com/Unity-Technologies/sentis-samples/tree/main/BlazeDetectionSample)
- [Sentis Samples License](https://github.com/Unity-Technologies/sentis-samples/blob/main/License.md)
- [Google MediaPipe](https://github.com/google-ai-edge/mediapipe)
- [MediaPipe Apache License 2.0](https://github.com/google-ai-edge/mediapipe/blob/master/LICENSE)

## VRM files

Virtual Avatar Studio does not include or redistribute a default VRM avatar. Users are responsible for reviewing and complying with the license and usage conditions embedded in each VRM file they load.

The README links to Seed-san only as an external example supplied by the VRM Consortium specification repository. Seed-san is not included in this repository or in the Windows distribution.
