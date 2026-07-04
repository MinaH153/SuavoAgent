#!/usr/bin/env node
// FSD Eval grader — scores observe/learn against ground truth using the agent's live
// heartbeat telemetry (config_json.stats.template_learning). See tools/FsdEval/README.md.
//
//   node grade.mjs --agent <id> --baseline                 # snapshot BEFORE the driver runs
//   node grade.mjs --agent <id> --run fsd-eval-run.json    # score AFTER the driver + a heartbeat
//
// Creds: SUPABASE_URL + SUPABASE_SERVICE_ROLE_KEY, else ~/Code/Suavo/.env.production.local.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const args = Object.fromEntries(
  process.argv.slice(2).flatMap((a, i, arr) =>
    a.startsWith("--") ? [[a.slice(2), arr[i + 1]?.startsWith("--") || arr[i + 1] === undefined ? true : arr[i + 1]]] : []));

function loadCreds() {
  let url = process.env.SUPABASE_URL || process.env.NEXT_PUBLIC_SUPABASE_URL;
  let key = process.env.SUPABASE_SERVICE_ROLE_KEY;
  if (url && key) return { url, key };
  const envFile = path.join(os.homedir(), "Code/Suavo/.env.production.local");
  if (fs.existsSync(envFile)) {
    const env = Object.fromEntries(fs.readFileSync(envFile, "utf8").split("\n")
      .filter((l) => l.includes("=")).map((l) => { const i = l.indexOf("="); return [l.slice(0, i).trim(), l.slice(i + 1).trim().replace(/^["']|["']$/g, "")]; }));
    url = url || env.NEXT_PUBLIC_SUPABASE_URL || env.SUPABASE_URL;
    key = key || env.SUPABASE_SERVICE_ROLE_KEY;
  }
  if (!url || !key) { console.error("Missing SUPABASE_URL / SUPABASE_SERVICE_ROLE_KEY"); process.exit(64); }
  return { url, key };
}

const { url, key } = loadCreds();
const H = { apikey: key, authorization: `Bearer ${key}` };

async function telemetry(agent) {
  const r = await fetch(`${url}/rest/v1/agent_instances?select=agent_version,status,health_status,last_heartbeat_at,config_json&id=eq.${agent}`, { headers: H });
  const [a] = await r.json();
  if (!a) throw new Error(`agent ${agent} not found`);
  let cfg = a.config_json; if (typeof cfg === "string") { try { cfg = JSON.parse(cfg); } catch {} }
  const tl = cfg?.stats?.template_learning || {};
  return {
    ver: a.agent_version, status: a.status, health: a.health_status, hb: a.last_heartbeat_at,
    phase: tl.phase,
    interactions: tl.interactionEventCount ?? 0,
    events: tl.behavioralEventCount ?? 0,
    routines: tl.learnedRoutineCount ?? 0,
    templates: tl.workflowTemplateCount ?? 0,
    assistOk: tl.supervisedSuccessCount ?? 0,
    assistCorr: tl.supervisedCorrectionCount ?? 0,
  };
}

const BASELINE_FILE = "fsd-eval-baseline.json";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

if (!args.agent) { console.error("--agent <id> required"); process.exit(64); }

if (args.baseline) {
  const t = await telemetry(args.agent);
  fs.writeFileSync(BASELINE_FILE, JSON.stringify({ ...t, capturedAt: new Date().toISOString() }, null, 2));
  console.log(`baseline snapshot → ${BASELINE_FILE}:`, JSON.stringify({ interactions: t.interactions, routines: t.routines, templates: t.templates, phase: t.phase }));
  process.exit(0);
}

if (!args.run) { console.error("--run <manifest.json> required to score"); process.exit(64); }
const run = JSON.parse(fs.readFileSync(args.run, "utf8"));
const baseline = fs.existsSync(BASELINE_FILE) ? JSON.parse(fs.readFileSync(BASELINE_FILE, "utf8")) : run.baseline;
if (!baseline) console.warn("[warn] no baseline (run --baseline before driving) — Observe Δ assumes baseline interactions=0");
const base = baseline || { interactions: 0, routines: 0, templates: 0 };

// Wait for a heartbeat that reflects the driver run (+ up to 10 min for the learning pass).
const driverDone = run.driverFinishedAt ? new Date(run.driverFinishedAt).getTime() : 0;
const deadline = Date.now() + 10 * 60_000;
let t = await telemetry(args.agent);
process.stdout.write("waiting for post-run heartbeat + learning pass");
while (Date.now() < deadline) {
  const fresh = new Date(t.hb).getTime() > driverDone;
  if (fresh && (t.templates > base.templates || t.routines > base.routines || (Date.now() - driverDone) > 6 * 60_000)) break;
  process.stdout.write(".");
  await sleep(20_000);
  t = await telemetry(args.agent);
}
process.stdout.write("\n");

// ── Score ────────────────────────────────────────────────────────────────────────────────
const expectedDelta = run.expectedInteractionDelta ?? (run.repsOk ?? run.reps) * (run.stepsPerRep ?? 3);
const obsDelta = Math.max(0, t.interactions - base.interactions);
const observe = expectedDelta > 0 ? Math.min(100, Math.round((obsDelta / expectedDelta) * 100)) : 0;

const routineFormed = t.routines > base.routines;
const templateFormed = t.templates > base.templates;
const learn = (routineFormed ? 50 : 0) + (templateFormed ? 50 : 0);

const grade = (s) => (s >= 80 ? "PASS" : s >= 50 ? "PARTIAL" : "FAIL");
const scorecard = {
  task: run.task, agent: args.agent, ver: t.ver, status: t.status, phase: t.phase,
  scoredAt: new Date().toISOString(),
  observe: { score: observe, verdict: grade(observe), expectedDelta, observedDelta: obsDelta,
    note: "interactionEventCount delta vs reps*steps; names HMAC-hashed at source (PHI-safe)" },
  learn: { score: learn, verdict: grade(learn), routineFormed, templateFormed,
    routines: `${base.routines}->${t.routines}`, templates: `${base.templates}->${t.templates}`,
    note: `MinFrequency=5 reps; driver ran ${run.repsOk}/${run.reps} clean` },
  execute: { score: null, verdict: "V2", note: "needs actuation IPC + Approved auto-rule, then run_learned_template" },
};

const line = (l, s, v, extra) => console.log(`  ${l.padEnd(9)} ${String(s).padStart(3)}  ${v.padEnd(8)} ${extra}`);
console.log(`\n╔═ FSD Eval — ${run.task} ═══════════════════════════════════`);
console.log(`  agent ${args.agent.slice(0, 8)} · v${t.ver} · ${t.status} · phase=${t.phase}`);
console.log(`  ─────────────────────────────────────────────────────────`);
console.log(`  STAGE     /100 VERDICT   DETAIL`);
line("Observe", observe, grade(observe), `+${obsDelta}/${expectedDelta} interactions`);
line("Learn", learn, grade(learn), `routines ${base.routines}->${t.routines}, templates ${base.templates}->${t.templates}`);
line("Execute", "—", "V2", "actuation + approval needed");
console.log(`╚══════════════════════════════════════════════════════════`);

fs.writeFileSync("fsd-eval-scorecard.json", JSON.stringify(scorecard, null, 2));
console.log("scorecard → fsd-eval-scorecard.json");
