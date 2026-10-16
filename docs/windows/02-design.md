# 디자인과 화면 구현

숫자의 단위는 논리 DIP다. 원본의 pt 수치를 그대로 대응시킨다. 200% 디스플레이의 32 DIP 버튼은 64 물리 픽셀이 된다. PNG의 해상도를 DIP 치수로 그대로 복사하지 않는다. 토큰의 기계 판독 값은 [design-tokens.json](design-tokens.json), 전체 화면은 [갤러리](assets/gallery.html)에 있다.

## 색·글꼴·간격

| 토큰 | 값 | 용도 |
|---|---|---|
| bgPrimary | #0F1115 | 리더 바탕 |
| bgSecondary | #1C1F26 | 사이드바·패널 |
| bgTertiary | #2A2E36 | hover·플레이스홀더·입력 배경 |
| textPrimary | #E6EAF2 | 본문·아이콘 |
| textSecondary | #AAB2C0 | 보조 설명·카운터 |
| borderSubtle | white, alpha .08 | 패널/컨트롤 경계 |
| accentPrimary | #0A84FF | 활성 버튼·진행·선택 |

간격은 4/8/12/16/24. 제목 20 semibold, 본문 14 regular, 캡션 12 regular. Windows 결정: UI 폰트 Segoe UI Variable → Segoe UI, 한글 fallback Malgun Gothic, CJK는 설치된 해당 언어 폰트. 라이선스가 확인된 폰트를 앱에 포함할 수 있다. 번역 레이어는 UI 글꼴 크기와 무관한 이미지 좌표계의 글자 크기를 쓴다.

UI 나타남/hover/선택 전환은 ease-out 120ms, 페이지 전환 토큰은 ease-out 150ms. 실제 이미지 준비를 기다리며 페이지 전환 애니메이션을 늘리지 않는다. Windows 애니메이션 감소 설정에서는 전환을 생략한다(Windows 결정).

## 창과 레이어

```text
┌ Windows 창/캡션: 드래그 영역, 최소화·최대화·닫기 ┐
│ Library 240 │             Viewer            │148│
│            │ [floating toolbar: top +12]    │썸 │
│ Files      │ [status banner: top +60]       │네 │
│ Continue   │                               │일 │
│ Favorites  │      image/translation         │   │
│ Bookmarks  │                               │   │
│ Volumes    │ [progress/edit: bottom +76]    │   │
│            │ [page counter + slider]       │   │
└────────────┴───────────────────────────────┴───┘
```

사이드바 섹션 순서는 이어보기 → (중첩 권이 있을 때) 권 목록 → 즐겨찾기 → 현재 책 북마크 → 다른 책 북마크 → 파일이다. 스크롤 안에서 빈 섹션은 적절히 숨기거나 빈 안내를 표시한다.

리더 레이어 순서는 이미지 → 번역 → 툴바/슬라이더 → 번역 진행/편집 바 → 권 카드 → 상태 배너 → 팝오버/시트 → 전역 로딩 차폐다. 상호 겹침은 뷰어 기준 좌표로 계산한다. 고정 라이브러리·썸네일은 뷰어 폭을 줄인다. 자동 숨김 라이브러리는 뷰어 위로 덮으며 페이지 위치와 배율을 바꾸지 않는다.

Windows 결정: macOS의 좌상단 신호등을 복제하지 않는다. 우상단 Windows 캡션 버튼을 사용하고 시스템 창 메뉴·Snap·DPI 변경·키보드 이동을 유지한다. 캡션 높이는 시스템값을 따른다. 800×600 최소치는 캡션을 제외한 콘텐츠다. 캡션/메뉴 영역을 이미지 맞춤 계산에 포함하지 않는다.

## 라이브러리

폭 240. 배경 bgSecondary, 헤더 가로 padding 8·세로 4, 32 DIP 새로고침/핀 버튼. 파일 아이콘 영역 폭 16, 이름 14, 확장자 12·textSecondary alpha .6. 폴더·아카이브 그림을 구분하고 활성 책은 파란 아이콘/이름으로 보인다. 행 상하 padding 2. 긴 이름은 말줄임표와 전체 경로 tooltip. 들여쓰기와 폴더 펼침 affordance를 유지한다.

