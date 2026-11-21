using System;
using System.Collections.Generic;
using System.Linq;
using SephiriaLive.Core;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"ok   - {name}"); }
    else { fail++; Console.WriteLine($"FAIL - {name}  {detail}"); }
}

// ---------- MatrixExtractor: 2D 배열 ----------
{
    var m = new int[2, 3] { { 1, 2, 3 }, { 4, 5, 6 } };
    var r = MatrixExtractor.Extract(m);
    Check("2D 배열 인식", r.Recognized && r.ShapeKind == "rect2d", r.ShapeKind);
    Check("2D 배열 크기", r.Rows == 2 && r.Cols == 3, $"{r.Rows}x{r.Cols}");
    Check("2D 배열 값", (int)r.Cells[1][2] == 6, r.Cells[1][2]?.ToString());
}

// ---------- MatrixExtractor: 배열의 배열 ----------
{
    var m = new int[][] { new[] { 1, 2 }, new[] { 3, 4 }, new[] { 5, 6 } };
    var r = MatrixExtractor.Extract(m);
    Check("jagged 배열 인식", r.Recognized && r.ShapeKind == "jagged", r.ShapeKind);
    Check("jagged 배열 크기", r.Rows == 3 && r.Cols == 2, $"{r.Rows}x{r.Cols}");
}

// ---------- MatrixExtractor: List<List<T>> ----------
{
    var m = new List<List<double>> { new() { 1.5, 2.5 }, new() { 3.5, 4.5 } };
    var r = MatrixExtractor.Extract(m);
    Check("List<List<>> 인식", r.Recognized && r.ShapeKind == "listOfList", r.ShapeKind);
    Check("List<List<>> 값", (double)r.Cells[0][1] == 2.5, r.Cells[0][1]?.ToString());
}

// ---------- MatrixExtractor: null / 미지원 타입 ----------
{
    var r = MatrixExtractor.Extract(null);
    Check("null 처리", !r.Recognized && r.ShapeKind == "null", r.ShapeKind);
}

// ---------- ReflectionFinder: 필드/프로퍼티 겸용 ----------
{
    var obj = new FakeSlab { xIdx = 2, rotation = 1 };
    Check("필드 읽기", (int)ReflectionFinder.GetMember(obj, "xIdx") == 2);
    Check("프로퍼티 읽기", (int)ReflectionFinder.GetMember(obj, "Rotation2") == 3); // rotation(1) + xIdx(2)
    var dump = ReflectionFinder.DumpMembers(obj);
    Check("DumpMembers가 필드를 모두 나열", dump.Exists(m => m.name == "xIdx"));
}

// ---------- ReflectionFinder: 타입 이름으로 찾기 (같은 AppDomain 내) ----------
{
    var t = ReflectionFinder.FindTypeByName("FakeGridInventory");
    Check("같은 프로세스 내 타입을 이름으로 탐색", t != null && t == typeof(FakeGridInventory));
}

// ---------- 실제 InventoryReader 파이프라인과 동일한 방식으로 가짜 인벤토리 전체 추출 ----------
{
    var fake = new FakeGridInventory
    {
        inventoryMatrix = new[] { new[] { "sword_earring", "" }, new[] { "", "dry" } },
        levelMatrix = new[,] { { 2, 0 }, { 0, 0 } },
        maxLevelMatrix = new[,] { { 3, 0 }, { 0, 0 } },
    };
    var lv = MatrixExtractor.Extract(ReflectionFinder.GetMember(fake, "levelMatrix"));
    Check("가짜 인벤토리 levelMatrix 추출", lv.Recognized && (int)lv.Cells[0][0] == 2);
    var inv = MatrixExtractor.Extract(ReflectionFinder.GetMember(fake, "inventoryMatrix"));
    Check("가짜 인벤토리 inventoryMatrix 추출", inv.Recognized && (string)inv.Cells[1][1] == "dry");
}

// ---------- MiniJson 직렬화 ----------
{
    var obj = new JObj { ["ok"] = true, ["n"] = 3, ["name"] = "건조\"테스트", ["arr"] = new List<object> { 1, 2, 3 }, ["nil"] = null };
    var json = MiniJson.Write(obj);
    Check("JSON에 bool 포함", json.Contains("\"ok\":true"), json);
    Check("JSON 문자열 이스케이프", json.Contains("건조\\\"테스트"), json);
    Check("JSON 배열", json.Contains("\"arr\":[1,2,3]"), json);
    Check("JSON null", json.Contains("\"nil\":null"), json);
}

// ---------- HttpResponseData.Html ----------
{
    var resp = HttpResponseData.Html("<html><body>hi</body></html>");
    Check("Html(): content-type", resp.ContentType.StartsWith("text/html"), resp.ContentType);
    Check("Html(): body 그대로", resp.Body.Contains("<body>hi</body>"));
}

