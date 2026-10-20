# ADR 0003: 분석과 번역을 겹쳐 돌리고 여러 쪽의 풍선을 묶어 번역한다

- 상태: 채택
- 날짜: 2026-10-19
- 갱신: 2026-10-20 LLM 번역 모델 후보 조사와 Gemma 4 E2B 시험("추가 검토" 절)
- 관련 코드: `TranslationScheduler.RunPipelineAsync`, `TranslateQueuedAsync`, `TranslateSlotsAsync`, `AnalyzePagesAsync`, `AnalyzeBandsAsync`, `TranslationOptions.TranslateChunk`·`AnalysisLookahead`, `ModelTextRecognizer.RecognizeAsync`, `OcrRuntimePolicy.PpOcrDetectorPrefersGpu`, `TilePlanner.DedupeDetections`
- 테스트: `tests/Panely.Tests/Translation/TranslationPipelineTests.cs`, `TranslationAlgorithmTests.DetectionDedupTreatsBothTextKindsAsOneKind`

## 배경

Fast 모드로도 책 한 권 번역에 3분이 넘게 걸렸다. 세평유(일본어 세로 웹툰, 720×576 조각 298쪽) 앞 60쪽을 strip 범위로 재 보니 51.2초였다. 이 PC는 i7-13700K, RTX 4070 Ti SUPER다.

| 단계 | 호출 | 시간 |
|---|---|---|
| 번역 (M2M100 int8, CPU) | 27번, 풍선 29개 | 23.5초 |
| 글자 인식 (manga-ocr CPU + PP-OCR 줄 탐지 CPU) | 25번, 상자 62개 | 19.5초 |
| 말풍선 탐지 (RT-DETR, GPU) | 25번 | 6.8초 |

세 단계가 band마다 차례로 돌아서 각 단계 시간을 더한 값이 전체 시간이었다. 번역기에는 쪽마다 풍선 1~2개씩 들어갔다. M2M100은 문장을 따로 넣으면 748ms/문장이고, 8개씩 묶으면 255ms/문장이다(같은 146문장).

원문 146개 중 7개는 같은 문장이 두 번 붙어 있었다(예: `男の悦びを男の悦びを`). 탐지기가 같은 자리의 글자 상자를 "말풍선 안 글자"(.70)와 "말풍선 밖 글자"(.50) 두 class로 함께 냈다. 중복 제거가 같은 class끼리만 비교해서 둘 다 남았고, 둘 다 읽혀 이어 붙었다.

## 검토한 방법

| 방법 | 결과 |
|---|---|
| M2M100을 GPU로 | int8은 DirectML 세션 생성 중 네이티브 충돌. fp16은 돌지만 출력이 깨진다("나는 جيڪي de de…"). **기각** |
| manga-ocr를 GPU로 | 485ms/상자로 CPU(138ms)보다 느리다. **기각** |
| PP-OCR 줄 탐지 입력 줄이기(짧은 변 960 → 640/480/320) | 205 → 95/53/29ms/상자로 빨라진다. 하지만 93상자 중 13~20개에서 세로쓰기 판정이 바뀌고 글자 크기가 평균 20% 달라진다. **기각** |
| PP-OCR 줄 탐지를 GPU로 | 입력이 같으므로 결과가 같고 205 → 139ms/상자. **채택** |
| 분석과 번역을 겹쳐 돌리고 쌓인 풍선을 묶어 번역 | 결과는 같고 번역 대기 시간이 분석 뒤로 숨는다. **채택** |
| 번역 모델 교체(TranslateGemma-4B 등, llama.cpp GPU) | 품질과 속도 모두 낫지만 다운로드 2.4GB, 라이선스, 별도 프로세스 문제가 있다. 2026-10-20 조사: 공식 저장소는 수동 승인 게이트라 앱이 받을 수 없고, 입력 형식이 고정이라 말투 지시를 공식 지원하지 않는다. **보류**("추가 검토" 절) |
| Gemma 4 E2B(Apache-2.0, 구글 공식 GGUF, llama.cpp) | 같은 146문장을 GPU에서 ~75ms/문장, CPU에서 ~620ms/문장. 반말을 반말로 유지하는 비율이 M2M100보다 높다. 다운로드 3.35GB, 앱 통합(실행기) 미정. **시험함, 채택은 따로 결정**("추가 검토" 절) |