미고정 상태의 왼쪽 12 DIP는 hot edge. 200ms 머무르면 펼치고, 패널에서 나가면 300ms 후 닫는다. 패널에 재진입하면 닫기 타이머 취소. 가장자리 hover 표시 선은 폭 3·accent alpha .35. 그림자 black alpha .45, blur radius 14, x=4. Escape 즉시 닫기. 패널 선택 후 리더에 포커스를 돌린다. 마우스 없는 사용자는 메뉴/단축키로 고정할 수 있다.

즐겨찾기는 별 아이콘, 제거는 hover/컨텍스트 메뉴로. 북마크 썸네일 24×34, radius 3; 쪽 번호와 제거 버튼 18×18. 읽기 링 11×11, 선 두께 1.5; 완독은 체크. 색만으로 상태를 전달하지 않는다. 이어보기에서 제목·진행을 보이고 항목 제거는 읽기 파일을 삭제하지 않는다.

## 플로팅 툴바

뷰어 위쪽·양쪽 inset 12. 높이 40 = 버튼 32 + 위아래 4. 바깥 radius 10, border 1. 원본은 ultra-thin material이다. Windows 결정: backdrop blur가 가능하면 쓰고, 없으면 bgSecondary alpha .94로 대체한다. blur 유무 때문에 배치·대비·기능이 달라지면 안 된다. 모든 항목은 아래 순서를 지킨다.

1. 열기 / 라이브러리 고정 / 툴바 고정.
2. 세로 구분선(높이 18, 양쪽 4).
3. single / double / vertical / 읽기 방향 / double에서만 표지 단독.
4. 화면 맞춤 / 가로 맞춤 / 세로 맞춤 / 축소 / 확대 / 크기 잠금.
5. 구분선, 즐겨찾기 / 페이지 북마크 / 북마크 목록 / 썸네일 / 번역 아이콘+언어 메뉴.
6. 최소 8 여유 공간, 이전 권 / 이전 쪽 / 다음 쪽 / 다음 권.

버튼 32×32, glyph 14 medium, radius 6. 기본 투명, hover bgTertiary, 선택 accent+white. 선택 핀은 채워진 아이콘. disabled도 사유 tooltip을 제공한다. tooltip은 버튼 아래 +4, 툴바 폭 안으로 수평 clamp, 포인터 이벤트 무시. 메뉴와 팝오버가 열린 동안 컨트롤이 사라져 조작을 잃지 않게 한다(Windows 결정).

폭은 고정 breakpoint로 자르지 않고 실측한다. 전체 버튼 → 맞춤/줌 그룹을 메뉴로 → 레이아웃 그룹까지 메뉴로 축약한다. 최대 번역 언어명·양쪽 패널·800 DIP 조건에서 이 세 가지로도 넘치면 낮은 우선순위 항목을 추가 overflow 메뉴에 넣는다(Windows 결정). 열기와 페이지 이동, 모든 명령의 접근은 유지한다. 화면 밖 잘림이나 클릭 불가 상태는 허용하지 않는다.

상/하단 각 80 DIP에 포인터가 있으면 툴바와 슬라이더를 함께 보여준다. 툴바 고정이면 항상 표시. 숨긴 UI는 hit-test와 Tab 순서에서 제외한다.

## 슬라이더와 미리보기

슬라이더는 책이 열려 있고 두 쪽 이상일 때만 표시. 뷰어 좌우 padding 24, 바닥 16. 카운터는 `Vol 1 / 3 · 5-6 / 24` 형식. 앞 번호를 클릭하면 입력창, Enter 확정·Escape/포커스 상실 취소. 폭 `max(32, digits(total)×9+10)`.

트랙 높이 2, thumb 지름 10, hit area 높이 20. bgTertiary 트랙, accent 채움, textPrimary thumb. 0..N-1 논리 인덱스·드래그 중 반올림, 끝에서 정수 스냅. RTL에서도 진행 방향은 왼쪽→오른쪽으로 유지. 번역 완료 쪽은 아래 높이 2의 마크. Windows 결정: 키보드 Home/End/화살표 및 UI Automation RangeValue도 제공한다.

