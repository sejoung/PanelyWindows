# Third-party notices

Panely Windows는 아래 구성 요소를 사용합니다. 각 구성 요소의 저작권과 라이선스는 해당 소유자에게 있습니다.

## 앱에 포함되는 라이브러리 (NuGet)

| 구성 요소 | 버전 | 라이선스 | 용도 |
|---|---|---|---|
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.50.5 | MIT | RAR 4/5·solid RAR 읽기 |
| [Microsoft.ML.OnnxRuntime.DirectML](https://github.com/microsoft/onnxruntime) | 1.24.4 | MIT | 모델 추론(CPU + DirectML GPU) |
| [Microsoft.AI.DirectML](https://www.nuget.org/packages/Microsoft.AI.DirectML) | 1.15.4 | Microsoft DirectML License(재배포 허용) | GPU 추론용 `DirectML.dll` |
| [Microsoft.ML.Tokenizers](https://github.com/dotnet/machinelearning) | 2.0.1 | MIT | M2M100 SentencePiece 토크나이저 |
| [Magick.NET-Q8-AnyCPU](https://github.com/dlemstra/Magick.NET) | 15.0.0 | Apache-2.0 (ImageMagick License 포함) | WIC 코덱이 없는 PC에서 HEIC/WebP 등 대체 디코딩 |
| .NET 10 런타임·WPF (self-contained 게시 시 포함) | 10.0 | MIT | 런타임·UI |

## 앱에 포함되지 않는 모델 (사용자가 직접 내려받음)

번역 기능을 켜고 언어팩을 받을 때만, 아래 고정 revision에서 파일을 내려받고 sha256을 검증합니다. Panely는 이 파일들을 재배포하지 않습니다.

| 모델 | 고정 revision | 라이선스 |
|---|---|---|
| [ogkalu/comic-text-and-bubble-detector](https://huggingface.co/ogkalu/comic-text-and-bubble-detector) `detector.onnx` | `16e8a622f91fabc6b5b65c96d32d1183f8843546` | Apache-2.0 (모델 카드) |
| [Xenova/m2m100_418M](https://huggingface.co/Xenova/m2m100_418M) ONNX (Fast: quantized, Accurate: fp32, KV 캐시 merged decoder) | `9c374f0b7aca709787cea97b047bfbbd1559d177` | 원본 가중치 [facebook/m2m100_418M](https://huggingface.co/facebook/m2m100_418M) MIT. ONNX 변환 저장소에는 별도 라이선스 표기가 없음 |
| [ogkalu/manga-ocr-onnx](https://huggingface.co/ogkalu/manga-ocr-onnx) (일본어 OCR, int8) | `c68e56437a2e0b13a7b1f4efa0ffce76efd0478d` | Apache-2.0 (원본 [kha-white/manga-ocr-base](https://huggingface.co/kha-white/manga-ocr-base) Apache-2.0) |
| [ogkalu/ppocr-v5-onnx](https://huggingface.co/ogkalu/ppocr-v5-onnx) (PP-OCRv5 mobile 탐지 + 중국어/영어/한국어 인식) | `3b76726c3606c4af92f5133b359ed14e0d30a455` | Apache-2.0 (원본 [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) Apache-2.0) |

OCR 구성(언어별 모델 선택, manga-ocr 디코딩, PP-OCR 전처리·CTC 해석)은 [ogkalu2/comic-translate](https://github.com/ogkalu2/comic-translate)(Apache-2.0)의 구현을 참고해 C#으로 옮겼다.

## 원본 Panely 자산

앱 아이콘(`docs/windows/assets/panely-icon-stacked.svg`에서 생성), 문구 카탈로그, 화면 디자인은 원본 Panely 저장소의 자산이며 Apache-2.0을 따릅니다. 전문은 `docs/windows/assets/LICENSE-original.txt` (Copyright 2026 beni kim).

## 테스트 전용

| 구성 요소 | 라이선스 | 비고 |
|---|---|---|
| xUnit, Microsoft.NET.Test.Sdk, coverlet | Apache-2.0 / MIT | 테스트 실행 |
| `tests/Panely.Tests/Core/Fixtures/rar/*.rar` | MIT (SharpCompress) | SharpCompress 테스트 저장소의 샘플 RAR. 같은 폴더의 `LICENSE-SharpCompress.txt` 참고. 앱에는 포함되지 않음 |