## 결정

1. 작업마다 분석(producer)과 번역(consumer)을 함께 돌린다. 분석은 쪽(strip은 band)을 읽어 대기열에 넣는다. 대기열은 `AnalysisLookahead`(6)개까지 쌓인다.
2. 번역은 대기열에서 기다리지 않고 꺼낼 수 있는 만큼 꺼낸다. 기다리는 풍선이 `TranslateChunk`(8)개가 차면 멈추고, 꺼낸 풍선을 8개씩 번역기에 넣는다. 번역이 밀리면 대기열이 쌓여 묶음이 커지고, 분석이 밀리면 바로 번역해 첫 결과가 늦어지지 않는다.
3. 번역 결과는 요청 순번으로 풍선(slot)에 맞춘다. 풍선 ID로 맞추면 strip에서 band마다 0부터 매긴 ID가 같은 쪽 안에서 겹쳐, 뒤 band의 번역이 앞 band 풍선에 들어갔다.
4. strip 풍선은 찾은 band를 함께 기록한다. 실행을 시작할 때 완료되지 않은 band의 풍선은 버린다. 그 band는 다시 읽으므로, 남겨 두면 자동 일시정지 후 재개할 때 풍선이 겹쳤다.
5. 처음부터 다시 번역하라는 요청(재번역, 원문 언어 변경)이 오면 `StripEpoch`를 올린다. 그 전에 읽기 시작한 band 결과는 버린다.
6. 더 급한 작업에 자리를 내줄 때는 분석을 멈추고, 이미 분석한 단위까지 번역을 마친 뒤 넘긴다. 한쪽이 오류로 멈추면 다른 쪽도 멈추고, 취소가 아닌 실제 오류를 작업 실패 사유로 알린다.
7. 일본어는 manga-ocr 글자 읽기(CPU)와 PP-OCR 줄 탐지(GPU)를 함께 돌린다. PP-OCR 인식 모델은 그대로 CPU다.
8. 탐지 중복 제거는 말풍선 안·밖 글자 상자를 같은 종류로 비교한다. 분석 규칙이 바뀌었으므로 `AnalysisVersion`을 6으로 올렸다. 이전 캐시는 다시 분석한다.

## 결과

세평유 60쪽을 같은 PC에서 다시 쟀다.

| 범위 | 전 | 후 |
|---|---|---|
| strip | 51.2초 | 22.2초 (첫 결과 6.2초) |
| 쪽 | — | 29.1초 (첫 결과 4.7초) |

- 책 한 권(298쪽)은 약 4분에서 2분 안쪽으로 줄어든다(60쪽 결과로 추정).
- 이제 번역(17.8초)과 분석(19.6초)이 거의 같은 시간을 쓴다. 더 빨라지려면 둘 다 줄어야 한다. 번역은 모델 교체가 가장 큰 후보다.
- 글자 인식과 번역이 같은 CPU를 나눠 쓴다. 코어가 적은 PC에서는 겹쳐 돌려 얻는 이득이 작다. 하지만 결과는 같고 느려지지는 않는다.
- 첫 결과가 나오기 전 대기는 이전과 비슷하다(모델 준비가 대부분이다).

## 추가 검토: LLM 번역 모델 (2026-10-20)

M2M100은 말풍선을 문맥 없이 한 문장씩 번역하고, 말투 맞춤(ADR 0004 6번)을 할 수 없다. 번역과 말투를 한 모델로 처리할 수 있는지 보려고 구글 모델 두 가지를 확인했다.

### TranslateGemma-4B: 보류

