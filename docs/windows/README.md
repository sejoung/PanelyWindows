# Panely Windows 독립 구현 명세

이 폴더 전체를 새 프로젝트에 복사하면 원본 Git 저장소, Swift 소스, Xcode, macOS 실행 환경 없이 Panely Windows를 설계·구현·검수할 수 있다. 앱 실행 파일이나 모델을 제공하는 패키지가 아니라 **제품 동작과 디자인을 재구현하기 위한 문서 패키지**다. Windows 구현에 필요한 공개 SDK·라이브러리·모델은 새 프로젝트에서 선택하고 고정한다. 여기서 “독립”은 외부 라이브러리를 쓰지 말라는 뜻이 아니다.

기준일: 2026-10-16. 원본 기준 커밋: `d6472bf43b00c7886350a26188e3f2030dbc8058`. 작성 시 작업 트리는 깨끗했으며, 소스·저장 모델·사용 설명서·화면 참고 이미지를 대조했다. 이 커밋 정보는 출처 기록이며 구현 때 저장소에 접근할 필요가 없다.

## 읽는 순서

| 문서 | 구현에 필요한 내용 |
|---|---|
| [01-product.md](01-product.md) | 기능 전체 범위, 기본값, 화면과 상태, 동등성 기준 |
| [02-design.md](02-design.md) | 색·치수·레이어·타이포·컴포넌트·화면별 디자인 |
| [03-reader.md](03-reader.md) | 펼침·RTL·맞춤·줌·휠·권 이동의 알고리즘 |
| [04-files.md](04-files.md) | 파일 형식·폴더 탐색·아카이브·캐시·비동기 로딩 |
| [05-data.md](05-data.md) | Windows 데이터 구조, 식별자, 저장·복원·정리 |
| [06-translation.md](06-translation.md) | OCR·번역·말풍선 편집·AI 교환·내보내기·작업 큐 |
| [07-windows.md](07-windows.md) | 구현 구조, OS 대체 계층, 의존성·모델 결정 사항 |
| [08-commands-and-copy.md](08-commands-and-copy.md) | Windows 단축키, 메뉴, 포커스, 한영 문구 변환 |
| [09-delivery-and-qa.md](09-delivery-and-qa.md) | 구현 단계, 동등성 검수, 테스트 데이터와 완료 조건 |
| [assets/README.md](assets/README.md) | 포함 자산·출처·권리·스크린샷 읽는 법 |

기계 판독 자료:

- [design-tokens.json](design-tokens.json): 디자인 수치와 기본 설정.
- [test-vectors.json](test-vectors.json): 펼침·맞춤·이미지 배치·OCR 분할·AI 응답 기대값.
- [domain.schema.json](domain.schema.json): 새 Windows 저장 형식의 JSON Schema.
- [localization.catalog.json](localization.catalog.json): 원본 한영 문구와 변형·치환자 정보 전체.
- [assets/gallery.html](assets/gallery.html): 오프라인으로 여는 24개 화면 갤러리.
- [bundle-manifest.json](bundle-manifest.json): 포함 파일의 크기·SHA-256. 무결성 확인용이며 자기 자신은 제외한다.

## 명세 해석

**유지**는 코드에서 확인한 제품 동작이다. **Windows 결정**은 OS 차이를 해결하기 위해 이 문서에서 정한 이식 기준이다. **검증 필요**는 새 공급자·모델·성능이 필요한 항목이다. 후자를 이미 구현되거나 검증된 것으로 읽지 않는다.

우선순위는 명시된 Windows 결정 → 본문 동작 규칙과 계산 예 → 디자인 토큰 → 스크린샷 → 원본 문구 목록이다. 스크린샷은 macOS의 샘플 화면이므로 단축키, OS 버튼, 번역 예문과 페이지 그림 안의 숫자를 제품 상태의 진실로 해석하지 않는다. 맞춤 재선택을 메뉴·단축키에도 적용하는 것과 모달 접근성 등 Windows 보완 결정은 별도로 표시했다.

## 동등성의 경계

읽기·라이브러리·즐겨찾기·북마크·설정·진단·실험적 번역·수정·AI 교환·내보내기·작업 창까지 모두 최종 범위다. 번역을 제외한 중간 버전은 “리더 단계 완료”로 표시한다. 전체 Windows 포팅 완료로 표시하지 않는다.

Apple Vision, Translation, Core ML, Foundation Models의 Windows 바이너리는 이 패키지에 없다. 대체 OCR·번역·말투 보정 모델의 선정, 다운로드 규칙, 배포 권리, 실제 품질 검증이 필요하다. UI와 데이터·큐·입출력 계약은 독립적으로 구현할 수 있지만, Apple 모델과 동일한 문장을 생성한다는 보장은 하지 않는다. 모델 의존성의 미결 사항은 [07-windows.md](07-windows.md)에 모았다.

## 새 구현 담당자에게 전달할 지시문

> 이 폴더만 기준으로 독립 Windows 앱을 구현한다. 먼저 01~09 문서를 읽고 기능 ID별 구현/검수 표를 만든다. 계산은 test-vectors.json으로 확인하고 UI는 design-tokens.json 및 assets/screenshots와 대조한다. macOS API나 원본 저장소 경로를 의존성으로 추가하지 않는다. 07 문서의 모델·코덱·RAR 검증을 초기에 수행한다. 단계별로 앱을 실행할 수 있게 만들되 미완성 기능을 전체 포팅 완료로 보고하지 않는다. 번역 제공자와 앱 UI는 06의 계약으로 분리한다. 실제 실행 환경에서 검증하지 않은 사항은 그 사실을 기록한다.
