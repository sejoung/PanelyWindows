# 데이터·복원·설정·진단

Windows 저장은 새 스키마다. macOS UserDefaults나 security-scoped bookmark의 바이너리 호환은 요구하지 않는다. 필요한 의미와 상한을 보존한다. JSON 계약은 [domain.schema.json](domain.schema.json), 기본값은 [design-tokens.json](design-tokens.json)에 있다. 스키마의 예제 타입은 앱 내부 클래스 이름을 강제하지 않는다.

## 저장 위치와 원자성

Windows 결정: 사용자별 `%LOCALAPPDATA%/Panely/` 아래 `Data/state.json`, `Data/edits/`, `Cache/extraction/`, `Cache/translation/`, `Cache/thumbnails/`, `Diagnostics/`, `Models/`. 사용자가 선택한 내보내기 폴더는 이 구조 밖에 있다. cache Clear가 Data/Models를 재귀 삭제해서는 안 된다.

state에는 `schemaVersion:1`, settings, sources, positions, progress, recents, favorites, bookmarks, seriesPreferences, exportHistory, exportOptions, lastLibraryRoot를 둔다. 날짜는 UTC ISO8601, bytes/count는 정수, 쪽 인덱스는 0-based. 같은 디렉터리의 temp에 쓰고 flush 후 replace. 복원 가능한 직전 사본 유지. 손상 파일은 격리·진단 기록 후 기본값으로 시작하고 사용자 편집 원본을 덮지 않는다. 미지원 미래 schema는 읽기 전용/복구 안내로 처리한다.

## SourceRef와 식별자

SourceRef는 `id(UUID), path, kind(folder|archive), title, volumeId?, fileId?`를 가진다. 실제 파일을 가리키는 source와 내부 책을 구분한다. BookRef는 `sourceId, innerPath?`. innerPath는 바깥 아카이브/컨테이너에서의 정규화 상대 경로이고 임시 해제 root가 아니다.

bookKey Windows 결정: `source:<UUID>` 또는 `source:<UUID>#<normalized innerPath>`. 상대 경로 구분자는 `/`, escaping/대소문자 규칙을 버전으로 고정한다. 파일 경로 비교는 파일 시스템 특성을 반영하며 대소문자 구별 디렉터리를 무조건 lower-case 하지 않는다. 새 경로에도 같은 파일 ID/volume ID를 확인할 수 있으면 SourceRef 경로를 갱신하여 모든 BookRef가 유지된다. 확인 불가이면 원래 경로를 보존하고 위치 찾기/재연결을 제공한다.

원본은 path를 primary, volume+file identity를 fallback으로 사용하고 중첩 책은 `outer#inner`로 구별한다. 새 UUID 방식은 이 의미를 보존하면서 경로/임시 디렉터리 교체에 대한 이식을 단순하게 한다. 같은 제목이라고 다른 파일을 합치지 않는다. 현재 위치·진행·북마크·즐겨찾기·번역 편집이 서로 다른 책으로 붙지 않는 것이 핵심이다.

pageId는 bookKey+정규화 이미지 상대경로/entry 경로+contentSignature로 만든다. signature에는 크기·mtime, 아카이브라면 외부 파일 signature를 포함한다. Windows 결정: 안정적인 원본 identity를 이용해 cache 재추출에도 pageId가 유지되게 한다. 실제 원본이 바뀌면 캐시를 무효화한다. edit가 이전 페이지에 붙는지 확인할 근거가 없으면 자동 적용하지 않고 별도로 보존한다.

seriesId는 일반 책의 부모 디렉터리, 중첩 책은 바깥 아카이브 identity를 기준으로 한다. 번역의 제목 매칭과 리더의 이 폴더 기반 시리즈 정의는 서로 다르다.

## 데이터와 상한