// ---------- MiniHttpServer: 실제 소켓으로 왕복 테스트 ----------
{
    var server = new MiniHttpServer(48765);
    server.Route("GET", "/inv", ctx => HttpResponseData.Json(new JObj { ["ok"] = true, ["msg"] = "hello" }));
    server.Route("POST", "/apply", ctx => HttpResponseData.Json(new JObj { ["ok"] = true, ["echo"] = ctx.Body }));
    server.Start();
    System.Threading.Thread.Sleep(200);

    using var http = new System.Net.Http.HttpClient();
    try
    {
        var getResult = http.GetStringAsync("http://127.0.0.1:48765/inv").GetAwaiter().GetResult();
        Check("HTTP GET /inv 응답", getResult.Contains("\"hello\""), getResult);

        var content = new System.Net.Http.StringContent("{\"placements\":[]}", System.Text.Encoding.UTF8, "application/json");
        var postResp = http.PostAsync("http://127.0.0.1:48765/apply", content).GetAwaiter().GetResult();
        var postBody = postResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("HTTP POST /apply 응답", postBody.Contains("placements"), postBody);

        var notFound = http.GetAsync("http://127.0.0.1:48765/nope").GetAwaiter().GetResult();
        Check("존재하지 않는 경로 404", (int)notFound.StatusCode == 404);
    }
    finally
    {
        server.Stop();
    }
}

// ---------- MiniJson.Parse ----------
{
    var parsed = MiniJson.Parse("{\"ok\":true,\"n\":3,\"name\":\"건조\\\"테스트\",\"arr\":[1,2.5,null,false],\"nested\":{\"a\":1}}") as JObj;
    Check("Parse: 객체 인식", parsed != null);
    Check("Parse: bool", (bool)parsed["ok"] == true);
    Check("Parse: 숫자", MiniJson.AsInt(parsed["n"]) == 3, parsed["n"]?.ToString());
    Check("Parse: 이스케이프된 문자열", (string)parsed["name"] == "건조\"테스트", (string)parsed["name"]);
    var arr = MiniJson.AsList(parsed["arr"]);
    Check("Parse: 배열 길이", arr.Count == 4, arr.Count.ToString());
    Check("Parse: 배열 안 null", arr[2] == null);
    Check("Parse: 배열 안 false", (bool)arr[3] == false);
    var nested = MiniJson.AsObj(parsed["nested"]);
    Check("Parse: 중첩 객체", MiniJson.AsInt(nested["a"]) == 1);
}
{
    // 왕복(round-trip): Write한 걸 다시 Parse했을 때 같은 모양이 나오는지
    var original = new JObj { ["x"] = 1, ["y"] = 2.5, ["s"] = "hello\nworld", ["list"] = new List<object> { 1, 2, 3 } };
    var roundtrip = MiniJson.AsObj(MiniJson.Parse(MiniJson.Write(original)));
    Check("왕복: 숫자", MiniJson.AsInt(roundtrip["x"]) == 1);
    Check("왕복: 문자열(개행 포함)", (string)roundtrip["s"] == "hello\nworld");
    Check("왕복: 리스트 길이", MiniJson.AsList(roundtrip["list"]).Count == 3);
}
{
    bool threw = false;
    try { MiniJson.Parse("{broken"); } catch (FormatException) { threw = true; }
    Check("Parse: 잘못된 JSON은 FormatException", threw);
}

// ---------- InventoryWriter: 가짜 인벤토리로 이동+회전 적용 ----------
{
    var inv = new FakeGridInventory2
    {
        stoneTablets = new List<FakeTablet>
        {
            new FakeTablet { xIdx = 0, yIdx = 0, rotation = 0, internalName = "dry" },
            new FakeTablet { xIdx = 1, yIdx = 0, rotation = 0, internalName = "sword_earring" },
        }
    };
    var cmds = new List<InventoryWriter.PlacementCommand>
    {
        new() { FromX = 0, FromY = 0, ToX = 2, ToY = 1, Rotation = 1 },
    };
    var result = InventoryWriter.Apply(inv, cmds);
    Check("Apply: 이동 1건 성공", result.Moved == 1, string.Join(" | ", result.Errors));
    Check("Apply: 재계산 메서드 호출됨", inv.RecalculateCallCount == 2, inv.RecalculateCallCount.ToString()); // 이동 1회 + 회전 1회
    var moved = inv.stoneTablets.First(t => t.internalName == "dry");
    Check("Apply: 좌표 반영", moved.xIdx == 2 && moved.yIdx == 1, $"{moved.xIdx},{moved.yIdx}");
    Check("Apply: 회전 반영(엔진→게임 변환, rot1 -> gameRot (4-1)%4=3)", moved.rotation == 3, moved.rotation.ToString());
}
{
    // 존재하지 않는 좌표에서 이동하려 하면 에러를 기록하되 죽지 않는다
    var inv = new FakeGridInventory2 { stoneTablets = new List<FakeTablet>() };
    var cmds = new List<InventoryWriter.PlacementCommand> { new() { FromX = 9, FromY = 9, ToX = 0, ToY = 0 } };
    var result = InventoryWriter.Apply(inv, cmds);
    Check("Apply: 못 찾은 아이템은 에러로 기록, 예외 없음", result.Moved == 0 && result.Errors.Count > 0);
}
{
    // JSON 바디 -> PlacementCommand 파싱
    var json = "{\"placements\":[{\"fromX\":0,\"fromY\":1,\"toX\":2,\"toY\":3,\"rotation\":2},{\"fromX\":1,\"fromY\":1,\"toX\":1,\"toY\":2}]}";
    var cmds = InventoryWriter.ParseCommands(MiniJson.Parse(json));
    Check("ParseCommands: 개수", cmds.Count == 2, cmds.Count.ToString());
    Check("ParseCommands: 좌표", cmds[0].ToX == 2 && cmds[0].ToY == 3);
    Check("ParseCommands: 회전 있음/없음 구분", cmds[0].Rotation == 2 && cmds[1].Rotation == null);
}

