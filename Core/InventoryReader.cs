using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SephiriaLive.Core
{
    /// <summary>
    /// 게임의 인벤토리 객체를 리플렉션으로 찾아 JSON 스냅샷을 만든다(순수 조회, 쓰기 없음).
    ///
    /// 실제 게임 구조 (2026-11-19, Assembly-CSharp.dll 메타데이터로 확인):
    ///   GridInventory : Mirror.NetworkBehaviour
    ///     byte Width, byte Height(프로퍼티)
    ///     SyncDictionary&lt;ItemPosition, NewItemOwnInstance&gt; inventoryMatrix
    ///     SyncDictionary&lt;ItemPosition, int&gt; levelMatrix, maxLevelMatrix, disableMatrix, ...
    ///     SyncDictionary&lt;ItemPosition, StoneTablet&gt; stoneTablets
    ///   ItemPosition { sbyte x, y }
    ///   NewItemOwnInstance { int InstanceID, EntityID; sbyte Quantity, XIdx, YIdx; StoneTablet StoneTablet; ItemEntity Entity(프로퍼티); string Name(프로퍼티) }
    ///   StoneTablet { int rotation; bool isRotatable; sbyte xIdx, yIdx; int entityID, instanceID }
    /// 이름이 바뀌면 아래 후보 배열만 고치면 된다.
    ///
    /// Unity 객체 탐색(FindObjectsByType)은 Unity 의존이라 Core에 넣지 않고, 플러그인이 InstanceFinder로 주입한다.
    /// 반드시 Unity 메인 스레드에서 호출할 것.
    /// </summary>
    public static class InventoryReader
    {
        public static string[] InventoryTypeNames = { "GridInventory" };
        public static string[] ItemMatrixNames = { "inventoryMatrix" };
        public static string[] LevelMatrixNames = { "levelMatrix" };
        public static string[] MaxLevelMatrixNames = { "maxLevelMatrix" };
        public static string[] ExtraMatrixNames = { "disableMatrix", "multiplyLevelMatrix", "dungeonTempLevels" };
        public static string[] WidthNames = { "Width", "NetworkWidth", "width" };
        public static string[] HeightNames = { "Height", "height" };

        /// <summary>타입 → 씬에 존재하는 인스턴스들. 플러그인이 UnityEngine.Object.FindObjectsByType으로 채운다.</summary>
        public static Func<Type, object[]> InstanceFinder;

        /// <summary>마지막 BuildSnapshot에서 고른 인벤토리 인스턴스(/apply가 재사용).</summary>
        public static object LastFoundInstance { get; private set; }

        public static Action<string> Log = _ => { };

        public static JObj BuildSnapshot(out object inventoryInstance) => BuildSnapshot(false, out inventoryInstance);

        public static JObj BuildSnapshot(bool includeMemberDump, out object inventoryInstance)
        {
            inventoryInstance = null;
            var errors = new List<object>();
            var snap = new JObj { ["ok"] = false, ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            try
            {
                Type invType = null;
                foreach (var n in InventoryTypeNames)
                {
                    invType = ReflectionFinder.FindTypeByName(n);
                    if (invType != null) break;
                }
                if (invType == null)
                {
                    snap["error"] = "inventory_type_not_found";
                    snap["message"] = $"인벤토리 타입을 찾지 못했습니다(후보: {string.Join(", ", InventoryTypeNames)}).";
                    return snap;
                }
                snap["inventoryType"] = invType.FullName;

                if (InstanceFinder == null)
                {
                    snap["error"] = "no_instance_finder";
                    snap["message"] = "InstanceFinder가 설정되지 않았습니다(플러그인 초기화 문제).";
                    return snap;
                }

                object[] instances;
                try { instances = InstanceFinder(invType) ?? new object[0]; }
                catch (Exception ex) { instances = new object[0]; errors.Add("InstanceFinder 예외: " + ex.Message); }
                snap["instanceCount"] = instances.Length;

                var chosen = ChooseInstance(instances, out var reason);
                if (chosen == null)
                {
                    snap["error"] = "no_inventory_instance";
                    snap["message"] = "씬에 인벤토리 인스턴스가 없습니다 — 게임(던전/마을)에 캐릭터가 들어간 뒤 다시 시도하세요.";
                    snap["errors"] = errors;
                    return snap;
                }
                inventoryInstance = chosen;
                LastFoundInstance = chosen;
                snap["chosenBy"] = reason;

                FillSnapshot(chosen, snap, errors);
                if (includeMemberDump)
                    snap["memberDump"] = ReflectionFinder.DumpToJson(ReflectionFinder.DumpMembers(chosen));
                snap["ok"] = true;
            }
            catch (Exception ex)
            {
                snap["error"] = "exception";
                snap["message"] = ex.GetType().Name + ": " + ex.Message;
                Log("[InventoryReader] " + ex);
            }
            snap["errors"] = errors;
            return snap;
        }

        /// <summary>
        /// 여러 인스턴스 중 로컬 플레이어 것을 고른다: isLocalPlayer/isOwned → UnitAvatar 연결 → 아이템이 가장 많은 것.
        /// </summary>
        public static object ChooseInstance(object[] instances, out string reason)
        {
            reason = null;
            var alive = instances.Where(i => i != null).ToList();
            if (alive.Count == 0) return null;
            if (alive.Count == 1) { reason = "only_instance"; return alive[0]; }

            foreach (var flag in new[] { "isLocalPlayer", "isOwned", "hasAuthority" })
            {
                var hit = alive.FirstOrDefault(i => ReflectionFinder.GetMember(i, flag) is bool b && b);
                if (hit != null) { reason = flag; return hit; }
            }
            var withAvatar = alive.Where(i => ReflectionFinder.GetMember(i, "UnitAvatar") != null).ToList();
            var pool = withAvatar.Count > 0 ? withAvatar : alive;
            reason = withAvatar.Count > 0 ? "has_UnitAvatar+most_items" : "most_items";
            return pool.OrderByDescending(i => MatrixExtractor.ToPositionMap(ReflectionFinder.GetFirstMember(i, ItemMatrixNames, out _)).Count).First();
        }

        static void FillSnapshot(object inv, JObj snap, List<object> errors)
        {
            snap["width"] = ReadInt(inv, WidthNames);
            snap["height"] = ReadInt(inv, HeightNames);

            var itemsSrc = ReflectionFinder.GetFirstMember(inv, ItemMatrixNames, out var itemsName);
            var levelSrc = ReflectionFinder.GetFirstMember(inv, LevelMatrixNames, out _);
            var maxLevelSrc = ReflectionFinder.GetFirstMember(inv, MaxLevelMatrixNames, out _);
            if (itemsSrc == null) errors.Add($"아이템 매트릭스를 찾지 못했습니다(후보: {string.Join(", ", ItemMatrixNames)}).");

            var items = MatrixExtractor.ToPositionMap(itemsSrc);
            var levels = MatrixExtractor.ToPositionMap(levelSrc);
            var maxLevels = MatrixExtractor.ToPositionMap(maxLevelSrc);
            var tablets = MatrixExtractor.ToPositionMap(ReflectionFinder.GetFirstMember(inv, InventoryWriter.TabletCollectionNames, out _));

            var itemList = new List<object>();
            foreach (var kv in items.OrderBy(k => k.Key.y).ThenBy(k => k.Key.x))
            {
                if (kv.Key.x < 0 || kv.Key.y < 0) continue;
                try { itemList.Add(DescribeItem(kv.Key.x, kv.Key.y, kv.Value, levels, maxLevels, tablets)); }
                catch (Exception ex) { errors.Add($"아이템 ({kv.Key.x},{kv.Key.y}) 읽기 실패: {ex.Message}"); }
            }
            snap["items"] = itemList;
            snap["itemCount"] = itemList.Count;
            snap["tabletCount"] = tablets.Count;

            var matrices = new JObj();
            foreach (var name in LevelMatrixNames.Concat(MaxLevelMatrixNames).Concat(ExtraMatrixNames))
            {
                if (ReflectionFinder.TryGetMember(inv, name, out var m) && m != null)
                    matrices[name] = MatrixExtractor.Extract(m).ToJson();
            }
            snap["matrices"] = matrices;
            snap["source"] = new JObj { ["itemMatrix"] = itemsName };
        }

        static JObj DescribeItem(int x, int y, object item, Dictionary<(int, int), object> levels,
            Dictionary<(int, int), object> maxLevels, Dictionary<(int, int), object> tablets)
        {
            var o = new JObj { ["x"] = x, ["y"] = y };
            if (item == null) return o;
            o["instanceId"] = ReadInt(item, new[] { "InstanceID", "instanceID", "IUID" });
            o["entityId"] = ReadInt(item, new[] { "EntityID", "entityID", "IEntityID" });
            o["quantity"] = ReadInt(item, new[] { "Quantity", "quantity" });
            o["name"] = ReadString(item, "Name");

            var entity = ReflectionFinder.GetMember(item, "Entity");
            if (entity != null)
            {
                o["assetName"] = ReadString(entity, "name");     // UnityEngine.Object.name (영문 에셋 이름일 가능성)
                o["itemType"] = ReadString(entity, "type");      // EItemType
                o["rarity"] = ReadString(entity, "rarity");      // EItemRarity
            }
            if (levels.TryGetValue((x, y), out var lv)) o["level"] = lv;
            if (maxLevels.TryGetValue((x, y), out var mlv)) o["maxLevel"] = mlv;

            tablets.TryGetValue((x, y), out var tablet);
            if (tablet == null) tablet = ReflectionFinder.GetMember(item, "StoneTablet");
            o["isTablet"] = tablet != null;
            if (tablet != null)
            {
                o["rotation"] = ReadInt(tablet, InventoryWriter.RotationNames);
                o["rotatable"] = ReflectionFinder.GetMember(tablet, "isRotatable");
            }
            return o;
        }

        static object ReadInt(object obj, string[] names)
        {
            var v = ReflectionFinder.GetFirstMember(obj, names, out _);
            return ReflectionFinder.IsNumber(v) ? (object)Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;
        }

        static string ReadString(object obj, string name)
        {
            try
            {
                var v = ReflectionFinder.GetMember(obj, name);
                return v?.ToString();
            }
            catch { return null; }
        }
    }
}
