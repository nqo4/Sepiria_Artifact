using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using SephiriaLive.Core;
using UnityEngine;

namespace SephiriaLive.Plugin
{
    /// <summary>
    /// 게임 폴더(BepInEx/plugins/SephiriaLive/)에 들어가 로드되는 진입점.
    /// 하는 일: 로컬 HTTP 서버를 띄우고 Core의 InventoryReader/Writer에 라우트를 연결하는 것뿐.
    /// 게임 데이터를 다루는 로직은 전부 Core에 있다(유닛 테스트 가능하도록). 이 클래스는 얇게 유지한다.
    ///
    /// 스레드: HTTP 핸들러는 백그라운드 스레드에서 돌지만, Unity API와 Mirror SyncDictionary는 메인 스레드 전용이다.
    /// 그래서 게임 상태를 읽고 쓰는 작업은 RunOnMainThread로 Update()에 넘겨 실행한다.
    ///
    /// ⚠ 처음 설치하면:
    ///   1) /debug 로 ok:true 인지, memberDump에 그럴듯한 필드가 보이는지 확인
    ///   2) /inv 가 실제 인벤토리와 비슷한 모양인지 확인한 다음에만
    ///   3) /apply(쓰기) 시도 — 이동 1건부터.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SephiriaLivePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.sephirialive.sync";
        public const string PluginName = "SephiriaLive Sync";
        public const string PluginVersion = "0.2.0";

        // SephiriaSync가 37777/47021/52611을 쓰므로 겹치지 않는 대역.
        static readonly int[] CandidatePorts = { 37788, 47022, 52612, 58123 };
        const int MainThreadTimeoutMs = 3000;

        MiniHttpServer _server;
        int _port;
        int _mainThreadId;
        readonly Queue<Action> _mainQueue = new Queue<Action>();
        volatile object _cachedInventoryInstance;
        bool _legacyInputBroken;

        void Awake()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            InventoryReader.Log = s => Logger.LogWarning(s);
            InventoryReader.InstanceFinder = FindInstances;

