# Unity_Virtual Avatar Studio

Unity 6와 Unity Inference Engine을 사용해 PC 웹캠 영상에서 얼굴과 상체 포즈를 추론하는 신입 포트폴리오 프로젝트입니다.

## 현재 기준점

- Unity 6000.3.10f1
- Universal Render Pipeline 17.3.0
- Unity Inference Engine 2.5.0
- BlazeFace 및 BlazePose ONNX 추론
- GPUCompute 백엔드
- PC 웹캠 입력과 추적 결과 디버그 표시
- 원본 웹캠 영상 출력 차단과 익명 상체 뼈대 미리보기
- KlakSpout 카메라 캡처 기반 OBS 아바타 전용 출력
- 웹캠 장치·해상도 선택 및 미러 설정 저장
- 추론 중 안전한 웹캠 전환
- MINI.vrm 런타임 로드 및 머리·양팔 직접 구동
- Windows 실행 파일과 에디터의 StreamingAssets VRM 경로 통일
- 중립 자세 캘리브레이션과 추적 손실 시 기본 자세 복귀

실행 중 `F1` 키로 웹캠 설정 패널을 표시하거나 숨길 수 있습니다. 선택한 장치, 해상도와 미러 설정은 `PlayerPrefs`에 저장됩니다.

아바타가 로드되고 포즈가 검출되면 `Calibrate Neutral Pose (3 sec)` 또는 `C` 키로 카운트다운을 시작합니다. 얼굴·양쪽 어깨·양쪽 팔꿈치만 화면 안에 둔 채 팔을 편안하게 내리면 마지막 자세가 중립 기준으로 저장됩니다. 손목과 손은 화면에 들어오지 않아도 됩니다.

### Arm Mapping Config

설정 패널 아래의 `Arm Mapping Config`에서 아바타마다 다른 팔 본 축과 웹캠 좌우 방향을 실행 중 조정할 수 있습니다.

1. `Enable bone-only test mode`를 켜고 `Neutral`, `T Pose`, `Both Up`을 눌러 웹캠과 무관하게 VRM 팔 방향을 확인합니다.
2. 필요하면 `Left/Right bone Z`를 움직여 정상적인 중립 자세와 올린 자세가 되는 부호를 찾습니다.
3. 테스트 모드를 끄고 웹캠을 시작한 뒤 중립 자세를 다시 캘리브레이션합니다.
4. 반대쪽 팔이 움직이면 `Swap left / right input`, 움직임 방향이 반대면 해당 `Invert`를 켭니다.
5. `Left/Right gain`은 움직임 크기, `Max delta`는 최대 가동 범위를 조절합니다.
6. `Input smoothing`은 클수록 입력 반응이 빨라지고 작을수록 부드러워집니다. 기본값은 0.4입니다.
7. `Max input jump`는 한 추론 프레임에서 허용할 방향 변화입니다. 관절이 순간적으로 튀면 낮추고 빠른 동작이 무시되면 높입니다. 기본값은 120도입니다.

설정 변경은 0.5초 뒤 PlayerPrefs에 자동 저장됩니다. 테스트 모드는 다음 실행에서 안전하게 꺼진 상태로 시작합니다. 기본값으로 돌아가려면 `Reset Arm Config`를 누릅니다.

팔 방향은 각도 숫자가 아닌 어깨에서 팔꿈치로 향하는 2D 단위 벡터 상태로 보간합니다. 따라서 같은 방향인 -180도와 +180도 사이에서 입력 표시가 바뀌더라도 아바타 팔은 반대편으로 회전하지 않습니다. 포즈를 잃었다가 다시 찾으면 필터를 현재 방향으로 초기화하며, 길이가 너무 짧거나 갑자기 튄 관절값은 직전 정상 방향을 유지합니다.

### Debug / Arm Validation