// ---------- [추가] 실제 게임 구조 흉내: 좌표 키 딕셔너리(posDict) ----------
{
    var levels = new Dictionary<FakePos, int> { [new FakePos(0, 0)] = 2, [new FakePos(2, 1)] = 5 };
    var r = MatrixExtractor.Extract(levels);
    Check("posDict 인식", r.Recognized && r.ShapeKind == "posDict", r.ShapeKind + " " + r.Note);
    Check("posDict 크기(행=y, 열=x)", r.Rows == 2 && r.Cols == 3, $"{r.Rows}x{r.Cols}");
    Check("posDict 값 위치", (int)r.Cells[1][2] == 5 && r.Cells[0][1] == null);
}

// ---------- [추가] game-api 모드: Swap 연쇄 이동 + Rotate 반복 호출 ----------
{
    var inv = new FakeGameInventory();
    inv.Put(0, 0, "A", isTablet: true);
    inv.Put(1, 0, "B");
    // A: (0,0)->(1,0), B: (1,0)->(2,0)  — 연쇄 이동이므로 B는 첫 Swap 후 (0,0)에 가 있다
    var cmds = new List<InventoryWriter.PlacementCommand>
    {
        new() { FromX = 0, FromY = 0, ToX = 1, ToY = 0, Rotation = 1 },
        new() { FromX = 1, FromY = 0, ToX = 2, ToY = 0 },
    };
    var result = InventoryWriter.Apply(inv, cmds);
    Check("game-api 모드 선택", result.Mode == "game-api", result.Mode);
    Check("game-api: 오류 없음", result.Ok, string.Join(" | ", result.Errors));
    Check("game-api: A는 (1,0)", inv.NameAt(1, 0) == "A", inv.Dump());
    Check("game-api: B는 (2,0)", inv.NameAt(2, 0) == "B", inv.Dump());
    Check("game-api: (0,0)은 비어 있음", inv.NameAt(0, 0) == null, inv.Dump());
    Check("game-api: Rotate()로 게임 rotation=3", inv.TabletOf("A").rotation == 3, inv.TabletOf("A").rotation.ToString());
}
{
    var inv = new FakeGameInventory();
    inv.Put(0, 0, "A", isTablet: true, rotatable: false);
    var result = InventoryWriter.Apply(inv, new List<InventoryWriter.PlacementCommand> { new() { FromX = 0, FromY = 0, ToX = 0, ToY = 0, Rotation = 2 } });
    Check("game-api: 회전 불가 석판은 에러로 기록", !result.Ok && result.Rotated == 0, string.Join(" | ", result.Errors));
}
{
    // 실게임: DoClickAction(ItemPosition)이 있으면 Rotate() 대신 그걸로 회전(레벨 재계산 경로)
    var inv = new FakeGameInventoryWithClick();
    inv.Put(0, 0, "A", isTablet: true);
    var result = InventoryWriter.Apply(inv, new List<InventoryWriter.PlacementCommand> { new() { FromX = 0, FromY = 0, ToX = 1, ToY = 0, Rotation = 1 } });
    Check("game-api: DoClickAction으로 회전(rotation=3)", result.Ok && inv.TabletOf("A").rotation == 3, string.Join(" | ", result.Errors));
    Check("game-api: DoClickAction은 이동 후 위치 (1,0)으로 3번 호출", inv.Clicks == "(1,0)(1,0)(1,0)", inv.Clicks);
}
{
    var inv = new FakeGameInventoryWithClick();
    var called = InventoryWriter.TryAutoArrange(inv, 7, true, out var gameResult, out var aerr);
    Check("TryAutoArrange: 게임 자동 배치 메서드 호출", called && aerr == null && (bool)gameResult && inv.Clicks == "auto(7,True)", inv.Clicks + aerr);
    Check("TryAutoArrange: 메서드 없으면 에러", !InventoryWriter.TryAutoArrange(new FakeGameInventory(), 1, true, out _, out var nerr) && nerr != null);
}
{
    var cmds = new List<InventoryWriter.PlacementCommand>
    {
        new() { FromX = 0, FromY = 0, ToX = 1, ToY = 1 },
        new() { FromX = 2, FromY = 2, ToX = 1, ToY = 1 },
    };
    var result = InventoryWriter.Apply(new FakeGameInventory(), cmds);
    Check("같은 목표 칸 중복 명령은 거부", !result.Ok && result.Moved == 0);
}

