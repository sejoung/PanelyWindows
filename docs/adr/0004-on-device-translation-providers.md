# ADR 0004: 번역은 내려받은 ONNX 모델로 PC 안에서만 한다

- 상태: 채택(첫 구현 때 정한 것을 2026-10-19에 사후 기록)
- 날짜: 2026-10-19
- 관련 코드: `TranslationService.CreateDefaultProviders`, `ModelManifest`, `ModelStore`, `OnnxSessions`, `OcrRuntimePolicy`, `ModelTextRecognizer`, `WindowsOcrRecognizer`, `M2M100Translator`, `UnavailableSpeechLevelCorrector`
- 테스트: `TranslationCoreTests.cs`(manifest 검증), `TranslationAlgorithmTests.cs`(탐지·OCR 후처리), `PANELY_TEST_DETECTOR_MODEL`을 줄 때만 도는 탐지 통합 테스트

## 배경

원본(macOS) Panely는 Apple Vision(OCR), Apple Translation(번역·언어팩), Foundation Models(말투 보정)를 썼다. Windows에는 같은 것이 없다. 첫 포팅 명세는 OCR·번역·말투 공급자와 GPU 실행 경로를 "미검증 결정 사항"으로 남겼고, 조건은 다음과 같았다.

- 자동으로 외부 API를 부르지 않는다. 언어팩을 설치한 뒤에는 읽기·OCR·번역에 네트워크 요청이 없어야 한다.
- 일본어 세로쓰기, 중국어, 영어, 한국어를 읽는다.
- 모델 파일은 revision·hash·license가 확인된 것만 설치한다.

## 결정

1. 단계마다 ONNX 모델을 쓰고 ONNX Runtime으로 돌린다. 파이프라인 구성은 [comic-translate](https://github.com/ogkalu2/comic-translate)를 따른다.

   | 단계 | 모델 | 라이선스 |
   |---|---|---|
   | 말풍선 탐지 | comic-text-and-bubble-detector (RT-DETR-v2) | Apache-2.0 |
   | 일본어 글자 | manga-ocr (int8) | Apache-2.0 |
   | 중국어·영어·한국어 글자, 언어 감지, 줄 찾기 | PP-OCRv5 mobile | Apache-2.0 |
   | 번역 Fast / Accurate | M2M100-418M (int8 / fp32, KV 캐시 decoder) | MIT |

2. 모델은 앱에 넣지 않는다. 사용자가 번역 패널에서 내려받고, 앱은 `ModelManifest`에 고정한 revision·SHA-256으로 확인한 뒤 설치한다. 확인하지 못한 값(placeholder)이 남은 manifest는 설치하지 않는다. 앱 캐시를 지워도 모델은 남는다.
3. GPU는 ONNX Runtime의 DirectML 실행 공급자로 쓴다. DirectX 12 GPU면 제조사와 상관없다. 재배포용 `DirectML.dll` 1.15.4를 앱에 함께 넣는다. 세션을 만들다 실패하면 CPU로 돌아가고, `PANELY_ORT_DEVICE=cpu`로 GPU를 끌 수 있다. 세션 생성은 `OnnxSessions` 한 곳에 모아 두어, 나중에 Windows ML 등으로 바꿀 때 이곳만 고치면 된다. Windows ML과 비교한 기록은 없다.
4. 모델마다 GPU와 CPU 중 실측으로 빠른 쪽을 쓴다(`OcrRuntimePolicy`, `M2M100Translator`). 말풍선 탐지와 줄 찾기는 GPU, manga-ocr·PP-OCR 인식·M2M100은 CPU다. M2M100 int8은 DirectML 세션 생성 중 네이티브 충돌(0xC0000005)이 나고, fp16은 출력이 깨진다. 수치는 `docs/IMPLEMENTATION-STATUS.md`와 ADR 0003에 있다.
5. Windows 내장 OCR(`Windows.Media.Ocr`)은 OCR 모델 manifest가 없을 때만 대체로 쓴다. 이 PC에서 세로 일본어를 한 글자도 읽지 못했고, 언어별 OCR 기능을 Windows 설정에서 따로 설치해야 하기 때문이다.
6. 말투 보정(ja↔ko 존댓말·반말 맞춤)은 넣지 않는다. 검증된 Windows 로컬 대체 모델이 없다. `UnavailableSpeechLevelCorrector`가 번역을 그대로 돌려주고, 설정에는 사유와 함께 비활성으로 보인다.
7. M2M100은 다른 번역 모델과 비교해서 고른 기록이 없다. MIT 라이선스이고, 다섯 언어를 모델 하나로 번역하며, ONNX 변환본이 있어서 채택했다.

## 결과

- 언어팩을 내려받은 뒤에는 오프라인에서 번역한다. 사용자가 AI 채팅으로 직접 넘길 때만 글이 PC 밖으로 나간다(ADR 0005).
- Fast 팩은 약 1GB, Accurate 팩은 약 2.5GB를 내려받는다.
- 번역 품질은 M2M100의 한계를 그대로 가진다. 짧은 대사와 관용구가 자주 틀린다. 더 큰 LLM(예: llama.cpp로 GPU에서 돌리는 4B급 번역 모델)으로 바꾸는 안은 따로 결정한다(ADR 0003 "검토한 방법" 참고).
- 중국어 간체·번체는 M2M100이 `zh` 하나로만 다뤄 구분하지 않는다.
- 모델을 바꾸면 캐시 identity(provider revision)가 달라져 기존 번역 캐시는 다시 만들어진다. 사용자 수정은 캐시와 따로 저장되어 남는다.
