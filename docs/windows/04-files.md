# 파일·아카이브·라이브러리·로딩

## 지원과 정렬

폴더와 cbz/zip/cbr/rar가 입력 소스다. 이미지 확장자는 `jpg jpeg png webp gif heic heif bmp tiff tif`. 대소문자를 구분하지 않는다. GIF/TIFF 등 다중 프레임은 첫 프레임을 정지 이미지로 읽는다. Windows에서도 HEIC/HEIF/WebP를 빠뜨리지 않는다. OS 코덱 존재 여부만으로 기능을 약속하지 말고, 설치되지 않은 깨끗한 PC에서 대체 decoder 또는 필요한 설치 흐름을 검수한다.

폴더 책의 쪽은 그 디렉터리 바로 아래 이미지다. 아카이브 책은 모든 이미지 entry의 전체 내부 경로를 자연 정렬한다. 폴더 이름순·권 목록도 같은 comparator를 쓴다. 정렬은 숫자 run을 숫자처럼 비교하여 `1,2,10` 순서다. Windows 결정: UI locale을 명시한 case-insensitive 자연 비교 후 동률은 ordinal 전체 경로로 정한다. Unicode·전각 숫자·한글·대소문자 동률을 fixture로 고정한다. macOS locale comparator와 모든 문자열의 순서가 일치한다고 가정하지 않는다.

숨김 파일, 점으로 시작하는 경로 구성 요소, `__MACOSX`, 리소스 포크 `._*`를 제외한다. Windows Hidden/System 속성도 제외한다(Windows 결정). `.txt`, 실행 파일 등 비이미지는 쪽으로 세지 않는다. 목록에 들어간 이미지가 깨졌다면 해당 쪽을 삭제하지 않고 오류 placeholder로 남긴다. 미지원 입력·비어 있는 책·암호 아카이브·손상 아카이브·해제 한도·권한 오류를 구분해서 설명한다. 암호 입력 기능은 원본에 없다.

## 형식 판별과 reader 계약

ZIP magic `50 4B 03 04` 또는 `50 4B 05 06`, RAR 접두 `52 61 72 21 1A 07`를 검사한다. 확장자보다 실제 헤더를 우선한다. 판별 불가/읽기 실패는 확장자로 reader를 선택하여 구체적인 열기 오류를 낸다. cbr 이름의 ZIP과 cbz 이름의 RAR도 읽는다.

```text
ArchiveReader:
    sourceRef
    listEntries() -> 고유 파일 경로 목록 (디렉터리 제외)
    readEntry(path, cancellation) -> bytes
    readPrefix(path, maxBytes, cancellation) -> bytes
```

한 아카이브당 접근을 직렬화하거나 공급자가 보장하는 동시성만 사용한다. ZIP 중앙 디렉터리를 여러 번 읽지 않도록 검사 단계에서 만든 reader/listing을 signature가 같으면 실제 로드에 넘긴다. RAR 4/5, solid, Unicode 경로, CRC 오류, 취소, 큰 파일 테스트를 모두 통과한 reader를 고른다. 특정 macOS wrapper를 Windows에서 재사용할 필요는 없다.

## 폴더/겉포장/시리즈 결정

```text
analyzeFolder(folder) -> 직접 이미지 존재 여부, 자연정렬한 하위 폴더+아카이브
if 직접 이미지 존재: folder 자체가 책
else if 하위 권 목록이 비어 있음: 빈 소스 오류
else: 첫 하위 항목으로 내려가 같은 규칙 반복
```

하위 항목 한 개는 겉포장(wrapper), 두 개 이상은 시리즈 레벨이다. 내려가며 찾은 시리즈 목록을 유지하고, 단일 wrapper 목록이 바깥 시리즈를 덮어쓰지 않게 한다. 안쪽에 별도 다권 시리즈를 발견하면 그 목록이 더 구체적인 시리즈다. `SeriesA/Vol01.zip -> 722/722 pages/*.jpg`를 열어도 다음 권은 SeriesA의 Vol02이고 `722 pages`가 아니다.

