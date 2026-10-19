# ADR 0001: 번역기가 글을 내지 못한 말풍선은 원문을 유지하고 쪽을 완료로 본다

- 상태: 채택
- 날짜: 2026-10-19
- 관련 코드: `TranslationScheduler.TranslateSlotsAsync`(ADR 0003 전에는 `TranslateBubblesAsync`), `ResetUnfinishedStrip`, `TranslationCache.Store`, `EditResolver.Resolve(pageComplete)`, `BubbleTextState.Untranslated`
- 테스트: `tests/Panely.Tests/Translation/UntranslatableBubbleTests.cs`

## 배경

세평유(일본어 세로 웹툰, 298쪽)를 번역하면 매번 "책을 다 번역하기 전에 번역이 중지되었습니다"로 끝났다.

- M2M100-418M(Fast)이 짧은 대사에서 첫 토큰으로 `</s>`를 골라 빈 문자열을 냈다. 책 전체 말풍선 254개 중 4개였다(`ぱち`, `かしこまりました`, `くっく`, `ぁぁーー`).
- 빈 결과는 `IsValidTranslation`에서 실패로 처리되어 `translation=null`로 남았다. 쪽은 "모든 풍선에 번역이 있어야 완료"였으므로 그 쪽은 끝나지 않았고, 캐시도 null 풍선이 있는 쪽을 저장하지 않았다.
- 끝나지 않은 쪽이 하나라도 있으면 `FinishJob`이 책을 Failed로 끝냈다.
- strip(세로 스크롤) 범위에서는 실패한 쪽의 band가 `CompletedBands`에 남아, 다시 시작해도 band를 다시 읽지 않고 곧바로 같은 실패로 끝났다.

## 검토한 방법

| 방법 | 결과 |
|---|---|
| 첫 토큰에서 `</s>`를 막기(HF `min_new_tokens=1`) | 빈칸은 없어지지만 `かしこまりました` → "엉덩이 났다.", `ぁぁーー` → "ᄏᄏᄏᄏᄏᄏ"처럼 틀린 번역이 나온다. 구두점(`。`, `!`)을 붙여도 같았다. **기각** |
| 빈 결과 풍선을 쪽에서 지우기 | 쪽은 끝나지만 사용자가 그 풍선을 볼 수도 고칠 수도 없다. **기각** |
| 빈 결과 풍선을 `translation=null`로 두고 쪽을 완료로 보기 | 원문이 그대로 보이고, 풍선을 눌러 이유를 확인하고 직접 번역을 넣을 수 있다. 저장 형식 스키마(`tests/Panely.Tests/Vectors/domain.schema.json`)가 `translation`을 nullable로, `isComplete`를 따로 두므로 스키마 변경이 없다. **채택** |

## 결정

1. 번역기가 **오류 없이** 빈 결과나 원문 그대로를 돌려준 풍선은 "번역하지 못함"으로 본다. `translation=null`로 두고 쪽은 완료(`isComplete=true`)로 캐시에 저장한다.
2. 번역기가 **오류**를 냈거나 결과가 오지 않은 풍선이 있으면 쪽은 미완료다. 책이 끝날 때 남은 쪽이 있으면 일반 안내 대신 마지막 오류 메시지를 보여 준다.
3. 완료된 쪽의 `translation=null` 풍선은 화면에서 `Untranslated` 상태다. 패치나 "…"을 그리지 않아 원문이 보이고, 팝오버에 "이 말풍선은 번역하지 못해 원문을 그대로 보여 줍니다. 번역 고치기에서 직접 입력할 수 있습니다."를 표시한다. 번역 중인(미완료) 쪽의 null은 지금처럼 `Pending`("…")이다.
4. 끝났거나 멈춘 strip 작업을 다시 시작하면, 작업 스레드가 다음 실행을 시작할 때 미완료 쪽이 걸친 band를 완료 목록에서 빼고 그 쪽들의 풍선을 비운다. 미완료 쪽은 자기 band가 모두 빠지므로 풍선이 중복되지 않는다. 이미 끝난 쪽에서 나온 풍선은 band를 다시 읽을 때 건너뛴다.
5. 디코딩에서 언어 태그와 `<unk>` 토큰은 만들지 않는다(글자로 풀리지 않아 빈 결과가 되므로). 첫 토큰의 `</s>`는 막지 않는다.

## 결과

- 짧은 감탄사·대답 몇 개 때문에 책 전체 번역이 멈추지 않는다. 대신 그 풍선은 번역되지 않은 채 원문으로 남는다.
- 진단 로그에는 원문을 남기지 않고 쪽 번호와 개수만 기록한다(`Page N: translator returned no text for K balloon(s)`).
- 쪽 재번역을 하면 그 풍선도 다시 시도한다. 번역 모델이 바뀌면 캐시 identity(provider revision)가 달라져 다시 번역된다.
- 번역 품질 문제(예: 반복된 OCR 원문에서 "ᄒᄒᄒ… 정말 감사하겠습니다" 같은 출력)는 이 결정의 범위가 아니다. M2M100의 한계이며 다른 번역 엔진을 검토할 때 다룬다.
