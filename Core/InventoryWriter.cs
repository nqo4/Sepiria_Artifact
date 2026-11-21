using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace SephiriaLive.Core
{
    /// <summary>
    /// 배치 명령(from → to, 회전)을 인벤토리에 적용한다. 절대 예외를 밖으로 던지지 않고 Errors에 기록한다.
    ///
    /// 두 가지 모드:
    ///  1) game-api (실제 게임): 인벤토리에 Swap(x1,y1,x2,y2) 메서드가 있으면 그것으로 이동하고,
    ///     석판 회전은 GridInventory.DoClickAction(ItemPosition)(게임 클릭 경로, 레벨 재계산 포함)을,
    ///     없으면 StoneTablet.Rotate()(rotation을 +1, 3 다음 0)를 목표 값이 될 때까지 호출한다.
    ///     GridInventory.Swap은 서버면 LocalSwap, 클라면 CmdSwap을 부르므로 Mirror 동기화/권한 체크를 게임이 처리한다.
    ///  2) field (테스트/폴백): 석판 리스트 원소의 xIdx/yIdx/rotation 필드를 직접 쓰고 재계산 메서드를 호출한다.
    ///
    /// 좌표는 모두 "적용 전 스냅샷 기준"이다 — 명령들이 서로 연쇄돼도(A→B, B→C) 원래 위치로 대상을 식별한다.
    /// 회전: 엔진(시계 방향 +) → 게임 rotation 변환은 (4 - r) % 4.
    /// </summary>
    public static class InventoryWriter
    {
        public class PlacementCommand
        {
            public int FromX, FromY, ToX, ToY;
            /// <summary>엔진 기준 회전(0~3). null이면 회전하지 않음.</summary>
            public int? Rotation;
            public override string ToString() => $"({FromX},{FromY})→({ToX},{ToY})" + (Rotation.HasValue ? $" rot{Rotation}" : "");
        }

        public class ApplyResult
        {
            public string Mode;
            public int Moved;
            public int Rotated;
            public List<string> Log = new List<string>();
            public List<string> Errors = new List<string>();
            public bool Ok => Errors.Count == 0;
        }

        public static readonly string[] TabletCollectionNames = { "stoneTablets", "StoneTablets", "tablets", "Tablets", "items", "slots" };
        public static readonly string[] XNames = { "xIdx", "XIdx", "x", "X" };
        public static readonly string[] YNames = { "yIdx", "YIdx", "y", "Y" };
        public static readonly string[] RotationNames = { "rotation", "Rotation", "rot" };
        public static readonly string[] RecalculateNames = { "RecalculateMatrix", "Recalculate", "RefreshMatrix", "RebuildMatrix", "UpdateMatrix", "Refresh" };

        public static int EngineToGameRotation(int engineRot) => ((4 - (engineRot % 4)) % 4 + 4) % 4;

        public static List<PlacementCommand> ParseCommands(object parsed)
        {
            var list = new List<PlacementCommand>();
            var items = parsed is JObj obj ? MiniJson.AsList(obj["placements"]) : MiniJson.AsList(parsed);
            foreach (var it in items)
            {
                var o = MiniJson.AsObj(it);
                if (o == null) continue;
                list.Add(new PlacementCommand
                {
                    FromX = MiniJson.AsInt(o["fromX"]),
                    FromY = MiniJson.AsInt(o["fromY"]),
                    ToX = MiniJson.AsInt(o["toX"]),
                    ToY = MiniJson.AsInt(o["toY"]),
                    Rotation = MiniJson.AsNullableInt(o["rotation"]),
                });
            }
            return list;
        }

        public static ApplyResult Apply(object inventory, List<PlacementCommand> commands)
        {
            var result = new ApplyResult();
            try
            {
                if (inventory == null) { result.Errors.Add("인벤토리 인스턴스가 null입니다."); return result; }
                if (commands == null || commands.Count == 0) { result.Log.Add("적용할 명령이 없습니다."); return result; }

                var dup = commands.GroupBy(c => (c.ToX, c.ToY)).FirstOrDefault(g => g.Count() > 1);
                if (dup != null) { result.Errors.Add($"같은 목표 칸 ({dup.Key.ToX},{dup.Key.ToY})으로 가는 명령이 여러 개입니다."); return result; }

                var swap = FindSwapMethod(inventory.GetType());
                if (swap != null) ApplyGameApi(inventory, swap, commands, result);
                else ApplyFieldMode(inventory, commands, result);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Apply 중 예상치 못한 예외: " + ex.GetType().Name + ": " + ex.Message);
            }
            return result;
        }

        static MethodInfo FindSwapMethod(Type t)
        {
            var m = ReflectionFinder.FindMethod(t, "Swap", 4);
            if (m == null) return null;
            return m.GetParameters().All(p => ReflectionFinder.IsNumber(Activator.CreateInstance(p.ParameterType))) ? m : null;
        }

        // ---------------- game-api 모드 ----------------

        static void ApplyGameApi(object inventory, MethodInfo swap, List<PlacementCommand> commands, ApplyResult result)
        {
            result.Mode = "game-api";
            // 적용 전 석판 위치 기록(회전 대상 식별용). stoneTablets는 SyncDictionary<ItemPosition, StoneTablet>.
            var tabletsByOrigin = new Dictionary<(int, int), object>();
            var coll = ReflectionFinder.GetFirstMember(inventory, TabletCollectionNames, out _);
            foreach (var kv in MatrixExtractor.ToPositionMap(coll)) if (kv.Value != null) tabletsByOrigin[kv.Key] = kv.Value;
            if (tabletsByOrigin.Count == 0)
                foreach (var t in ReflectionFinder.EnumerateValues(coll))
                    if (TryGetPos(t, out var x, out var y)) tabletsByOrigin[(x, y)] = t;

            // 원래 위치(origin) ↔ 현재 위치 추적. 빈 칸도 "자기 자신이 원점인 빈 아이템"으로 취급하면 swap 추적이 단순해진다.
            var originAt = new Dictionary<(int, int), (int, int)>();    // 현재 위치 → 그 칸에 있는 것의 원점
            var currentOf = new Dictionary<(int, int), (int, int)>();   // 원점 → 현재 위치
            (int, int) OriginAt((int, int) pos) => originAt.TryGetValue(pos, out var o) ? o : pos;
            (int, int) CurrentOf((int, int) origin) => currentOf.TryGetValue(origin, out var c) ? c : origin;

            foreach (var cmd in commands)
            {
                var origin = (cmd.FromX, cmd.FromY);
                var cur = CurrentOf(origin);
                var to = (cmd.ToX, cmd.ToY);
                if (cur != to)
                {
                    if (!ReflectionFinder.TryInvoke(inventory, swap, new object[] { cur.Item1, cur.Item2, to.Item1, to.Item2 }, out _, out var err))
                    {
                        result.Errors.Add($"{cmd}: Swap 실패 — {err}");
                        continue;
                    }
                    var displaced = OriginAt(to);
                    originAt[to] = origin; currentOf[origin] = to;
                    originAt[cur] = displaced; currentOf[displaced] = cur;
                    result.Moved++;
                    result.Log.Add($"{cmd}: Swap({cur.Item1},{cur.Item2},{to.Item1},{to.Item2})");
                }

                if (cmd.Rotation.HasValue)
                {
                    if (!tabletsByOrigin.TryGetValue(origin, out var tablet))
                    {
                        result.Errors.Add($"{cmd}: 회전 요청이 있지만 원래 위치에 석판이 없습니다.");
                        continue;
                    }
                    RotateWithGameApi(inventory, tablet, cmd, result);
                }
            }
        }

        static void RotateWithGameApi(object inventory, object tablet, PlacementCommand cmd, ApplyResult result)
        {
            int target = EngineToGameRotation(cmd.Rotation.Value);
            // 실게임: StoneTablet.Rotate()만 부르면 레벨 재계산이 안 된다. 게임 UI 클릭 경로인
            // GridInventory.DoClickAction(ItemPosition) → LocalDoClickAction이 Permission(Get/ReleasePermission)으로 감싸 재계산한다.
            var click = FindClickAction(inventory.GetType());
            object clickPos = null;
            if (click != null && TryGetPos(tablet, out var tx, out var ty))
            {
                clickPos = Activator.CreateInstance(click.GetParameters()[0].ParameterType);
                if (!(ReflectionFinder.SetMember(clickPos, "x", tx) && ReflectionFinder.SetMember(clickPos, "y", ty))) clickPos = null;
            }
            var rotate = ReflectionFinder.FindMethod(tablet.GetType(), "Rotate", 0);
            if (!TryGetInt(tablet, RotationNames, out var rot, out _))
            {
                result.Errors.Add($"{cmd}: 석판 rotation 필드를 찾을 수 없습니다.");
                return;
            }
            if (rot == target) return;
            if (clickPos == null && rotate == null)
            {
                result.Errors.Add($"{cmd}: 석판에 Rotate() 메서드가 없습니다.");
                return;
            }
            for (int i = 0; i < 4 && rot != target; i++)
            {
                bool ok = clickPos != null
                    ? ReflectionFinder.TryInvoke(inventory, click, new[] { clickPos }, out _, out var err)
                    : ReflectionFinder.TryInvoke(tablet, rotate, new object[0], out _, out err);
                if (!ok)
                {
                    result.Errors.Add($"{cmd}: Rotate 실패 — {err}");
                    return;
                }
                TryGetInt(tablet, RotationNames, out var after, out _);
                if (after == rot)
                {
                    result.Errors.Add($"{cmd}: Rotate()를 호출해도 rotation이 바뀌지 않습니다(회전 불가 석판?).");
                    return;
                }
                rot = after;
            }
            if (rot == target) { result.Rotated++; result.Log.Add($"{cmd}: 게임 rotation={target} ({(clickPos != null ? "DoClickAction" : "Rotate")})"); }
            else result.Errors.Add($"{cmd}: 목표 rotation {target}에 도달하지 못했습니다(현재 {rot}).");
        }

        // DoClickAction(ItemPosition) — 인자가 x/y 필드를 가진 구조체일 때만.
        static MethodInfo FindClickAction(Type t)
        {
            var m = ReflectionFinder.FindMethod(t, "DoClickAction", 1);
            var pt = m?.GetParameters()[0].ParameterType;
            if (pt == null || !pt.IsValueType) return null;
            var inst = Activator.CreateInstance(pt);
            return ReflectionFinder.HasMember(inst, "x") && ReflectionFinder.HasMember(inst, "y") ? m : null;
        }

        // ---------------- 게임 자동 배치 (엔진 비교용) ----------------

        /// <summary>
        /// 게임 자체 자동 배치 GridInventory.AutoArrangeInventoryForBestCharmLevels(maxIterations, allowTabletRotation) 호출.
        /// 서버(호스트)에서만 동작하고 가방을 실제로 바꾼다. 반환: (호출 성공, 게임 반환값, 에러).
        /// </summary>
        public static bool TryAutoArrange(object inventory, int maxIterations, bool allowTabletRotation, out object gameResult, out string error)
        {
            gameResult = null; error = null;
            var m = inventory == null ? null : ReflectionFinder.FindMethod(inventory.GetType(), "AutoArrangeInventoryForBestCharmLevels", 2);
            if (m == null) { error = "AutoArrangeInventoryForBestCharmLevels(2) 없음"; return false; }
            return ReflectionFinder.TryInvoke(inventory, m, new object[] { maxIterations, allowTabletRotation }, out gameResult, out error);
        }

        // ---------------- field 모드 ----------------

        static void ApplyFieldMode(object inventory, List<PlacementCommand> commands, ApplyResult result)
        {
            result.Mode = "field";
            var coll = ReflectionFinder.GetFirstMember(inventory, TabletCollectionNames, out var collName);
            if (coll == null)
            {
                result.Errors.Add($"석판 컬렉션을 찾을 수 없습니다(후보: {string.Join(", ", TabletCollectionNames)}).");
                return;
            }
            var recalc = RecalculateNames.Select(n => ReflectionFinder.FindMethod(inventory.GetType(), n, 0)).FirstOrDefault(m => m != null);
            if (recalc == null) result.Log.Add("재계산 메서드를 찾지 못했습니다 — 좌표만 바꿉니다.");

            // 적용 전 위치 기준으로 대상 식별
            var byOrigin = new Dictionary<(int, int), object>();
            foreach (var t in ReflectionFinder.EnumerateValues(coll))
                if (t != null && TryGetPos(t, out var x, out var y)) byOrigin[(x, y)] = t;

            foreach (var cmd in commands)
            {
                if (!byOrigin.TryGetValue((cmd.FromX, cmd.FromY), out var item))
                {
                    result.Errors.Add($"{cmd}: ({cmd.FromX},{cmd.FromY})에서 '{collName}' 원소를 찾지 못했습니다.");
                    continue;
                }
                if (cmd.FromX != cmd.ToX || cmd.FromY != cmd.ToY)
                {
                    bool okX = XNames.Any(n => ReflectionFinder.HasMember(item, n) && ReflectionFinder.SetMember(item, n, cmd.ToX));
                    bool okY = YNames.Any(n => ReflectionFinder.HasMember(item, n) && ReflectionFinder.SetMember(item, n, cmd.ToY));
                    if (!okX || !okY) { result.Errors.Add($"{cmd}: 좌표 필드 쓰기 실패."); continue; }
                    CallRecalc(inventory, recalc, result);
                    result.Moved++;
                    result.Log.Add($"{cmd}: 좌표 필드 갱신");
                }
                if (cmd.Rotation.HasValue)
                {
                    int target = EngineToGameRotation(cmd.Rotation.Value);
                    if (!RotationNames.Any(n => ReflectionFinder.HasMember(item, n) && ReflectionFinder.SetMember(item, n, target)))
                    {
                        result.Errors.Add($"{cmd}: rotation 필드 쓰기 실패.");
                        continue;
                    }
                    CallRecalc(inventory, recalc, result);
                    result.Rotated++;
                    result.Log.Add($"{cmd}: rotation={target}");
                }
            }
        }

        static void CallRecalc(object inventory, MethodInfo recalc, ApplyResult result)
        {
            if (recalc == null) return;
            if (!ReflectionFinder.TryInvoke(inventory, recalc, new object[0], out _, out var err))
                result.Errors.Add("재계산 호출 실패 — " + err);
        }

        // ---------------- 공통 ----------------

        static bool TryGetPos(object item, out int x, out int y)
        {
            y = 0;
            return TryGetInt(item, XNames, out x, out _) & TryGetInt(item, YNames, out y, out _);
        }

        static bool TryGetInt(object obj, string[] names, out int value, out string matched)
        {
            value = 0;
            var v = ReflectionFinder.GetFirstMember(obj, names, out matched);
            if (!ReflectionFinder.IsNumber(v)) return false;
            value = Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return true;
        }
    }
}