// ---------- [추가] GradeCalculator: 페이지와 같은 등급 계산(절대평가) ----------
{
    JObj Art(string tier, int l, string[] s, string[] kw) => new JObj { ["t"] = tier, ["l"] = l, ["s"] = s.Cast<object>().ToList(), ["kw"] = kw.Cast<object>().ToList() };
    var inv = new[] { Art("Common", 1, new[] { "PRECISION" }, new string[0]), Art("Common", 1, new[] { "PRECISION", "STURDY" }, new string[0]) };
    var counts = GradeCalculator.TagCounts(inv);
    Check("GradeCalculator: 가방 태그 수", counts["PRECISION"] == 2 && counts["STURDY"] == 1);
    var crossbow = new JObj { ["type"] = "Crossbow", ["kw"] = new List<object> { "guard" }, ["redef"] = new List<object> { "basic_attack" } };
    var synergy = GradeCalculator.Score(Art("Rare", 3, new[] { "PRECISION" }, new[] { "guard" }), counts, crossbow);
    // 빌드 점수 = 태그 2×3 + 무기 지원 1.5 = 7.5 (희귀도·최대 레벨은 등급에 안 씀, TieBreak = 희귀 3 + 레벨 3×0.1)
    Check("GradeCalculator: 빌드 점수 = 태그×3 + 무기 지원 1.5, 희귀도·레벨은 TieBreak 로만", Math.Abs(synergy.Total - 7.5) < 1e-9 && Math.Abs(synergy.TieBreak - 3.3) < 1e-9 && !synergy.Caution, synergy.Total.ToString());
    var conflict = GradeCalculator.Score(Art("Legend", 3, new[] { "PRECISION" }, new[] { "guard", "basic_attack" }), counts, crossbow);
    Check("GradeCalculator: 무기가 바꾸는 동작에 의존 → 충돌", conflict.Caution && conflict.Unusable.Count == 0);
    var dashAttack = GradeCalculator.Score(Art("Common", 3, new[] { "PRECISION" }, new[] { "dash", "dash_attack" }), counts, crossbow);
    Check("GradeCalculator: 원거리 무기에서 대시 공격은 못 씀 → 감점·주의", dashAttack.Caution && dashAttack.Unusable.SequenceEqual(new[] { "dash_attack" })
        && Math.Abs(dashAttack.Total - (6 - 3)) < 1e-9, dashAttack.Total.ToString());
    var sword = new JObj { ["type"] = "GreatSword", ["kw"] = new List<object>(), ["redef"] = new List<object>() };
    Check("GradeCalculator: 근접 무기는 대시 공격 사용 가능", !GradeCalculator.Score(Art("Common", 3, new string[0], new[] { "dash_attack" }), counts, sword).Caution);

    // 빌드와 무관한 아이템은 희귀도가 달라도 같은 점수(마그마 구슬 vs 차가운 자물쇠 사례)
    var legendOff = GradeCalculator.Score(Art("Legend", 3, new[] { "EMBER" }, new[] { "status" }), counts, crossbow);
    var uncommonOff = GradeCalculator.Score(Art("Uncommon", 4, new string[0], new string[0]), counts, crossbow);
    Check("GradeCalculator: 빌드와 무관하면 희귀도와 상관없이 같은 빌드 점수", legendOff.Total == 0 && uncommonOff.Total == 0 && legendOff.TieBreak > uncommonOff.TieBreak);

    // 한 줄 요약: 주의(못 씀·충돌) → 겹치는 콤보 묶음(많은 태그 순) → 무기 지원
    var tagKo = new JObj { ["PRECISION"] = "정밀", ["STURDY"] = "견고" };
    var kwKo = new JObj { ["guard"] = "가드", ["dash_attack"] = "대시공격", ["basic_attack"] = "일반공격" };
    var many = GradeCalculator.Score(Art("Rare", 3, new[] { "STURDY", "PRECISION" }, new[] { "guard" }), counts, crossbow);
    Check("GradeCalculator: 요약 = '정밀/견고 콤보, 가드'", GradeCalculator.Summary(many, counts, tagKo, kwKo) == "정밀/견고 콤보, 가드", GradeCalculator.Summary(many, counts, tagKo, kwKo));
    Check("GradeCalculator: 요약은 못 씀·충돌이 먼저", GradeCalculator.Summary(dashAttack, counts, tagKo, kwKo) == "대시공격 못 씀, 정밀 콤보"
        && GradeCalculator.Summary(conflict, counts, tagKo, kwKo).StartsWith("일반공격 충돌"), GradeCalculator.Summary(dashAttack, counts, tagKo, kwKo));
    Check("GradeCalculator: 근거가 없으면 빈 요약", GradeCalculator.Summary(legendOff, counts, tagKo, kwKo) == "");

    // 주요 피해 방식: 고른 것과 맞으면 +2, 요약에 "화염 피해"
    var fireArt = new JObj { ["t"] = "Rare", ["l"] = 3, ["s"] = new List<object> { "PRECISION" }, ["kw"] = new List<object>(), ["dt"] = new List<object> { "fire", "ice" } };
    var noFocus = GradeCalculator.Score(fireArt, counts, crossbow);
    var focus = GradeCalculator.Score(fireArt, counts, crossbow, new HashSet<string> { "fire", "lightning" });
    Check("GradeCalculator: 고른 피해 방식과 맞으면 맞는 방식당 +2", Math.Abs(focus.Total - noFocus.Total - 2) < 1e-9 && focus.DamageHits.SequenceEqual(new[] { "fire" }), focus.Total + " vs " + noFocus.Total);
    var dmgKo = new JObj { ["fire"] = "화염" };
    Check("GradeCalculator: 요약에 '화염 피해'", GradeCalculator.Summary(focus, counts, tagKo, kwKo, 3, dmgKo) == "정밀 콤보, 화염 피해", GradeCalculator.Summary(focus, counts, tagKo, kwKo, 3, dmgKo));

    // 절대평가: 전체 점수 분포(0..99)에서의 위치
    var all = Enumerable.Range(0, 100).Select(i => (double)i).ToList();
    var top = new GradeCalculator.Result { Total = 95 };
    var mid = new GradeCalculator.Result { Total = 60 };
    var low = new GradeCalculator.Result { Total = 3 };
    var capped = new GradeCalculator.Result { Total = 99, Caution = true };
    GradeCalculator.AssignGrades(new[] { top, mid, low, capped }, all);
    Check("GradeCalculator: 절대평가 상위 8% S, 50% 이상 B, 하위 D", top.Grade == "S" && mid.Grade == "B" && low.Grade == "D", top.Grade + mid.Grade + low.Grade);
    Check("GradeCalculator: 주의 표시가 있으면 점수가 높아도 C 이하", capped.Grade == "C", capped.Grade);
    Check("GradeCalculator: 백분위(같은 값은 절반)", Math.Abs(GradeCalculator.Percentile(new[] { 1.0, 2, 2, 3 }, 2) - 0.5) < 1e-9);
}

