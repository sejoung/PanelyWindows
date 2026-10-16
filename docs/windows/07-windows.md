# Windows 구현 구조와 플랫폼 대체

이 문서는 새 프로젝트를 시작하기 위한 **제안 구조**다. 원본의 코드 구조·빌드 도구를 옮길 필요는 없다. 01~06의 사용자 동작·데이터·계산을 만족하면 다른 Windows 프레임워크로 구현할 수 있다.

## 기준 기술

Windows 11 x64, C# + .NET 10 LTS + WPF + MVVM을 기본안으로 한다. 선택 이유는 커스텀 다크 컨트롤, 벡터/텍스트 렌더링, 파일 중심 desktop 앱, 키보드 명령을 한 UI 계층에서 제어하기 쉽기 때문이다. 이 적합성 판단은 본 명세의 설계 제안이다. WPF는 Windows 전용이며 resolution-independent vector rendering·컨트롤·바인딩·스타일을 제공한다. [공식 개요](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/). .NET 10은 현재 LTS로 안내되어 있다. patch 버전은 구현 착수 때 지원 정책에 맞춰 고정한다. [공식 지원 정책](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)(2026-10-16 확인).

UI를 기본 WPF 테마로 대체하지 않는다. 색·템플릿·치수는02를 따른다. 뷰어는 DrawingVisual/커스텀 렌더 표면과 가상화된 viewport로 시작하고 성능 검증 후 GPU 표면을 선택한다. “WPF를 썼다”만으로 큰 이미지·트랙패드·메모리 성능이 충족되는 것은 아니다. WinUI/Avalonia/Qt로 바꾸면 caption/input/렌더·가상화·접근성 어댑터를 다시 검수한다.

## 독립 프로젝트 경계

```text
Panely.Windows       창·커맨드·스타일·UI Automation·포커스
Panely.Application   ReaderSession·LoadCoordinator·Library·Jobs·Settings
Panely.Domain        Spread/Fit/Position/BookRef/TranslationPlan 순수 계산
Panely.Rendering     이미지·변환·patch geometry·text fitting·export painter
Panely.Infrastructure 파일·ZIP/RAR·코덱·감시·저장·cache·로그
Panely.Translation   OCR/Detector/Translator/SpeechLevel/LanguagePack 어댑터
Panely.Tests         계산·fixture·상태·통합·시각·배포 검수
```

Application은 UI 객체나 native handle에 의존하지 않는다. Infrastructure/Translation을 interface로 주입하여 깨진 파일·느린 decode·언어팩 없음·취소 경합을 테스트할 수 있게 한다. 공개 module/package 이름은 제안이며 반드시 이 수만큼 프로젝트로 나누지는 않아도 된다.

| 경계 | 책임 |
|---|---|
| ReaderSession | source,logicalPage,layout,direction,fit,series,loadEpoch |
| ViewerController | baseline/current zoom,scroll,fit state,anchor,session series factors |
| SourceResolver | 형식·폴더 탐색·중첩·SourceRef/BookRef 연결 |
| ImageScheduler | 치수·decode·visible 우선순위·취소·메모리·preload |
| LibraryService | 트리 snapshot/diff·펼침·active 경로·권 목록 |
| StateRepository | schema migration·atomic write·debounce·flush |
| CacheService | versioned key·lease·staging·publish·LRU·size/clear |
| TranslationScheduler | 전역1개 번역, 언어/책별 controller, user pause |
| ExportScheduler | 번역 의존 job·한 책씩 render/write·이력 |
| PlatformServices | picker,Explorer,clipboard,association,watcher,window lifecycle |

단일 UI dispatcher에서 관찰 상태를 바꾸고 파일 I/O·압축·decode·추론은 worker에서 실행한다. CancellationToken과 epoch를 함께 써서 취소 자체가 불가능한 native 작업의 늦은 결과도 막는다. 큰 bitmap과 OCR Tensor를 UI observable 상태에 무제한 보관하지 않는다.

