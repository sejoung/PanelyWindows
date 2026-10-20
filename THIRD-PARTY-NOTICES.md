# Third-party notices

Panely Windows는 아래 구성 요소를 사용합니다. 각 구성 요소의 저작권과 라이선스는 해당 소유자에게 있습니다.

## 앱에 포함되는 라이브러리 (NuGet)

| 구성 요소 | 버전 | 라이선스 | 용도 |
|---|---|---|---|
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.50.5 | MIT | RAR 4/5·solid RAR 읽기 |
| [Microsoft.ML.OnnxRuntime.DirectML](https://github.com/microsoft/onnxruntime) | 1.24.4 | MIT | 모델 추론(CPU + DirectML GPU) |
| [Microsoft.AI.DirectML](https://www.nuget.org/packages/Microsoft.AI.DirectML) | 1.15.4 | Microsoft DirectML License(재배포 허용) | GPU 추론용 `DirectML.dll` |
| [LLamaSharp](https://github.com/SciSharp/LLamaSharp) · LLamaSharp.Backend.Cpu · LLamaSharp.Backend.Vulkan.Windows | 0.30.0 | MIT (포함된 [llama.cpp](https://github.com/ggml-org/llama.cpp)·ggml도 MIT) | 번역 모델(GGUF) 실행. CPU, Vulkan GPU |
| [Magick.NET-Q8-AnyCPU](https://github.com/dlemstra/Magick.NET) | 15.0.0 | Apache-2.0 (ImageMagick License 포함) | WIC 코덱이 없는 PC에서 HEIC/WebP 등 대체 디코딩 |
| .NET 10 런타임·WPF (self-contained 게시 시 포함) | 10.0 | MIT | 런타임·UI |

## 앱에 포함되지 않는 모델 (사용자가 직접 내려받음)

번역 기능을 켜고 언어팩을 받을 때만, 아래 고정 revision에서 파일을 내려받고 sha256을 검증합니다. Panely는 이 파일들을 재배포하지 않습니다.

| 모델 | 고정 revision | 라이선스 |
|---|---|---|
| [ogkalu/comic-text-and-bubble-detector](https://huggingface.co/ogkalu/comic-text-and-bubble-detector) `detector.onnx` | `16e8a622f91fabc6b5b65c96d32d1183f8843546` | Apache-2.0 (모델 카드) |
| [google/gemma-4-E2B-it-qat-q4_0-gguf](https://huggingface.co/google/gemma-4-E2B-it-qat-q4_0-gguf) `gemma-4-E2B_q4_0-it.gguf` (번역) | `675cff42a74c774d6cb76f76d8eacb49b48c9b93` | Apache-2.0 (Gemma 4, 모델 카드) |
| [ogkalu/manga-ocr-onnx](https://huggingface.co/ogkalu/manga-ocr-onnx) (일본어 OCR, int8) | `c68e56437a2e0b13a7b1f4efa0ffce76efd0478d` | Apache-2.0 (원본 [kha-white/manga-ocr-base](https://huggingface.co/kha-white/manga-ocr-base) Apache-2.0) |
| [ogkalu/ppocr-v5-onnx](https://huggingface.co/ogkalu/ppocr-v5-onnx) (PP-OCRv5 mobile 탐지 + 중국어/영어/한국어 인식) | `3b76726c3606c4af92f5133b359ed14e0d30a455` | Apache-2.0 (원본 [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) Apache-2.0) |

OCR 구성(언어별 모델 선택, manga-ocr 디코딩, PP-OCR 전처리·CTC 해석)은 [ogkalu2/comic-translate](https://github.com/ogkalu2/comic-translate)(Apache-2.0)의 구현을 참고해 C#으로 옮겼다.

## 원본 Panely 자산

앱 아이콘(`assets/panely-icon-stacked.svg`에서 생성), 문구 카탈로그(`src/Panely.Core/Localization/localization.catalog.json`), 화면 디자인은 원본 Panely 저장소의 자산이며 Apache-2.0을 따릅니다. 전문은 `assets/LICENSE-original.txt` (Copyright 2026 beni kim).

## 테스트 전용

| 구성 요소 | 라이선스 | 비고 |
|---|---|---|
| xUnit, Microsoft.NET.Test.Sdk, coverlet | Apache-2.0 / MIT | 테스트 실행 |
| `tests/Panely.Tests/Core/Fixtures/rar/*.rar` | MIT (SharpCompress) | SharpCompress 테스트 저장소의 샘플 RAR. 같은 폴더의 `LICENSE-SharpCompress.txt` 참고. 앱에는 포함되지 않음 |
| `tests/Panely.Tests/Vectors/translation-before-after.png` | Apache-2.0 (원본 Panely) | 원본 macOS 앱의 번역 화면 스크린샷. 말풍선 탐지 통합 테스트 입력. 앱에는 포함되지 않음 |
