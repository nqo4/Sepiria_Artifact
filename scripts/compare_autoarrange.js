// 게임 자동 배치 vs 엔진 비교 (실게임 필요, 가방을 바꿨다가 원래대로 되돌린다).
//   node scripts/compare_autoarrange.js [--port 37788] [--iterations 50] [--shuffle 시드] [--out 결과.json]
//   --shuffle: 먼저 가방을 무작위로 섞어 나쁜 출발점에서 비교하고, 끝나면(실패해도) 처음 배치로 되돌린다.
// 순서: 현재 가방 읽기 → 엔진 계산 → 게임 자동 배치 실행·레벨 읽기 → 되돌리기 → 엔진 배치 적용·레벨 읽기 → 되돌리기.
// 두 결과 모두 게임이 계산한 실제 레벨로 비교한다. Node 18+ (fetch).
'use strict';
const fs = require('fs');
const E = require('../Plugin/companion/engine.js');
const db = require('../Plugin/companion/placement_db.json');
const ARTS = require('../Plugin/companion/data.json').artifacts;

const arg = (name, def) => { const i = process.argv.indexOf('--' + name); return i > 0 ? process.argv[i + 1] : def; };
const BASE = `http://127.0.0.1:${arg('port', 37788)}`;
const ITER = Number(arg('iterations', 50));

