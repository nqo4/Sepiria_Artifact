# SephiriaLive

스팀 게임 **Sephiria(세피리아)** 용 BepInEx 5.x 플러그인 + 동반 로컬 웹페이지.

- 게임 안의 인벤토리(아티팩트/석판 배치)를 로컬 HTTP(`127.0.0.1`)로 읽어오고, 계산된 배치를 다시 적용한다.
- 목표 무기/핵심 아이템을 기준으로 선택지에 S~D 추천 등급을 매기고, 무기가 특정 조건(일반공격/연격/탄환 등)을 재정의하는지 따로 점검한다.

> 개인 프로젝트, 개발 중. 아직 실게임 검증 전 단계입니다. 사용 전 세이브를 백업하세요.

개발을 이어가려면 **[docs/DEVELOPMENT_GUIDE.md](docs/DEVELOPMENT_GUIDE.md)** 부터 보세요 (환경 준비, 로드맵, 실게임 확인 절차).

## 구조

```
Core/      순수 로직 (BepInEx/Unity 의존성 0) — JSON, HTTP 서버, 리플렉션, 인벤토리 읽기/쓰기
Plugin/    BepInEx 진입점 + companion/ (동반 웹페이지 템플릿, 배치 DB)
Tests/     Core 유닛 테스트 (가짜 객체 기반)
data/      위키 스크래핑 원본
scripts/   전처리 스크립트
```

## 빌드

필요: .NET 8 SDK. Plugin 빌드에는 게임 + BepInEx 5가 설치되어 있어야 합니다(게임 DLL을 직접 참조, 저장소에는 포함하지 않음).

```powershell
dotnet run --project Tests                                   # Core 테스트
dotnet build Plugin -c Release                               # 플러그인 빌드
dotnet build Plugin -c Release -p:DeployToGame=true          # 빌드 + BepInEx/plugins/SephiriaLive 에 설치
dotnet build Plugin -c Release -p:SephiriaGameDir="D:\Games\Sephiria"   # 게임 경로가 다를 때
```

## 엔드포인트 (포트 후보 37788 → 47022 → 52612 → 58123)

| 경로 | 설명 |
|---|---|
| `GET /health` | 플러그인 동작 확인 (게임 상태 접근 없음) |
| `GET /debug` | 인벤토리 스냅샷 + 멤버 덤프 (진단용) |
| `GET /inv` | 인벤토리 스냅샷 (폴링용) |
| `GET /items` | 아티팩트·무기 정의(이름, 등급, 태그, 최대 레벨, 효과 문장, 무기 강화 트리) |
| `GET /tablets` | 석판 효과 범위(query, 게임 ParseQuery 칸) — 가방 석판 + 전체 석판 카탈로그 |
| `POST /apply` | `{"placements":[{"fromX","fromY","toX","toY","rotation"?}]}` 배치 적용 |
| `GET /` | 동반 추천 페이지 |

외부 웹사이트에서 오는 요청(Origin 헤더가 로컬/`file://`이 아닌 경우)은 403으로 차단합니다.