## macOS 기능의 대체

| 원본 역할 | Windows 구현 계약 |
|---|---|
| SwiftUI/AppKit/NSScrollView | UI 템플릿 + 렌더 surface + transform/scroll state |
| hiddenTitleBar/traffic lights | Windows caption·Snap·hit-test·최소화/최대화/닫기 |
| NSImage/ImageIO | codec adapter, 첫 프레임·EXIF·background decode·header dimensions |
| ZIPFoundation | ZIP list/prefix/read/write, CRC·cancel·streaming limits 검수 |
| UnrarKit/UnRAR | Windows RAR adapter,4/5/solid·Unicode·CRC·cancel 검수 |
| security scoped bookmarks | normal desktop access + SourceRef file identity·재연결 |
| UserDefaults/Codable | versioned state JSON 또는 동일 계약 DB |
| FSEvents/DispatchSource | 디렉터리 변경 감시+source monitor, debounce·overflow 처리 |
| Vision/NaturalLanguage | local OCR+script/language classifier, 세로 CJK 검수 |
| Core ML detector | ONNX 등의 검증된 모델 런타임·동등 output adapter |
| Apple Translation | 로컬 번역 모델·언어팩·Fast/Accurate 공급자 |
| Foundation Models | 로컬 말투 후처리+보존성 검증 |
| material/SF Symbols | fallback 배경+재배포 가능한 vector icons |
| OSLog/swift-log | 제한된 파일 로그+선택적 Windows event sink |
| NSSave/OpenPanel/Finder | 파일/폴더 picker, 저장 dialog, Explorer reveal |

Windows 자체 AI 기능의 존재만으로 세로 일본어 OCR, 모든 언어쌍 번역, 임의 PC의 로컬 실행을 보장하지 않는다. 실제 API/모델의 최소 OS·HW·언어·배포 조건을 capability로 노출한다.

## 의존성 결정 표

| 항목 | 기본 방향 | 구현 전에 끝낼 검증 |
|---|---|---|
| ZIP | .NET ZIP API를 후보로 검증 | entry streaming/prefix·CRC·대형 ZIP·경로 방어·취소 |
| RAR | RAR4/5/solid를 지원하는 native/managed reader 후보 | 동일 fixture, x64/ARM64 바이너리·배포 조건 |
| 이미지 | WIC 기반+필요한 보완 decoder | 10확장자, codec 없는 PC, EXIF·첫 프레임·alpha·색 |
| 추론 | ONNX Runtime CPU 기준, 검증한 GPU 옵션 | op 지원·정밀도·메모리·실패 fallback·인스톨러 |
| detector | 06의 공개 원모델에서 검증 ONNX 확보 | revision/hash/input/output·bbox golden·license |
| OCR | local CJK·vertical 가능한 공급자 선정 | ja/ko/en/zh-Hans/zh-Hant·효과음/작은 글자·strip |
| MT | local 번역 모델 공급자 선정 |5언어의 요청 쌍·Fast/Accurate·상업 배포·메모리·오프라인 |
| 말투 | ja↔ko 보존형 local rewrite | 부정/시제/이름/명령 보존·거절/언어 오류 fallback |
| 폰트/아이콘 | OS font fallback·허가된 vector | 한글/일본어/중문 coverage·재배포 파일·license |

이 표의 ZIP/RAR/codec/번역 공급자 선정은 **미검증 결정 사항**이다. 모델·라이브러리 이름을 하나 적는 것으로 지원 완료 처리하지 않는다. 제품 설계는 이 문서만으로 가능하지만 모델 확보와 실환경 검증은 별도 구현 작업이다.

