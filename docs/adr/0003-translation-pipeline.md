# ADR 0003: 분석과 번역을 겹쳐 돌리고 여러 쪽의 풍선을 묶어 번역한다

- 상태: 채택
- 날짜: 2026-10-19
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
| 번역 모델 교체(TranslateGemma-4B 등, llama.cpp GPU) | 품질과 속도 모두 낫지만 다운로드 2.4GB, 라이선스, 별도 프로세스 문제가 있다. 따로 결정한다 |

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