일반 파일을 직접 열면 부모의 하위 폴더·지원 아카이브를 sibling으로 자연 정렬한다. 저장한 innerPath로 바로 진입할 경우 target에서 허용 root까지 올라가며 가장 가까운 다권 레벨을 찾고, 없으면 바깥 목록을 쓴다. 자동 탐색에 실제 경로 방문 집합을 두어 junction/symlink 루프를 막는다(Windows 결정).

파일 트리는 루트 아래 폴더/아카이브를 지연 탐색하며 currentSource의 조상을 펼친다. 현재 루트 내부 항목 선택은 루트를 보존한다. 중첩 캐시의 내부 권은 원본 트리에 없으므로 별도 Volumes 섹션에 표시한다. 일반 폴더 권은 Files에 이미 있으므로 중복 섹션을 만들지 않는다.

## 중첩 아카이브와 추출

평평한 아카이브는 전체 추출 없이 entry 단위로 읽는다. 지원 아카이브 entry가 있으면 cache staging으로 전체 해제 후 내부 아카이브도 폴더로 해제한다. 원본의 재귀 깊이 3은 내부 아카이브뿐 아니라 디렉터리로 내려갈 때도 증가한다. 이를 문서상 “아카이브 3개 중첩 허용”으로 바꿔 해석하지 않는다. Windows 기준도 외부 해제 후 `walk(depth=0); depth>=3이면 중지; 하위 폴더/내부 해제 폴더는 depth+1`로 고정한다.

누적 추출 한도는 5×1024³ bytes(5 GiB). 각 해제 전에 선언 크기를 확인하고 실제 쓰기 중에도 한도를 적용한다(실제 streaming 한도는 Windows 보완 결정). 내부 아카이브를 지우고 해제 폴더로 바꾸는 경우 누적량은 `oldTotal - archiveBytes + expandedBytes`. overflow는 포화 덧셈으로 처리해 무한히 작아지지 않게 한다. 실패·취소 시 미완료 staging만 정리한다.

Windows 보완 결정: `..`, 절대경로, 드라이브 경로, UNC, ADS `:`, reserved device name, 목적지 밖 symlink/junction을 거부한다. canonical destination이 추출 root 안인지 각 entry마다 확인한다. 파일 수·단일 이미지 픽셀·메모리도 별도 상한을 두고 명확한 오류를 낸다. 원본 숫자가 없는 상한은 실제 대형 책 샘플로 정하고 릴리스 설정에 기록한다.

## 영속 해제 캐시

예산 10×1024³ bytes(10 GiB), 최근 접근 순 LRU. 입력 identity는 정규화 경로·파일 크기·mtime milliseconds. 원본 key는 이 세 값을 `path|size|mtimeMillis`로 묶어 SHA-256 첫 8bytes의 소문자 hex16. Windows 결정: 새 캐시는 전체 SHA-256을 써도 되며 저장 형식에 버전을 둔다. 경로와 캐시 디렉터리 이름을 reader의 영속 bookKey로 사용하지 않는다.

히트는 완성된 entry만 인정한다. 새 해제는 독립 staging에서 끝내고 원자적으로 publish한다. 읽는 중·번역/내보내기가 참조하는 entry는 lease로 보호한다. LRU와 수동 Clear 모두 lease entry를 제외한다. 따라서 총량이 예산보다 잠깐 커질 수 있고 보호 해제 후 정리한다. 캐시 UI는 total/clearable/limit를 구별한다. 로딩 중 Clear 비활성, 현재 책 캐시 유지 안내.

소스 변경은 signature를 바꾸므로 재추출한다. 경로·크기·mtime가 모두 같은 내용 교체를 탐지한다는 보장은 없다. Windows에서 content hash 강화 시 버전을 달리한다. extraction, translation, edits, diagnostics는 각각 독립 정리 영역이다.

## 로딩 파이프라인

```text
open intent 접수 -> 이전 책 위치 flush -> epoch 증가·이전 로드/페이지 작업 취소
-> 소스 접근/형식 확인 -> 아카이브 해결/캐시·추출
-> 저장 상대 경로 확인 -> 폴더 해결·sibling 선택
-> 쪽 목록 -> 시리즈 설정·명시 쪽/저장 위치 적용
-> 현재 이미지·vertical 치수/placeholder -> reader에 반영
-> 소스 감시/최근·진행 연결
```