ONNX Runtime 공식 Windows 안내는 신규 Windows 경로로 Windows ML을 안내하며 DirectML은 지속 유지 단계로 설명한다. 따라서 GPU API를 DirectML로 영구 고정하지 않고 runtime adapter로 둔다. CPU baseline으로 결과를 먼저 비교하고 선택한 Windows ML/GPU 경로의 최소 OS와 패키징 조건을 검수한다. [공식 Windows 안내](https://onnxruntime.ai/docs/get-started/with-windows.html), [DirectML 공급자](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)(2026-10-16 확인).

## 모델 패키지 manifest

모델마다 다음 정보가 있어야 재현 가능한 독립 빌드가 된다. 이 manifest는 모델 선택 뒤 채운다. URL을 런타임의 floating latest로 두지 않는다.

```json
{
  "id": "detector-example",
  "revision": "PINNED_REVISION_REQUIRED",
  "files": [{"name":"model.onnx", "bytes":0, "sha256":"REQUIRED"}],
  "license": "VERIFIED_LICENSE_REQUIRED",
  "source": "PINNED_SOURCE_URL_REQUIRED",
  "languages": [],
  "mode": "detector",
  "input": {"layout":"NCHW", "size":[1,3,640,640], "rgbRange":[0,1], "resize":"stretch"},
  "output": {"scoresAreSigmoid":true, "boxes":"normalized-cxcywh"},
  "runtime": {"cpu":true, "gpu":"VALIDATE"}
}
```

이 예제의0바이트/REQUIRED는 배포값이 아니다. 검증된 값 없는 manifest는 설치 불가로 처리한다. 다운로드는 사용자에게 크기·목적·진행·취소·오류·재시도를 보여주고 임시 파일→checksum→publish로 설치한다. 팩 설치 후 로컬 읽기/OCR/번역의 네트워크 요청이 없어야 한다. 부족한 팩과 불가능한 언어쌍을 구별한다. 모델 파일을 앱 cache Clear 대상으로 넣지 않는다.

Fast/Accurate를 실제로 구별할 방법을 정한다(다른 모델·quantization·decoding 설정 등). 같은 실행을 두 이름으로 표시하지 않는다. 각 variant의 정확도·속도·메모리·언어쌍을 기록하고 같은 tokenizer revision과 묶어 배포한다. hardware 부족이면 호환 CPU 경로나 명확한 unsupported를 제공한다.

## 시작·종료·탐색기 연결

앱별 사용자 세션의 single-instance mutex와 로컬 IPC로 후속 열기 요청을 전달한다. 두 프로세스가 같은 state/cache staging을 동시에 쓰지 않는다. IPC 입력은 파일 경로·허용 intent만 받아 임의 명령을 실행하지 않는다. 확장자 cbz/cbr/zip/rar의 연결 열기를 제공하되 ZIP/RAR의 시스템 기본 연결을 강제로 바꾸지 않는다. 폴더 열기·파일 열기는 동일 파이프라인으로 연결한다.

메인 리더 닫기는 앱 세션 종료로 정의한다. 설정/작업 보조 창만 남겨 reader 작업을 계속 돌리지 않는다. 위치 flush, 작업 cancel, partial 정리, cache lease release 후 종료. 앱 강제 종료 시에는 다음 실행에서 orphan staging 정리를 수행한다. 새 열기 요청을 마지막 폴더 복원보다 우선한다.

사용자가 선택한 파일/폴더를 일반 사용자 권한으로 읽고, 앱 설치를 위해 관리자 권한을 필수로 요구하지 않는 배포를 목표로 한다. 프로그램·상태·모델·사용자 내보내기의 위치를 분리한다. 서명된 installer/MSIX/portable 중 실제 배포 방식을 선택하고 파일 연결·native dependency·ARM64·업데이트 후 사용자 데이터 보존을 검수한다. 자동 업데이트 서비스는 별도 요구가 없는 한 추가하지 않는다.

## 원본 의존성 기록

원본은 ZIPFoundation0.9.20, swift-log1.12.1, 로컬 UnrarKit2.10 기반 package를 사용했다. Windows 구현의 필수 package 목록이 아니라 포맷/동작 대조에 쓰는 출처다. 원본 Core ML 변환 모델의 GPU IoU/속도 수치는 Windows ONNX의 검증 결과로 재사용하지 않는다.
