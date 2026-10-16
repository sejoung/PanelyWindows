# 포함 자산과 출처

이 디렉터리를 포함한 `windows-port` 폴더 전체가 한 전달 단위다. 원본 저장소의 경로를 다시 찾을 필요가 없도록24개 PNG·앱 아이콘 SVG·원본 라이선스를 복사했다. 파일 해시는 [bundle-manifest.json](../bundle-manifest.json)에서 확인한다.

## 화면 자료

[gallery.html](gallery.html)을 브라우저에서 열면 전체 화면을 오프라인으로 볼 수 있다. CDN·외부 font·JavaScript·서버가 없다. PNG를 클릭하면 원래 크기로 열린다. 모두 기준 커밋의 **기존 macOS 샘플**이며 Windows 실행 증거가 아니다. 라이브러리 제목·페이지 번호·번역 내용은 샘플이다. 새 구현의 상태는01~08의 규칙을 따른다.

| 번호 | 화면 |
|---|---|
|01~04|한 페이지·두 페이지·세로·RTL|
|05~07|라이브러리 채움/빈 상태·툴바|
|08~11|끝/이전 권 카드·로딩·직접 이동|
|12~16|저장소·진단·표지 offset·라이브러리/언어 설정|
|17~20|번역 읽기·비교·패널·말풍선 팝오버|
|21~24|내보내기·작업 창·실험 설정·AI 채팅|

스크린샷은 스타일·구성 참고다. 포함된 macOS 시스템 아이콘 이미지를 잘라 Windows 버튼 자산으로 쓰는 용도가 아니다. glyph는02의 의미/치수에 맞춰 Windows용 vector로 준비한다. 스크린샷의 영어 키보드 기호를 Windows tooltip에 그대로 복사하지 않는다.

## 앱 아이콘과 라이선스

[panely-icon-stacked.svg](panely-icon-stacked.svg)은1024×1024 viewBox의 기존 앱 아이콘 원본이다. Windows `.ico`/installer 아이콘은 이 SVG에서 필요한16/24/32/48/64/128/256 크기를 export하고 작은 크기의 선명도를 확인한다. SVG 내부에 필요한 gradient/filter가 포함되어 있고 외부 파일을 참조하지 않는다.

[LICENSE-original.txt](LICENSE-original.txt)는 원본 저장소의 Apache-2.0 라이선스 전문을 그대로 복사한 것이다. 저작권 표기는 `Copyright [2026] [beni kim]`. 새 구현에서 재사용한 원본 자산의 출처/라이선스를 유지한다. 이 파일은 Windows에서 새로 선택할 모델·RAR 라이브러리·폰트·아이콘의 라이선스를 대신하지 않는다. 모델 가중치·macOS framework·SF Symbols font는 포함되지 않는다.

## 기록된 원본 위치

다음은 작성 근거를 식별하는 문자열이며 파일을 가져오라는 지시나 패키지의 의존 경로가 아니다.

| 자료 | 원본 위치 |
|---|---|
|24개 PNG|`docs/screenshots/*.png`|
|SVG|`docs/icon/panely-icon-stacked.svg`|
|라이선스|`LICENSE`|
|한영 문구|`Panely/Localizable.xcstrings` → 패키지의 catalog JSON|
|색·간격·폰트·모션|`Panely/DesignSystem/Tokens` 및 Primitives|
|기본값·시리즈·캐시·식별자|ReaderPreferences/ReaderSeriesPreferencesStore/ExtractionCacheStore/PositionKey|
|읽기 계산|SpreadCalculator/FitCalculator/ImageStackView/WheelPageTurnEngine|
|화면 배치|ReaderScene/ViewerArea/PanelyToolbar/LibrarySidebar/ThumbnailSidebar|
|읽기 복원·권·북마크|ReaderViewModel의 Source/LoadPipeline/Volumes/ReadingProgress/Bookmarks 확장|
|번역|TranslationController/Manager/Plan/OverlayLayout/PageEdits/AIChatExchange/Export|
|자동 갱신의 현재 기본값|AppDependencies의 live `libraryAutoRefreshEnabled:true`|

원본 설명서와 상충한 자동 갱신 기본값은 코드를 우선했다. 이어보기는 현재 열린 책도 포함하고, 임의 책 “읽음으로 표시” 메뉴는 필수 범위에 넣지 않았다. 툴바의 같은 fit 재선택 리셋은 현재 동작이며 이를 메뉴·단축키에 확장하는 것, Windows caption·충돌 없는 단축키·추출 경로 검사는 문서에 표시한 Windows 결정이다.

## 실제 데이터 예제

- [state.json](../examples/state.json): 아카이브 안 Volume01의 읽기 위치·북마크·시리즈 설정.
- [translation-page.json](../examples/translation-page.json): 일본어 풍선의 한국어 기계 번역.
- [page-edits.json](../examples/page-edits.json): 같은 풍선의 사용자 한국어 수정.
- [AI 요청](../examples/ai-chat-request.txt), [AI 응답](../examples/ai-chat-reply.txt): 파일 교환 형식.

이 예제의 경로/UUID/내용/모델 revision은 샘플이다. 배포 모델이나 실제 사용자 데이터를 가리키지 않는다. JSON Schema는 state를 root로 검증한다. 다른 두 JSON은 같은 schema의 `$defs.TranslatedPage`/`$defs.PageEdits`를 entry point로 사용한다. 참조 UUID 존재·bookKey 중복·안전한 상대경로·페이지 범위는 application validation도 필요하다.
