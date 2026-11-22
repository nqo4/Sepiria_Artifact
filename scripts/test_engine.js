// engine.js 검증: node scripts/test_engine.js
//  1) ParseQuery 해석 — data/engine_fixtures/parse_*.json (플러그인 /tablets/fixtures = 게임 ParseQuery 결과)과 칸 단위 비교
//  2) 레벨 계산 — 실게임 스냅샷(/inv)의 levelMatrix 와 비교
//  3) 최적화 — 점수가 줄지 않고, placements 가 올바른 순열인지
'use strict';
const fs = require('fs');
const path = require('path');
const E = require(process.env.ENGINE || '../Plugin/companion/engine.js');

const ROOT = path.join(__dirname, '..');
const FX = path.join(ROOT, 'data', 'engine_fixtures');
const db = JSON.parse(fs.readFileSync(path.join(ROOT, 'Plugin', 'companion', 'placement_db.json'), 'utf8'));
const game = JSON.parse(fs.readFileSync(path.join(ROOT, 'data', 'slabs_game.json'), 'utf8')).tablets;
const byAsset = new Map(game.map(t => [t.assetName, t]));

let pass = 0, fail = 0;
function check(name, ok, detail) {
  if (ok) pass++; else { fail++; console.log('FAIL - ' + name + (detail ? ' :: ' + detail : '')); }
}
const norm = cells => cells.map(c => `${c[0]},${c[1]},${String(c[2]).replace(/^\+/, '')}`).sort().join(' ');

// 1) ParseQuery
const parseFiles = fs.existsSync(FX) ? fs.readdirSync(FX).filter(f => /^parse_.*\.json$/.test(f)) : [];
if (!parseFiles.length) console.log('(parse_*.json 없음 — 게임에서 /tablets/fixtures 를 받아 저장하면 해석 검증을 함)');
for (const f of parseFiles) {
  const fx = JSON.parse(fs.readFileSync(path.join(FX, f), 'utf8'));
  const failsByTablet = {};
  let n = 0;
  for (const [asset, perPos] of Object.entries(fx.tablets)) {
    const t = byAsset.get(asset);
    if (!t || !t.name || t.name === '...' || /^Item_/.test(t.name)) continue; // 내부용 제외
    for (const [pos, expect] of Object.entries(perPos)) {
      const [x, y] = pos.split(',').map(Number);
      for (const kind of ['effect', 'cond']) {
        const q = kind === 'effect' ? t.query : t.conditionQuery;
        const want = norm(expect[kind] || []);
        const got = norm(E.parseQuery(q, fx.width, fx.height, x, y, fx.rotation).map(c => [c.x, c.y, c.value]));
        n++;
        if (got !== want) (failsByTablet[`${t.name}(${asset}) ${kind}`] ||= []).push(`@${pos} want[${want}] got[${got}]`);
      }
    }
  }
  const bad = Object.keys(failsByTablet);
  check(`ParseQuery ${f} (${n}건)`, bad.length === 0, bad.map(k => `\n  ${k}: ${failsByTablet[k].length}곳, 예 ${failsByTablet[k][0]}`).join(''));
}

// 2) 실게임 스냅샷 레벨
for (const f of fs.readdirSync(FX).filter(f => /^inv_.*\.json$/.test(f))) {
  const snap = JSON.parse(fs.readFileSync(path.join(FX, f), 'utf8'));
  const board = E.boardFromSnapshot(snap, db.slabs);
  const r = E.evaluate(board);
  const cells = snap.matrices.levelMatrix.cells, diffs = [];
  for (let y = 0; y < cells.length; y++) for (let x = 0; x < cells[y].length; x++) {
    if (cells[y][x] == null) continue;
    const it = board.items.find(i => i.x === x && i.y === y);
    if (it && it.kind === 'tablet') continue; // 석판 자기 칸 레벨(-1)은 별도 규칙
    if ((r.levels.get(x + ',' + y) || 0) !== cells[y][x]) diffs.push(`(${x},${y}) engine ${r.levels.get(x + ',' + y) || 0} game ${cells[y][x]}`);
  }
  check(`levelMatrix 일치 ${f}`, diffs.length === 0, diffs.join(', '));

  // 3) 최적화
  const o = E.optimize(board, { iterations: 5000, seed: 7 });
  check(`optimize 점수가 줄지 않음 ${f}`, o.after >= o.before, `${o.before} -> ${o.after}`);
  const dest = new Set(o.placements.map(p => p.toX + ',' + p.toY));
  check(`optimize 목표 칸 중복 없음 ${f}`, dest.size === o.placements.length);
  check(`optimize 결과 재평가 = after ${f}`, E.evaluate(o.board).levelSum === o.after);
}

