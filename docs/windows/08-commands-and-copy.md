# Windows 명령·포커스·한영 문구

단축키는 Windows 결정이다. 원본 Command는 보통 Ctrl, Option은 Alt로 옮기되 Command+Control pin 단축키는 충돌을 피해 Ctrl+Alt로 정한다. OS의 Ctrl+Alt+Delete, Alt+F4, Alt+Space, Win 키 조합은 가로채지 않는다. 입력 언어/IME와 충돌하는 키는 메뉴로 항상 접근할 수 있어야 한다.

## 명령 표

| 명령 ID | 한국어 / English | Windows 키 | 활성 조건/효과 |
|---|---|---|---|
| file.open | 열기… / Open… | Ctrl+O | 파일/폴더 선택 |
| file.reload | 책 다시 불러오기 / Reload Book | Ctrl+R | source 있음·로딩 아님 |
| file.recent | 최근 항목 열기 / Open Recent | 메뉴 | 최대10개·Clear Menu |
| app.settings | 설정… / Settings… | Ctrl+, | 설정 창 전면 |
| file.export | 번역본 내보내기… / Export Translated Book… | 메뉴 | 번역 기능·책·대상 결과/가능 상태 |
| diagnostics.export | 진단 리포트 내보내기… / Export Diagnostic Report… | 메뉴 | 저장 dialog |
| diagnostics.clear | 진단 로그 지우기 / Clear Diagnostic Logs | 메뉴 | 확인 후 파일 로그만 |
| diagnostics.folder | 로그 폴더 열기 / Open Diagnostic Logs Folder | 메뉴 | Explorer |
| cache.clear | 추출 캐시 지우기 / Clear Extraction Cache | 메뉴 | 로딩 아님·lease 제외 |
| library.pin | 라이브러리 고정/해제 / Pin/Unpin Library | Ctrl+Alt+S | 영속 toggle |
| toolbar.pin | 툴바 고정/해제 / Pin/Unpin Toolbar | Ctrl+Alt+T | 영속 toggle |
| thumbnails.toggle | 미리보기 표시/숨기기 / Show/Hide Thumbnails | Ctrl+Alt+P | 책 있음 |
| layout.single | 한 페이지 / Single Page | Ctrl+Shift+1 | 즉시 single |
| layout.double | 두 페이지 / Double Page | Ctrl+Shift+2 | 즉시 double |
| layout.vertical | 세로 스크롤 / Vertical Scroll | Ctrl+Shift+3 | 즉시 vertical |
| reading.direction | 읽기 방향 전환 / Reading Direction | 메뉴/툴바 | vertical에서 비활성 |
| spread.cover | 표지 한 장 / Standalone Cover | 메뉴/툴바 | double만 노출 |
| fit.screen | 화면에 맞춤 / Fit to Screen | Ctrl+1 | 같은 선택도 reset |
| fit.width | 가로에 맞춤 / Fit to Width | Ctrl+2 | 같은 선택도 reset |
| fit.height | 세로에 맞춤 / Fit to Height | Ctrl+3 | 같은 선택도 reset |
| zoom.in | 확대 / Zoom In | Ctrl++ (Ctrl+=도 허용) |1.25배, numpad도 지원 |
| zoom.out | 축소 / Zoom Out | Ctrl+- |1/1.25배 |
| zoom.reset | 배율 초기화 / Reset Zoom | Ctrl+0 | 현재 fit baseline |
| view.lock | 보기 크기 잠금/해제 / Lock/Unlock View Size | Ctrl+L | auto-fit 역수 |
| wheel.toggle | 휠 페이지 넘김 켜기/끄기 / Enable/Disable Scroll Page Turning | 메뉴 | vertical에 영향 없음 |
| page.forward | 읽기 앞으로 / Forward | Space, 방향별 화살표 | reader focus만·권 카드 포함 |
| page.backward | 읽기 뒤로 / Backward | 방향별 화살표 | reader focus만·이전 권 의도 |
| page.jump | 페이지로 이동… / Go to Page… | Ctrl+G | 쪽2개 이상 |
| bookmark.toggle | 북마크 추가/제거 / Add/Remove Page Bookmark | Ctrl+D | 현재 펼침 시작 쪽 |
| bookmark.previous | 이전 북마크 / Previous Bookmark | Ctrl+Shift+[ | 이전 항목 있음 |
| bookmark.next | 다음 북마크 / Next Bookmark | Ctrl+Shift+] | 다음 항목 있음 |
| favorite.toggle | 즐겨찾기 추가/제거 / Add/Remove Favorite | Ctrl+Shift+D | 책 있음 |
| volume.previous | 이전 권 / Previous Volume | [, Ctrl+[ | bare는 reader keydown만 |
| volume.next | 다음 권 / Next Volume | ], Ctrl+] | bare는 reader keydown만 |
| translation.toggle | 원본/번역 / Original/Translation | Ctrl+Shift+T | 번역 기능·책 |
| translation.run | 이 책 번역/일시정지 / Translate/Pause This Book | Ctrl+Alt+Y | 번역 기능·책 |
| translation.edit | 말풍선 편집/완료 / Edit Balloons/Done | Ctrl+Shift+E | 결과 또는 편집 중 |
| jobs.show | 작업 / Jobs | Ctrl+Alt+J | 작업 창 전면 |
| window.fullscreen | 전체 화면 / Full Screen | F11 | Windows 추가 결정 |
| app.exit | 종료 / Exit | Alt+F4 | 세션 flush·종료 |