// ---------- [추가] InventoryReader: storage(쓸 수 있는 칸 수)와 아티팩트 켜짐 상태 ----------
{
    var bag = new FakeBag();
    bag.Put(0, 0, "A"); bag.Put(1, 0, "B");
    bag.charms[new FakePos(0, 0)] = new FakeCharmOnBoard { IsEffectEnabled = true };
    bag.charms[new FakePos(1, 0)] = new FakeCharmOnBoard { IsEffectEnabled = false };
    var (oldNames, oldFinder) = (InventoryReader.InventoryTypeNames, InventoryReader.InstanceFinder);
    InventoryReader.InventoryTypeNames = new[] { nameof(FakeBag) };
    InventoryReader.InstanceFinder = _ => new object[] { bag };
    try
    {
        var snap = InventoryReader.BuildSnapshot(out _);
        var items = MiniJson.AsList(snap["items"]).Select(MiniJson.AsObj).ToList();
        Check("InventoryReader: storage", (int?)snap["storage"] == 28, MiniJson.Write(snap));
        Check("InventoryReader: 아티팩트 enabled(IsEffectEnabled)", items.Count == 2 && (bool)items[0]["enabled"] && !(bool)items[1]["enabled"], MiniJson.Write(items));
    }
    finally { (InventoryReader.InventoryTypeNames, InventoryReader.InstanceFinder) = (oldNames, oldFinder); }
}

