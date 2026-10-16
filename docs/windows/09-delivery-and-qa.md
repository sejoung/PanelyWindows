# 구현 순서·동등성 검수·완료 조건

이 패키지를 작성하면서 Windows 앱을 구현하거나 macOS 앱을 다시 실행하지는 않았다. 포함 이미지는 기존 샘플 snapshot이며 새 Windows 실행 결과가 아니다. 이 문서는 앞으로 수행할 검수와 목표를 정의한다.

## 구현 단계

| 단계 | 산출물 | 통과 기준 |
|---|---|---|
| 0 기반 검증 | reader render prototype, RAR/codec fixture 결과, detector/OCR/MT 공급자 실험 | 큰 이미지·CJK 세로·5언어·배포 가능성을 실제 PC에서 확인 |
| 1 디자인 shell | 다크 창·양쪽 패널·툴바·메뉴·고정/hover·800×600 |02 치수와 모든 입력 접근,125/200%DPI |
| 2 읽기 코어 | 폴더·ZIP/RAR·세 레이아웃·RTL·fit/zoom·휠·권 카드 |03 계산 vector와 탐색 경계 통과 |
| 3 저장/라이브러리 | 트리 diff·watcher·최근·진행·북마크·즐겨찾기·설정·진단 | 재실행/이동식 드라이브/급속 열기/데이터 정리 검수 |
| 4 번역 | provider·팩·strip·patch·여러 언어·큐·편집 | 아래 번역 품질/취소/보존 검수 |
| 5 교환/출력 | AI 파일 round-trip·원자적 export·Jobs·이력 | 잘못된 ID·partial·slice seam·원본 hash 보존 |
| 6 릴리스 | installer·file association·native dll·라이선스·기능 표 | 깨끗한 Windows PC에서 최종 기능 모두 통과 |

0의 번역 공급자 실험을 뒤로 미루지 않는다. UI가 끝난 뒤 핵심 모델을 실행할 수 없다는 사실이 드러나면 전체 동등성이 불가능해질 수 있다. 단계2나3만 끝났다면 “리더 구현 단계”로 이름 붙인다.

## 독립 패키지 검수

새 빈 디렉터리에 `windows-port`만 복사하고 README에서 모든 상대 링크가 열리는지 확인한다. 상위 docs/소스/테스트/이미지 참조가 없어야 한다. HTTP 링크는 근거·공개 공급자 확인용이며 본문 규칙을 이해하기 위해 필수로 열 필요가 없어야 한다. manifest에 있는 파일 크기·SHA-256을 확인한다. 이미지24개·아이콘·license·문구·JSON이 함께 전달되어야 한다.

## 테스트 fixture 만들기

원본 저장소 fixture 없이 다음 샘플을 새 프로젝트에서 생성한다. 페이지에 큰 번호·위/아래·좌/우·경계 색을 그려 자동 이동을 검증한다. 테스트에 사용할 이미지 내용/폰트는 재배포 가능한 것으로 만든다. 포함 screenshot은 UI 참고이며 OCR 품질 정답 데이터는 아니다.

| 샘플 | 구성 | 확인 |
|---|---|---|
| single-one | 이미지1개 | 슬라이더 숨김·빈 next·fit |
| order-24 | `1,2,10,01,한글,Ａ` 등24쪽 | 자연정렬 고정·숨김 제외 |
| double-odd/even | 각5/6쪽·크기 다른 좌우 | 표지 offset·마지막 단독·RTL·같은 높이 |
| tall-webtoon | 720×12000 및720×576 여러 조각 | lazy·현재 쪽·crop OCR·slice seam |
| short-tail | 긴 중간쪽+매우 짧은 마지막쪽 | 끝 스크롤 시 N-1·완독·Next 카드 |
| series-wrapper | Series/Vol01.zip/722/722 pages + Vol02 | wrapper가 sibling을 덮지 않음 |
| nested | 디렉터리 깊이0..4와 zip-in-zip 혼합 | 깊이 예산·재추출 key·내부 권 위치 |
| archives | ZIP, ZIP 이름cbr, RAR4/5/solid, RAR 이름cbz | 실제 형식·CRC·cancel·Unicode |
| failures | 손상 이미지·암호·빈·불가용·추출 한도 경계 | 오류별 상태·레이아웃 보존 |
| translation-golden |5언어·수직/수평·그림/flat·흑풍선·겹침·효과음 | OCR bbox·문장·patch·편집·export |