await 뒤마다 epoch를 검사한다. A를 연 직후 B를 열면 A의 성공·오류·spinner 종료·캐시 결과가 B의 화면 상태를 덮지 않는다. 이미 시작한 decode를 중단할 수 없다면 완성 결과를 캐시에 보관할 수 있지만 stale UI에는 게시하지 않는다. 실패 시 이전 책의 source와 새 책의 페이지가 섞인 부분 상태를 남기지 않는다. 라이브러리 트리 문맥은 가능한 유지한다.

저장 쪽 복원은 일반 열기·트리 선택·즐겨찾기·이어보기·이전 권에 적용, 다음 권은 첫 쪽. 다른 책 북마크는 명시 pageIndex가 모든 복원보다 우선한다. 이어보기/북마크의 지정 innerPath가 사라지면 오류를 내고 다른 첫 권을 대신 열지 않는다. 즐겨찾기는 best-effort fallback을 허용한다.

## 이미지 로딩 정책

- 헤더로 치수만 읽는다. 아카이브는 우선64KiB prefix, 실패 시 전체 entry. UI 스레드에서 decode하지 않는다.
- 페이지 모드는 현재 펼침을 우선, 인접 ±2쪽을 미리 읽는다. 슬라이더 연속 이동은 직전 페이지 작업을 취소한다.
- vertical 최초 현재 쪽 ±3, 가시 범위가 생기면 범위 양쪽 +2. 보존 버퍼는 가시 범위 양쪽 +10이지만 메모리 예산을 넘으면 먼 이미지를 먼저 버린다.
- 작업 동시성 `max(2,min(8,CPU count))`. 결과를 배치 게시하여 이미지 한 장마다 전체 레이아웃을 다시 만들지 않는다.
- 이미지 메모리 예산은 RAM/16을 150MiB..1GiB로 clamp, 최대100개 캐시. 대략 w×h×bytesPerPixel로 비용을 계산한다. 압축 파일 크기로 메모리 예산을 계산하지 않는다.
- 원본 render view의 visible 앞뒤 한 viewport materialization, 재사용 pool24는 참고값이다. Windows 구현도 가상화하고 천 쪽을 모두 시각 객체로 만들지 않는다.

decode 실패한 쪽은 보이는 동안 매번 재시도하지 않는다. keep window 밖으로 나가면 실패 상태를 해제하여 돌아올 때 다시 읽을 수 있게 한다. 전체 reload도 재시도한다. 페이지 모드에서는 새 펼침의 decode가 끝날 때 이미지와 committed pageId들을 함께 교체한다. 슬라이더가 가리키는 새 인덱스만 기준으로 아직 남아 있는 이전 이미지 위에 새 쪽 번역을 그리지 않는다.

## 변경 감시와 새로고침

현재 열린 아카이브 파일 또는 열린 폴더와 그 페이지 이미지 변경을 감시한다. 새 페이지 추가·삭제·수정 시 Reload/Dismiss 배너만 띄우고 자동 페이지 이동은 하지 않는다. 파일 저장의 짧은 rename/delete burst는 debounce 후 다시 확인한다. 네트워크 드라이브·이동식 드라이브의 일시 끊김을 영구 삭제로 간주하지 않는다.

라이브러리 자동 갱신은 현재 코드의 live 기본값 **true**다. 기존 설명서의 “비활성” 기술은 이 기준 커밋과 다르다. 재귀 변경 알림을 1.5초 debounce하고 필터링하여 트리 diff를 적용한다. Windows에서도 기본 활성으로 구현하되 cache·로그·무관한 파일 쓰기를 제외한다. 자동 갱신과 헤더 수동 새로고침 모두 펼침·선택·스크롤을 보존하고 목록을 잠깐 비우지 않는다. 현재 책 소스 변경 배너는 트리 자동 갱신과 독립이다. 앱이 활성화되면 최근/이어보기의 가용성을 갱신한다. Windows 감시 버퍼 overflow 시 수동 새로고침을 안내하고 무한 재스캔하지 않는다.