// 단위 검사
check('회전: 게임 rotation 1 은 UP→LEFT', JSON.stringify(E.parseQuery('UP 1', 6, 5, 2, 2, 1)) === JSON.stringify([{ x: 1, y: 2, value: '1' }]));
check('조건 PLACED: 맨 윗줄이 아니면 효과 없음',
  E.evaluate({ w: 3, h: 3, items: [{ x: 1, y: 1, kind: 'tablet', query: 'LEFT 1', cond: 'TOP PLACED' }, { x: 0, y: 1, kind: 'artifact', maxLevel: 3 }] }).levelSum === 0);
check('조건 PLACED: 맨 윗줄이면 효과 있음',
  E.evaluate({ w: 3, h: 3, items: [{ x: 1, y: 0, kind: 'tablet', query: 'LEFT 1', cond: 'TOP PLACED' }, { x: 0, y: 0, kind: 'artifact', maxLevel: 3 }] }).levelSum === 1);
check('MUL/2 는 합산 후 곱함, maxLevel 로 자름',
  E.evaluate({ w: 3, h: 1, items: [{ x: 0, y: 0, kind: 'tablet', query: 'RIGHT 2' }, { x: 2, y: 0, kind: 'tablet', query: 'LEFT MUL/2' }, { x: 1, y: 0, kind: 'artifact', maxLevel: 3 }] }).levelSum === 3);
check('X 는 비활성(점수 0)',
  E.evaluate({ w: 3, h: 1, items: [{ x: 0, y: 0, kind: 'tablet', query: 'RIGHT 2' }, { x: 2, y: 0, kind: 'tablet', query: 'LEFT X' }, { x: 1, y: 0, kind: 'artifact', maxLevel: 3 }] }).levelSum === 0);

{
  // 레벨 0(maxLevel 0) 아이템은 점수와 무관하니 옮기지 않아야 함
  const b = { w: 4, h: 3, items: [
    { x: 0, y: 0, kind: 'artifact', maxLevel: 0, name: 'zero' },
    { x: 3, y: 2, kind: 'tablet', query: 'UP 1', rotatable: true, rotation: 0, name: 'up' },
    { x: 0, y: 2, kind: 'artifact', maxLevel: 2, name: 'a' },
  ] };
  const o = E.optimize(b, { iterations: 3000, seed: 3 });
  check('optimize: 점수 무관 이동은 되돌림', o.after === 1 && !o.placements.some(p => p.fromX === 0 && p.fromY === 0), JSON.stringify(o.placements));
}

