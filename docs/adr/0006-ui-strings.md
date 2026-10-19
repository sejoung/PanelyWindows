# ADR 0006: 화면 문구는 원본 카탈로그 위에 Windows 문구를 덧씌우고 테스트로 검사한다

- 상태: 채택(첫 구현 때 정한 것을 2026-10-19에 사후 기록·보강)
- 날짜: 2026-10-19
- 관련 코드: `Loc`, `src/Panely.Core/Localization/localization.catalog.json`, `src/Panely.Core/Localization/Strings/*.json`, `LocExtension`
- 테스트: `tests/Panely.Tests/Contracts/LocalizationLintTests.cs`

## 배경

원본 Panely의 문구 카탈로그(538개, 영어·한국어)를 그대로 받았다. 이 카탈로그에는 macOS 단축키 기호(⌘⇧⌥⌃), Finder, 시스템 설정, Apple Intelligence, "Mac" 같은 표현이 섞여 있다. Windows에만 있는 기능(DirectML, Windows OCR, 탐색기 연결 등)의 새 문장도 필요하다.

카탈로그를 직접 고치면 원본과 비교하기 어렵다. 또 코드에 영어를 바로 쓰면 한국어 UI에 영어가 그대로 보인다.

## 결정

1. 지원 언어는 영어와 한국어다. 기본은 시스템 언어를 따르고(한국어가 아니면 영어), 설정에서 바꾸면 메뉴와 창이 즉시 바뀐다.
2. 화면 문구는 모두 `Loc.T("영어 원문 key", 인자…)`로 가져온다. XAML은 `{sh:Loc 'key'}`를 쓴다. 조회 순서는 다음과 같다.
   1. `Strings/*.json`(Windows 문구)
   2. 원본 카탈로그
   3. 영어 key 그대로
3. 원본 카탈로그는 받은 그대로 두는 참고 snapshot이다. macOS 표현은 같은 key로 `Strings/*.json`에 en·ko를 다시 적어 덮는다(예: "Your text leaves your Mac…" → "이 PC 밖으로"). Windows에서 새로 생긴 문장도 이 파일들에 넣는다. `core.json`, `infra.json`, `reader.json`, `translation.json`으로 영역을 나눈다.
4. 원본 치환자(`%@`, `%lld`, `%1$@`)는 위치를 지켜 변환한다. 사용자 파일명이나 OCR 글자는 format string으로 해석하지 않는다.
5. 다음을 테스트로 막는다.
   - 코드가 쓰는 key가 문구 파일에 있는가. `Loc.T`, `Loc.TIn`, `MenuCommand`, `TopMenu`의 문자열 리터럴을 찾는다.
   - 한국어가 영어와 같지 않은가. 고유명사(CBZ, ZIP, Panely 등)는 예외로 둔다.
   - Windows 문구의 en·ko 치환자가 같은가.
   - 같은 key를 두 파일에서 다르게 정의하지 않았는가.
   - 화면에 보이는 문구에 macOS 표현(⌘ ⌥ ⌃ ⇧, Finder, macOS, Apple Intelligence, System Settings, 단어 "Mac")이 남지 않았는가. "Mac" 단어 검사는 2026-10-19에 추가했다. "Machine"처럼 다른 단어 안의 Mac은 잡지 않는다.

## 결과

- 새 화면 문구를 넣을 때는 카탈로그에 같은 뜻의 key가 있는지 먼저 찾는다. 있으면 그 key를 쓰고, 없거나 macOS 표현이면 `Strings/*.json`에 en·ko를 함께 넣는다. 빠뜨리면 `verify.cmd`가 실패한다.
- 변수로 key를 넘기는 곳(`Loc.T(key)`)과 문구가 아닌 고유명사("Panely", "English", "한국어")는 검사 대상이 아니다. 이런 곳을 새로 만들면 위 검사 정규식에도 추가한다.
- 카탈로그 key 중 코드에서 쓰지 않는 것은 원본에는 있었지만 Windows에서 빠진 기능을 찾는 단서가 된다(ADR 0007).
- 다른 언어를 더하려면 카탈로그와 `Strings/*.json`에 언어 열을 더하고, `Loc.Resolve`와 설정의 언어 목록, 위 테스트의 한국어 검사를 넓혀야 한다.