| 데이터 | 필드/순서 | 유지·정리 |
|---|---|---|
| Position | bookKey, pageIndex, updatedAt | 최대4000, 2초 debounce; 책 전환/종료 즉시 flush |
| Progress | page, total, finished, updatedAt | 최대4000, 오래된 것부터, 2초 debounce |
| Recent | id, sourceId, title, openedAt | 중복 갱신·최근순,50개 저장/10개 메뉴 |
| Favorite | id, BookRef, title, addedAt | 사용자가 제거할 때까지, 원본 명시 상한 없음 |
| Bookmark | id, BookRef, pageIndex, createdAt | 책당500·전체200책; 쪽 순 표시 |
| SeriesPreferences | direction?, layout?, fitMode?, translationSource?, translationTarget?, autoTranslate?, updatedAt | 최대2000 시리즈, 최근순 보존 |
| ExportRecord | id,title,target,format,path,pageCount,bytes,date | 최근50개, 재실행 유지 |
| PageEdits | pageId/scope별 풍선 수정 | 사용자 명시 제거까지; cache 정리와 분리 |

상한을 넘는 북마크는 해당 책에서 가장 오래된 것, 책 개수 초과는 가장 최근 북마크 시각이 오래된 책부터 제거한다. 참조 없는 book metadata도 정리한다. duplicate page toggle은 새 항목을 더하지 않고 제거한다. double에서는 현재 펼침 시작 쪽을 toggle 대상으로 삼는다. 툴바 목록의 현재 위치 표시는 visible range를 쓰지만 toggle 활성 여부는 시작 쪽만 검사한다. 다음/이전 북마크는 currentPageIndex보다 엄격히 큰/작은 쪽을 찾으며 없으면 비활성. 자동 wrap하지 않는다. jump 결과는 펼침 시작으로 snap한다. Windows 보완 결정: 펼침 안 두 번째 쪽 북마크로 같은 펼침에 계속 머무는 경우, 다음 탐색은 visible range 밖을 찾게 하여 반복 정지를 방지한다.

debounce는 메모리의 pending 값을 즉시 조회 가능하게 한다. A책 저장 대기 중 B책으로 전환해도 A 최종 위치가 사라지지 않게 flush한다. 종료·세션 비활성 저장을 연결하고 자주 바뀌는 vertical 스크롤이 디스크 전체 재쓰기를 매 프레임 유발하지 않게 한다.

## 읽기 진행과 이어보기

`fraction = total>0 ? clamp((page+1)/total,0,1) : 0`. double의 마지막 펼침에서는 fraction이 1보다 작아도 finished=true일 수 있다. finished 판정은 [03-reader.md](03-reader.md)의 nextStart가 없는지로 계산한다. 이미 완독인 책을 앞쪽에서 잠깐 읽는 것만으로 완독을 자동 해제하지 않는다(같은 total). 페이지 수가 바뀌면 새 유효 값으로 재평가한다.

권 끝 카드의 처음부터 다시 읽기는 position을0, finished=false로 만들고 실제 첫 쪽을 연다. 임의의 책을 수동으로 “읽음/읽는 중”으로 바꾸는 메뉴는 현재 구현 범위에서 확인되지 않았으므로 필수 기능으로 추가하지 않는다. 기존 위치만 있고 total을 모르면 정밀 링 대신 일반 읽는 중 표식을 사용한다.

이어보기는 현재 열려 있는 책도 포함하여, 사용 가능하고 total>0인 unfinished 진행 중 가장 최근 것을 제안한다. 동률은 최근 소스의 탐색 순서를 따른다. 최근 목록에 있는 바깥 아카이브의 내부 책, 기억한 폴더의 하위 책도 포함한다. 존재 확인은 백그라운드에서 하고 UI 계산 때마다 아카이브를 열지 않는다. 이어보기 제거는 진행 제안 기록만 제거한다. 파일 삭제·즐겨찾기·북마크 삭제로 연결하지 않는다.

최근 항목 가용성은 available / temporarilyUnavailable / invalidReference / unknown으로 나눈다. 이동식 드라이브를 잠시 뺀 상태에서 항목을 자동 삭제하지 않는다. 실패 배너의 Remove from List는 해당 recent와 그 source 진행 기록을 명시적으로 제거한다. 전체 최근 Clear는 최근 목록을 비우는 동작이며 사용자 북마크/수정은 유지한다.