// 4) 아티팩트 발동 조건 (게임 CharmActivateCriteria_XXX.GetCriteria 규칙) — 6×5 가방, storage 28(마지막 줄 4칸)
{
  const W = 6, S = 28;
  const met = (criteria, x, y, others = []) => {
    const it = { x, y, kind: 'artifact', criteria };
    const at = new Map([[x + ',' + y, it], ...others.map(o => [o.x + ',' + o.y, o])]);
    return E.criteriaMet(it, at, W, S);
  };
  const art = (x, y, extra = {}) => ({ x, y, kind: 'artifact', ...extra });
  check('조건 없음 → 켜짐', met(null, 2, 2));
  check('TopInInventory: 맨 윗줄만', met('TopInInventory', 3, 0) && !met('TopInInventory', 3, 1));
  check('BottomInInventory: 마지막 6칸(칸 번호 ≥ storage-6)', met('BottomInInventory', 4, 3) && met('BottomInInventory', 0, 4) && !met('BottomInInventory', 3, 3));
  check('SideEnd: x=0 또는 x=5', met('SideEnd', 0, 2) && met('SideEnd', 5, 2) && !met('SideEnd', 3, 2));
  check('Inside: 가장자리 아님', met('Inside', 2, 2) && !met('Inside', 0, 2) && !met('Inside', 2, 0) && !met('Inside', 5, 1) && !met('Inside', 3, 3));
  check('Outlined: 가장자리', met('Outlined', 0, 2) && met('Outlined', 3, 0) && met('Outlined', 1, 4) && !met('Outlined', 2, 2));
  check('BothSideCharm: 양옆이 아티팩트', met('BothSideCharm', 2, 1, [art(1, 1), art(3, 1)]) && !met('BothSideCharm', 2, 1, [art(1, 1), { x: 3, y: 1, kind: 'tablet' }]) && !met('BothSideCharm', 0, 1, [art(1, 1)]));
  check('BothSidesAreEmpty: 양옆 빔(덜 찬 마지막 줄 끝 고려)', met('BothSidesAreEmpty', 2, 1) && !met('BothSidesAreEmpty', 2, 1, [art(1, 1)]) && !met('BothSidesAreEmpty', 3, 4) && met('BothSidesAreEmpty', 2, 4));
  const full8 = [[1, 1], [2, 1], [3, 1], [1, 2], [3, 2], [1, 3], [2, 3], [3, 3]].map(([x, y]) => art(x, y));
  check('NeighborsAreFull: 8칸 모두 참', met('NeighborsAreFull', 2, 2, full8) && !met('NeighborsAreFull', 2, 2, full8.slice(1)) && !met('NeighborsAreFull', 0, 2, full8));
  check('Near8MagicBook: 주변 8칸에 마법서', met('Near8MagicBook', 2, 2, [art(3, 3, { magicBook: true })]) && !met('Near8MagicBook', 2, 2, [art(3, 3)]));
  check('FullHP 등 배치 무관 조건 → 켜짐', met('FullHP', 2, 2));

  const items = [{ x: 0, y: 1, kind: 'tablet', query: 'RIGHT 2' }, { x: 1, y: 1, kind: 'artifact', maxLevel: 3, criteria: 'TopInInventory' }];
  const off = E.evaluate({ w: W, h: 5, storage: S, items });
  check('조건 불만족 아티팩트는 레벨 합에서 빠짐(게임 IsEffectEnabled)', off.levelSum === 0 && off.enabled[1] === false);
  const on = E.evaluate({ w: W, h: 5, storage: S, items: items.concat({ x: 2, y: 1, kind: 'tablet', query: 'LEFT IGNORECRITERIA' }) });
  check('IGNORECRITERIA 받으면 조건 무시하고 켜짐', on.levelSum === 2 && on.enabled[1] === true);
  check('게임 점수 공식: 10000×켜진 레벨 + 1000×켜짐 + 10×원래 레벨 − 750×꺼짐', on.score === 2 * 10000 + 1000 + 20 && off.score === 20 - 750);

  const o = E.optimize({ w: W, h: 5, storage: S, items: [{ x: 0, y: 3, kind: 'tablet', query: 'DOWN 2', rotatable: true }, art(5, 0, { maxLevel: 3 })] }, { iterations: 4000, seed: 5 });
  check('optimize: 잠긴 칸(칸 번호 ≥ storage)에는 놓지 않음', o.placements.every(p => p.toY * W + p.toX < S) && o.after === 2, JSON.stringify(o.placements));
}