// ---------- [추가] TabletReader: 석판 query / 회전 query / 적용 범위 ----------
{
    var t = new FakeStoneTablet { xIdx = 2, yIdx = 0, rotation = 1, query = "Q" };
    t.EffectRange.Add(new FakeEffectData { position = new FakePos(0, 0), effectType = FakeEffectType.IncreaseConstLevel, levelParam = 1 });
    var o = TabletReader.Describe(t);
    var rotated = MiniJson.AsList(o["rotatedQueries"]);
    var eff = MiniJson.AsList(o["effectRange"]).Select(MiniJson.AsObj).ToList();
    Check("TabletReader: query/좌표/회전", (string)o["query"] == "Q" && (int)o["x"] == 2 && (int)o["rotation"] == 1, MiniJson.Write(o));
    Check("TabletReader: GetRotatedQuery 0~3", rotated.Count == 4 && (string)rotated[3] == "Q@3", MiniJson.Write(rotated));
    Check("TabletReader: EffectRange 칸·종류·레벨", eff.Count == 1 && (int)eff[0]["x"] == 0 && (string)eff[0]["type"] == "IncreaseConstLevel" && (int)eff[0]["level"] == 1, MiniJson.Write(eff));

    var cells = TabletReader.ParseCells(t, "UP 1", 6, 5, 2, 2, 0, out var perr).Select(MiniJson.AsObj).ToList();
    Check("TabletReader: ParseQuery → 상대 좌표", perr == null && cells.Count == 1 && (int)cells[0]["dx"] == 0 && (int)cells[0]["dy"] == -1 && (string)cells[0]["value"] == "1", perr + MiniJson.Write(cells));

    var fx = TabletReader.BuildFixtures(new[] { new KeyValuePair<string, object>("T", new FakeStoneTablet { query = "UP 1" }) }, 2, 2, 0);
    var at01 = MiniJson.AsObj(MiniJson.AsObj(MiniJson.AsObj(fx["tablets"])["T"])["0,1"]);
    var cell = MiniJson.AsList(MiniJson.AsList(at01["effect"])[0]);
    Check("TabletReader: 정답 데이터는 절대 좌표 [x,y,value]", (int)cell[0] == 0 && (int)cell[1] == 0 && (string)cell[2] == "1" && at01["cond"] == null, MiniJson.Write(at01));

    var inv = new FakeGameInventory();
    inv.Put(3, 1, "B", isTablet: true);
    inv.Put(1, 0, "A", isTablet: true);
    var list = TabletReader.DescribeInventory(inv).Select(MiniJson.AsObj).ToList();
    Check("TabletReader: 인벤토리 석판을 위치 순서로", list.Count == 2 && (int)list[0]["x"] == 1 && (int)list[1]["x"] == 3, MiniJson.Write(list));
}

// ---------- [추가] ItemReader: 아티팩트(ItemEntity+Charm_Basic) / 무기(WeaponEntity+WeaponSimple+WeaponBasic) ----------
{
    var entity = new FakeItemEntity { id = 1188, Name = "혈석 귀걸이", type = "Charm", rarity = "Legend", categories = new List<string> { "CURSE" } };
    var a = ItemReader.DescribeCharm(entity, new FakeCharm { maxLevel = 3 });
    var eff = MiniJson.AsList(a["effects"]);
    Check("ItemReader: 아티팩트 기본 정보", (int)a["id"] == 1188 && (string)a["name"] == "혈석 귀걸이" && (int)a["maxLevel"] == 3 && (string)MiniJson.AsList(a["categories"])[0] == "CURSE", MiniJson.Write(a));
    Check("ItemReader: BuildEffectString 줄 단위", eff.Count == 2 && (string)eff[1] == "HP 3" && a["effectsRaw"] == null, MiniJson.Write(eff));

    Check("ItemReader: 발동 조건 없음 · 마법서 아님", a["criteria"] == null && (bool)a["magicBook"] == false, MiniJson.Write(a));
    var book = ItemReader.DescribeCharm(entity, new Charm_Magic { maxLevel = 1, criteria = new CharmActivateCriteria_TopInInventory() });
    Check("ItemReader: 발동 조건 이름(접두어 제거) · 마법서(Charm_Magic)", (string)book["criteria"] == "TopInInventory" && (bool)book["magicBook"], MiniJson.Write(book));

    var raw = ItemReader.DescribeCharm(entity, new FakeCharmRaw { maxLevel = 2 });
    var reff = MiniJson.AsList(raw["effects"]);
    Check("ItemReader: BuildEffectString 실패 시 GetEffectStringCount/GetEffectString", reff.Count == 2 && (string)reff[1] == "E1@2" && (bool)raw["effectsRaw"], MiniJson.Write(raw));

    var e3 = new FakeWeaponEntity { id = 3, Name = "더 큰 검" };
    var e2 = new FakeWeaponEntity { id = 2, Name = "큰 검", standardEnhancements = new() { new FakeEnhancement { enhanced = e3 } } };
    var e1 = new FakeWeaponEntity { id = 1, Name = "검", standardEnhancements = new() { new FakeEnhancement { enhanced = e2 } } };
    var simple = new FakeWeaponSimple { weaponType = "Sword", addons = new[] { new FakeWeaponAddon { text = "강화 A" }, new FakeWeaponAddon { text = " " } } };
    var w1 = ItemReader.DescribeWeapon(e1, null, null);
    var w2 = ItemReader.DescribeWeapon(e2, simple, new FakeWeaponBasic { hasUniqueEffect = true, uniqueEffectName = "U", uniqueEffectDescription = "D" });
    var w3 = ItemReader.DescribeWeapon(e3, null, new FakeWeaponBasic());
    ItemReader.AssignTiers(new List<JObj> { w3, w1, w2 });
    Check("ItemReader: standardEnhancements로 tier/parentId", (int)w1["tier"] == 1 && (int)w2["tier"] == 2 && (int)w3["tier"] == 3 && (int)w3["parentId"] == 2 && w1["parentId"] == null, MiniJson.Write(w3));
    var weff = MiniJson.AsList(w2["effects"]);
    Check("ItemReader: 무기 강화 효과(addons) + 고유 효과", weff.Count == 1 && (string)weff[0] == "강화 A" && (string)w2["uniqueEffectDescription"] == "D" && w3["uniqueEffectName"] == null, MiniJson.Write(w2));
}