| 항목 | 확인 결과 |
|---|---|
| 공식 저장소 `google/translategemma-4b-it` | 수동 승인 게이트(`gated: manual`). 로그인 없이 받으면 401. 앱이 고정 주소에서 바로 받을 수 없다 |
| 라이선스 | Gemma 이용 약관. 재배포하면 3.1절(약관 사본, 3.2절 사용 제한 고지, 수정 표시, NOTICE 문구)을 따라야 하고, 3.2절에 구글의 원격 사용 제한 조항이 있다 |
| 형식 | 공식은 safetensors뿐. 게이트 없는 제3자 GGUF(예: `mradermacher/translategemma-4b-it-GGUF`, Q4_K_M 2.49GB)는 있지만 구글이 올린 파일이 아니라 원본과 같은지 보증할 수 없다. ONNX 변환본은 찾지 못했다 |
| 입력 | 원문·목표 언어 코드와 텍스트(또는 이미지)만 받는 고정 템플릿. 다른 지시는 "공식 지원하지 않음". 입력 문맥 2K 토큰 |

말투 지시를 공식 지원하지 않으므로 "번역과 말투를 한 모델로"라는 목적에 맞지 않고, 다운로드 경로도 막혀 있어 보류한다.

### Gemma 4 E2B: 시험함

범용 지시 모델이라 말투와 문맥을 지시로 줄 수 있다. Apache-2.0이고 게이트가 없으며, 구글이 직접 올린 GGUF가 있다.

- 모델: `google/gemma-4-E2B-it-qat-q4_0-gguf` revision `675cff42a74c774d6cb76f76d8eacb49b48c9b93`, `gemma-4-E2B_q4_0-it.gguf` 3,349,516,256 bytes, sha256 `fa401b55b07ee70a54c6dae3903c783a6e65064312529ea57175cb5f8dec6634`
- 실행기: llama.cpp `b11748` `llama-server` Windows Vulkan x64 빌드(36.8MB, GitHub 릴리스 sha256 확인). 앱에 넣지 않고 별도 프로세스로 띄워 HTTP로 시험했다.
- 입력: 위 세평유 298쪽에서 나온 말풍선 146개의 OCR 원문(`docs/IMPLEMENTATION-STATUS.md`의 "빠르게·정확하게 비교"와 같은 원문). 읽는 순서로 16개씩 보내고, 앞 말풍선 6개의 원문·번역을 문맥으로 넣었다. 지시는 "존댓말·반말 유지, 이름 일관, 줄마다 `[번호] 번역`"이다. temperature 0.

| 장치 | 모델 로딩 | 146개 번역 | 안정 구간 | 생성 속도 | 메모리 |
|---|---|---|---|---|---|
| GPU (RTX 4070 Ti SUPER, Vulkan) | 5.7초 | 33.4초(첫 묶음 18.4초는 초기화) | 16개당 약 1.2초(~75ms/문장) | 69.6 tok/s | 개인 2.5GB, VRAM +1.6GB |
| CPU (i7-13700K) | 2.1초 | 90.2초 | 16개당 약 10초(~620ms/문장) | 25.7 tok/s | 작업 집합 1.8GB(모델 파일 매핑 포함), 개인 0.8GB |

비교: M2M100 int8은 같은 146문장을 8개씩 묶어 CPU에서 255ms/문장이다(배경 절).

- 146개 모두 형식대로 돌아왔다(누락 0). GPU와 CPU 결과는 146개 중 131개가 같았다.
- 말투(어미 정규식으로 센 대략값): 일본어 경어체로 보이는 32개 중 존댓말 유지는 Fast 17, Accurate 22, Gemma 21. 그 밖의 114개가 존댓말로 바뀐 경우는 Fast 19, Accurate 23, **Gemma 7**이다. Gemma가 놓친 32개 중 다수는 "-지만", "들었습니다만"처럼 규칙에 안 걸린 경우였고, 실제로 반말이 된 것은 3~4개다.
- 일본어 장음 기호를 줄표로 쓴 `ーー`가 3개 번역에 그대로 남았다. 후처리로 바꿀 수 있다.
- 품질은 골든 세트로 검수하지 않았다. 일부를 읽어 본 바로는 Accurate에서 보인 뜻이 반대로 된 번역(예: "순종하는 개로" → "순종할 수 없는 개가")이 Gemma에는 없었지만, 뜻이 바뀐 번역이 하나 있었다("이해시키는 것" → "말해 주십시오"). 나란히 보기 표로 개발자가 직접 평가한다.

남은 결정:

0. 결정됨(2026-10-20): Fast/Accurate를 없애고 Gemma 4 E2B 하나로 바꾼다. [ADR 0008](0008-gemma-translator.md). 아래 1~4는 그 결정 전 검토 기록이다.
1. 실행기: LLamaSharp으로 앱 안에서 돌아가는 것은 확인했다(아래 절). 남은 것은 컨텍스트 재사용으로 중간 지연과 메모리를 줄이는 일, 리눅스 네이티브 제외, ARM64(Vulkan 없음) 처리다. ONNX Runtime 경로와 별도로 GPU 백엔드(Vulkan)를 하나 더 싣게 된다.
2. 다운로드 3.35GB(Accurate 2.3GB보다 크다)와 GPU 없는 PC의 속도(~620ms/문장, M2M100 Fast의 약 2.4배).
3. 첫 묶음의 초기화 시간(GPU 18초)을 숨길 방법.
4. 품질 평가 결과에 따라 Fast/Accurate를 대체할지, 세 번째 모드로 둘지.

### LLamaSharp 앱 내 실행 확인 (2026-10-20)

저장소 밖 콘솔 시험 프로젝트(.NET 10, `net10.0-windows10.0.19041.0`, win-x64)에서 앱과 같은 구성으로 확인했다.

- 패키지: `LLamaSharp` 0.30.0(MIT, 2026-10-17), `LLamaSharp.Backend.Cpu`·`LLamaSharp.Backend.Vulkan` 0.30.0, `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4(앱과 같은 버전)
- 순서: ONNX Runtime DirectML로 말풍선 탐지 세션을 만들어 추론 → 같은 프로세스에서 LLamaSharp으로 Gemma 4 E2B를 올려 위 146문장 번역(`StatelessExecutor`, greedy) → 탐지 추론을 다시 실행

| 항목 | GPU (Vulkan) | CPU (AVX2) |
|---|---|---|
| 모델 로딩 | 1.6초 | 1.8초 |
| 146개 번역 | 45.9초(첫 묶음 20.2초, 7.4·9.2초 묶음 각 1개) | 94.6초 |
| 안정 구간 | 16개당 약 1.4초 | 16개당 약 10~11초 |
| 누락 | 0 | 0 |
| llama-server와 같은 번역 | 137/146 | 124/146 |
| 번역 뒤 탐지 추론(DirectML) | 정상(19ms) | 정상(177ms) |
| 프로세스 최대 개인 메모리(탐지 세션 포함) | 5.8GB | 5.2GB |

- **공존**: DirectML(ONNX Runtime)과 Vulkan(llama.cpp)을 한 프로세스에서 함께 써도 충돌하지 않았다.
- **대화 템플릿**: LLamaSharp의 `LLamaTemplate`(llama.cpp `llama_chat_apply_template`)은 Gemma 4의 Jinja 템플릿을 알아보지 못해 `MissingTemplateException`이 난다. llama-server `/apply-template`(추론 끔)이 만든 형식 `<|turn>user\n…<turn|>\n<|turn>model\n`을 직접 만들고 `<turn|>`에서 멈추게 해서 해결했다. 모델이나 템플릿이 바뀌면 이 형식을 다시 확인해야 한다.
- **중간 지연과 메모리**: llama-server보다 묶음 지연(7~9초)이 가끔 생기고 개인 메모리가 크다. `StatelessExecutor`가 요청마다 컨텍스트(8K)를 새로 만드는 것이 원인으로 보이지만 확인하지 않았다. 앱에 넣을 때는 컨텍스트를 한 번 만들어 재사용한다.
- **배포 크기(win-x64)**: 네이티브 약 72MB(Vulkan 53MB, CPU AVX·AVX2·AVX512·noAVX 각 약 4.8MB) + 관리 DLL 약 3MB. `LLamaSharp.Backend.Vulkan`이 끌어오는 리눅스 Vulkan 네이티브(56MB)도 출력에 복사되므로 게시에서 빼야 한다. 현재 게시본(268MB) 대비 약 27% 증가.
- **ARM64**: CPU 백엔드에는 win-arm64(4.7MB)가 있지만 Vulkan 백엔드에는 win-x64만 있다. ARM64에서는 CPU로만 돌고, ARM 기기에서의 속도는 재지 않았다.