번역 실행은 원본 Alt+Command+T를 단순 Ctrl+Alt+T로 옮기면 툴바 고정과 겹치므로 Ctrl+Alt+Y로 정했다. tooltip·메뉴·번역 패널의 힌트도 같은 command registry에서 생성하여 충돌을 되살리지 않는다.

## 메뉴 구성

File: Open, Reload, Open Recent(+Clear Menu), Export Translated Book, Settings, Export Diagnostic Report, Clear Diagnostic Logs, Open Logs Folder, Clear Extraction Cache, Exit.

Edit: Undo/Redo/Cut/Copy/Paste/Select All(현재 입력 컨트롤 대상). AI 응답 전체 Undo는 번역 UI가 제공하는 특정 액션이며 텍스트 입력 Undo와 구분한다.

View: Pin Library/Toolbar, Show Thumbnails, Single/Double/Vertical, Reading Direction/Standalone Cover, Fit Screen/Width/Height, Zoom In/Out/Reset, Lock View Size, Wheel Turning, Translation Show/Hide, Translate/Pause, Edit/Done, Show Hidden Balloons, Full Screen.

Go: Go to Page, Toggle Bookmark, Previous/Next Bookmark, Bookmarks(현재 책 쪽 + 다른 책 submenu), Remove All in This Book, Remove All Bookmarks, Favorite, Previous/Next Volume.

Window: Reader, Jobs. Help: About(버전·빌드·출처·라이선스), 선택적인 로컬 사용 안내. Settings/Jobs는 여러 번 누르면 새 창 대신 기존 창을 활성화한다. 임의 메뉴 텍스트가 사라졌다는 이유로 UI에서 해당 기능도 빠지면 안 된다.

## 포커스 우선순위

1. 모달/팝오버 텍스트 입력: 해당 창의 키 처리가 우선.
2. 편집 draft: Enter 저장, Shift+Enter 줄바꿈, Escape draft 취소. IME 조합 중 Enter를 앱 저장으로 처리하지 않는다.
3. 편집 모드 바: Escape 편집 종료.
4. 임시 라이브러리: Escape 닫기.
5. 리더: 화살표·Space·bare bracket.

Quick jump는 유효 정수만 수용, 범위 밖 clamp, 잘못된 텍스트는 원래 쪽 유지. 모달 Go는 Enter 확정, Escape 취소. 스크롤바·드래그·풍선 클릭으로 리더 명령 처리가 영구 꺼지지 않게 포커스를 복귀시킨다. 메뉴의 modifier 명령도 입력 취소/복사 단축키를 빼앗지 않는다.

## 문구 자산 사용

[localization.catalog.json](localization.catalog.json)은 원본538개 entry를 sourceKey와 en/ko resource tree로 보존한 snapshot이다. 빈 key·추출용 separator·legacy/macOS 문구도 포함한다. JSON에 코드 경로나 Xcode 런타임은 필요하지 않다. `stringUnit.value`, plural `variations`, substitution을 재귀적으로 읽고 누락은 영어 sourceKey로 fallback한다.

실제 Windows `.resx`/resource JSON을 만들 때 의미별 안정된 command ID로 매핑하고 다음을 변환한다.

| 원본 | Windows 표시 |
|---|---|
| ⌘ / ⇧ / ⌥ / ⌃ | Ctrl / Shift / Alt / 원본 키가 아닌 본 표의 최종 조합 |
| Finder / Reveal in Finder | File Explorer / Show in Explorer |
| Mac/macOS 정보 | PC/Windows 정보, 실제 capability |
| System Settings > Translation Languages | 앱 언어팩 관리 위치 |
| macOS26/26.4 요구 문구 | 현재 공급자/팩/하드웨어 지원 상태 |
| Apple Intelligence 요구 | 선택한 말투 공급자의 지원 상태 |
| Restart Now | 실제 재시작 필요 구성에서만 표시 |

`%@`, `%lld`, `%1$@`, `%2$lld`를 C# formatting에 직접 넘기지 않는다. 위치 인덱스를 보존하여 `{0}`, `{1}` 등으로 변환하고 타입·개수 검사를 한다. 변형 tree의 치환자·복수형도 처리한다. 예를 들어 원문 입력 순서가 filename,count이면 한국어 `%2$lld…%1$@` 순서를 그대로 유지한다. 사용자 파일명·OCR 문자열을 format string으로 해석하지 않는다.

영어/한국어 필수 상태 문구 예:

| ID | English | 한국어 |
|---|---|---|
| reader.empty | Open a folder or comic archive to start reading. | 폴더나 만화 압축 파일을 열어 읽기를 시작하세요. |
| source.changed | This book changed on disk. | 디스크에서 이 책이 변경되었습니다. |
| image.failed | Couldn't read this page. | 이 페이지를 읽을 수 없습니다. |
| pack.missing | Download the {0} → {1} language pack. | {0} → {1} 언어팩을 다운로드하세요. |
| model.unavailable | This model can't run on this PC: {0} | 이 PC에서 모델을 실행할 수 없습니다: {0} |
| job.preparing | Preparing models… | 모델 준비 중… |
| export.retry | Try Again | 다시 시도 |
| edit.confirmDelete | Delete your edits in every book? | 모든 책에서 직접 수정한 내용을 삭제할까요? |

이 표의 Windows 전용 문장은 새 문구다. 원본 catalog를 그대로 리소스로 복사해 macOS 조건을 사용자에게 보여주지 않는다. 언어명 English/한국어는 자기 언어로 표시하고 번역 언어명은 현재 UI locale로 표시한다.