            foreach (var port in CandidatePorts)
            {
                try
                {
                    var server = new MiniHttpServer(port) { Log = s => Logger.LogWarning(s) };
                    RegisterRoutes(server);
                    server.Start();
                    _server = server;
                    _port = port;
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

        static object[] FindInstances(Type t)
        {
            return UnityEngine.Object.FindObjectsByType(t, FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Cast<object>().ToArray();
        }

        void RegisterRoutes(MiniHttpServer server)
        {
            server.Route("GET", "/health", ctx => HttpResponseData.Json(new JObj
            {
                ["ok"] = true, ["plugin"] = PluginName, ["version"] = PluginVersion, ["port"] = _port,
            }));

            // /debug: 사람이 읽는 진단(멤버 덤프 포함). /inv: 플래너가 폴링하는 가벼운 스냅샷.
            server.Route("GET", "/debug", ctx => RunOnMainThread(() => BuildSnapshotResponse(true)));
            server.Route("GET", "/inv", ctx => RunOnMainThread(() => BuildSnapshotResponse(false)));

            // /tablets: 석판 효과 범위 정의(query)를 게임에서 직접 읽음 — 가방의 석판 + 로드된 전체 석판 카탈로그.
            server.Route("GET", "/tablets", ctx => RunOnMainThread(BuildTabletsResponse));

            server.Route("GET", "/", ctx => ServeCompanionPage());
            server.Route("GET", "/recommend.html", ctx => ServeCompanionPage());

            server.Route("POST", "/apply", ctx =>
            {
                List<InventoryWriter.PlacementCommand> commands;
                try { commands = InventoryWriter.ParseCommands(MiniJson.Parse(ctx.Body)); }
                catch (FormatException ex)
                {
                    return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "bad_json", ["message"] = ex.Message }, 400);
                }
                return RunOnMainThread(() =>
                {
                    var inv = _cachedInventoryInstance;
                    if (inv == null || IsDestroyed(inv))
                        return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "no_inventory_cached", ["message"] = "먼저 /inv 를 호출해 인벤토리를 찾아야 합니다." }, 409);

                    var result = InventoryWriter.Apply(inv, commands);
                    Logger.LogInfo($"[SephiriaLive] /apply mode={result.Mode} moved={result.Moved} rotated={result.Rotated} errors={result.Errors.Count}");
                    return HttpResponseData.Json(new JObj
                    {
                        ["ok"] = result.Ok,
                        ["mode"] = result.Mode,
                        ["moved"] = result.Moved,
                        ["rotated"] = result.Rotated,
                        ["log"] = result.Log.Cast<object>().ToList(),
                        ["errors"] = result.Errors.Cast<object>().ToList(),
                    });
                });
            });
        }

        static bool IsDestroyed(object o) => o is UnityEngine.Object uo && uo == null; // Unity의 "가짜 null" 검사

        /// <summary>작업을 메인 스레드(Update)에서 실행하고 결과를 기다린다. 타임아웃이면 503.</summary>
        HttpResponseData RunOnMainThread(Func<HttpResponseData> work)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) return work();

            HttpResponseData result = null;
            Exception error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                var abandoned = false;
                lock (_mainQueue)
                {
                    _mainQueue.Enqueue(() =>
                    {
                        if (Volatile.Read(ref abandoned)) return;
                        try { result = work(); }
                        catch (Exception ex) { error = ex; }
                        finally { try { done.Set(); } catch (ObjectDisposedException) { } }
                    });
                }
                if (!done.Wait(MainThreadTimeoutMs))
                {
                    Volatile.Write(ref abandoned, true);
                    return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "main_thread_timeout", ["message"] = "게임 메인 스레드가 응답하지 않습니다(로딩 중이거나 일시정지?)." }, 503);
                }
            }
            if (error != null)
                return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "exception", ["message"] = error.GetType().Name + ": " + error.Message }, 500);
            return result;
        }

        HttpResponseData ServeCompanionPage()
        {
            try
            {
                var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                var htmlPath = Path.Combine(Path.Combine(dllDir, "companion"), "recommend.html");
                if (!File.Exists(htmlPath))
                {
                    return HttpResponseData.Json(new JObj
                    {
                        ["ok"] = false,
                        ["error"] = "companion_page_missing",
                        ["message"] = $"'{htmlPath}' 를 찾을 수 없습니다. 플러그인 폴더에 companion/recommend.html 이 있는지 확인하세요.",
                    }, 404);
                }
                return HttpResponseData.Html(File.ReadAllText(htmlPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "companion_page_read_failed", ["message"] = ex.Message }, 500);
            }
        }

        HttpResponseData BuildSnapshotResponse(bool includeDump)
        {
            var snapshot = InventoryReader.BuildSnapshot(includeDump, out var inv);
            if (inv != null) _cachedInventoryInstance = inv; // /apply가 재사용
            snapshot["plugin"] = new JObj { ["version"] = PluginVersion, ["port"] = _port };
            return HttpResponseData.Json(snapshot);
        }

        HttpResponseData BuildTabletsResponse()
        {
            var errors = new List<object>();
            var res = new JObj { ["ok"] = true };

            InventoryReader.BuildSnapshot(out var inv);
            if (inv != null) { _cachedInventoryInstance = inv; res["inventory"] = TabletReader.DescribeInventory(inv); }
            else errors.Add("인벤토리 없음 — 가방 석판은 생략");

            // ItemEntity(ScriptableObject).resourcePrefab 에 StoneTablet 컴포넌트가 붙은 것 = 석판 아이템 정의
            var entityType = ReflectionFinder.FindTypeByName("ItemEntity");
            var tabletType = ReflectionFinder.FindTypeByName("StoneTablet");
            var catalog = new List<object>();
            if (entityType == null || tabletType == null) errors.Add("ItemEntity/StoneTablet 타입을 찾지 못했습니다.");
            else
            {
                foreach (var entity in Resources.FindObjectsOfTypeAll(entityType))
                {
                    try
                    {
                        if (!(ReflectionFinder.GetMember(entity, "resourcePrefab") is GameObject prefab) || prefab == null) continue;
                        var tablet = prefab.GetComponent(tabletType);
                        if (tablet == null) continue;
                        var o = TabletReader.DescribeEntity(entity, tablet);
                        // 게임 해석기로 칸 계산: 9×9 가상 보드 중앙(4,4), 회전 0. 보드 기준 키워드는 이 크기 기준.
                        o["cells"] = TabletReader.ParseCells(tablet, o["query"] as string, 9, 9, 4, 4, 0, out var perr);
                        if (perr != null) o["parseError"] = perr;
                        catalog.Add(o);
                    }
                    catch (Exception ex) { errors.Add($"{entity.name}: {ex.Message}"); }
                }
            }
            res["catalog"] = catalog.OrderBy(o => MiniJson.AsInt(((JObj)o)["entityId"])).ToList();
            res["catalogCount"] = catalog.Count;
            res["errors"] = errors;
            return HttpResponseData.Json(res);
        }

        void Update()
        {
            // HTTP 스레드가 넘긴 작업 처리
            while (true)
            {
                Action next;
                lock (_mainQueue)
                {
                    if (_mainQueue.Count == 0) break;
                    next = _mainQueue.Dequeue();
                }
                try { next(); } catch (Exception ex) { Logger.LogError("[SephiriaLive] 메인 스레드 작업 실패: " + ex); }
            }

            // F10: 스냅샷 즉시 갱신. 게임이 "새 Input System 전용"이면 레거시 Input이 예외를 던지므로 한 번만 경고하고 끈다.
            if (_legacyInputBroken) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.F10))
                {
                    var snap = InventoryReader.BuildSnapshot(out var inv);
                    if (inv != null) _cachedInventoryInstance = inv;
                    Logger.LogInfo($"[SephiriaLive] F10 — 스냅샷 갱신 ok={snap["ok"]} items={snap["itemCount"]}");
                }
            }
            catch (InvalidOperationException ex)
            {
                _legacyInputBroken = true;
                Logger.LogWarning("[SephiriaLive] 레거시 Input을 쓸 수 없어 F10 단축키를 끕니다: " + ex.Message);
            }
        }

        void OnDestroy()
        {
            _server?.Dispose();
        }
    }
}
