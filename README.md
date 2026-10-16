# Panely for Windows

로컬 만화·이미지를 읽는 Windows 리더입니다. 폴더·CBZ/ZIP·CBR/RAR을 열고 한 장/두 장/세로 스크롤, 오른쪽→왼쪽 읽기, 라이브러리, 이어보기, 북마크를 제공합니다. 실험 기능으로 기기 안에서 말풍선을 번역해 보여주고 번역본을 내보냅니다.
명세는 [`docs/windows/`](docs/windows/README.md), 기능별 구현·검수 현황은 [`docs/IMPLEMENTATION-STATUS.md`](docs/IMPLEMENTATION-STATUS.md)에 있습니다.

## 구성

| 프로젝트 | 역할 |
|---|---|
| `src/Panely.Domain` | 순수 계산: 펼침·맞춤·줌·휠 상태 머신·자연 정렬·OCR 타일·말풍선 묶기·패치 배치·AI 응답 파서 |
| `src/Panely.Core` | 파일/ZIP/RAR·추출 캐시·상태 저장(state.json)·진단 로그·라이브러리 감시·리더 세션·번역/내보내기 작업 큐·모델 관리·Windows OCR·ONNX 어댑터·한영 문구 |
| `src/Panely.App` | WPF UI: 리더 창, 툴바, 라이브러리, 뷰어, 썸네일, 설정, 작업 창, 번역 패널·편집·내보내기·AI 채팅 시트 |
| `tests/Panely.Tests` | xUnit. `docs/windows/test-vectors.json` 기대값 포함 |
| `tools/` | `make-samples.ps1`(QA 샘플 생성), `publish.ps1`(배포본), `IconBuilder`(아이콘 생성) |

## 빌드·실행

필요한 것: Windows 10 19041 이상(1차 대상 Windows 11 x64), [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
스크립트는 PATH의 `dotnet`이나 `%LOCALAPPDATA%\Microsoft\dotnet`에서 SDK를 찾습니다. 저장소 루트의 `.cmd`는 더블클릭이나 cmd에서 실행할 수 있고, 인자는 같은 이름의 `tools\*.ps1`로 그대로 넘어갑니다.

| 스크립트 | 하는 일 |
|---|---|
| `build.cmd` | 솔루션 빌드 + 전체 테스트 (`-Configuration Release`, `-SkipTests`, `-Filter Translation`, `-Clean`) |
| `install-local.cmd` | 빌드·테스트 → self-contained 게시 → `%LOCALAPPDATA%\Programs\Panely`에 설치 → 시작 메뉴 "Panely (Local)" → 실행 |
| `uninstall-local.cmd` | 로컬 설치 제거. 사용자 데이터는 남김 (`-RemoveDevData`, `-RemoveData`) |

로컬에서 바로 테스트할 때는 이렇게 씁니다.

```powershell
install-local.cmd                          # 테스트 후 설치하고 실행
install-local.cmd -SkipTests               # 빠르게(설치만 몇 초)
install-local.cmd -Samples -Isolated       # QA 샘플 라이브러리를 열고, 실제 데이터 대신 %LOCALAPPDATA%\Panely-dev 사용
install-local.cmd -Open "D:\Comics\Series A"
install-local.cmd -NoLaunch -DesktopShortcut
```

- 관리자 권한이 필요 없고, 설치본은 .NET 런타임이 없어도 실행됩니다.
- 실행 중인 설치본은 위치를 저장하도록 먼저 정상 종료한 뒤 교체합니다.
- 버전은 `1.0.0-dev.<날짜.시각>+<커밋>` 형식이라 설정 › 진단이나 정보 창에서 어떤 빌드인지 확인할 수 있습니다. 설치 폴더의 `install-info.json`에도 남습니다.

dotnet CLI로 직접 할 수도 있습니다.

```powershell
dotnet build Panely.sln
dotnet test tests/Panely.Tests
dotnet run --project src/Panely.App -- "D:\Comics\Series A"
```

배포본(.NET 런타임이 없는 PC에서도 실행, 관리자 권한 불필요):

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1                 # artifacts\Panely-win-x64(.zip)
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime win-arm64
```

QA용 샘플 라이브러리(자연 정렬, 두 장, 웹툰, 겉포장 시리즈, 중첩 zip, 손상 파일, 영어 말풍선 페이지):

```powershell
powershell -ExecutionPolicy Bypass -File tools\make-samples.ps1 -Out D:\PanelySamples
```

## 데이터 위치

`%LOCALAPPDATA%\Panely\` 아래에 `Data\state.json`, `Data\edits\`(직접 고친 번역), `Cache\extraction|translation|thumbnails\`, `Diagnostics\`, `Models\`가 생깁니다. 캐시를 지워도 Data와 Models는 남습니다.

| 환경 변수 | 용도 |
|---|---|
| `PANELY_DATA_ROOT` | 데이터 위치 바꾸기(테스트·포터블) |
| `PANELY_DEV_PSEUDO_TRANSLATION=1` | 모델 없이 번역 UI를 확인하는 개발용 가짜 공급자(기본 꺼짐, UI에 표시됨) |
| `PANELY_TEST_DETECTOR_MODEL` | 실제 `detector.onnx` 경로를 주면 탐지 통합 테스트 실행 |

## 번역(실험 기능)

설정 › 실험적 기능에서 **번역 보조**를 켭니다. 처음 번역할 때 번역 패널이나 설정에서 언어팩을 내려받습니다(고정 revision, sha256 검증).

- Fast: M2M100-418M int8, 약 630MB
- Accurate: M2M100-418M fp32, 약 2.5GB
- 말풍선 탐지: 약 170MB

설치한 뒤에는 네트워크 없이 동작합니다. 글자 인식은 Windows OCR을 쓰므로, 일본어·중국어 책을 번역하려면 Windows 설정 › 시간 및 언어에서 해당 언어의 OCR(광학 문자 인식) 기능이 설치되어 있어야 합니다. 말투 맞춤은 검증된 Windows 로컬 모델이 없어 비활성 상태입니다. 자세한 제약은 구현 현황 문서를 보세요.

## 단축키(일부)

| 동작 | 키 |
|---|---|
| 열기 / 설정 | Ctrl+O / Ctrl+, |
| 한 장 / 두 장 / 세로 | Ctrl+Shift+1 / 2 / 3 |
| 화면·가로·세로 맞춤 | Ctrl+1 / 2 / 3 |
| 확대 / 축소 / 초기화 | Ctrl++ / Ctrl+- / Ctrl+0 |
| 앞으로 / 뒤로 | Space, 방향키(읽기 방향 반영) |
| 이전 / 다음 권 | [ / ] |
| 쪽 이동 / 북마크 / 즐겨찾기 | Ctrl+G / Ctrl+D / Ctrl+Shift+D |
| 라이브러리·툴바 고정, 썸네일 | Ctrl+Alt+S / T / P |
| 원본↔번역 / 번역 실행 / 말풍선 편집 / 작업 창 | Ctrl+Shift+T / Ctrl+Alt+Y / Ctrl+Shift+E / Ctrl+Alt+J |
| 전체 화면 | F11 |

전체 목록은 [`docs/windows/08-commands-and-copy.md`](docs/windows/08-commands-and-copy.md)에 있습니다.

## 라이선스

[LICENSE](LICENSE). 사용한 라이브러리와 모델은 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에 정리했습니다.