// 5) 사용자 설정: 최소 레벨 → 우선도 → 나머지 레벨 합 순서
{
  // 3×1 가방, 석판(왼쪽 +2, 오른쪽 +1), 아티팩트 A·B(최대 3) — 누가 +2 칸을 받는지만 달라진다
  const board = prefs => ({ w: 3, h: 1, storage: 3, items: [
    { x: 1, y: 0, kind: 'tablet', query: 'LEFT 2\nRIGHT 1', name: 't' },
    { x: 0, y: 0, kind: 'artifact', maxLevel: 3, name: 'A', ...prefs.A },
    { x: 2, y: 0, kind: 'artifact', maxLevel: 3, name: 'B', ...prefs.B },
  ] });
  const lvOf = (o, n) => { const ev = E.evaluate(o.board); return ev.perItem[o.board.items.findIndex(i => i.name === n)]; };
  const o1 = E.optimize(board({ A: {}, B: { priority: 1 } }), { iterations: 3000, seed: 1 });
  check('우선도 높은 아이템이 큰 레벨을 받음', lvOf(o1, 'B') === 2 && lvOf(o1, 'A') === 1 && o1.after === 3, JSON.stringify(o1.placements));
  const o2 = E.optimize(board({ A: { minLevel: 2 }, B: { priority: 3 } }), { iterations: 3000, seed: 1 });
  check('최소 레벨이 우선도보다 먼저', lvOf(o2, 'A') === 2 && lvOf(o2, 'B') === 1 && o2.shortfall === 0, JSON.stringify(o2.placements));
  const o3 = E.optimize(board({ A: { minLevel: 3 } }), { iterations: 3000, seed: 1 });
  check('못 맞추는 최소 레벨은 미달로 보고', o3.shortfall === 1 && o3.unmet.length === 1 && o3.unmet[0].item.name === 'A' && o3.unmet[0].need === 3);
  const o4 = E.optimize(board({ A: { minLevel: 2 }, B: { minLevel: 2, priority: 1 } }), { iterations: 3000, seed: 1 });
  check('최소 레벨을 다 못 채우면 중요 아티팩트 것부터', lvOf(o4, 'B') === 2 && o4.unmet.length === 1 && o4.unmet[0].item.name === 'A', JSON.stringify(o4.placements));
  // 콤보 목표: 캘세더니 열쇠는 줄(y%4)로, 하얀 종이는 양옆 공통 콤보로 — 엔진이 맞는 자리로 옮긴다
  const key2 = { w: 2, h: 4, storage: 8, items: [{ x: 0, y: 0, kind: 'artifact', maxLevel: 1, name: 'K', assetName: E.COMBO_BY_ROW, comboTarget: 'GLACIER' }] };
  const ok2 = E.optimize(key2, { iterations: 2000, seed: 1 });
  const k2 = ok2.board.items.find(i => i.name === 'K');
  check('콤보 목표: 캘세더니 열쇠를 빙하 줄(y=2)로 옮김', k2.y === 2 && ok2.unmet.length === 0, JSON.stringify(ok2.placements));
  const paper = { w: 3, h: 2, storage: 6, items: [
    { x: 0, y: 0, kind: 'artifact', maxLevel: 1, name: 'P', assetName: E.COMBO_BY_SIDES, comboTarget: 'EMBER' },
    { x: 1, y: 0, kind: 'artifact', maxLevel: 1, name: 'E1', tags: ['EMBER'] },
    { x: 0, y: 1, kind: 'artifact', maxLevel: 1, name: 'E2', tags: ['EMBER', 'STURDY'] },
    { x: 2, y: 1, kind: 'artifact', maxLevel: 1, name: 'S', tags: ['STURDY'] } ] };
  const op = E.optimize(paper, { iterations: 4000, seed: 1 });
  const pAt = new Map(op.board.items.map(i => [i.x + ',' + i.y, i]));
  const P = op.board.items.find(i => i.name === 'P');
  check('콤보 목표: 하얀 종이 양옆을 잉걸불 아티팩트로', E.comboOf(P, pAt) === 'EMBER' && op.unmet.length === 0, JSON.stringify(op.placements));
  // 위치 목표: 조건형(레이에 별조각 = 왼쪽 칸 마법서), 개수형(거대 망원경 = 주변 8칸 행성 수)
  const star = E.optimize({ w: 3, h: 1, storage: 3, items: [
    { x: 0, y: 0, kind: 'artifact', maxLevel: 1, name: 'R', assetName: '1069_DoubleMagic', goal: true },
    { x: 1, y: 0, kind: 'artifact', maxLevel: 1, name: 'X' },
    { x: 2, y: 0, kind: 'artifact', maxLevel: 1, name: 'MB', magicBook: true } ] }, { iterations: 3000, seed: 1 });
  const sAt = new Map(star.board.items.map(i => [i.x + ',' + i.y, i]));
  const R = star.board.items.find(i => i.name === 'R');
  check('위치 목표(조건형): 레이에 별조각 왼쪽에 마법서', E.goalValue(R, sAt) === 1 && star.unmet.length === 0, JSON.stringify(star.placements));
  const tele = E.optimize({ w: 4, h: 3, storage: 12, items: [
    { x: 0, y: 0, kind: 'artifact', maxLevel: 1, name: 'T', assetName: '1108_PlanetModule', goal: true },
    { x: 3, y: 2, kind: 'artifact', maxLevel: 1, name: 'P1', tags: ['PLANET'] },
    { x: 3, y: 0, kind: 'artifact', maxLevel: 1, name: 'P2', tags: ['PLANET'] },
    { x: 0, y: 2, kind: 'artifact', maxLevel: 1, name: 'P3', tags: ['PLANET'] } ] }, { iterations: 6000, seed: 1 });
  const tAt = new Map(tele.board.items.map(i => [i.x + ',' + i.y, i]));
  check('위치 목표(개수형): 거대 망원경 주변 8칸에 행성 3개', E.goalValue(tele.board.items.find(i => i.name === 'T'), tAt) === 3, JSON.stringify(tele.placements));
  const ev = E.evaluate({ w: 1, h: 1, items: [{ x: 0, y: 0, kind: 'artifact', maxLevel: 2, minLevel: 5 }] });
  check('최소 레벨은 최대 레벨을 넘지 않게 잘림', ev.unmet[0].need === 2);
}

console.log(`\n=== engine: ${pass} passed, ${fail} failed ===`);
process.exit(fail ? 1 : 0);