`Arm Validation`은 Calibration을 대신하지 않는 문제 진단용 기능입니다. 먼저 Calibration을 완료하고 Bone Test를 끈 다음, 접힌 `Debug / Test` 영역에서 필요할 때만 실행합니다.

검사는 좁은 공간에서도 손목을 화면에 넣지 않고 실행할 수 있도록 12초 동안 다음 순서로 진행됩니다.

1. 3초 동안 편안한 중립 자세
2. 왼쪽 팔꿈치 들기
3. 다시 중립 자세
4. 오른쪽 팔꿈치 들기

검사가 끝나면 좌우 반응 각도, -180/+180도 경계 통과 안정성, 출력 급변, 거부된 이상 입력 수와 포즈 유실 시간을 기준으로 `PASS` 또는 `CHECK`를 표시합니다. 최근 결과는 PlayerPrefs에 저장됩니다.

사용자 화면의 웹캠 설정, 아바타 상태, 캘리브레이션, 팔 매핑, 추적 상태와 검증 결과는 모두 한글로 표시됩니다. 프로젝트에 포함한 `NotoSansKR-Regular.otf` 영구 Font 에셋을 `Resources`에서 한 번 불러와 16px 전용 IMGUI 스킨에 적용하며, 각 OnGUI 종료 시 Unity 원본 스킨을 복원합니다. 실행 중 생성·제거되는 OS 동적 폰트를 사용하지 않으므로 운영체제 폰트 설치 여부에 의존하지 않고 스크립트 재로드 후 잘못된 폰트 참조도 남기지 않습니다. 한글 글리프 누락 가능성이 있는 기존 TextMeshPro 추적 상태는 비활성화하고 동일 내용을 좌측 하단 IMGUI 상태창에 표시합니다. 장치명·파일명·오류 원문과 FPS 등의 기술 단위는 진단을 위해 유지합니다.

팔 동작 검증을 시작하면 설정 패널을 F1로 숨겨도 메인 화면 상단 중앙에 단계별 한글 안내와 진행 막대가 크게 표시됩니다. 글자 크기는 화면 높이에 따라 28~52px 범위로 조절되며, 완료 결과는 5초 동안 `팔 동작 검증 통과` 또는 `팔 동작 검증: 확인 필요`로 표시됩니다.

검증 진행 안내와 완료 결과는 일반 설정 및 추적 상태보다 마지막에 렌더링하고 전용 최상단 GUI 깊이를 사용하므로 다른 런타임 IMGUI 요소에 가려지지 않습니다.

### 개인정보 보호 출력

원본 `WebCamTexture`는 얼굴·포즈 AI 추론 입력으로만 사용하고 `RawImage`나 다른 화면 요소에는 연결하지 않습니다. 우측 상단 미리보기 영역에는 불투명한 어두운 배경 위에 얼굴 랜드마크와 양쪽 어깨·팔꿈치 상체 뼈대만 표시합니다. 웹캠 텍스처가 실수로 화면 요소에 직접 연결되면 매 프레임 보호 검사가 이를 감지하여 즉시 불투명 화면으로 교체합니다.

원본 카메라 프레임을 PNG/JPG로 변환하거나 파일에 저장하는 경로는 없으며, 선택적인 UDP 출력도 영상이 아닌 머리·팔 회전과 표정 수치만 전송하고 기본 비활성화 상태입니다. F1 설정창 표시 여부와 관계없이 원본 영상은 출력하지 않습니다. 카메라를 중지하면 익명 뼈대와 추적점도 즉시 지웁니다.

### OBS 아바타 전용 출력

Main Camera에는 KlakSpout 2.0.6의 `SpoutSender`를 연결하고 송신자 이름을 `VAS_Avatar`로 고정합니다. 캡처 방식은 `Camera`, 알파 유지는 끈 상태입니다. 기존 Main Camera가 이미 렌더링한 결과를 공유하므로 두 번째 카메라나 장면 중복 렌더링을 만들지 않습니다.