위험한 경로/큰 선언 크기 fixture는 실제 대용량 데이터를 채우지 않고 작은 synthetic archive metadata/stream으로 한도·방어를 검사한다. 정상 큰 책도 별도로 검수한다.

## 기능 ID별 수용 기준

| ID | 최소 검수 절차와 기대 |
|---|---|
| APP-01 | 앱 닫힘/열림 각각 탐색기 파일 열기. 리더 한 개, 마지막 명시 요청이 화면 소유 |
| APP-02 | Windows가 밝은 테마여도 기본 다크. en↔ko 즉시 메뉴·팝오버 변경, fallback en |
| FILE-01 | 위 archive matrix 전부 열고 쪽 수·CRC 실패·solid 뒤쪽 접근 확인 |
| FILE-02 | 10확장자를 codec 없는 clean PC에서 확인, 첫 프레임·회전·오류 타일 유지 |
| FILE-03 | wrapper/nested를 열고 다음 권·5GiB 경계·10GiB LRU·lease·path traversal 확인 |
| FILE-04 | A 느린 열기 직후 B. B 화면에 A 결과/오류 없음. 디스크 변경 후 위치 고정 배너 |
| READ-01 | N=0,1,2,5,6 모든 index·cover on/off·레이아웃 전환, 펼침 vector 일치 |
| READ-02 | RTL 시 화면 좌우만 반전, Space/휠 논리 순서 고정, vertical에서 RTL 무시 |
| READ-03 | 세 fit 수식·재선택 reset·Ctrl+wheel anchor·pinch·baseline doubleclick 확인 |
| READ-04 | 잠금/수동줌/자동맞춤 조합·같은/다른 시리즈·재실행 session zoom 해제 |
| READ-05 | slider scrub 취소·쪽 입력 clamp/잘못된 값·thumbnail click·focus 복귀 |
| READ-06 | 새 권 첫 쪽 Previous 없음→backward 준비→다음 이동, 마지막 forward 다음 권 첫 쪽 |
| READ-07 |1000쪽·초고해상도·긴 strip에서 전부 decode하지 않음, 메모리 상한·UI 응답 |
| UI-01 |199ms hover 미표시/200ms 표시,299ms 이탈 유지/300ms 닫기, 재진입 취소·Esc |
| UI-02 | 800 DIP+양 패널+번역5언어명에서 버튼 잘림 없음. auto-hide hit-test/Tab 제외 |
| UI-03 | 긴 책 thumbnail 가상화, 열기/복원/쪽 전환에서 활성 위치·번역 배지 일치 |
| LIB-01 | 파일 생성/rename/delete 자동 diff(1.5s), 수동 refresh, 조상·펼침·선택 유지 |
| LIB-02 | double 마지막 펼침 finished sticky, 짧은 tail, 현재 책 포함 이어보기·Restart |
| LIB-03 |51개 열기→50저장/10메뉴, 드라이브 분리 복구, invalid/temporary·명시 제거 |
| LIB-04 |북마크 한쪽 toggle·이전/다음·타책·일괄 삭제·500/200cap·즐겨찾기 내부 권 |
| LIB-05 | cold start root, 명시 열기 우선, reopen off/Forget, 시리즈별 설정 복원 |
| SET-01 |5개 탭·바뀐 기본값·스크롤·disabled 사유·작업 중 cache clear 불가 |
| DIAG-01 |768KiB trim·256KiB report·3파일 ZIP·level 변경·경로 축약·Clear 격리 |
| TR-01 | off에서 숨김, 팩 없음/다운로드 취소/체크섬/미지원 쌍·언어 감지 수정 |
| TR-02 | 영역 grouping·CJK 수직·효과음·reread·strip seam·pending→complete 저장 |
| TR-03 | ko 표시하며 en 작업, 현재 책 우선·user pause·시리즈 stem·background 유지 |
| TR-04 | patch bounds·겹침·클릭·원문 selectable·배경 톤·말투 의미 보존 |
| TR-05 | 언어별 직접 수정/전언어 원문·hide·added·revert·cache clear에도 edit 보존 |
| TR-06 | 전체/선택 페이지 export·UTF8·codeblock 응답·중복/누락/unknown·일괄 Undo |
| TR-07 | CBZ/ZIP/folder·translateFirst/asIs·파일명 충돌·원본 bytes·92%JPEG·seam |
| JOB-01 | current/background/export 상태·실패 이유·Retry·Cancel terminal·완료50재실행 |