오른쪽 썸네일 폭 148, 이미지 폭 112. 알려진 h/w를 .3..3으로 clamp, 미상은 1.5. 셀 간격 8, padding 8, radius 4, 활성 테두리 accent 2. 번호 캡션, 번역 완료 원형 배지. 현재 쪽/책 변경 때 활성 셀을 가운데로 스크롤한다. 천 쪽도 보이는 셀만 생성한다.

## 카드·오류·로딩

권 카드: maxWidth 360, padding 16, radius 14, regular material 위 bgSecondary alpha .55, subtle border, 그림자 black .35/radius18/y6. 제목 16 semibold, eyebrow 12 semibold, 작은 보조 11. Next 카드는 아래 inset24, Previous는 위 inset24. 버튼 padding12×8·radius8. 상태 배너/툴바와 겹칠 때 카드 위치를 이동한다(Windows 결정).

로딩: 전체 black alpha .45, 가운데 스피너·14 본문, padding16×12, radius12. 로딩이 새 열기를 영구 막지 않게 취소/후속 열기 정책을 연결한다. 이미지 오류 타일은 원래 쪽의 치수·인덱스를 유지하고 “이미지를 읽을 수 없음”과 파일명을 표시한다.

## 번역·설정 화면

| 화면 | 기준 크기/내용 | 참고 |
|---|---|---|
| 번역 패널 | 폭340, padding12. 헤더+실험 배지+설정, 주요 작업, 언어 View, 책·시리즈, 팁, 편집 | [19](assets/screenshots/19-translation-panel.png) |
| 번역 표시 | 언어명이 실제 보이는 언어를 가리킴, 원본도 명시 | [17](assets/screenshots/17-translation-reading.png) |
| 풍선 팝오버 | 원문·대상/다른 언어·편집/원문 수정/숨김/되돌리기. 텍스트 선택 가능 | [20](assets/screenshots/20-translation-balloon-popover.png) |
| 내보내기 시트 | 폭460, padding16, 언어·형식·미번역 정책·저장 위치 | [21](assets/screenshots/21-translation-export-sheet.png) |
| AI 채팅 시트 | 폭520, padding16, 전체/쪽 선택·파일 저장·프롬프트·응답 입력 | [24](assets/screenshots/24-ai-chat-sheet.png) |
| 작업 창 | 초기500×560, 최소420×320. 작업 카드 padding12·목록16 | [22](assets/screenshots/22-jobs-window.png) |
| 설정 | 최소560×280, 일반/라이브러리/저장소/진단/실험. 보통 폼 폭420, 진단560 | [12](assets/screenshots/12-storage-cache.png), [23](assets/screenshots/23-experimental-settings.png) |

가로 수치는 원본 기준이며 Windows caption/tab/padding을 더해 내용이 잘리지 않게 창 폭을 확보한다. 세로는 ScrollViewer로 넘친 내용을 스크롤한다. 모든 시트는 owner 중심에 열고 초기 포커스·Escape·Tab 순환·닫힌 후 원래 컨트롤 포커스를 제공한다.

## 아이콘과 접근성

SF Symbols 이름은 의미 참고만 사용한다. Windows에는 자체 vector path 또는 재배포 가능한 공개 아이콘을 포함한다. 열기=폴더, 펼침=두 사각형, 세로=쌓인 사각형, 맞춤=축 방향 화살표, 고정=핀, 잠금=자물쇠, 즐겨찾기=별, 북마크=리본, 번역=언어/문자, 작업=목록. 윤곽선 14 DIP 계열과 active filled 변형을 통일하고 버튼 의미를 tooltip·접근성 이름으로 제공한다.

키보드 포커스 링은 페이지 테두리와 구별되는 accent 2 DIP 외곽선으로 표시한다(Windows 결정). 고대비 모드에서는 시스템 강조/텍스트 색을 허용한다. 한글 200% 텍스트, 100/125/150/200% DPI, 혼합 DPI 모니터 이동에서 잘림·클릭 오차를 검수한다.
