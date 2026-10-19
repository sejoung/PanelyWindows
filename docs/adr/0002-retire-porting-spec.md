# ADR 0002: 첫 포팅 명세(docs/windows)를 지우고 이후 결정은 ADR로 남긴다

- 상태: 채택
- 날짜: 2026-10-19

## 배경

`docs/windows/`는 원본(macOS) Panely를 Windows로 옮기려고 처음에 받은 명세 묶음이었다. 01~09 문서, 디자인 토큰, 스키마, 문구 카탈로그, test vector, macOS 스크린샷 24장, 패키지 해시 manifest가 들어 있었다. 첫 구현은 끝났다(구현 현황은 `docs/IMPLEMENTATION-STATUS.md`).

이 명세를 계속 두면 두 가지 문제가 생긴다.

- 코드가 바뀔 때마다 명세와 어긋난다. 이미 탐지 모델 출력 형식, OCR 엔진, GPU/CPU 분배 등이 명세와 다르게 정해졌다.
- 명세 일부를 고치면 패키지 해시 테스트(`bundle-manifest.json`)가 깨진다. 이는 문서를 바꾸지 말라는 뜻이었고, 일회성 전달물에 맞는 규칙이었다.

## 결정

1. `docs/windows/`를 지운다. 원문은 git 기록(커밋 `45c0cb1`)에 남는다.
2. 그중 빌드·테스트·배포에 실제로 쓰는 파일은 쓰는 곳 옆으로 옮겨 계속 관리한다.

   | 파일 | 새 위치 | 쓰임 |
   |---|---|---|
   | `localization.catalog.json` | `src/Panely.Core/Localization/` | 앱에 포함하는 한영 문구 |
   | `domain.schema.json`, `examples/` | `tests/Panely.Tests/Vectors/` | 저장 형식(state.json·번역 캐시·수정) 계약 테스트 |
   | `test-vectors.json` | `tests/Panely.Tests/Vectors/` | 펼침·맞춤·줌 등 계산 기대값 |
   | `assets/screenshots/18-translation-before-after.png` | `tests/Panely.Tests/Vectors/translation-before-after.png` | 말풍선 탐지 통합 테스트 입력 |
   | `assets/LICENSE-original.txt`, `assets/panely-icon-stacked.svg` | `assets/` | 원본 자산 라이선스 고지(배포본 동봉), 아이콘 원본 |

3. `bundle-manifest.json`과 그 해시 테스트, `design-tokens.json`, 01~09 문서, 나머지 스크린샷(macOS 화면이라 Windows 앱 자료로 쓰지 않음)은 지운다.
4. 앞으로 동작의 기준은 코드와 테스트다. 명세에 없던 선택, 명세와 다르게 한 선택, 나중에 이유를 알아야 할 선택은 `docs/adr/NNNN-제목.md`로 남긴다.
5. 저장 형식 스키마는 이제 이 저장소가 소유한다. 형식을 바꾸면 기존 사용자 데이터를 읽을 수 있는지 확인하고 스키마와 예제를 함께 고친다.

## 결과

- 코드 주석의 "06 문서", "05-data.md" 같은 명세 참조는 그대로 남아 있다. 지운 문서를 가리키므로, 해당 코드를 고칠 때 설명을 주석 자체로 옮기거나 ADR로 바꾼다.
- `docs/IMPLEMENTATION-STATUS.md`의 기능 ID(APP-01 등)는 지운 명세의 ID다. 표는 첫 포팅 시점의 기록으로 둔다.
- 명세 패키지 해시 테스트 1개가 사라졌다. 나머지 계약 테스트(스키마, 문구 key·치환자, test vector)는 그대로 돈다.
