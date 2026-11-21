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
using UnityEngine.UI;

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
        const int AutoArrangeTimeoutMs = 120000;

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
            LoadGradeWeapon();
            LoadGradeDamage();

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

            // /tablets/fixtures?w=6&h=5&rot=0: 엔진 검증용 — 모든 석판 × 모든 칸에서 게임 ParseQuery 결과(읽기 전용, 계산만).
            server.Route("GET", "/tablets/fixtures", ctx =>
            {
                int.TryParse(ctx.QueryParam("w"), out var w); int.TryParse(ctx.QueryParam("h"), out var h); int.TryParse(ctx.QueryParam("rot"), out var rot);
                if (w < 1 || h < 1 || w > 12 || h > 12 || rot < 0 || rot > 3)
                    return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "bad_params", ["message"] = "w,h는 1~12, rot는 0~3" }, 400);
                return RunOnMainThread(() =>
                {
                    var res = TabletReader.BuildFixtures(FindTabletPrefabs(), w, h, rot);
                    res["ok"] = true;
                    return HttpResponseData.Json(res);
                });
            });

            // /items: 아티팩트(ItemEntity type=Charm)·무기(WeaponEntity) 정의 덤프 — 위키 스크래핑 대체.
            server.Route("GET", "/items", ctx => RunOnMainThread(BuildItemsResponse));

            // 게임 안 선택지 등급의 기준 무기: 사이트에서 고른 무기(에셋 이름). {"weapon": null} 이면 지금 든 무기로.
            server.Route("GET", "/grade-weapon", ctx => HttpResponseData.Json(new JObj { ["ok"] = true, ["weapon"] = _gradeWeaponAsset }));
            server.Route("POST", "/grade-weapon", ctx => SetGradeWeapon(ctx.Body));
            // 게임 안 등급의 주요 피해 방식(여러 개): {"damage": ["fire","ice"]}. 빈 목록이면 반영 안 함.
            server.Route("GET", "/grade-damage", ctx => HttpResponseData.Json(new JObj { ["ok"] = true, ["damage"] = _gradeDamage.Cast<object>().ToList() }));
            server.Route("POST", "/grade-damage", ctx => SetGradeDamage(ctx.Body));

            // /icon?name=<에셋 이름>: 아이템·무기 아이콘(게임 Sprite → PNG). 만든 PNG는 캐시해서 다음부터는 메인 스레드를 안 탄다.
            server.Route("GET", "/icon", ctx => ServeIcon(ctx.QueryParam("name"), "", name =>
            {
                var sprite = FindIconSprite(name);
                return sprite == null ? null : TextureRegionToPng(sprite.texture, sprite.textureRect);
            }));

            // /keyword-icon?name=<키워드>: 효과 문장의 <sprite="Keyword" name=MP> 아이콘(TMP 스프라이트 묶음 "Keyword" → PNG)
            server.Route("GET", "/keyword-icon", ctx => ServeIcon(ctx.QueryParam("name"), "kw:", FindKeywordIconPng, cacheMiss: false));

            server.Route("GET", "/", ctx => ServeCompanionPage());
            server.Route("GET", "/recommend.html", ctx => ServeCompanionPage());

            // POST /autoarrange {"iterations":50,"allowRotation":true}: 게임 자체 자동 배치 실행(가방을 바꿈) — 엔진 결과 비교용.
            // 응답에 실행 전/후 스냅샷을 담는다(되돌리기는 호출 측이 /apply 로).
            server.Route("POST", "/autoarrange", ctx =>
            {
                JObj body;
                try { body = MiniJson.AsObj(MiniJson.Parse(string.IsNullOrWhiteSpace(ctx.Body) ? "{}" : ctx.Body)) ?? new JObj(); }
                catch (FormatException ex) { return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "bad_json", ["message"] = ex.Message }, 400); }
                int iterations = Math.Max(1, Math.Min(500, body["iterations"] == null ? 50 : MiniJson.AsInt(body["iterations"])));
                bool allowRotation = !(body["allowRotation"] is bool b && !b);
                return RunOnMainThread(() =>
                {
                    var before = InventoryReader.BuildSnapshot(false, out var inv);
                    if (inv == null) return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "no_inventory", ["before"] = before }, 409);
                    _cachedInventoryInstance = inv;
                    var ok = InventoryWriter.TryAutoArrange(inv, iterations, allowRotation, out var gameResult, out var err);
                    Logger.LogInfo($"[SephiriaLive] /autoarrange iterations={iterations} rotation={allowRotation} ok={ok} result={gameResult} err={err}");
                    var after = InventoryReader.BuildSnapshot(false, out _);
                    return HttpResponseData.Json(new JObj { ["ok"] = ok, ["gameResult"] = gameResult, ["error"] = err, ["iterations"] = iterations, ["before"] = before, ["after"] = after });
                }, AutoArrangeTimeoutMs); // 섞인 가방에서는 3초를 넘김 — 시간 초과로 응답만 끊기면 게임은 계속 바꾸는 중이라 호출 측이 헷갈린다
            });

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
        HttpResponseData RunOnMainThread(Func<HttpResponseData> work, int timeoutMs = MainThreadTimeoutMs)
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
                if (!done.Wait(timeoutMs))
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

        /// <summary>석판 아이템 정의(에셋 이름 → 프리팹의 StoneTablet 컴포넌트). 메인 스레드 전용.</summary>
        static List<KeyValuePair<string, object>> FindTabletPrefabs()
        {
            var list = new List<KeyValuePair<string, object>>();
            var entityType = ReflectionFinder.FindTypeByName("ItemEntity");
            var tabletType = ReflectionFinder.FindTypeByName("StoneTablet");
            if (entityType == null || tabletType == null) return list;
            foreach (var entity in Resources.FindObjectsOfTypeAll(entityType))
            {
                var prefab = ReflectionFinder.GetMember(entity, "resourcePrefab") as GameObject;
                var tablet = prefab != null ? prefab.GetComponent(tabletType) : null;
                if (tablet != null) list.Add(new KeyValuePair<string, object>(entity.name, tablet));
            }
            return list;
        }

        HttpResponseData BuildItemsResponse()
        {
            var errors = new List<object>();
            var artifacts = new List<JObj>();
            var weapons = new List<JObj>();

            var entityType = ReflectionFinder.FindTypeByName("ItemEntity");
            var charmType = ReflectionFinder.FindTypeByName("Charm_Basic");
            if (entityType == null || charmType == null) errors.Add("ItemEntity/Charm_Basic 타입을 찾지 못했습니다.");
            else
            {
                foreach (var entity in Resources.FindObjectsOfTypeAll(entityType))
                {
                    try
                    {
                        if (ReflectionFinder.GetMember(entity, "type")?.ToString() != "Charm") continue;
                        var prefab = ReflectionFinder.GetMember(entity, "resourcePrefab") as GameObject;
                        artifacts.Add(ItemReader.DescribeCharm(entity, prefab != null ? prefab.GetComponent(charmType) : null));
                    }
                    catch (Exception ex) { errors.Add($"{entity.name}: {ex.Message}"); }
                }
            }

            var weaponType = ReflectionFinder.FindTypeByName("WeaponEntity");
            var weaponBasicType = ReflectionFinder.FindTypeByName("WeaponBasic");
            var weaponSimpleType = ReflectionFinder.FindTypeByName("WeaponSimple");
            if (weaponType == null) errors.Add("WeaponEntity 타입을 찾지 못했습니다.");
            else
            {
                foreach (var w in Resources.FindObjectsOfTypeAll(weaponType))
                {
                    try
                    {
                        var prefab = ReflectionFinder.GetMember(w, "mainWeaponPrefab") as GameObject;
                        var basic = prefab != null && weaponBasicType != null ? prefab.GetComponentInChildren(weaponBasicType, true) : null;
                        var simple = prefab != null && weaponSimpleType != null ? prefab.GetComponentInChildren(weaponSimpleType, true) : null;
                        weapons.Add(ItemReader.DescribeWeapon(w, simple, basic));
                    }
                    catch (Exception ex) { errors.Add($"{w.name}: {ex.Message}"); }
                }
                ItemReader.AssignTiers(weapons);
            }

            return HttpResponseData.Json(new JObj
            {
                ["ok"] = true,
                ["artifacts"] = artifacts.OrderBy(o => MiniJson.AsInt(o["id"])).Cast<object>().ToList(),
                ["weapons"] = weapons.OrderBy(o => MiniJson.AsInt(o["id"])).Cast<object>().ToList(),
                ["artifactCount"] = artifacts.Count,
                ["weaponCount"] = weapons.Count,
                ["errors"] = errors,
            });
        }

        readonly Dictionary<string, byte[]> _iconCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Dictionary<string, Sprite> _iconSprites;

        static HttpResponseData IconResponse(string name, byte[] png) => png != null
            ? HttpResponseData.Binary(png, "image/png")
            : HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "icon_not_found", ["name"] = name }, 404);

        /// <summary>에셋 이름 → 아이콘 Sprite. ItemEntity.icon(아티팩트·석판·포션 등)과 WeaponEntity.icon. 메인 스레드 전용.</summary>
        Sprite FindIconSprite(string assetName)
        {
            if (_iconSprites == null)
            {
                var map = new Dictionary<string, Sprite>(StringComparer.Ordinal);
                foreach (var typeName in new[] { "ItemEntity", "WeaponEntity" })
                {
                    var t = ReflectionFinder.FindTypeByName(typeName);
                    if (t == null) continue;
                    foreach (var e in Resources.FindObjectsOfTypeAll(t))
                        if (ReflectionFinder.GetMember(e, "icon") is Sprite s && s != null && !map.ContainsKey(e.name)) map[e.name] = s;
                }
                if (map.Count > 0) _iconSprites = map; // 로딩 전(0개)이면 다음 요청 때 다시 찾는다
                else return null;
            }
            return _iconSprites.TryGetValue(assetName, out var sprite) ? sprite : null;
        }

        /// <summary>이름 → PNG(처음 한 번만 메인 스레드에서 만들고 캐시, 없는 것도 null 로 캐시해 반복 탐색 방지).</summary>
        HttpResponseData ServeIcon(string name, string cachePrefix, Func<string, byte[]> make, bool cacheMiss = true)
        {
            if (string.IsNullOrEmpty(name)) return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "name_required" }, 400);
            var key = cachePrefix + name;
            lock (_iconCache) if (_iconCache.TryGetValue(key, out var cached)) return IconResponse(name, cached);
            return RunOnMainThread(() =>
            {
                byte[] png = null;
                try { png = make(name); }
                catch (Exception ex) { Logger.LogWarning($"[SephiriaLive] 아이콘 변환 실패 {key}: {ex.Message}"); }
                if (png != null || cacheMiss) lock (_iconCache) _iconCache[key] = png;
                return IconResponse(name, png);
            });
        }

        static bool _keywordMissLogged;

        /// <summary>
        /// 효과 문장 키워드 아이콘(TMP 스프라이트 묶음 "Keyword" 안의 이름)의 glyphRect 영역을 PNG로. TextMeshPro DLL을 참조하지 않고 리플렉션으로 읽는다.
        /// TMP 는 스프라이트 묶음을 처음 필요할 때 불러오므로 다음 순서로 찾는다:
        ///  1) 메모리의 TMP_SpriteAsset 중 이름 "Keyword"  2) Resources.Load(TMP_Settings.defaultSpriteAssetPath + "Keyword")
        ///  3) 게임 정적 필드 EmeraldShopPrice.keywordSpriteAsset  4) 그 이름의 아이콘을 가진 아무 묶음.
        /// 메인 스레드 전용.
        /// </summary>
        static byte[] FindKeywordIconPng(string name)
        {
            var assetType = ReflectionFinder.FindTypeByName("TMP_SpriteAsset");
            if (assetType == null) return null;
            var loaded = Resources.FindObjectsOfTypeAll(assetType);
            var candidates = new List<UnityEngine.Object>(loaded.Where(a => a.name == "Keyword"));
            var settings = ReflectionFinder.FindTypeByName("TMP_Settings");
            var path = settings == null ? null : ReflectionFinder.GetMember(settings, "defaultSpriteAssetPath") as string;
            if (path != null) { var r = Resources.Load(path + "Keyword", assetType); if (r != null) candidates.Add(r); }
            var shop = ReflectionFinder.FindTypeByName("EmeraldShopPrice");
            if (shop != null && ReflectionFinder.GetMember(shop, "keywordSpriteAsset") is UnityEngine.Object f && f != null) candidates.Add(f);
            candidates.AddRange(loaded);

            foreach (var asset in candidates.Distinct())
                foreach (var ch in ReflectionFinder.EnumerateValues(ReflectionFinder.GetMember(asset, "spriteCharacterTable")))
                {
                    if (ReflectionFinder.GetMember(ch, "name") as string != name) continue;
                    var rect = ReflectionFinder.GetMember(ReflectionFinder.GetMember(ch, "glyph"), "glyphRect");
                    var sheet = ReflectionFinder.GetMember(asset, "spriteSheet") as Texture;
                    if (rect == null || sheet == null) continue;
                    int I(string fld) => Convert.ToInt32(ReflectionFinder.GetMember(rect, fld));
                    return TextureRegionToPng(sheet, new Rect(I("x"), I("y"), I("width"), I("height")));
                }
            if (!_keywordMissLogged)
            {
                _keywordMissLogged = true;
                Debug.LogWarning($"[SephiriaLive] 키워드 아이콘 '{name}' 못 찾음 — TMP_SpriteAsset {loaded.Length}개: {string.Join(", ", loaded.Select(a => a.name).Take(20))}, 경로={path}");
            }
            return null;
        }

        /// <summary>
        /// 게임 텍스처는 보통 CPU에서 읽을 수 없고(isReadable=false) 여러 아이콘이 한 장(아틀라스)에 모여 있다.
        /// 그래서 RenderTexture에 그린 뒤 r 영역만(아래 왼쪽 기준 픽셀 좌표) 읽어 PNG로 만든다.
        /// </summary>
        static byte[] TextureRegionToPng(Texture src, Rect r)
        {
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                tex = new Texture2D(Mathf.RoundToInt(r.width), Mathf.RoundToInt(r.height), TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (tex != null) Destroy(tex);
            }
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

            // 아이템 선택 화면 등급: 0.5초마다 다시 계산해 게임 UI 배지를 갱신
            if (Time.unscaledTime >= _nextGradeRefresh)
            {
                _nextGradeRefresh = Time.unscaledTime + 0.5f;
                try { RefreshRewardGrades(); }
                catch (Exception ex) { Logger.LogWarning("[SephiriaLive] 선택지 등급 계산 실패: " + ex.Message); }
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

        // ---------------- 아이템 선택 화면(Sephirite 보상)·상점에 등급 표시 (게임 UI 안에 배지) ----------------
        // 선택지 = UI_SephiriteRewardElement(reward.entityID). 아티팩트만 등급(석판 등 제외).
        // 계산은 Core GradeCalculator(추천 페이지와 같은 식) — 현재 가방 태그 + 현재 무기 키워드 + 희귀도·최대 레벨.

        float _nextGradeRefresh;
        Dictionary<string, JObj> _artifactsByAsset, _weaponsByAsset;
        JObj _tagKo, _kwKo, _dmgKo; // 키워드 요약용 한국어 이름(data.json tagKo·kwKo·dmgKo)
        volatile string _gradeWeaponAsset; // 사이트에서 고른 등급 기준 무기(에셋 이름), 없으면 null

        static string GradeWeaponPath => Path.Combine(Paths.ConfigPath, "SephiriaLive.grade-weapon.txt");
        static string GradeDamagePath => Path.Combine(Paths.ConfigPath, "SephiriaLive.grade-damage.txt");
        volatile string[] _gradeDamage = new string[0]; // 사이트에서 고른 주요 피해 방식(data.json dt 값)

        void LoadGradeDamage()
        {
            try
            {
                if (File.Exists(GradeDamagePath))
                    _gradeDamage = File.ReadAllText(GradeDamagePath, Encoding.UTF8).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            }
            catch (Exception ex) { Logger.LogWarning("[SephiriaLive] 주요 피해 방식 설정을 읽지 못했습니다: " + ex.Message); }
        }

        HttpResponseData SetGradeDamage(string body)
        {
            List<object> list;
            try { list = MiniJson.AsList(MiniJson.AsObj(MiniJson.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))?["damage"]); }
            catch (FormatException ex) { return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "bad_json", ["message"] = ex.Message }, 400); }
            var values = list.Select(o => o as string).ToArray();
            // 파일에 그대로 쓰므로 영문 소문자·_ 만, 12개 이하
            if (values.Length > 12 || values.Any(v => v == null || v.Length > 20 || !System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-z_]+$")))
                return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "bad_damage" }, 400);
            values = values.Distinct().ToArray();
            try { File.WriteAllText(GradeDamagePath, string.Join(",", values), new UTF8Encoding(false)); }
            catch (Exception ex) { return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "save_failed", ["message"] = ex.Message }, 500); }
            _gradeDamage = values;
            Logger.LogInfo($"[SephiriaLive] 주요 피해 방식 = {(values.Length == 0 ? "(없음)" : string.Join(",", values))}");
            return HttpResponseData.Json(new JObj { ["ok"] = true, ["damage"] = values.Cast<object>().ToList() });
        }

        void LoadGradeWeapon()
        {
            try { _gradeWeaponAsset = File.Exists(GradeWeaponPath) ? File.ReadAllText(GradeWeaponPath, Encoding.UTF8).Trim() : null; }
            catch (Exception ex) { Logger.LogWarning("[SephiriaLive] 등급 기준 무기 설정을 읽지 못했습니다: " + ex.Message); }
            if (string.IsNullOrEmpty(_gradeWeaponAsset)) _gradeWeaponAsset = null;
        }

        HttpResponseData SetGradeWeapon(string body)
        {
            object weapon;
            try { weapon = MiniJson.AsObj(MiniJson.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))?["weapon"]; }
            catch (FormatException ex) { return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "bad_json", ["message"] = ex.Message }, 400); }
            var asset = weapon as string;
            // 에셋 이름처럼 생긴 값만(파일에 그대로 쓰므로): 영문·숫자·_·'·- , 200자 이하
            if (asset != null && (asset.Length > 200 || !System.Text.RegularExpressions.Regex.IsMatch(asset, @"^[A-Za-z0-9_'\-]+$")))
                return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "bad_weapon" }, 400);
            try
            {
                if (asset == null) { if (File.Exists(GradeWeaponPath)) File.Delete(GradeWeaponPath); }
                else File.WriteAllText(GradeWeaponPath, asset, new UTF8Encoding(false)); // BOM 없이
            }
            catch (Exception ex) { return HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "save_failed", ["message"] = ex.Message }, 500); }
            _gradeWeaponAsset = asset;
            Logger.LogInfo($"[SephiriaLive] 등급 기준 무기 = {asset ?? "(지금 든 무기)"}");
            return HttpResponseData.Json(new JObj { ["ok"] = true, ["weapon"] = asset });
        }

        void LoadGradeData()
        {
            if (_artifactsByAsset != null) return;
            _artifactsByAsset = new Dictionary<string, JObj>(); _weaponsByAsset = new Dictionary<string, JObj>();
            var path = Path.Combine(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "companion"), "data.json");
            if (!File.Exists(path)) { Logger.LogWarning("[SephiriaLive] companion/data.json 이 없어 선택지 등급을 표시하지 않습니다."); return; }
            var data = MiniJson.AsObj(MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8)));
            foreach (var a in MiniJson.AsList(data?["artifacts"]).Select(MiniJson.AsObj)) if (a?["v"] is string v) _artifactsByAsset[v] = a;
            foreach (var w in MiniJson.AsList(data?["weapons"]).Select(MiniJson.AsObj)) if (w?["v"] is string v) _weaponsByAsset[v] = w;
            _tagKo = MiniJson.AsObj(data?["tagKo"]); _kwKo = MiniJson.AsObj(data?["kwKo"]); _dmgKo = MiniJson.AsObj(data?["dmgKo"]);
        }

        static object CallStatic(string typeName, string method, object arg)
        {
            var t = ReflectionFinder.FindTypeByName(typeName);
            var m = t == null ? null : ReflectionFinder.FindMethod(t, method, 1);
            return m != null && m.IsStatic && ReflectionFinder.TryInvoke(null, m, new[] { arg }, out var r, out _) ? r : null;
        }

        /// <summary>
        /// 등급 계산 준비: 현재 가방 태그 수, 기준 무기(사이트에서 고른 무기 → 없으면 지금 든 무기), 전체 아티팩트 점수 분포.
        /// 반환한 함수는 아이템 번호(entityID) → 등급 결과(아티팩트가 아니면 null). 선택 화면·상점이 같이 쓴다.
        /// </summary>
        Func<object, (GradeCalculator.Result result, string keys)> BuildGrader()
        {
            LoadGradeData();
            if (_artifactsByAsset.Count == 0) return null;

            var snap = InventoryReader.BuildSnapshot(out var inv);
            var invArtifacts = MiniJson.AsList(snap["items"]).Select(MiniJson.AsObj)
                .Where(i => i?["itemType"] as string == "Charm" && i["assetName"] is string)
                .Select(i => _artifactsByAsset.TryGetValue((string)i["assetName"], out var d) ? d : null).Where(d => d != null);
            var tagCounts = GradeCalculator.TagCounts(invArtifacts);

            JObj weapon = null;
            var chosen = _gradeWeaponAsset;
            if (chosen != null) _weaponsByAsset.TryGetValue(chosen, out weapon);
            var avatar = inv == null ? null : ReflectionFinder.GetMember(inv, "UnitAvatar");
            var wcType = ReflectionFinder.FindTypeByName("WeaponControllerSimple");
            if (weapon == null && avatar != null && wcType != null)
                foreach (var wc in UnityEngine.Object.FindObjectsByType(wcType, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (!Equals(ReflectionFinder.GetMember(wc, "unitAvatar"), avatar)) continue;
                    var cur = ReflectionFinder.GetMember(wc, "currentWeapon");
                    var we = cur == null ? null : CallStatic("WeaponDatabase", "FindWeaponById", ReflectionFinder.GetMember(cur, "entityId")) as UnityEngine.Object;
                    if (we != null) _weaponsByAsset.TryGetValue(we.name, out weapon);
                    break;
                }

            // 절대평가: 같은 빌드·무기 기준으로 전체 아티팩트를 점수 매긴 분포에서의 위치
            var focus = new HashSet<string>(_gradeDamage);
            var allTotals = _artifactsByAsset.Values.Select(d => GradeCalculator.Score(d, tagCounts, weapon, focus).Total).ToList();
            return entityId =>
            {
                var entity = CallStatic("ItemDatabase", "FindItemById", entityId) as UnityEngine.Object;
                if (entity == null || ReflectionFinder.GetMember(entity, "type")?.ToString() != "Charm") return (null, null); // 석판·포션 등은 등급 없음
                if (!_artifactsByAsset.TryGetValue(entity.name, out var def)) return (null, null);
                var r = GradeCalculator.Score(def, tagCounts, weapon, focus);
                GradeCalculator.AssignGrades(new[] { r }, allTotals);
                return (r, GradeCalculator.Summary(r, tagCounts, _tagKo, _kwKo, 3, _dmgKo));
            };
        }

        static UnityEngine.Object[] ActiveObjects(string typeName)
        {
            var t = ReflectionFinder.FindTypeByName(typeName);
            return t == null ? new UnityEngine.Object[0] : UnityEngine.Object.FindObjectsByType(t, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }

        Font _gameFont; // 게임 UI(legacy Text) 글꼴 — 배지 글자를 게임과 같은 픽셀 글꼴로

        /// <summary>
        /// 게임 UI 안에 등급 배지(툴팁·스크롤 마스크에 자연스럽게 가려짐):
        ///  · 아이템 선택 화면: 받침대 UI_SephiriteRewardElement(reward.entityID)
        ///  · 상점: UI_ShopPanel.shopInventoryIconList 의 UI_NewInventoryIcon(Item.EntityID) — 같은 아이콘 클래스가 내 가방에도 쓰여서 상점 목록 것만
        /// 둘 다 없으면 아무것도 안 함.
        /// </summary>
        void RefreshRewardGrades()
        {
            var rewards = ActiveObjects("UI_SephiriteRewardElement");
            var shops = ActiveObjects("UI_ShopPanel");
            if (rewards.Length == 0 && shops.Length == 0) return;
            var grader = BuildGrader();
            if (grader == null) return;

            foreach (var el in rewards)
            {
                var reward = ReflectionFinder.GetMember(el, "reward");
                var font = (ReflectionFinder.GetMember(el, "nameText") as Text)?.font;
                if (font != null) _gameFont = font;
                var (r, keys) = reward == null ? (null, null) : grader(ReflectionFinder.GetMember(reward, "entityID"));
                SetGradeLabel(((Component)el).transform as RectTransform, r, _gameFont, keys, compact: false);
            }

            if (shops.Length == 0) return;
            if (_gameFont == null) _gameFont = UnityEngine.Object.FindObjectsByType<Text>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(t => t.font).FirstOrDefault(f => f != null);
            foreach (var shop in shops)
                foreach (var icon in ReflectionFinder.EnumerateValues(ReflectionFinder.GetMember(shop, "shopInventoryIconList")).OfType<Component>())
                {
                    if (icon == null || !icon.gameObject.activeInHierarchy) continue;
                    var item = ReflectionFinder.GetMember(icon, "Item");
                    SetGradeLabel(icon.transform as RectTransform, item == null ? null : grader(ReflectionFinder.GetMember(item, "EntityID")).result, _gameFont, null, compact: true);
                }
        }

        const string LabelName = "SephiriaLiveGrade";

        /// <summary>
        /// op.gg 처럼 깔끔한 한 줄 라벨: "<등급색>A</> 정밀/그림자 콤보, 치명타".
        ///  · 선택 화면(compact=false): 아이템 바로 위, 아이템 너비 안(글자가 길면 자동으로 줄어듦), 반투명 어두운 띠.
        ///  · 상점(compact=true): 목록 줄 간격이 좁아 아이콘 안 오른쪽 위에 등급 글자만.
        /// result 가 null(아티팩트 아님)이면 떼어낸다. 클릭을 막지 않도록 raycastTarget 은 끈다.
        /// </summary>
        static void SetGradeLabel(RectTransform rect, GradeCalculator.Result result, Font font, string keys, bool compact)
        {
            if (rect == null) return;
            var existing = rect.Find(LabelName);
            if (result == null) { if (existing != null) Destroy(existing.gameObject); return; }

            RectTransform label;
            if (existing == null)
            {
                label = NewUi(LabelName, rect);
                var bg = label.gameObject.AddComponent<Image>();
                bg.raycastTarget = false;
                var text = NewUi("Text", label).gameObject.AddComponent<Text>();
                text.raycastTarget = false;
                text.supportRichText = true;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
                text.resizeTextForBestFit = true; // 아이템 너비를 넘지 않게 글자 크기를 줄임
                text.color = Color.white;
            }
            else label = (RectTransform)existing;
            label.SetAsLastSibling();

            var t = label.Find("Text").GetComponent<Text>();
            t.font = font ?? t.font ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var grade = "<color=#" + ColorUtility.ToHtmlStringRGB(GradeColor(result.Grade)) + ">" + result.Grade + "</color>";
            float w = rect.rect.width;
            if (compact)
            {
                float size = Mathf.Clamp(w * 0.28f, 10f, 22f);
                label.anchorMin = label.anchorMax = label.pivot = new Vector2(1f, 1f);
                label.sizeDelta = new Vector2(size, size);
                label.anchoredPosition = new Vector2(-2f, -2f); // 아이콘 안쪽
                t.alignment = TextAnchor.MiddleCenter;
                t.resizeTextMinSize = 6; t.resizeTextMaxSize = Mathf.RoundToInt(size * 0.85f);
                t.text = grade;
            }
            else
            {
                float h = Mathf.Clamp(w * 0.16f, 10f, 20f);
                label.anchorMin = label.anchorMax = new Vector2(0.5f, 1f);
                label.pivot = new Vector2(0.5f, 0f);
                label.sizeDelta = new Vector2(w, h);
                label.anchoredPosition = new Vector2(0f, 2f); // 아이템 바로 위
                t.alignment = TextAnchor.MiddleLeft; // 등급 글자는 설명 유무와 상관없이 항상 왼쪽
                t.resizeTextMinSize = 5; t.resizeTextMaxSize = Mathf.RoundToInt(h * 0.8f);
                t.text = string.IsNullOrEmpty(keys) ? grade : grade + " " + keys;
            }
            var tr = (RectTransform)t.transform;
            tr.offsetMin = new Vector2(2f, 0f); tr.offsetMax = new Vector2(-2f, 0f);
            label.GetComponent<Image>().color = new Color(0.05f, 0.04f, 0.07f, 0.72f);
        }

        static RectTransform NewUi(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; // 기본: 부모 꽉 채움
            return rt;
        }

        static Color GradeColor(string g)
        {
            switch (g)
            {
                case "S": return new Color(1f, 0.27f, 0.27f);   // 빨강
                case "A": return new Color(1f, 0.85f, 0.2f);    // 노랑
                case "B": return new Color(0.3f, 0.6f, 1f);     // 파랑
                case "C": return new Color(0.35f, 0.85f, 0.4f); // 초록
                default: return new Color(0.62f, 0.62f, 0.62f); // 회색
            }
        }

        void OnDestroy()
        {
            _server?.Dispose();
        }
    }
}