## 복원과 시리즈 설정

마지막 라이브러리 root는 파일 직접 열기와 구분해서 기억한다. cold start 때 reopen=true이고 명시 열기 요청/로딩이 없으면 복원한다. false로 바꾸어도 저장 root는 보존한다. Forget Now는 저장 root만 제거하고 다음 폴더 열기는 다시 기억한다. OS 파일 열기가 늦게 도착해도 root 복원이 이를 덮지 않게 load epoch/launch intent를 공유한다.

시리즈 override는 nullable이며 새 시리즈는 전역 기본값을 사용한다. 방향·레이아웃·맞춤 변경은 해당 시리즈와 전역에 기록한다. 번역 출발 언어·대상·자동 시작은 시리즈 단위다. 확대 배수·열린 풍선·hover·편집 draft·Previous 준비 상태는 세션 상태다.

## 설정 5개 탭

| 탭 | 컨트롤 |
|---|---|
| 일반 | System Default / English / 한국어. 즉시 창·메뉴·팝오버 갱신 |
| 라이브러리 | 마지막 폴더 다시 열기, Last opened 경로·tooltip, Forget Now |
| 저장소 | Extraction total / Clearable / 10GiB limit / Clear, 로딩·보호 상태 안내 |
| 진단 | 앱 버전·빌드·Windows 버전·세션 ID·파일 로그 크기·정책·레벨·내보내기·삭제·폴더 |
| 실험 | 번역 보조, 기본 언어, Fast/Accurate, 말투 맞춤, 언어팩 상태, 수정 쪽 수·전체 수정 삭제, 번역 캐시 용량·삭제 |

Windows는 앱이 소유한 메뉴를 즉시 바꾸므로 macOS의 시스템 메뉴 재시작 경고를 그대로 노출하지 않는다. native 언어 재시작이 실제 필요한 구성이라면 읽는 책을 보존한 Restart Now를 제공하고 해당 경우만 설명한다.

모든 북마크 일괄 삭제·책 수정 되돌리기·전체 수정 삭제·로그 삭제는 대상과 영향을 명시해 확인한다. 개별 toggle·cache 정리는 해당 동작의 명세를 따른다. 번역 캐시 삭제 시 작업을 멈추고 in-flight 결과가 다시 기록되지 않도록 generation을 바꾼다. 사용자 edits는 별도 삭제 버튼만 지운다.

## 진단

레벨은 trace/debug/info/notice/warning/error/critical, 기본 info. 이벤트 category는 app/reader/load/cache/library/persistence/image/diagnostics. session UUID·launch UTC를 기록한다. 텍스트 로그 한 개 `recent-log.txt`, 최대768KiB 초과 시 최신 약384KiB로 trim, 리포트는 최신256KiB. 유효 UTF-8/줄 경계에서 자르고 log I/O 실패로 리더를 중단하지 않는다.

리포트 이름은 `Panely-Diagnostic-Report-yyyyMMdd-HHmmss.zip`. 다음3파일을 포함한다.

| 파일 | 내용 |
|---|---|
| diagnostic-report.md | 앱/빌드/OS·세션·캐시 total/clearable/limit·로그 정책·설정·리더 상태·최근 오류·경로 축약 설명 |
| recent-log.txt | 제한된 최신 로그 |
| recent-load-events.txt | 최근 열기/로딩 이벤트, 파일명·확장자·종류 중심 |

절대 경로·사용자명·OCR 원문·번역 본문을 이벤트 metadata로 자동 기록하지 않는다. Windows 결정: 시스템 예외 문자열의 알려진 경로도 축약하고 UNC credential·사용자 폴더를 마스킹한다. “모든 시스템 오류에 개인정보가 절대 없다”고 주장하지 않는다. 내보내기는 사용자 저장 위치로만 쓰며 자동 전송하지 않는다. 로그 Clear는 Diagnostics 파일 로그만 제거하고 OS 이벤트·읽기 기록·캐시·북마크에는 영향을 주지 않는다.
