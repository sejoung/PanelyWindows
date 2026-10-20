# ADR 0008: 번역 모델을 Gemma 4 E2B 하나로 바꾸고 llama.cpp(LLamaSharp)로 앱 안에서 돌린다

- 상태: 채택
- 날짜: 2026-10-20
- 대체: ADR 0004의 번역 모델(M2M100 Fast/Accurate)과 6번(말투 보정 없음), 7번(M2M100 선택) 결정
- 관련 코드: `GemmaTranslator`, `ITranslator`(`TranslationContextLine`, `PrepareAsync`), `TranslationScheduler`(문맥 전달·`TranslationOptions.TranslateChunk`·`ContextLines`), `ModelCatalog.TranslatorId`·`ObsoleteIds`, `ModelStore.RemoveObsolete`, `ExperimentalSettingsView`, `Panely.App.csproj`(`KeepOnlyWindowsLlamaNatives`)
- 테스트: `TranslationCoreTests.cs`(`GemmaWithoutInstalledModelNeedsDownload`, 프롬프트·파싱·후처리, `RemoveObsoleteDeletesOldPackFoldersOnly`), `TranslationPipelineTests.EarlierTranslationsGoToTheTranslatorAsContext`, `StateStoreTests.StateFromBeforeTheGemmaTranslatorStillLoads`, `PANELY_TEST_LLM_MODEL`을 줄 때만 도는 `GemmaTranslatorIntegrationTests`(App.Tests)

## 배경

M2M100(ADR 0004)은 말풍선을 문맥 없이 한 문장씩 번역해서 누가 말하는지, 말투, 이름을 놓쳤고, 말투 보정은 맞는 모델이 없어 넣지 못했다. 사용자는 품질 차이 때문에 Fast(int8)와 Accurate(fp32) 중에서 골라야 했다.

ADR 0003 "추가 검토"에서 구글 번역 모델 두 가지를 확인했다.

- TranslateGemma-4B: 공식 저장소가 수동 승인 게이트라 앱이 받을 수 없고, 입력 형식이 고정이라 말투 지시를 공식 지원하지 않는다. 보류했다.
- Gemma 4 E2B: Apache-2.0, 게이트 없음, 구글 공식 GGUF가 있다. 같은 146문장에서 반말을 반말로 유지하는 비율이 M2M100보다 높았다. 개발자가 나란히 비교한 평가로는 Fast·Accurate보다 훨씬 나았다(1인 평가, 골든 세트 아님).

LLamaSharp 0.30.0으로 앱 프로세스 안에서 돌고, ONNX Runtime DirectML 탐지 세션과 공존하는 것도 확인했다.

## 결정

1. 번역 모델은 Gemma 4 E2B 하나다. 번역 방식(Fast/Accurate) 옵션과 `TranslationMode`를 없앤다.
   - 모델: `google/gemma-4-E2B-it-qat-q4_0-gguf` revision `675cff42a74c774d6cb76f76d8eacb49b48c9b93`, `gemma-4-E2B_q4_0-it.gguf` 3,349,516,256 bytes, sha256 `fa401b55b07ee70a54c6dae3903c783a6e65064312529ea57175cb5f8dec6634`.
   - 설정 항목을 지운다. 열거형 값만 줄이면 옛 `state.json`의 `"accurate"`가 역직렬화 오류를 내서 저장 데이터 전체가 손상 처리되기 때문이다. 항목이 없으면 옛 키는 무시되고 다음 저장 때 사라진다. `SchemaVersion`은 올리지 않는다.
2. 실행기는 llama.cpp(LLamaSharp). GPU는 Vulkan(win-x64), Vulkan 드라이버가 없거나 `PANELY_ORT_DEVICE=cpu`면 CPU(AVX 단계별 자동 선택). ARM64는 Vulkan 빌드가 없어 CPU만 쓴다. 탐지·OCR은 ONNX Runtime DirectML 그대로다.
   - 네이티브 라이브러리 선택은 프로세스에 한 번이라, 첫 번역 때 `NativeLibraryConfig`를 정한다. 실제 장치는 llama.cpp 로그("using device Vulkan0 (…)")로 알아내 설정 화면에 보인다.
   - 백엔드 패키지는 RID가 있는 Panely.App에서 참조한다. props가 모든 OS 파일을 복사하므로 `KeepOnlyWindowsLlamaNatives`가 그 RID의 Windows 파일만 남긴다(리눅스 Vulkan 56MB 등 제외).
