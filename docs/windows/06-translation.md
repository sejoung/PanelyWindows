# 번역·편집·AI 교환·내보내기

최종 기능 동등성에는 이 문서 전체가 포함된다. 번역은 로컬에서 실행하고 원본 이미지 파일을 수정하지 않는다. 자동 외부 API 호출은 없다. 사용자가 AI 채팅으로 텍스트를 전달할 때만 앱 밖으로 내용이 나간다. Apple 전용 모델을 Windows에서 호출할 수 있다고 가정하지 않는다. 제공자는 [07-windows.md](07-windows.md)의 미결 항목을 해결해야 한다.

## 기능·언어·상태

기본 off인 Translation Assist를 실험 설정에서 켠다. 언어는 `ja`, `zh-Hans`, `zh-Hant`, `ko`, `en`. 기본 대상은 UI 언어이며 미지원이면 en. 원본은 macOS26 이상에서 지원하며 Fast는26.4 이후, 그 전에는 Accurate로 실행한다. Windows UI에서는 이 macOS 조건을 노출하지 않고 실제 모델/하드웨어 능력을 설명한다.

번역 작업 phase는 idle / detectingLanguage / preparing / translating / paused / queued. 완료는 phase=idle과 completedPages로 표현할 수 있다. 사유는 needsLanguageDownload / unsupportedPair / couldNotDetectLanguage / failed다. UI는 작업 상태·사유·재개 가능 여부를 따로 보유한다. Windows 내부 모델에 completed/failed를 명시적으로 추가해도 된다.

첫 실행에는 모델 로딩·준비를 표시하고 아직 pace가 없으면 가짜 ETA를 쓰지 않는다. 원본의 “1~2분”은 Mac 경험이며 Windows 성능 보장이 아니다. 현재 페이지부터 뒤쪽을 먼저 번역하고 남은 앞쪽으로 돌아간다. 쪽은 OCR 결과가 나오자마자 말풍선과 `…`를 표시하고 번역이 도착하는 대로 채운다. 완료 쪽만 영속 캐시에 저장한다.

## UI 흐름

툴바 번역 버튼은 번역이 없으면 패널을 열고, 결과/실행 작업이 있으면 원본↔번역을 전환한다. 옆 언어 메뉴 이름은 **실제로 보이는 언어**다. 시리즈 선호가 Korean이어도 새 권에 한국어 결과가 없으면 Original을 표시하고 “이 책을 한국어로 번역”을 제안한다. 원본 표시 중에도 풍선을 클릭해 원문/번역을 볼 수 있다.

패널 구성: 제목·실험 배지·설정 → Translate/Download/Resume/Pause 주요 버튼 → 팩 상태·진행 → View(원본과 대상 언어별 완료량) → 다른 언어 번역 → This Book & Series(원어, 자동 번역, 전체 시리즈, 책 재번역, AI, 내보내기) → 풍선 클릭 팁 → Edit Balloons. 언어별 더보기에서 시작/일시정지/중지. 책을 보이는 언어와 백그라운드에서 만드는 언어를 분리한다.

현재 책 번역 진행과 다른 책/내보내기 진행 pill은 뷰어 하단76 위. 클릭하면 Jobs. 번역 완료 쪽은 thumbnail 배지와 슬라이더 마크. 원본/번역·편집 선택이 다른 책의 작업을 취소해서는 안 된다.

## 공급자 계약

```text
LanguagePackProvider:
  capability(source,target,mode) -> supported/installed/downloading/unavailable(reason)
  install(pack,cancel) -> progress, checksum-verified installed package
TextRecognizer:
  recognize(image,language,regions?,cancel) -> TextRegion[]
BalloonDetector:
  detect(image,cancel) -> DetectedRegion[] or unavailable
Translator:
  translate([{id,text}],source,target,mode,cancel) -> [{id,text/error}]
SpeechLevelCorrector:
  correct(original,translation,source,target,cancel) -> candidate/unchanged
```

Translator는 결과를 입력 순서로 가정하지 말고 ID로 맞춘다. 빈 결과·다른 언어·오류를 성공으로 캐시하지 않는다. 파이프라인의 epoch/generation을 결과에 붙이고 소스/언어/모드/재번역 변경 뒤의 오래된 결과를 버린다. 읽기용 이미지 decode와 OCR 작업 자원 사용을 분리한다.

## 탐지·OCR

