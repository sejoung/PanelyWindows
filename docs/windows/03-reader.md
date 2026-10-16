# 읽기 엔진의 동작과 계산

이 문서의 인덱스는 0부터, 사용자에게 보이는 쪽 번호는 1부터다. 페이지 수 N은 음수가 될 수 없다. 모든 범위는 `[start,end)`다. UI·복원·북마크·슬라이더·키보드는 같은 계산을 사용한다. 계산 예는 [test-vectors.json](test-vectors.json)에 있다.

## 펼침

single/vertical의 step=1, double의 step=2. offset은 double이면서 표지 단독일 때만 1, 나머지는 0이다.

```text
spread(index,N,step,coverAlone):
    if N<=0 or step<=0: return [0,0)
    i = clamp(index,0,N-1)
    offset = (coverAlone and step>=2) ? 1 : 0
    start = (i<offset) ? 0 : offset + floor((i-offset)/step)*step
    length = (start<offset) ? 1 : min(step,N-start)
    return [start,start+length)
nextStart = end<N ? end : null
previousStart = start>0 ? spread(start-1).start : null
```

| N=6 | 인덱스 그룹 | 화면 번호 |
|---|---|---|
| double, cover=false | (0,1) (2,3) (4,5) | 1-2, 3-4, 5-6 |
| double, cover=true | (0) (1,2) (3,4) (5) | 1, 2-3, 4-5, 6 |

`jump(index)`는 spread.start로 맞춘다. `jumpPage(number)`는 1..N으로 clamp 후 1을 빼고 jump한다. 표지 옵션을 바꿨을 때 같은 페이지가 포함된 새 펼침으로 이동하고 인덱스가 같아도 이미지를 다시 구성한다. 레이아웃 전환에서도 페이지는 유효 범위/새 펼침 경계로 맞춘다. wide page를 자동으로 단독 처리하는 별도 예외는 없다.

## 방향과 탐색

논리 페이지 순서는 자연 정렬 결과 그대로다. double RTL은 화면에서만 두 이미지를 뒤집는다. 페이지 카운터·저장·북마크·번역 ID는 뒤집지 않는다. vertical의 effectiveDirection은 LTR이고 실제 표시/스크롤은 위→아래다. 저장된 RTL 선택은 지우지 않고 돌아오면 다시 적용한다.

| 입력 | LTR | RTL | vertical |
|---|---|---|---|
| Right | forward | backward | forward |
| Left | backward | forward | backward |
| Space | forward | forward | forward |
| 휠 아래/위 | forward/backward | forward/backward | 실제 스크롤 |
| 툴바 다음/이전 쪽 | next()/previous() | next()/previous() | 해당 쪽으로 이동 |

툴바의 페이지 버튼은 책 안에서만 이동한다. 키보드·휠의 forward/backward는 권 경계 안내를 함께 처리한다. `[`/`]`와 권 버튼은 명시적 권 이동이다. bare bracket은 keydown만 받고 autorepeat는 막는다. 텍스트 입력 중에는 이 규칙을 적용하지 않는다.

## 권 경계

끝 펼침이고 다음 sibling이 있으면 Up next 카드가 즉시 보인다. forward 한 번으로 다음 권을 연다. 다음 권이 없으면 카드가 없다. 다음 권은 **첫 쪽부터** 연다. 명시적 다음 권 이동도 같은 load intent를 사용한다.

이전 권은 비대칭이다. 단순히 새 책 첫 쪽을 열었을 때 Previous 카드가 뜨지 않는다. 첫 쪽에서 backward하면 카드를 준비하고, 한 번 더 backward하면 이전 권을 연다. 이전 펼침에서 backward로 첫 쪽에 도착했다면 바로 준비하여 다음 backward에서 권을 넘긴다. 첫 쪽을 벗어나거나 다른 소스를 열면 준비 상태를 해제한다. 이전 권은 저장된 읽기 위치를 복원하며, 저장 위치가 없으면 첫 펼침이다.

명시적 권 이동이 실패하면 폴더 접근 필요 / 첫 권 / 마지막 권을 구분한다. 첫/마지막 안내는 3초 후 해제, 권한 안내는 사용자 동작까지 유지한다. 일반 desktop Windows에서는 macOS 보안 권한 요청 대신 실제 디렉터리 읽기 가능 여부를 검사한다.

## 이미지의 문서 공간

이미지 픽셀과 화면 DIP를 분리한다. 원본 EXIF 회전을 적용한 픽셀 크기를 기준으로 문서를 배치한다(Windows 결정). 확대 전에 문서 내부 pageRect를 만든다.

double의 공통 높이 H=max(h1,h2). 각 페이지의 displayHeight=H, displayWidth=wi×H/hi. 둘은 간격 없이 나란히 배치한다. 합계 폭이 펼침 폭. 비율이 서로 달라도 높이는 같다. H를 맞추기 위해 원본 데이터 자체를 리샘플 저장하지 않는다.

vertical은 원본 폭을 같게 늘리지 않는다. W=max(wi), yi=앞 페이지 높이 합, pageRect=((W-wi)/2, yi, wi, hi). 간격 0. 작은 이미지가 가운데 놓인다. 전체 높이=sum(hi). 모든 페이지 rect와 placeholder는 디코딩 전에 확보한다.

화면 변환의 한 예:

```text
screenDIP = contentOriginDIP + (pageOriginDoc + pointPx*pageScale - scrollDoc)*zoom
pagePx = (((screenDIP-contentOriginDIP)/zoom + scrollDoc)-pageOriginDoc)/pageScale
```

창→DIP 변환은 DPI 계층에서 한 번만 한다. 번역·클릭·드래그·확대 중심 모두 같은 행렬/역행렬을 사용한다. 이미지보다 뷰포트가 크면 여분을 반씩 나누어 중앙에 놓고, 커지면 clamp된 패닝을 허용한다. 오버레이 스크롤바로 뷰포트 크기가 변하지 않게 한다.