3. 프롬프트는 Gemma 4 대화 형식 `<|turn>user\n…<turn|>\n<|turn>model\n`을 직접 만든다. LLamaSharp의 템플릿 기능은 Gemma 4의 Jinja 템플릿을 모른다(`MissingTemplateException`). 형식은 llama-server `/apply-template`(추론 끔)이 만든 것과 같다.
4. 번역기에 앞 문맥을 넘긴다. `ITranslator.TranslateAsync`가 `TranslationContextLine` 목록을 받고, 스케줄러는 작업마다 직전에 번역한 풍선 `ContextLines`(6)개를 넘긴다. 묶음은 `TranslateChunk` 16개.
5. 말투 맞춤은 별도 단계가 아니라 프롬프트 규칙이다("polite speech stays polite, plain or casual speech stays casual", 한국어 대상이면 존댓말/반말 예시). `ISpeechLevelCorrector`와 "말투 맞추기" 설정을 지운다.
6. 출력은 `[n] 번역` 줄로 받는다. 빠진 줄은 한 줄씩 다시 묻고, 그래도 없으면 빈 결과(ADR 0001의 번역하지 못한 말풍선)다. 일본어가 아닌 번역에 남은 장음 기호 줄표(`ーー`)는 줄표로 바꾼다.
7. 컨텍스트(8K)를 하나 만들어 재사용하고 요청마다 KV 캐시만 비운다(`StatelessExecutor`는 요청마다 컨텍스트를 새로 만들어 지연·메모리가 컸다).
8. 번역 작업을 시작할 때 `PrepareAsync`로 모델을 올리고 짧게 생성해 둔다. GPU 첫 생성의 셰이더 준비(약 20초)가 첫 쪽 분석과 겹친다.
9. 캐시 identity의 provider revision은 `모델 id@revision+p{프롬프트 버전}`이다. 프롬프트를 바꾸면 버전을 올린다. 옛 M2M100 결과는 revision이 달라 쓰지 않는다.
10. 이전 버전이 받아 둔 M2M100 팩 폴더(최대 약 2.9GB)는 시작할 때 백그라운드로 지운다(`ModelCatalog.ObsoleteIds`). `Models/manifests`로 같은 id를 다시 선언했으면 남긴다.

## 결과

측정(i7-13700K, RAM 64GB, RTX 4070 Ti SUPER, 세평유 298쪽 세로 웹툰, 일본어 → 한국어, 말풍선 146개, 새 데이터 폴더, 앱 실행부터 전 쪽 완료까지 1회):

| | 전체 | 첫 번역 표시 | 최대 작업 집합 | 최대 개인 메모리 |
|---|---|---|---|---|
| M2M100 Fast (이전) | 83.0초 | 10.6초 | 3.5GB | 5.2GB |
| M2M100 Accurate (이전) | 90.2초 | 13.6초 | 5.8GB | 7.4GB |
| Gemma 4 E2B, GPU, 미리 준비 없음 | 81.3초 | 27.7초 | 4.0GB | 7.6GB |
| **Gemma 4 E2B, GPU** | **57.9초** | **9.5초** | 3.8GB | 7.5GB |
| **Gemma 4 E2B, CPU만(`PANELY_ORT_DEVICE=cpu`)** | 324.9초 | 14.7초 | 6.0GB | 6.8GB |

- 146개 모두 번역됐다(빈 결과 0). 옛 M2M100 팩 폴더는 시작 직후 지워졌다.
- 다운로드는 3.35GB로 Fast(약 0.6GB)보다 크고 Accurate(2.3GB)보다 크다. 게시본은 win-x64 기준 약 75MB 커진다(Vulkan 53MB, CPU 4종 약 19MB, 관리 DLL).
- GPU가 없는 PC에서는 번역이 CPU로 돌아 느리다. 매뉴얼에 적는다.
- 품질은 여전히 골든 세트로 검수하지 않았다. 원문 인식(OCR) 오류는 그대로 번역에 들어간다.
- 중국어 간체↔번체도 이제 서로 번역할 수 있다(M2M100은 `zh` 하나였다).