검수 표는 각 ID마다 실제 build/OS/기기/fixture/결과/증거 링크/제약을 남긴다. 미검증·실패를 pass로 바꾸지 않는다.

## 시각 검수

참고 이미지는 [assets/gallery.html](assets/gallery.html)에서 비교한다. 원본 색/패딩/폭/버튼/구성은 유지하고 Windows caption·폰트 glyph·아이콘만 허용된 플랫폼 차이다. 원본 screenshot은 서로 다른 크기의 샘플이므로 동일 viewport/DPI로 맞추고, 페이지 샘플 그림 내용은 UI 비교에서 마스킹한다.

| 화면군 | 검수 상태 |
|---|---|
|1~4 reader | single/double/vertical/RTL, sidebar 유무, fit, image alignment |
|5~7 sidebar/toolbar | populated/empty, 모든 controls, narrow 축약, keyboard focus |
|8~11 boundary/loading/jump | 카드·차폐·counter·input |
|12~16 settings/offset | 저장소·진단·표지·라이브러리·언어 |
|17~20 translation | 실제 언어명·before/after·패널·편집 풍선 |
|21~24 export/jobs/experimental/AI | 폼·오류·완료·탭·긴 문구 |

100/125/150/200% DPI와 혼합 DPI, en/ko, 최대 길이 제목,800×600/1280×800/1920×1080 콘텐츠에서 확인한다. pixel diff는 같은 Windows 렌더러·폰트로 만든 baseline 사이에만 자동 판정한다. macOS와 bit-exact diff 통과를 요구하지 않는다.

## 번역 품질과 성능 게이트

Windows용 OCR/MT/말투 선택은 문서 작성 시 미완료다. 모델 선택 단계에서 재배포 가능한 골든 세트와 사람 검수로 기준을 고정한다. 제안: 각5언어 최소20페이지, 수직 일본어/중문·한글 대사·짧은 응답·이름·효과음·seam 풍선·어두운 풍선 포함. CER/누락/잘못된 풍선 합치기, 사람이 평가한 의미·부정/시제·화자 말투를 기록한다. 탐지 ONNX는 같은 input의 검증 원모델과 box/score 비교를 한다. 수치 기준을 측정 없이 이미 달성했다고 적지 않는다.

설계 목표(기존 앱 실측값 아님):16GB RAM·SSD 기준 cache-hit 쪽 넘김 p95<100ms, 입력에 대한 visual feedback<100ms, 읽기 중 긴 UI freeze 없음. 실제 측정은 decode/cache/OCR GPU 공유를 구별하고,4K 이미지·1000쪽·10배줌·긴 strip의 RAM/VRAM peak·IO·cancel 지연을 남긴다. 성능 미달은 파일을 생략하여 해결하지 않는다. 재현 기기와 샘플 없이 수치를 보장하지 않는다.

## 최종 완료

모든 기능 ID가 통과하고, 모델/codec/RAR·폰트/아이콘·installer의 재배포 자료를 포함하며, 새 Windows PC에서 원본 저장소 없이 실행/복원/번역/내보내기를 재현했을 때 전체 포팅 완료다. 패키지 목록·정확한 버전·hash·native dependency·모델 manifest·알려진 차이·테스트 증거를 새 프로젝트에 보관한다. 기능 제외가 필요하면 그 차이를 명시적으로 합의하고 제품 표를 수정한다.
