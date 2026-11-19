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
