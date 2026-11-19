using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using SephiriaLive.Core;
using UnityEngine;

namespace SephiriaLive.Plugin
{
    /// <summary>
    /// 게임 폴더(BepInEx/plugins/)에 들어가 실제로 로드되는 진입점.
    /// 여기서 하는 일은 딱 두 가지뿐 — 로컬 HTTP 서버를 띄우고, Core의 InventoryReader/Writer에
    /// 라우트를 연결하는 것. 실제 게임 데이터를 다루는 로직은 전부 Core에 있고(그래야 유닛 테스트가
    /// 가능하므로), 이 클래스 자체는 테스트하지 않는다 — Unity/BepInEx 런타임 밖에서는 애초에 로드도
    /// 안 되기 때문에, Plugin.cs는 "얇게" 유지하는 게 원칙이다.
    ///
    /// ⚠ 아직 실제 게임에 붙여서 검증한 적이 없다. 처음 설치하면:
    ///   1) 반드시 /debug 를 먼저 열어서 ok:true 인지, memberDump에 그럴듯한 필드가 보이는지 확인
    ///   2) /inv 가 실제 인벤토리와 비슷한 모양으로 나오는지 확인한 다음에만
    ///   3) /apply(쓰기)를 시도할 것 — 세이브 자체는 안 건드리지만, 잘못된 필드에 값을 쓰면
    ///      최소한 그 실행 중에는 게임 상태가 이상해질 수 있다.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SephiriaLivePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.sephirialive.sync";
        public const string PluginName = "SephiriaLive Sync";
        public const string PluginVersion = "0.1.0";

        // 다른 사용자가 공유해준 SephiriaSync가 37777/47021/52611을 쓰므로,
        // 두 모드를 동시에 깔아도 포트가 겹치지 않도록 다른 대역을 후보로 둔다.
        static readonly int[] CandidatePorts = { 37788, 47022, 52612, 58123 };

        MiniHttpServer _server;
        object _cachedInventoryInstance;
        // 자동 폴링 주기(SephiriaSync와 동일하게 1초) — 현재는 F10 수동 갱신만 구현되어 있고
        // 타이머 기반 자동 스캔 루프는 아직 붙이지 않았다. 붙일 때 이 값을 Update()에서 사용할 것.
        const float ScanIntervalSeconds = 1f;

        void Awake()
        {
            foreach (var port in CandidatePorts)
            {
                try
                {
                    var server = new MiniHttpServer(port);
                    server.Log = s => Logger.LogInfo(s);
                    RegisterRoutes(server);
                    server.Start();
                    _server = server;
                    Logger.LogInfo($"[SephiriaLive] 준비 완료 — http://127.0.0.1:{port}/debug 로 진단 확인 가능");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[SephiriaLive] 포트 {port} 사용 실패, 다음 후보 시도: {ex.Message}");
                }
            }

            if (_server == null)
                Logger.LogError("[SephiriaLive] 모든 후보 포트를 사용할 수 없어 서버를 시작하지 못했습니다.");
        }

        void RegisterRoutes(MiniHttpServer server)
        {
            // /debug, /inv 둘 다 같은 스냅샷 — /debug는 사람이 읽는 진단용, /inv는 플래너가 매초 폴링하는 용도.
            server.Route("GET", "/debug", ctx => BuildSnapshotResponse());
            server.Route("GET", "/inv", ctx => BuildSnapshotResponse());

            // 추천 UI(companion/recommend.html)를 그대로 서빙 — 사용자가 브라우저에서
            // http://127.0.0.1:<port>/ 를 직접 열면 됨(게임 창 옆에 띄워두는 용도).
            // DLL과 같은 폴더의 companion/recommend.html을 매 요청마다 디스크에서 새로 읽는다
            // (재빌드 없이 파일만 바꿔도 바로 반영되게, 그리고 메모리에 큰 문자열을 들고 있지 않게).
            server.Route("GET", "/", ctx => ServeCompanionPage());
            server.Route("GET", "/recommend.html", ctx => ServeCompanionPage());

            server.Route("POST", "/apply", ctx =>
            {
                try
                {
                    if (_cachedInventoryInstance == null)
                        return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "no_inventory_cached", ["message"] = "먼저 /inv 를 한 번 호출해 인벤토리를 찾아야 합니다." });

                    var parsed = MiniJson.Parse(ctx.Body);
                    var commands = InventoryWriter.ParseCommands(parsed);
                    var result = InventoryWriter.Apply(_cachedInventoryInstance, commands);
                    return HttpResponseData.Json(new JObj
                    {
                        ["ok"] = result.Ok,
                        ["moved"] = result.Moved,
                        ["rotated"] = result.Rotated,
                        ["log"] = result.Log.Cast<object>().ToList(),
                        ["errors"] = result.Errors.Cast<object>().ToList(),
                    });
                }
                catch (Exception ex)
                {
                    return new HttpResponseData
                    {
                        Status = 500,
                        Body = MiniJson.Write(new JObj { ["ok"] = false, ["error"] = "exception", ["message"] = ex.Message }),
                    };
                }
            });
        }

        HttpResponseData ServeCompanionPage()
        {
            try
            {
                var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                var htmlPath = Path.Combine(dllDir, "companion", "recommend.html");
                if (!File.Exists(htmlPath))
                {
                    return HttpResponseData.Json(new JObj
                    {
                        ["ok"] = false,
                        ["error"] = "companion_page_missing",
                        ["message"] = $"'{htmlPath}' 를 찾을 수 없습니다. SephiriaLive 플러그인 폴더에 companion/recommend.html 이 같이 있는지 확인하세요.",
                    });
                }
                return HttpResponseData.Html(File.ReadAllText(htmlPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "companion_page_read_failed", ["message"] = ex.Message });
            }
        }

        HttpResponseData BuildSnapshotResponse()
        {
            var snapshot = InventoryReader.BuildSnapshot(out _);
            // /apply가 쓸 수 있게 방금 찾은 인스턴스를 기억해둔다 (InventoryReader는 순수 조회라 인스턴스를 들고 있지 않음).
            _cachedInventoryInstance = InventoryReader.LastFoundInstance;
            return HttpResponseData.Json(snapshot);
        }

        void Update()
        {
            // F10: 다른 사용자가 공유해준 SephiriaSync처럼 "배치+적용 원격 실행" 핫키.
            // 여기서는 아직 자동배치 계산 로직이 없으므로(그건 플래너/엔진 쪽 몫), 1단계로는
            // 최신 스냅샷을 즉시 갱신(캐시 무효화)하는 것까지만 하고 로그를 남긴다.
            if (Input.GetKeyDown(KeyCode.F10))
            {
                Logger.LogInfo("[SephiriaLive] F10 — 최신 인벤토리 스냅샷으로 갱신");
                BuildSnapshotResponse();
            }
        }

        void OnDestroy()
        {
            _server?.Dispose();
        }
    }
}
