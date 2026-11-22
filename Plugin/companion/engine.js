/*
 * SephEngine — 세피리아 가방 배치 엔진 (순수 함수, Node/브라우저 겸용).
 *
 * 레벨 규칙은 게임 코드(Assembly-CSharp)를 그대로 따른다:
 *   StoneTablet.ApplyEffect
 *     1) conditionQuery 를 ParseQuery → 조건 칸. PLACED 칸이 있으면 그중 하나가 석판 자기 위치여야 하고,
 *        그 밖의 조건 칸(아이템 필요)은 모두 채워져 있어야 효과가 켜진다.
 *     2) query 를 ParseQuery → 효과 칸. 숫자 = levelMatrix 에 더함, X = 비활성, IGNORECRITERIA = 조건 무시,
 *        MUL/n = multiplyLevelMatrix 에 n 을 더함.
 *   GridInventory.ReleasePermission
 *     3) multiplyLevelMatrix 값이 0 이 아닌 칸은 level *= 그 값.
 *   점수 = 비활성이 아닌 아티팩트마다 clamp(level, 0, maxLevel) 의 합.
 *
 * 좌표: x 는 오른쪽 +, y 는 아래 +. 회전은 게임 rotation(r=1 이면 UP→LEFT, 화면 반시계).
 * /apply 로 보낼 때는 엔진 회전 = (4 - 게임 회전) % 4 (엔진 1 = 시계 방향).
 * ParseQuery 해석은 플러그인 /tablets/fixtures(게임 결과)로 검증한다: node scripts/test_engine.js
 */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.SephEngine = api;
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // ---------- ParseQuery ----------
  const DIR = { UP: [0, -1], DOWN: [0, 1], LEFT: [-1, 0], RIGHT: [1, 0] };
  const FIXED = {
    O: [0, 0],
    DIAUPLEFT: [-1, -1], DIAUPRIGHT: [1, -1], DIADOWNLEFT: [-1, 1], DIADOWNRIGHT: [1, 1],
    KNIGHTUPLEFT: [-1, -2], KNIGHTUPRIGHT: [1, -2], KNIGHTDOWNLEFT: [-1, 2], KNIGHTDOWNRIGHT: [1, 2],
    KNIGHTUPLEFT_INVERT: [-2, -1], KNIGHTUPRIGHT_INVERT: [2, -1], KNIGHTDOWNLEFT_INVERT: [-2, 1], KNIGHTDOWNRIGHT_INVERT: [2, 1],
  };
  // 석판에서 보드 끝까지 뻗는 반직선(자기 칸 제외)
  const RAYS = {
    HORIZONTAL: [[1, 0], [-1, 0]], VERTICAL: [[0, 1], [0, -1]],
    RIGHT_RISING: [[1, -1]], LEFT_FALLING: [[-1, 1]], LEFT_RISING: [[-1, -1]], RIGHT_FALLING: [[1, 1]],
  };
  // 보드 가장자리 줄/열 전체 (회전 0 기준)
  const EDGES = ['TOP', 'LEFTEND', 'BOTTOM', 'RIGHTEND']; // 게임 rotation +1 마다 다음 것으로(반시계)

  // 게임 rotation r 만큼 반시계 회전: (dx, dy) → (dy, -dx)
  function rot(dx, dy, r) {
    for (let i = 0; i < ((r % 4) + 4) % 4; i++) [dx, dy] = [dy, -dx];
    return [dx, dy];
  }

  function fixedOffset(kw) {
    if (FIXED[kw]) return FIXED[kw];
    const m = /^(UP|DOWN|LEFT|RIGHT)\1{0,3}$/.exec(kw);
    if (!m) return null;
    const n = kw.length / m[1].length;
    return [DIR[m[1]][0] * n, DIR[m[1]][1] * n];
  }

  /** query 한 덩어리 → [{x, y, value}] (절대 좌표). 고정 칸은 보드 밖도 그대로 둔다(게임과 동일). */
  function parseQuery(query, w, h, x, y, r) {
    const out = [];
    for (const line of String(query || '').split('\n')) {
      const parts = line.trim().split(/\s+/);
      if (!parts[0]) continue;
      const kw = parts[0];
      const value = parts.slice(1).join(' ');
      const off = fixedOffset(kw);
      if (off) {
        const [dx, dy] = rot(off[0], off[1], r);
        out.push({ x: x + dx, y: y + dy, value });
      } else if (RAYS[kw]) {
        for (const [ux, uy] of RAYS[kw]) {
          const [dx, dy] = rot(ux, uy, r);
          for (let cx = x + dx, cy = y + dy; cx >= 0 && cy >= 0 && cx < w && cy < h; cx += dx, cy += dy) out.push({ x: cx, y: cy, value });
        }
      } else if (EDGES.includes(kw)) {
        const edge = EDGES[(EDGES.indexOf(kw) + r) % 4];
        for (let cy = 0; cy < h; cy++) for (let cx = 0; cx < w; cx++) {
          if ((edge === 'TOP' && cy === 0) || (edge === 'BOTTOM' && cy === h - 1) ||
              (edge === 'LEFTEND' && cx === 0) || (edge === 'RIGHTEND' && cx === w - 1)) out.push({ x: cx, y: cy, value });
        }
      } else if (kw === 'CHECKERBOARD' || kw === 'CHECKERBOARD2') {
        // CHECKERBOARD: 석판과 같은 색 칸, CHECKERBOARD2: 다른 색 칸 (자기 칸 제외)
        const same = kw === 'CHECKERBOARD';
        for (let cy = 0; cy < h; cy++) for (let cx = 0; cx < w; cx++) {
          if (cx === x && cy === y) continue;
          if ((((cx + cy) - (x + y)) % 2 === 0) === same) out.push({ x: cx, y: cy, value });
        }
      }
      // shortcut: IDX/RIDX/X_PLUS 등은 일반 석판에서 안 쓰여 미구현 — 쓰이면 fixtures 로 규칙 확인 후 추가
    }
    return out;
  }

  // ---------- 레벨 계산 ----------
  const key = (x, y) => x + ',' + y;

  /**
   * board = { w, h, items: [{ x, y, kind: 'artifact'|'tablet'|'other', maxLevel?, query?, cond?, rotation? }] }
   * 반환: { score, levels: Map(key → level), disabled: Set, perItem: [level|null] }
   */
  function evaluate(board) {
    const { w, h, items } = board;
    const at = new Map();
    for (const it of items) at.set(key(it.x, it.y), it);
    const level = new Map(), mult = new Map(), disabled = new Set(), ignore = new Set();

    for (const t of items) {
      if (t.kind !== 'tablet') continue;
      const r = t.rotation || 0;
      const crit = t.cond ? parseQuery(t.cond, w, h, t.x, t.y, r) : [];
      if (crit.length) {
        let allHit = true, anyPlaced = false, hasPlaced = false;
        for (const c of crit) {
          if (c.value === 'PLACED') { hasPlaced = true; if (c.x === t.x && c.y === t.y) anyPlaced = true; }
          else if (!at.has(key(c.x, c.y))) allHit = false; // shortcut: ANY/CHARM 구분 없이 "아이템 있음"으로 봄
        }
        if (!allHit || (hasPlaced && !anyPlaced)) continue;
      }
      for (const c of parseQuery(t.query, w, h, t.x, t.y, r)) {
        const k = key(c.x, c.y);
        const n = Number(c.value);
        if (c.value !== '' && !Number.isNaN(n)) level.set(k, (level.get(k) || 0) + n);
        else if (c.value === 'X') disabled.add(k);
        else if (c.value === 'IGNORECRITERIA') ignore.add(k);
        else if (/^MUL\/\d+$/.test(c.value)) mult.set(k, (mult.get(k) || 0) + Number(c.value.slice(4)));
      }
    }
    for (const [k, m] of mult) if (m !== 0 && level.has(k)) level.set(k, level.get(k) * m);

    // 게임 점수(GridInventory.EvaluateCurrentAutoArrangeScore)를 그대로: 켜진 아티팩트 레벨 합이 1순위
    const storage = board.storage ?? w * h;
    let levelSum = 0, onCount = 0, offCount = 0, rawSum = 0, overflow = 0, negative = 0;
    const enabled = items.map(it => {
      if (it.kind !== 'artifact') return null;
      const k = key(it.x, it.y);
      return !disabled.has(k) && (ignore.has(k) || criteriaMet(it, at, w, storage));
    });
    const perItem = items.map((it, i) => {
      if (it.kind !== 'artifact') return null;
      const raw = level.get(key(it.x, it.y)) || 0, max = it.maxLevel ?? 0;
      const lv = Math.max(0, Math.min(raw, max));
      rawSum += raw;
      if (raw < 0) negative += -raw;
      if (raw > max) overflow += raw - max;
      if (enabled[i]) { onCount++; levelSum += lv; } else offCount++;
      return enabled[i] ? lv : 0;
    });
    const score = levelSum * 10000 + onCount * 1000 + rawSum * 10 + overflow - offCount * 750 - negative * 250;

    // 사용자 설정(아이템별 minLevel·priority>0 = 중요): 0순위 중요 아티팩트의 최소 레벨(전부는 못 채울 때 중요한 것부터),
    // 1순위 전체 최소 레벨 부족분, 2순위 우선도×레벨(남는 레벨은 중요 아티팩트로), 3순위 게임 점수.
    // 가중치를 크게 벌려서 앞 순위가 항상 이기게 한다(레벨 1 = 게임 점수 10000).
    let shortfall = 0, importantShortfall = 0, prioSum = 0;
    const unmet = [];
    items.forEach((it, i) => {
      if (it.kind !== 'artifact') return;
      const need = Math.min(it.minLevel || 0, it.maxLevel ?? 0);
      if (perItem[i] < need) {
        shortfall += need - perItem[i];
        if ((it.priority || 0) > 0) importantShortfall += need - perItem[i];
        unmet.push({ item: it, level: perItem[i], need });
      }
      prioSum += (it.priority || 0) * perItem[i];
    });
    // 콤보 목표(위치로 콤보가 정해지는 아티팩트): 못 맞추면 최소 레벨 미달보다 무겁게(중요 아티팩트 최소 레벨 다음)
    let comboMiss = 0;
    items.forEach(it => {
      if (it.kind !== 'artifact' || !it.comboTarget || comboOf(it, at) === it.comboTarget) return;
      comboMiss++;
      unmet.push({ item: it, combo: it.comboTarget, got: comboOf(it, at) });
    });
    const objective = score - importantShortfall * 1e10 - comboMiss * 1e9 - shortfall * 1e8 + prioSum * 1e6;
    return { score, objective, levelSum, shortfall, comboMiss, prioSum, unmet, levels: level, disabled, ignore, enabled, perItem };
  }

  // ---------- 위치로 콤보가 정해지는 아티팩트 ----------
  // 캘세더니 열쇠(Charm_3Elemental_ByRow): lineCategory[y % 4]. 순서는 툴팁 "견고, 잉걸불, 빙하, 마법공학" 순.
  // shortcut: lineCategory 값은 프리팹에 있어 게임 툴팁 순서로 가정 — 실게임 줄별 콤보와 다르면 /items 덤프로 확인해 고칠 것
  const ROW_COMBO = ['STURDY', 'EMBER', 'GLACIER', 'MAGITECH'];
  const COMBO_BY_ROW = '1067_Lightning_Damage', COMBO_BY_SIDES = '1258_WhitePaper';
  // 하얀 종이(Charm_WhitePaper): 양옆 아티팩트가 같은 콤보면 그 콤보(+1). 겹치는 콤보가 여럿이면 목표가 그중 하나인지만 본다.
  function comboOf(it, at) {
    if (it.assetName === COMBO_BY_ROW) return ROW_COMBO[((it.y % 4) + 4) % 4];
    if (it.assetName === COMBO_BY_SIDES) {
      const l = at.get(key(it.x - 1, it.y)), r = at.get(key(it.x + 1, it.y));
      if (!l || !r || l.kind !== 'artifact' || r.kind !== 'artifact') return null;
      const both = (l.tags || []).filter(t => (r.tags || []).includes(t));
      return it.comboTarget && both.includes(it.comboTarget) ? it.comboTarget : (both[0] || null);
    }
    return null;
  }

  // ---------- 아티팩트 발동 조건 (CharmActivateCriteria_XXX.GetCriteria) ----------
  // 숫자 6·7·5 는 게임 코드에 박혀 있는 값(가로 6칸 가방 기준) 그대로.
  const N8 = [[0, 1], [1, 1], [1, 0], [1, -1], [0, -1], [-1, -1], [-1, 0], [-1, 1]];
  function criteriaMet(it, at, w, storage) {
    const { x, y } = it, idx = y * w + x;
    const get = (cx, cy) => at.get(key(cx, cy));
    const inBag = (cx, cy) => cx >= 0 && cy >= 0 && cx < w && cy * w + cx < storage;
    switch (it.criteria) {
      case undefined: case null: case '': return true;
      case 'TopInInventory': return y === 0;
      case 'BottomInInventory': return idx >= storage - 6;
      case 'SideEnd': return x === 0 || x === 5;
      case 'Inside': return x > 0 && y > 0 && x < w - 1 && idx + 7 <= storage - 1;
      case 'Outlined': return x <= 0 || y <= 0 || x >= w - 1 || idx >= storage - 6;
      case 'BothSideCharm': {
        const l = get(x - 1, y), r = get(x + 1, y);
        return x > 0 && x < w - 1 && !!l && !!r && l.kind === 'artifact' && r.kind === 'artifact';
      }
      case 'BothSidesAreEmpty': {
        // 마지막 줄이 덜 찼으면 그 줄의 오른쪽 끝은 storage 로 정해진다
        const rowEnd = Math.min(w, storage - y * w);
        return x > 0 && x < rowEnd - 1 && !get(x - 1, y) && !get(x + 1, y);
      }
      case 'NeighborsAreFull': return N8.every(([dx, dy]) => inBag(x + dx, y + dy) && !!get(x + dx, y + dy));
      case 'Near8MagicBook': return N8.some(([dx, dy]) => { const n = get(x + dx, y + dy); return !!n && !!n.magicBook; });
      default: return true; // shortcut: FullHP 등 배치와 무관한 조건은 켜졌다고 본다 — 새 조건이 생기면 여기에 추가
    }
  }

  // ---------- 최적화 (시뮬레이티드 어닐링) ----------
  function mulberry32(seed) {
    return function () {
      seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
      let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  /**
   * 가방 칸 안에서 아이템 위치와 회전 가능한 석판의 회전을 바꿔 점수를 최대화한다.
   * opts: { iterations=20000, seed=1, startTemp=2, endTemp=0.05 }
   * 반환: { before, after, board(최적 배치), placements(/apply 형식, 바뀐 것만) }
   */
  function optimize(board, opts = {}) {
    const { iterations = 20000, seed = 1, startTemp = 2, endTemp = 0.05 } = opts;
    const rnd = mulberry32(seed);
    const { w, h } = board;
    const storage = board.storage ?? w * h;
    const movable = board.items.filter(it => it.x >= 0 && it.y >= 0 && it.x < w && it.y < h);
    const fixed = board.items.filter(it => !movable.includes(it)); // 포션 칸(y=100) 등
    const cells = [];
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (y * w + x < storage) cells.push([x, y]); // 잠긴 칸 제외

    let cur = movable.map(it => ({ ...it, origin: [it.x, it.y], originRot: it.rotation || 0 }));
    const evalOf = items => evaluate({ w, h, storage, items: items.concat(fixed) });
    const score = items => evalOf(items).objective / 10000; // 레벨 1 = 1 단위로 맞춰 온도 설정을 그대로 쓴다(최소 레벨·우선도 포함)
    let curScore = score(cur);
    const before = evalOf(cur).levelSum;
    let best = cur.map(it => ({ ...it })), bestScore = curScore;
    const rotatable = cur.map((it, i) => (it.kind === 'tablet' && it.rotatable ? i : -1)).filter(i => i >= 0);

    for (let i = 0; i < iterations && cur.length; i++) {
      const temp = startTemp * Math.pow(endTemp / startTemp, i / iterations);
      const next = cur.map(it => ({ ...it }));
      if (rotatable.length && rnd() < 0.25) {
        const t = next[rotatable[Math.floor(rnd() * rotatable.length)]];
        t.rotation = ((t.rotation || 0) + 1 + Math.floor(rnd() * 3)) % 4;
      } else {
        const a = next[Math.floor(rnd() * next.length)];
        const [cx, cy] = cells[Math.floor(rnd() * cells.length)];
        const b = next.find(it => it.x === cx && it.y === cy);
        if (b === a) continue;
        if (b) { b.x = a.x; b.y = a.y; }
        a.x = cx; a.y = cy;
      }
      const s = score(next);
      if (s >= curScore || rnd() < Math.exp((s - curScore) / temp)) {
        cur = next; curScore = s;
        if (s > bestScore) { best = next.map(it => ({ ...it })); bestScore = s; }
      }
    }

    // 정리: 점수에 도움이 안 되는 이동/회전은 되돌린다(어닐링이 레벨 0 아이템 등을 아무 데나 옮겨 두는 것 방지)
    for (let changed = true; changed;) {
      changed = false;
      for (const it of best) {
        if ((it.rotation || 0) !== it.originRot && it.kind === 'tablet') {
          const r = it.rotation; it.rotation = it.originRot;
          if (score(best) >= bestScore) changed = true; else it.rotation = r;
        }
        if (it.x === it.origin[0] && it.y === it.origin[1]) continue;
        const occ = best.find(o => o !== it && o.x === it.origin[0] && o.y === it.origin[1]);
        const [px, py] = [it.x, it.y];
        if (occ) { occ.x = px; occ.y = py; }
        it.x = it.origin[0]; it.y = it.origin[1];
        if (score(best) >= bestScore) changed = true;
        else { it.x = px; it.y = py; if (occ) { occ.x = it.origin[0]; occ.y = it.origin[1]; } }
      }
    }
    bestScore = score(best);

    const placements = [];
    for (const it of best) {
      const moved = it.x !== it.origin[0] || it.y !== it.origin[1];
      const rotated = it.kind === 'tablet' && (it.rotation || 0) !== it.originRot;
      if (!moved && !rotated) continue;
      const p = { fromX: it.origin[0], fromY: it.origin[1], toX: it.x, toY: it.y };
      if (rotated) p.rotation = (4 - (it.rotation || 0)) % 4; // 엔진 회전(시계 방향)
      placements.push(p);
    }
    // before/after = 켜진 아티팩트 레벨 합(게임 점수 1순위). 최적화 자체는 게임 점수 전체(동점 처리 포함)로 한다.
    const fin = evalOf(best);
    return { before, after: fin.levelSum, shortfall: fin.shortfall, unmet: fin.unmet, board: { w, h, storage, items: best.concat(fixed) }, placements };
  }

  // ---------- /inv 스냅샷 → board ----------
  /**
   * snapshot: /inv 응답, slabDb: placement_db.json 의 slabs (assetName 으로 query/조건 조회),
   * artifactDb: data.json 의 artifacts (v 로 발동 조건 cr / 마법서 mb 조회, 없으면 조건 없음으로 봄)
   */
  function boardFromSnapshot(snapshot, slabDb, artifactDb) {
    const byAsset = new Map((slabDb || []).map(s => [s.assetName, s]));
    const artByV = new Map((artifactDb || []).map(a => [a.v, a]));
    const items = (snapshot.items || []).map(it => {
      if (it.isTablet) {
        const s = byAsset.get(it.assetName) || {};
        return { x: it.x, y: it.y, kind: 'tablet', name: it.name, assetName: it.assetName, rotation: it.rotation || 0,
                 rotatable: !!it.rotatable, query: s.query || '', cond: s.conditionQuery || '' };
      }
      if (it.itemType === 'Charm') {
        const a = artByV.get(it.assetName) || {};
        return { x: it.x, y: it.y, kind: 'artifact', name: it.name, assetName: it.assetName, instanceId: it.instanceId, maxLevel: it.maxLevel ?? 0, criteria: a.cr || null, magicBook: !!a.mb, tags: a.s || [] };
      }
      return { x: it.x, y: it.y, kind: 'other', name: it.name, assetName: it.assetName };
    });
    return { w: snapshot.width, h: snapshot.height, storage: snapshot.storage ?? snapshot.width * snapshot.height, items };
  }

  return { parseQuery, evaluate, optimize, boardFromSnapshot, criteriaMet, rotate: rot, comboOf, ROW_COMBO, COMBO_BY_ROW, COMBO_BY_SIDES };
});