TextRegion: text, rect(x,y,w,h), isVertical, glyphSize. 모든 rect는 회전 보정된 **페이지 픽셀의 좌상단 원점**이다. glyphSize는 세로 글자 열 너비 또는 가로 줄 높이다. 공급자의 정규화/좌하단 좌표는 어댑터에서 변환한다.

탐지 참조 모델은 `ogkalu/comic-text-and-bubble-detector`, RT-DETR-v2. 원본 계약은640×640 RGB stretch, 0..1, `[1,3,640,640]`, scores `[1,300,3]`(sigmoid 적용), boxes `[1,300,4]`(normalized cx,cy,w,h). class0 balloon,1 textInBalloon,2 freeText. threshold .4. 한 query가 여러 class를 통과할 수 있다. box는 `(cx-w/2)*W,(cy-h/2)*H,w*W,h*H`로 변환, score 내림차순. Windows ONNX output 이름·layout·활성화는 모델별 manifest에서 검증한다. sigmoid를 두 번 적용하거나 letterbox를 stretch처럼 취급하지 않는다.

현재 [모델 카드](https://huggingface.co/ogkalu/comic-text-and-bubble-detector)는 Apache-2.0, 학습640 리사이즈와 세 class를 명시한다(2026-10-16 확인). 새 프로젝트는 특정 revision·파일 hash·라이선스를 기록한다. 이 문서 묶음에는 가중치나 변환 프로그램이 포함되지 않는다.

탐지는 긴 이미지에서 `chunkHeight=max(W*3/2,640)`, H가 chunkHeight×4/3보다 크면 overlap=chunkHeight/3으로 자른다. seam에 걸린 rect는 제외하고 다시 페이지 좌표로 이동. 같은 kind의 두 rect가 작은 면적 대비 교집합>.5면 score 높은 것을 남긴다.

OCR 타일은 탐지 chunk와 다르다. `tileHeight=max(2400,floor(W*3/2))`, H>max(tileHeight,2W)이면 overlap=floor(tileHeight×5/12). step=tileHeight-overlap. 마지막 band는 남은 높이로 잘라 끝까지 덮는다. 처음/마지막이 아닌 seam에서6px 이내로 닿는 OCR rect는 버린다. 타일 간 중복은 작은 면적 대비 교집합>.5일 때 텍스트가 긴 것을 남긴다.

탐지 text box는 있는데 OCR이 비어 있으면 해당 crop을 확대하여 재인식한다. 동시 crop reread2, 분석 decode 캐시 최대4이미지/48,000,000픽셀은 원본 기준 참고 예산. 인식하지 못한 글자도 사용자가 영역을 추가할 수 있으므로 전체 페이지 실패로 처리하지 않는다.

## 풍선 그룹과 필터

원문 normalization 후 자체 문자가 없는 잡음을 제거한다. 일본어는 kana/han, 중국어 han, 한국어 hangul, 영어는 ASCII 알파벳2개 이상. 한글/영어 줄 결합은 공백, 일본어/중국어는 빈 문자열.

탐지 결과가 있으면 텍스트 영역의 .5 이상을 덮는 text box 중 coverage 최대를 고른다. textInBalloon이면 그 text box를 .5 이상 덮는 가장 작은 balloon을 container로 사용한다. freeText는 자체 container로 남긴다. text box가 없으면 영역을 .5 이상 덮는 가장 작은 balloon을 찾는다. 같은 container는 한 풍선으로, 서로 다른 container는 섞지 않는다.

탐지 없는 영역은 보수적인 연결 요소로 묶는다. glyph=max(1,min(glyphA,glyphB)). 세로/가로 방향이 같아야 한다.

| 방향 | 결합 조건 |
|---|---|
| 세로 | gapX<1.3glyph, overlapY>.3×작은 높이, top차<2.5glyph |
| 가로 | gapY<.8glyph, overlapX>.5×작은 폭 |

풍선 내부는 높이 겹침>.3일 때 일본어·중국어/세로 글자는 오른쪽 열부터, 그 외 왼쪽부터, 겹치지 않으면 위부터 읽는다. 풍선 전체는 top 오름차순, top차≤1이면 centerX 내림차순으로 ID 부여. 일본어 한 열이 두 조각으로 잘려 별도 문장이 되지 않게 한다.

효과음은 번역하지 않는다. 단순히 “짧으면 제외”하면 이름·대답을 잃는다. 참조 규칙: 알파벳 성격의 문자≤6, han이 있으면 제외하지 않음; 일본어 촉음/작은 모음 종결·장음·최소2글자 단위 반복·짧은 비음 리듬, 한국어 반복/끝 음절 중복/모음 교대·단음절 효과음만 보수적으로 제외. 문장 내부 구두점이 있으면 한국어 반복 대사는 남긴다. en/zh는 이 효과음 heuristic을 적용하지 않는다. 검수 예: `スッ`, `パチパチ`, `짝짝` 제외; `はい`, `メラス`, `비켜, 비켜!`는 유지. 새 모델에 맞춘 세부 분류는 품질 fixture와 함께 버전 관리한다.

## vertical strip OCR

짧은 이미지 조각들을 **읽기 화면과 같은** maxWidth·수평 중앙·연속 y 공간으로 합쳐 overlapping band 단위로 인식한다. 각 이미지를 따로 OCR하여 seam의 풍선 두 조각을 따로 번역하지 않는다. band 크기는 OCR tile 규칙과 같다.

각 band의 ownedRange는 첫 band lower=0, 나머지 top+overlap/2; 마지막 upper=stripHeight, 나머지 top+height-overlap/2. ownedRange가 strip을 겹치거나 빠짐 없이 분할한다. 풍선 중심 Y를 소유하는 band에서 한 번만 결과를 채택한다. 해당 원본 쪽에 page-local 좌표로 보관하되 rect가 다음 이미지 아래로 뻗을 수 있다. 한 페이지를 담당하는 모든 unit이 완료되어야 그 페이지를 완료 캐시로 기록한다.

## 결과·캐시·말투

TranslatedPage: imageWidth,imageHeight,bubbles,isComplete,speechLevelChecked?. Bubble: id,rect,glyphSize,isVertical,source,translation(nullable),background(r,g,b,isUniform),balloonRect?. pageID/다른 언어/편집 표시는 런타임 조합 값으로 캐시 원본과 분리한다.

캐시 identity는 pageId·source>target·scope(page|strip)·effectiveMode·analysisVersion·provider/model revision을 포함한다. 원본 형식 버전은2이며 Windows는 별도 schema/provider 버전을 둔다. page/strip을 섞지 않고 모드 변경은 별도 번역으로 취급한다. 손상 캐시는 miss로 보고 다시 생성한다. 언어별 완료 상태는 독립이다.

말투 맞춤은 ja↔ko에 적용하는 선택적 로컬 후처리다. 원문의 종결을 근거로 존대·반말을 보정하되 단어/시제/부정/의문·명령 유형을 바꾸면 원래 번역을 유지한다. 사용자의 직접 수정은 덮지 않는다. 기존 캐시도 미확인 플래그이면 다음 로드에서 확인할 수 있다. Windows에 동등한 보정기가 없으면 옵션을 효과 있는 것처럼 켜두지 말고 준비 불가 사유를 표시한다. 최종 전체 완료에는 검증한 보정 경로 또는 명시적인 기능 차이 승인이 필요하다.

## 번역 패치 렌더링

배경은 풍선 텍스트 주변 톤을 sampling하여 r,g,b와 flat 여부를 저장한다. luminance=.299r+.587g+.114b, .5 미만이면 white 글씨, 아니면 black. 풍선 내부 여백에 잉크/그림이 있으면 무조건 전체 흰 사각형으로 덮지 않는다. 원본은 풍선 내부 추가 잉크 비율 .03을 안전 판정 참고값으로 쓴다.

기본 patch=원문 rect에 .3glyph padding. flat 배경+balloonRect가 있으면 balloonRect의 중앙66% 사각형과 union. 그렇지 않고 세로 글이면 최소 폭 `min(patchHeight*.9,glyph*9)`까지 중앙 확장. 페이지 폭으로 clip, 높이는 원문 자체가 페이지 밖으로 나가는 경우까지 허용한다.

두 원문 rect가 겹치면 연결 그룹의 한 patch에 번역을 ID순 줄바꿈으로 그린다. 글자가 겹치지 않는데 padding만 겹치면 원문 사이 중간선까지 확장을 줄이되 원문 자체를 자르지 않는다. 그룹 풍선은 각 원문 rect+padding을 개별 hit area로 유지한다.

패치 radius=max(4,.5glyph), flat이면 solid 배경; artwork면 alpha .92+feather radius=max(6,.8glyph). 내부 padding .3glyph. 번역 글자 semibold·수평 중앙·수직 중앙, 크기6..max(.95glyph,8)에서 들어가는 최대치를 이진 탐색(정밀도 .5px). 한글 어절 우선 줄바꿈, 영문 한 단어가 폭을 넘지 않게 측정. artwork 위에는 반대색 외곽선과 fill을 분리 그려 내부 glyph 획선이 드러나지 않게 한다.

동일한 patch geometry와 text painter를 뷰어·내보내기가 공유한다. 확대·DPI 때문에 클릭 지점이 풍선에서 벗어나지 않게 역변환한다.

## 사용자 수정

읽기 중 풍선 클릭 팝오버/우클릭에서 Edit Translation, Fix Original, Hide, Revert 가능. 페이지 우클릭에는 Translate This Page Again, Add a Balloon Here, AI Chat, Edit Balloons를 둔다. 편집 모드는 Ctrl+Shift+E, Done/Escape 종료. 연필은 직접 수정, 숨김은 점선. 텍스트 Enter 저장·Shift+Enter 줄바꿈·Escape 취소. 원본의 Alt/Ctrl+Enter 줄바꿈도 받을 수 있다.

PageEdits는 pageId+scope로 저장한다. 한 풍선 수정은 recognizedSource+원래 rect가 식별 근거이며 새 분석에서도 원문 동일+rect intersection이면 매칭한다. 숫자 bubble ID만으로 영속 매칭하지 않는다. 수정은 다음 우선순위를 따른다.

1. 숨김이면 읽기/내보내기에서 제외, 편집 모드에는 점선으로 유지.
2. 해당 target의 사용자 translation이 있으면 그것을 표시.
3. correctedSource의 재번역 결과가 있으면 표시.
4. correctedSource를 재번역 중이면 pending, 미처리이면 기존 번역+재번역 필요 표시.
5. 아무 수정 없으면 기계 번역.

원문 수정·숨김은 모든 언어에 적용, 직접 번역은 해당 언어에만 적용한다. correctedSource 변경 시 모든 보유 언어를 다시 번역하되 사용자 번역을 덮지 않는다. Revert는 번역만/원문만/둘 다를 선택할 수 있다. 숨김 후 Undo, 한 풍선 복구, 숨김 전체 표시, 책 전체 수정 되돌리기를 제공한다.

편집 드래그로 추가한 풍선은 확대 crop OCR을 시도한다. 없으면 빈 풍선을 만들어 Fix Original/직접 번역으로 채운다. added는 glyphSize/isVertical/background를 가지고, Hide 대신 Remove다. 원본 런타임 추가 ID는1,000,000+수정 배열 index. Windows는 UUID를 써도 된다. 기계 캐시 삭제·모드 변경·재번역으로 사용자 edits를 지우지 않는다. 새 분석에 맞지 않는 edit는 적용되지 않을 수 있으며 별도 저장은 보존한다.

## AI 채팅 파일 교환

앱이 AI 서비스에 직접 요청하지 않는다. 기본 전체 책, Choose Pages로 좁힘. hidden/빈 번역은 제외, 쪽 오름차순·풍선 ID순으로 한 번 export snapshot을 만든다. ID는 표시 쪽1-based와 그 export의 풍선 순서1-based를 조합한 `p12-3`. 편집 이후에도 응답을 이 snapshot에 매핑하며 다른 책에 적용하지 않는다.

```text
# Panely — "Sample" — Japanese → Korean — 2 balloons, pages 1–2
# Each line: [id] original → current translation
[p1-1] こんにちは → 안녕하세요
[p2-1] ありがとう / またね → 고마워 / 또 봐
```

파일 저장·프롬프트 복사·응답 `.txt` 열기·응답 붙여넣기를 제공한다. 프롬프트는 의미 보존·화자 말투·이름 일관·풍선 길이·모든 ID 회신을 요구한다. 원본 전체 문구는 [localization.catalog.json](localization.catalog.json)에 있다.

응답 parser 계약: 각 줄 앞의 공백/`>`/`*`/`-`/`•`/backtick을 허용, `[p숫자-숫자]` 뒤 `:：.-` 구분 허용. 같은 ID는 마지막 줄 우선, 마지막 `→` 뒤 텍스트만 채택, 양끝의 한 쌍 `""`/`“”`/`「」` 제거, ` / `는 줄바꿈. 빈 내용 무시, 모르는 ID는 count만, export에 있었는데 답에 없는 ID는 순서대로 보고. 변경된 것만 해당 언어의 사용자 수정으로 적용하고 unchanged/unknown/missing 수를 보여준다. Undo는 적용 전 PageEdits snapshot을 복원한다. 파일 읽기는 UTF-8(BOM 허용)을 기본으로 하고 다른 인코딩은 명시 처리한다(Windows 결정).

## 작업 스케줄러와 시리즈

번역 활성 작업은 앱 전체 한 번에1개. 큐는 현재 화면의 책/대상 언어 우선, 다음은 대기순. 사용자 pause는 queued와 구별하여 자동 재개하지 않는다. 일반 책은 다른 책으로 이동해도 계속; 중첩 cache 책은 떠날 때 일시정지하고 돌아오면 재개할 수 있다. cache lease를 지원해 이 제약을 없애려면 Windows 추가 결정으로 기록한다.

전체 시리즈 번역은 sibling 전체가 아니라 제목 stem이 같은 권만 미리 보여주고 큐에 넣는다. stem은 확장자 제거, 소문자, 괄호/대괄호/중괄호/【】/（）tag 제거, volume/vol/v/ch/chapter/ep/episode/# 및 숫자, `1권/2화/3巻/4話/5卷/6集`, `제/第` 숫자, 나머지 ASCII·전각 숫자 제거 후 영숫자만 남긴다. `Story v01`, `Story v02 (Digital)`는 같고 다른 제목은 다르다. `01`, `02`는 빈 stem으로 같은 목록에서 매칭된다. 내보내기 시리즈는 사용자가 선택한 폴더의 권 목록도 허용하므로 번역 제목 필터를 모든 경우에 강제하지 않는다.

종료하면 실행 중 작업은 멈추고 완료 캐시는 남는다. 원본은 모든 작업 큐를 디스크에 완전 복원하는 계약이 없으므로 다음 시작에서 같은 책을 다시 시작하면 캐시 이후로 이어간다. Windows에서 durable queue를 추가하면 별도 버전으로 처리한다. 실패 background 작업은 이유와 dismiss가 가능하게 남긴다.

## 번역본 내보내기

진입점: 툴바 언어 메뉴, File, 패널, 라이브러리 책/시리즈 우클릭. 안 열어 둔 일반 책도 가능, 중첩 책은 열려 있을 때만 보장한다. 시트는 target / cbz|zip|folder / translateFirst|exportAsIs / destination. 선택을 기억하고 목적지 폴더는 한 번 고른 뒤 재사용한다. 번역이 전혀 없는 target의 ExportAsIs는 nothingTranslated 이유를 표시한다.

책 하나당 job, preparing → translating(필요하면 공유 번역 큐) → waiting → exporting(done,total) → finished/failed/cancelled. 렌더링/쓰기는 한 책씩 제한하며 읽기를 막지 않는다. cancelled는 늦은 task 성공이 finished로 바꾸지 못한다. 실패는 이유·Try Again, 실행 중은 Cancel. 완료 기록은50개 영속, Open/Show in Explorer 제공.

파일 이름은 `Title [언어명]`, 충돌하면 ` 2`, ` 3`를 붙이고 덮어쓰지 않는다. Windows 금지문자·reserved name·끝 공백/마침표도 정리한다. 파일명 번호 폭=max(3,digits(pageCount)), `001.jpg`부터 논리 읽기 순서. CBZ/ZIP은 동일 ZIP 컨테이너이고 이미 압축된 이미지 entry는 store로 저장 가능하다.

원본 형식이 PNG이고 번역을 그렸으면 PNG, 그 외는 JPEG quality .92. 그릴 번역이 없는 쪽은 확장자와 원본 bytes를 그대로 복사한다. 번역 쪽은 원래 픽셀 해상도로 렌더링, 사용자 edit 적용·hidden 제외. strip 풍선이 다음 slice로 넘어가면 다음 이미지에도 위 풍선 patch의 넘친 부분을 dx=(현재W-이전W)/2, dy=-이전H로 옮겨 그린다. 화면과 같은 렌더러를 사용한다.

출력 디렉터리에 고유 `.partial` staging으로 만들고 완료 후 원자적 rename. 취소/오류는 partial만 정리. 파일명 충돌을 마지막 rename 시점에도 확인하여 경합에서 원본/기존 출력을 덮지 않는다. 디스크 부족·권한 상실은 failed 이유로 남기고 사용자 edits/번역 캐시는 보존한다.