async function get(path) { return (await fetch(BASE + path)).json(); }
async function post(path, body) {
  return (await fetch(BASE + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).json();
}

const gridItems = s => (s.items || []).filter(i => i.x >= 0 && i.y >= 0 && i.x < s.width && i.y < s.height);
/** 게임이 계산한 레벨 합(= 게임 자동 배치 점수의 1순위 항목): 아티팩트마다 clamp(level, 0, maxLevel) */
// 켜진(enabled) 아티팩트만 — 게임 자동 배치 점수 1순위와 같다
const gameLevelSum = s => gridItems(s).filter(i => i.itemType === 'Charm' && i.enabled !== false).reduce((a, i) => a + Math.max(0, Math.min(i.level ?? 0, i.maxLevel ?? 0)), 0);
const layout = s => gridItems(s).map(i => `${i.name}@${i.x},${i.y}${i.isTablet ? ' r' + i.rotation : ''}`).sort().join(' | ');

/** 지금 가방(now)을 target 스냅샷 배치로 되돌리는 /apply 명령 (instanceId 로 대응) */
function placementsTo(now, target) {
  const byId = new Map(gridItems(now).map(i => [i.instanceId, i]));
  const out = [];
  for (const t of gridItems(target)) {
    const c = byId.get(t.instanceId);
    if (!c) throw new Error(`되돌릴 아이템을 찾지 못함: ${t.name}`);
    const p = { fromX: c.x, fromY: c.y, toX: t.x, toY: t.y };
    if (t.isTablet && c.rotation !== t.rotation) p.rotation = (4 - t.rotation) % 4; // 게임 회전 → 엔진 회전
    if (p.fromX !== p.toX || p.fromY !== p.toY || p.rotation != null) out.push(p);
  }
  return out;
}

/** 게임이 아직 가방을 바꾸는 중일 수 있으니(시간 초과 등) 두 번 연속 같게 읽힐 때까지 기다린다 */
async function stableInv() {
  let prev = null;
  for (let i = 0; i < 30; i++) {
    const s = await get('/inv');
    if (s.ok && prev && layout(s) === layout(prev)) return s;
    if (s.ok) prev = s;
    await new Promise(r => setTimeout(r, 1000));
  }
  throw new Error('가방이 30초 안에 안정되지 않음');
}

async function restore(target, label) {
  const now = await stableInv();
  const pl = placementsTo(now, target);
  if (pl.length) {
    const r = await post('/apply', { placements: pl });
    if (!r.ok) throw new Error(`${label} 되돌리기 실패: ${JSON.stringify(r.errors)}`);
  }
  const back = await get('/inv');
  if (layout(back) !== layout(target)) throw new Error(`${label} 되돌린 뒤 배치가 다름\n지금: ${layout(back)}\n원래: ${layout(target)}`);
  return back;
}

/** --shuffle <시드>: 아이템을 쓸 수 있는 칸에 무작위로 흩고 회전 가능한 석판은 무작위 방향으로 — 나쁜 출발점 만들기 */
function shufflePlacements(snap, seed) {
  let s = seed >>> 0;
  const rnd = () => ((s = (Math.imul(s ^ (s >>> 15), 2654435761) + 0x6D2B79F5) >>> 0) / 4294967296);
  const cells = [];
  for (let y = 0; y < snap.height; y++) for (let x = 0; x < snap.width; x++) if (y * snap.width + x < (snap.storage ?? snap.width * snap.height)) cells.push([x, y]);
  for (let i = cells.length - 1; i > 0; i--) { const j = Math.floor(rnd() * (i + 1)); [cells[i], cells[j]] = [cells[j], cells[i]]; }
  return gridItems(snap).map((it, i) => {
    const p = { fromX: it.x, fromY: it.y, toX: cells[i][0], toY: cells[i][1] };
    if (it.isTablet && it.rotatable) p.rotation = Math.floor(rnd() * 4);
    return p;
  });
}

(async () => {
  const original = await get('/inv');
  if (!original.ok) throw new Error('가방을 읽지 못함: ' + (original.message || original.error));
  const shuffleSeed = arg('shuffle');
  let before = original;
  try {
  if (shuffleSeed != null) {
    const r = await post('/apply', { placements: shufflePlacements(original, Number(shuffleSeed)) });
    if (!r.ok) throw new Error('섞기 실패: ' + JSON.stringify(r.errors));
    before = await get('/inv');
  }
  const report = { at: new Date().toISOString(), board: `${before.width}x${before.height}`, items: gridItems(before).length, before: layout(before) };
  if (shuffleSeed != null) { report.shuffleSeed = Number(shuffleSeed); report.originalLevelSum = gameLevelSum(original); }
  report.startLevelSum = gameLevelSum(before);

  // 1) 엔진 (계산만)
  const board = E.boardFromSnapshot(before, db.slabs, ARTS);
  const ev0 = E.evaluate(board);
  report.engineModelMatchesGame = ev0.levelSum === report.startLevelSum;
  // 아티팩트마다 엔진의 켜짐/꺼짐(발동 조건 규칙)이 게임 IsEffectEnabled 와 같은지
  report.enabledMismatch = board.items.map((it, i) => ({ it, eng: ev0.enabled[i], game: (before.items.find(s => s.x === it.x && s.y === it.y) || {}).enabled }))
    .filter(r => r.it.kind === 'artifact' && r.game != null && r.eng !== r.game)
    .map(r => `${r.it.name}@${r.it.x},${r.it.y} 조건=${r.it.criteria || '없음'} 엔진=${r.eng} 게임=${r.game}`);
  let best = null;
  const t0 = Date.now();
  for (const seed of [1, 2, 3]) {
    const r = E.optimize(board, { iterations: 20000, seed });
    if (!best || r.after > best.after || (r.after === best.after && r.placements.length < best.placements.length)) best = r;
  }
  report.engine = { predicted: best.after, moves: best.placements.length, ms: Date.now() - t0 };

  // 2) 게임 자동 배치
  const t1 = Date.now();
  const auto = await post('/autoarrange', { iterations: ITER, allowRotation: true });
  if (!auto.ok) throw new Error('게임 자동 배치 실패: ' + auto.error);
  report.game = { levelSum: gameLevelSum(auto.after), moves: placementsTo(before, auto.after).length, ms: Date.now() - t1, layout: layout(auto.after) };
  // 엔진이 게임의 결과 배치를 같은 점수로 보는지(엔진 모델 검증)
  report.game.engineScoreOfGameLayout = E.evaluate(E.boardFromSnapshot(auto.after, db.slabs, ARTS)).levelSum;
  await restore(before, '게임 자동 배치');

  // 3) 엔진 배치를 실제로 적용해 게임 레벨 읽기
  if (best.placements.length) {
    const r = await post('/apply', { placements: best.placements });
    if (!r.ok) throw new Error('엔진 배치 적용 실패: ' + JSON.stringify(r.errors));
  }
  const engineAfter = await get('/inv');
  report.engine.actualLevelSum = gameLevelSum(engineAfter);
  report.engine.layout = layout(engineAfter);
  await restore(before, '엔진 배치');
  report.restored = true;

  console.log(JSON.stringify(report, null, 1));
  const out = arg('out');
  if (out) fs.writeFileSync(out, JSON.stringify({ report, original, before, gameAfter: auto.after, engineAfter }, null, 1));
  } finally {
    // 중간에 실패해도 처음 가방 배치로 되돌린다
    if (shuffleSeed != null || before !== original) await restore(original, '처음 배치');
  }
})().catch(e => { console.error('오류:', e.message); process.exit(1); });