## 맞춤과 확대

문서 크기 D=(w,h), 뷰포트 V=(vw,vh):

```text
fitScreen = min(vw/w, vh/h)
fitWidth = vw/w
fitHeight = vh/h
어느 차원이든 <=0이면 1
```

single/double은 표시 문서 크기를 사용한다. vertical의 fit 기준은 **첫 페이지 rect 크기**다. 긴 전체 스트립을 화면 높이에 맞추지 않는다. zoom 허용 범위 .1..10, 버튼 배수 1.25, 더블클릭은 fit baseline ↔ baseline×2. baseline 근접 허용오차는 더블클릭 .01, 툴바 isAtFit .001. 단순 원본 1×↔2×가 아니다.

맞춤 버튼은 `fitMode==target && abs(zoom-baseline)<.001`일 때만 파랗게 표시한다. 수동 확대하면 셋 모두 비선택. 원본 툴바는 같은 모드를 다시 누르면 별도 resetZoom 경로로 baseline에 복귀한다. 선택값이 바뀌어야만 갱신되는 방식으로 만들지 않는다. Windows 결정: 이 재선택 리셋을 메뉴·단축키에도 동일하게 적용한다(원본 메뉴는 같은 fitMode 항목을 비활성화한다).

Ctrl+wheel은 커서 위치를 문서 좌표로 바꾸고 그 점을 유지하며 확대한다. 원본의 연속 delta 기준 배수는 `1 + delta*.01`, 결과를 .1..10으로 clamp. Windows WHEEL_DELTA=120을 그대로 넣으면 안 된다. Windows 결정: 일반 휠 한 notch=zoom delta10(약 10%), 정밀 delta는 DIP 상당 값으로 정규화하고 핀치는 연속 scale로 전달한다. 이 보정 계수는 입력 어댑터에 한 곳에 둔다.

## 재맞춤·잠금·시리즈 줌

새 baseline은 geometry 변경 때 계산하되 현재 zoom을 재설정할지는 다음 규칙으로 정한다.

```text
if force or not initialFitApplied: reset
else if sizeLocked: preserve
else if userHasZoomedAwayFromPreviousBaseline: preserve
else: reset
```

force는 명시적 맞춤/Reset, 시리즈 기억이 없는 새 책에 사용한다. 창·고정 사이드바·레이아웃 변경은 크기 잠금 중 force를 만들지 않는다. 자동 재맞춤이 켜져 있어도 수동 줌은 보존한다. UI의 Lock View Size는 autoFitOnResize의 역수다.

시리즈 이동 시 기억하는 값은 절대 zoom이 아니라 `zoom/oldBaseline`의 수동 배수다. 같은 시리즈 새 권의 baseline에 배수를 곱한다. 다른 시리즈에 처음 들어가면 맞춤으로, 같은 세션에서 돌아오면 해당 시리즈 배수 복원. 앱을 종료하면 이 기억은 사라진다. 영속 맞춤 모드와 섞어 저장하지 않는다.

## vertical 현재 쪽과 가시 범위

pageTops를 이진 탐색해 뷰포트 중심 Y를 포함하는 쪽을 현재 쪽으로 잡는다. 맨 위에만 닿으면 0, 맨 아래에만 닿으면 N-1로 고정한다(문서 좌표 epsilon .5). 이 규칙으로 아주 짧은 마지막 credits 이미지도 완독 처리된다. 전체 스트립이 화면에 들어와 양끝에 동시에 닿으면 중심 규칙을 쓴다.

가시 범위는 viewport 상단/하단이 걸치는 쪽까지 포함한다. 현재 쪽만 로드하지 않는다. jump는 대상 쪽 시작 y로 이동하되 기존 수평 pan은 유효 범위에서 보존한다. 페이지 인덱스 업데이트는 이미지 재로딩을 일으키지 않고 필요한 가시 범위만 요청한다.

## 휠 페이지 넘김 상태 머신

단일/두 장에서만 작동. ctrl-zoom과 핀치가 우선, 사용자가 옵션을 끄면 모두 pan. 콘텐츠가 세로로 들어오거나 해당 방향 경계에 있을 때만 넘길 수 있다. 경계 epsilon은 문서 좌표 1. 아래는 논리 forward로 정규화된 입력을 기준으로 한다.

| 입력 종류 | 규칙 |
|---|---|
| 일반 마우스 휠 | 경계에서 notch 즉시 1회, 이후 300ms cooldown. 남는 이벤트 소비 |
| phase가 있는 정밀 터치패드 | began에서 초기화. 첫 유효 delta가 경계 방향이면 자격 확보. 누적 60 DIP에서 1회 넘김 |
| 넘긴 제스처의 나머지/관성 | 전부 소비. 새 페이지 pan 또는 추가 넘김 금지 |
| 경계 아닌 곳에서 시작한 제스처 | 같은 제스처 중 경계에 닿아도 계속 pan. 새 제스처가 필요 |
| 방향을 되돌린 제스처 | 누적값이 반대로 넘어가면 자격 해제, pan |
| phase 없는 정밀 장치 | 경계 누적60, 1회 latch. 마지막 입력 후 250ms 넘게 쉬면 reset |

레이아웃·휠 옵션을 바꾸면 상태 머신 초기화. Windows 드라이버에서 신뢰할 수 있는 phase가 없는 경우 정밀 무phase 경로를 사용하며, 일반 WM_MOUSEWHEEL을 무조건 터치패드 제스처로 추정하지 않는다. OS delta 부호와 사용자 자연 스크롤 설정은 어댑터에서 한 번 정규화한다.