프로젝트의 Graphics Settings에는 `UniversalRP`를 연결하고, 기존 Renderer2D는 기본 인덱스 0으로 보존한 채 Main Camera만 인덱스 1의 `VASUniversalRenderer`를 사용합니다. Built-in 파이프라인과 URP Renderer2D는 이 구성에서 KlakSpout의 카메라 캡처 동작을 실행하지 않아 송신자 이름과 해상도만 보이는 검은 프레임이 발생할 수 있으므로, 방송 카메라는 CapturePass를 실행하는 Universal Renderer로 고정합니다.

Spout에는 Main Camera가 렌더링한 아바타와 배경만 전달됩니다. 웹캠 설정·상태·캘리브레이션·팔 검증은 `OnGUI`, 익명 추적 뼈대는 `Screen Space - Overlay` Canvas이므로 카메라 출력에 포함되지 않습니다. 원본 웹캠 텍스처는 계속 AI 추론 입력에만 사용됩니다. 송출 초기화에 실패하더라도 개인정보 보호를 우회하는 `Game View` 캡처로 자동 전환하지 않습니다.

OBS Studio에는 Windows 64비트용 `Spout2 Plugin for OBS`를 별도로 설치해야 합니다. 설치 후 OBS 소스에서 `Spout 2 Capture`를 추가하고 송신자 `VAS_Avatar`를 선택합니다. 노트북처럼 내장 GPU와 외장 GPU가 함께 있는 환경에서는 Unity Player와 OBS를 Windows 그래픽 설정에서 같은 GPU로 지정합니다. KlakSpout은 Windows Direct3D 11/12만 지원합니다.

Editor Game View에서는 현재 Game View 크기가 송출 해상도가 됩니다. Windows Player는 프로젝트 기본값인 1920x1080 고정 창을 사용합니다. 실기에서는 OBS 화면을 기준으로 다음을 확인합니다.

1. 설정창과 추적 뼈대를 모두 표시한 상태에서도 OBS에는 아바타와 배경만 나오는지 확인합니다.
2. 카메라 시작·중지, 미러 전환, 캘리브레이션과 팔 검증 중에도 웹캠 원본과 안내 UI가 한 프레임도 나타나지 않는지 확인합니다.
3. Unity보다 OBS를 먼저 또는 나중에 실행해도 `VAS_Avatar`가 다시 연결되는지 확인합니다.
4. 송출 전후 60초 평균 렌더·추론 통계와 OBS의 렌더링 지연 및 누락 프레임을 비교합니다.
5. 방송 중에는 Player를 최소화하지 않고 뒤로 보내며, 포커스 이동 후에도 추론과 아바타 갱신이 계속되는지 확인합니다.

### 경량 성능 통계

좌측 하단 상태창은 Game 렌더, AI 추론 처리시간과 실제 추론 완료 주기를 분리합니다. 렌더는 순간·EMA 평균 FPS와 ms, 추론 처리는 순간·평균 ms, 추론 갱신은 연속 완료 시각 간격에서 계산한 실제 순간·평균 FPS와 ms를 표시합니다. 카메라 변경 후 렌더 30프레임과 추론 5회를 워밍업으로 제외하며, 평균 계산에는 배열·큐·LINQ 없이 통계별 고정 float와 단순 곱셈·덧셈만 사용합니다. 상태 문자열은 초당 4회만 갱신합니다.

강제 VSync 변경과 Application.targetFrameRate 제한은 적용하지 않으며 Unity 프로젝트 및 실행 환경의 기본 렌더 설정을 사용합니다. 상태창 높이는 GUIStyle.CalcHeight로 실제 한글 줄바꿈 높이를 계산해 첫 줄이나 마지막 줄이 잘리지 않도록 구성합니다. 컴포넌트 비활성화 시 임시 GUI 스킨만 제거하고 프로젝트의 영구 Font 에셋은 Unity 리소스 수명주기에 맡겨 `UnityEditor.ScriptReloadProperties`가 삭제된 폰트를 복원하지 않도록 합니다.

