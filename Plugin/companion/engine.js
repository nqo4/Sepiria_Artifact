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

    let score = 0;
    const perItem = items.map(it => {
      if (it.kind !== 'artifact') return null;
      const k = key(it.x, it.y);
      const lv = Math.max(0, Math.min(level.get(k) || 0, it.maxLevel ?? 0));
      if (!disabled.has(k)) score += lv;
      return disabled.has(k) ? 0 : lv;
    });
    return { score, levels: level, disabled, ignore, perItem };
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
    const movable = board.items.filter(it => it.x >= 0 && it.y >= 0 && it.x < w && it.y < h);
    const fixed = board.items.filter(it => !movable.includes(it)); // 포션 칸(y=100) 등
    const cells = [];
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) cells.push([x, y]);

    let cur = movable.map(it => ({ ...it, origin: [it.x, it.y], originRot: it.rotation || 0 }));
    const score = items => evaluate({ w, h, items: items.concat(fixed) }).score;
    let curScore = score(cur);
    const before = curScore;
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
    return { before, after: bestScore, board: { w, h, items: best.concat(fixed) }, placements };
  }

  // ---------- /inv 스냅샷 → board ----------
  /** snapshot: /inv 응답, slabDb: placement_db.json 의 slabs (assetName 으로 query/조건 조회) */
  function boardFromSnapshot(snapshot, slabDb) {
    const byAsset = new Map((slabDb || []).map(s => [s.assetName, s]));
    const items = (snapshot.items || []).map(it => {
      if (it.isTablet) {
        const s = byAsset.get(it.assetName) || {};
        return { x: it.x, y: it.y, kind: 'tablet', name: it.name, assetName: it.assetName, rotation: it.rotation || 0,
                 rotatable: !!it.rotatable, query: s.query || '', cond: s.conditionQuery || '' };
      }
      if (it.itemType === 'Charm') return { x: it.x, y: it.y, kind: 'artifact', name: it.name, assetName: it.assetName, maxLevel: it.maxLevel ?? 0 };
      return { x: it.x, y: it.y, kind: 'other', name: it.name, assetName: it.assetName };
    });
    return { w: snapshot.width, h: snapshot.height, items };
  }

  return { parseQuery, evaluate, optimize, boardFromSnapshot, rotate: rot };
});
