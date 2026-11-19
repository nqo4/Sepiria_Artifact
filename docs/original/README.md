# 원본 복구 파일 (참고용 보관)

클라우드 샌드박스에서 복구해 넘겨받은 파일을 **원래 모양 그대로** 보관합니다. 빌드에는 쓰지 않습니다.

| 파일 | 현재 저장소에서 쓰는 버전 |
|---|---|
| `SephiriaLive_resume_prompt.md` | 개발 재개 프롬프트 원문 → 정리본은 루트 `CLAUDE.md`, `GAPS.md` |
| `SephiriaLivePlugin.cs` | `Plugin/SephiriaLivePlugin.cs` (메인 스레드 처리·CSRF 차단 추가) |
| `SephiriaLive.Plugin.csproj` | `Plugin/SephiriaLive.Plugin.csproj` (UnityStub/libs 대신 게임 DLL 직접 참조) |
| `Tests_Program.cs` | `Tests/Program.cs` (이 원본 43개 테스트 + 실게임 구조 테스트 추가) |

데이터 파일(`placement_db.json`, `raw_slab_tooltips.json`, `slabs_effectpattern_wiki.json`)은
원본과 바이트 단위로 같은 내용이 `Plugin/companion/`, `data/`에 그대로 들어 있습니다.