단일 순간값만으로 성능 상태를 판정하지 않으며, 일정 시간 유지된 평균 렌더 및 추론 수치를 기준으로 후속 최적화 여부를 결정합니다.

### 로컬 모델 준비와 재배포 범위

공개 저장소에는 제3자 모델 바이너리를 포함하지 않습니다. ONNX 파일은 Unity Technologies의 Sentis Blaze Detection Sample에서 다음 5개를 받아 `Assets/Models`에 배치합니다.

- `blaze_face_short_range.onnx`
- `hand_detector.onnx`
- `hand_landmarks_detector.onnx`
- `pose_detection.onnx`
- `pose_landmarks_detector_full.onnx`

아바타는 본인이 사용·배포할 권리를 가진 VRM 파일을 `Assets/StreamingAssets/Models/MINI.vrm`에 배치합니다. 현재 로컬 테스트에 사용한 VRM은 내부 메타데이터가 재배포 금지이므로 Git 추적 대상에서 제외했습니다. ONNX와 VRM의 `.meta` 파일은 씬과 에셋 참조를 유지하기 위해 저장소에 포함합니다.

### Windows 방송 실행 기준

`MINI.vrm`은 `Assets/StreamingAssets/Models`에 저장하고 에디터와 Windows 실행 파일 모두 `Application.streamingAssetsPath`를 기준으로 불러옵니다. 빌드 후에는 실행 파일의 `_Data/StreamingAssets/Models/MINI.vrm`으로 함께 배포되므로 에디터에서만 아바타가 표시되는 경로 차이를 방지합니다.

Player Settings의 백그라운드 실행을 활성화하여 OBS나 다른 운영 도구로 포커스를 옮겨도 웹캠 추론과 아바타 갱신이 계속되도록 구성합니다. Windows 실기에서는 아바타 로드, 카메라 수동 시작, 익명 뼈대 표시, 캘리브레이션과 OBS 전환 후 동작 지속을 확인합니다.

## 1차 구현 범위

1. 웹캠 장치 및 해상도 선택
2. 얼굴·포즈 추론 주기 제어
3. VRM 머리·상체·팔 구동
4. 캘리브레이션과 EMA/Quaternion 보간
5. 설정 저장과 성능 모니터링
6. Windows 실행 빌드

손 추적과 네트워크 출력은 기본적으로 비활성화하며 핵심 기능 완성 후 선택적으로 다룹니다.

## 폴더

- `Assets/VAS/Tracking`: Sentis 추론과 웹캠 파이프라인
- `Assets/VAS/Mapping`: 추적 결과 변환
- `Assets/VAS/Network`: 선택적 UDP 통신 코드
- `Assets/VAS/Resources/Fonts`: 한글 런타임 UI 폰트와 배포 라이선스
- `Assets/StreamingAssets/Models`: Windows 실행 파일에 원본 그대로 포함되는 VRM 모델
- `Assets/PC`: PC 수신 및 VRM 매핑 코드
- `Assets/Models`: ONNX 및 VRM 모델
- `Assets/Shared`: 공용 데이터와 필터

## 출처

Blaze 추론 구조는 Unity Technologies의 공식 Sentis 샘플을 기준으로 검토합니다.

- https://github.com/Unity-Technologies/sentis-samples

아바타 전용 OBS 출력에는 KlakSpout 2.0.6과 OBS Spout2 플러그인을 사용합니다.

- https://github.com/keijiro/KlakSpout
- https://github.com/Off-World-Live/obs-spout2-plugin

한글 런타임 UI에는 Noto 프로젝트의 `Noto Sans KR Regular`를 사용하며 SIL Open Font License 1.1 원문을 폰트와 함께 포함합니다.

- https://github.com/notofonts/noto-cjk