// ---------- [추가] MiniHttpServer: 외부 Origin(CSRF) 차단 ----------
{
    var server = new MiniHttpServer(48766);
    server.Route("POST", "/apply", ctx => HttpResponseData.Json(new JObj { ["ok"] = true }));
    var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF };
    server.Route("GET", "/icon", ctx => ctx.QueryParam("name") == "x" ? HttpResponseData.Binary(png, "image/png") : HttpResponseData.Text("no", 404));
    server.Start();
    using var http = new System.Net.Http.HttpClient();
    try
    {
        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "http://127.0.0.1:48766/apply")
        { Content = new System.Net.Http.StringContent("{}") };
        req.Headers.Add("Origin", "https://evil.example");
        var resp = http.SendAsync(req).GetAwaiter().GetResult();
        Check("외부 Origin 요청은 403", (int)resp.StatusCode == 403, ((int)resp.StatusCode).ToString());

        var req2 = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "http://127.0.0.1:48766/apply")
        { Content = new System.Net.Http.StringContent("{}") };
        req2.Headers.Add("Origin", "null"); // file:// 로 연 동반 페이지
        var resp2 = http.SendAsync(req2).GetAwaiter().GetResult();
        Check("file:// (Origin: null) 요청은 허용", (int)resp2.StatusCode == 200, ((int)resp2.StatusCode).ToString());

        var iconResp = http.GetAsync("http://127.0.0.1:48766/icon?name=x").GetAwaiter().GetResult();
        var iconBytes = iconResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        Check("바이너리 응답(PNG) 바이트 그대로 + 캐시 허용", iconBytes.SequenceEqual(png) && iconResp.Content.Headers.ContentType?.MediaType == "image/png"
            && iconResp.Headers.CacheControl?.MaxAge == TimeSpan.FromHours(1), BitConverter.ToString(iconBytes));

        bool threw = false;
        var dupServer = new MiniHttpServer(48766);
        try { dupServer.Start(); } catch (System.Net.Sockets.SocketException) { threw = true; } finally { dupServer.Stop(); }
        Check("이미 쓰는 포트면 Start()가 예외(다음 후보로 넘어가도록)", threw);
    }
    finally { server.Stop(); }
}

Console.WriteLine();
Console.WriteLine($"=== {pass} passed, {fail} failed ===");
Environment.Exit(fail == 0 ? 0 : 1);

// ---- 테스트용 가짜 클래스들 (실제 게임 타입을 흉내만 낸 것, 실제 필드명과 다를 수 있음) ----
class FakeSlab
{
    public int xIdx;
    public int rotation;
    public int Rotation2 => rotation + xIdx; // 프로퍼티 읽기 테스트용 더미
}

class FakeGridInventory
{
    public string[][] inventoryMatrix;
    public int[,] levelMatrix;
    public int[,] maxLevelMatrix;
}

class FakeTablet
{
    public int xIdx;
    public int yIdx;
    public int rotation;
    public string internalName;
}

class FakeGridInventory2
{
    public List<FakeTablet> stoneTablets;
    public int RecalculateCallCount;
    // InventoryWriter가 리플렉션으로 찾는 "재계산" 후보 이름 중 하나를 실제로 구현해둔다.
    void RecalculateMatrix() { RecalculateCallCount++; }
}

// ---- 실제 게임(GridInventory/StoneTablet/ItemPosition) 구조를 흉내 낸 가짜 ----
struct FakePos
{
    public sbyte x, y;
    public FakePos(int x, int y) { this.x = (sbyte)x; this.y = (sbyte)y; }
}

class FakeStoneTablet
{
    public int rotation;
    public bool isRotatable = true;
    public sbyte xIdx, yIdx;
    public void Rotate() { if (!isRotatable) return; rotation = (rotation + 1) % 4; }
    public string query, conditionQuery;
    public List<FakeEffectData> EffectRange = new();
    public string GetRotatedQuery(string q, int r) => r == 0 ? q : $"{q}@{r}";
    // 실제: static List<AdditionMetadata> ParseQuery(string, int w, int h, int storage, ItemPosition origin, int rotation, out List<HumanReadableAddition>)
    public static List<FakeMeta> ParseQuery(string q, int w, int h, int storage, FakePos origin, int rotation, out List<string> human)
    {
        human = new List<string>();
        var parts = q.Split(' ');
        return parts[0] == "UP" ? new List<FakeMeta> { new() { position = new FakePos(origin.x, origin.y - 1), value = parts[1] } } : new List<FakeMeta>();
    }
}

struct FakeMeta
{
    public FakePos position;
    public string value;
}

