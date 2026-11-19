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
  check(`optimize 결과 재평가 = after ${f}`, E.evaluate(o.board).score === o.after);
}

// 단위 검사
check('회전: 게임 rotation 1 은 UP→LEFT', JSON.stringify(E.parseQuery('UP 1', 6, 5, 2, 2, 1)) === JSON.stringify([{ x: 1, y: 2, value: '1' }]));
check('조건 PLACED: 맨 윗줄이 아니면 효과 없음',
  E.evaluate({ w: 3, h: 3, items: [{ x: 1, y: 1, kind: 'tablet', query: 'LEFT 1', cond: 'TOP PLACED' }, { x: 0, y: 1, kind: 'artifact', maxLevel: 3 }] }).score === 0);
check('조건 PLACED: 맨 윗줄이면 효과 있음',
  E.evaluate({ w: 3, h: 3, items: [{ x: 1, y: 0, kind: 'tablet', query: 'LEFT 1', cond: 'TOP PLACED' }, { x: 0, y: 0, kind: 'artifact', maxLevel: 3 }] }).score === 1);
check('MUL/2 는 합산 후 곱함, maxLevel 로 자름',
  E.evaluate({ w: 3, h: 1, items: [{ x: 0, y: 0, kind: 'tablet', query: 'RIGHT 2' }, { x: 2, y: 0, kind: 'tablet', query: 'LEFT MUL/2' }, { x: 1, y: 0, kind: 'artifact', maxLevel: 3 }] }).score === 3);
check('X 는 비활성(점수 0)',
  E.evaluate({ w: 3, h: 1, items: [{ x: 0, y: 0, kind: 'tablet', query: 'RIGHT 2' }, { x: 2, y: 0, kind: 'tablet', query: 'LEFT X' }, { x: 1, y: 0, kind: 'artifact', maxLevel: 3 }] }).score === 0);

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

console.log(`\n=== engine: ${pass} passed, ${fail} failed ===`);
process.exit(fail ? 1 : 0);