enum FakeEffectType { None, IncreaseConstLevel }
struct FakeEffectData
{
    public FakePos position;
    public FakeEffectType effectType;
    public int levelParam;
}

class FakeItem
{
    public string Name;
    public FakeStoneTablet StoneTablet;
}

class FakeGameInventory
{
    public Dictionary<FakePos, FakeItem> inventoryMatrix = new();
    public Dictionary<FakePos, FakeStoneTablet> stoneTablets = new();

    public void Put(int x, int y, string name, bool isTablet = false, bool rotatable = true)
    {
        var item = new FakeItem { Name = name };
        if (isTablet)
        {
            item.StoneTablet = new FakeStoneTablet { isRotatable = rotatable, xIdx = (sbyte)x, yIdx = (sbyte)y };
            stoneTablets[new FakePos(x, y)] = item.StoneTablet;
        }
        inventoryMatrix[new FakePos(x, y)] = item;
    }

    // 실제 GridInventory.Swap(sbyte xLeft, sbyte yLeft, sbyte xRight, sbyte yRight) 와 같은 시그니처
    public void Swap(sbyte xl, sbyte yl, sbyte xr, sbyte yr)
    {
        var a = new FakePos(xl, yl); var b = new FakePos(xr, yr);
        inventoryMatrix.TryGetValue(a, out var ia); inventoryMatrix.TryGetValue(b, out var ib);
        inventoryMatrix.Remove(a); inventoryMatrix.Remove(b);
        if (ia != null) inventoryMatrix[b] = ia;
        if (ib != null) inventoryMatrix[a] = ib;
        stoneTablets.Clear();
        foreach (var kv in inventoryMatrix)
            if (kv.Value.StoneTablet != null) { kv.Value.StoneTablet.xIdx = kv.Key.x; kv.Value.StoneTablet.yIdx = kv.Key.y; stoneTablets[kv.Key] = kv.Value.StoneTablet; }
    }

    public string NameAt(int x, int y) => inventoryMatrix.TryGetValue(new FakePos(x, y), out var i) ? i.Name : null;
    public FakeStoneTablet TabletOf(string name) => inventoryMatrix.Values.First(i => i.Name == name).StoneTablet;
    public string Dump() => string.Join(" ", inventoryMatrix.Select(kv => $"{kv.Value.Name}@({kv.Key.x},{kv.Key.y})"));
}

class FakeItemEntity { public int id; public string Name, type, rarity; public List<string> categories; public string Context = "flavor"; }
class FakeCharm
{
    public int maxLevel;
    public string BuildEffectString(object avatar, string bullet, string endl, int level, int virtualLevelOffset, bool showAllLevel, bool ignoreAvatarStatus) => $"MP {level}{endl}HP {level}{endl}";
}
class CharmActivateCriteria_TopInInventory { }
class Charm_Magic : FakeCharm { public object criteria; }
class FakeCharmOnBoard { public bool IsEffectEnabled; }
// 스냅샷 테스트용: 실제 GridInventory 처럼 CurrentInventoryStorage 와 charms(위치 → Charm_Basic)를 가진 가방
class FakeBag : FakeGameInventory
{
    public byte Width = 6;
    public int Height => 5;
    public int CurrentInventoryStorage = 28;
    public Dictionary<FakePos, FakeCharmOnBoard> charms = new();
}
class FakeCharmRaw
{
    public int maxLevel;
    public string BuildEffectString(object avatar, string bullet, string endl, int level, int virtualLevelOffset, bool showAllLevel, bool ignoreAvatarStatus) => throw new NullReferenceException();
    public int GetEffectStringCount() => 2;
    public string GetEffectString(int idx, int level, int virtualLevelOffset, bool showAllLevel) => $"E{idx}@{level}";
}
class FakeEnhancement { public bool enabled = true; public FakeWeaponEntity enhanced; }
class FakeWeaponEntity { public int id; public string Name; public bool enabled = true, isDefaultWeapon; public List<FakeEnhancement> standardEnhancements = new(); }
class FakeWeaponAddon { public string text; public string GetEffectText() => text; }
class FakeWeaponSimple { public string weaponType; public FakeWeaponAddon[] addons; }
class FakeWeaponBasic { public bool hasUniqueEffect; public string uniqueEffectName, uniqueEffectDescription; }

// 실제 GridInventory.DoClickAction(ItemPosition pos): 그 칸 아이템의 클릭 동작(석판이면 Rotate)을 재계산과 함께 실행
class FakeGameInventoryWithClick : FakeGameInventory
{
    public string Clicks = "";
    public void DoClickAction(FakePos pos)
    {
        Clicks += $"({pos.x},{pos.y})";
        if (inventoryMatrix.TryGetValue(pos, out var item)) item.StoneTablet?.Rotate();
    }
    public bool AutoArrangeInventoryForBestCharmLevels(int maxIterations, bool allowTabletRotation) { Clicks += $"auto({maxIterations},{allowTabletRotation})"; return true; }
}
