// 相談役（Claude API）と Claude Code を交互に話させる。毎回 人が見て Enter で進める
// 使い方: node tools/duo/duo.mjs "やりたいこと"   （ANTHROPIC_API_KEY が要る）
import Anthropic from "@anthropic-ai/sdk";
import { spawn, execSync } from "node:child_process";
import { readFileSync, readdirSync, mkdirSync, appendFileSync } from "node:fs";
import { createInterface } from "node:readline/promises";
import { homedir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");
const spec = readFileSync(join(root, "設計メモ", "pokemon-sugoroku-spec.md"), "utf8").replace(/^﻿/, "");

// Claude Code の本体：CLAUDE_PATH か、VS Code 拡張の中のいちばん新しいもの
function claudePath() {
  if (process.env.CLAUDE_PATH) return process.env.CLAUDE_PATH;
  const ext = join(homedir(), ".vscode", "extensions");
  const dir = readdirSync(ext).filter(d => d.startsWith("anthropic.claude-code-")).sort().pop();
  if (!dir) throw new Error("Claude Code が見つかりません。CLAUDE_PATH に claude.exe の場所を入れてください");
  return join(ext, dir, "resources", "native-binary", "claude.exe");
}

const ROLE = `あなたは Unity で作っている「ポケモンすごろく」の相談役です。作業するのは Claude Code（このプロジェクトのフォルダで動くコーディング担当）です。
あなたの仕事：
- 下の仕様書をもとに、Claude Code に「1回に1つ」具体的な作業を指示する。どのファイル・どの仕様の節かも書く
- Claude Code の報告を読んで、仕様と合っているか確かめ、次の指示か直してほしいことを書く
- 仕様書に書いていないことは勝手に決めない。決める必要があるときは「ユーザーへの質問：」として書く
- 目標が終わったら、最後の行に【完了】と書く
返事は日本語で、Claude Code にそのまま渡せる文にしてください。`;

if (!process.env.ANTHROPIC_API_KEY) {
  console.log("ANTHROPIC_API_KEY がありません。https://platform.claude.com で API キーを作って、PowerShell で\n  $env:ANTHROPIC_API_KEY = \"sk-ant-...\"\nとしてから もう一度動かしてください。");
  process.exit(1);
}
const client = new Anthropic();
const messages = [];
let sessionId = null;
const log = join(here, "logs", new Date().toISOString().replace(/[:T]/g, "-").slice(0, 16) + ".md");
mkdirSync(dirname(log), { recursive: true });
const write = (who, text) => { console.log(`\n===== ${who} =====\n${text}`); appendFileSync(log, `\n## ${who}\n\n${text}\n`); };

async function consult(userText) {
  messages.push({ role: "user", content: userText });
  const res = await client.beta.messages.stream({
    model: "claude-opus-5-5",
    max_tokens: 32000,
    output_config: { effort: "high" },
    betas: ["server-side-fallback-2026-07-01"],
    fallbacks: "default", // 断られたときは サーバーが別のモデルで答え直す
    system: [
      { type: "text", text: ROLE },
      { type: "text", text: "# 仕様書\n\n" + spec, cache_control: { type: "ephemeral", ttl: "1h" } },
    ],
    messages,
  }).finalMessage();
  messages.push({ role: "assistant", content: res.content });
  if (res.stop_reason === "refusal") return "（相談役が答えられませんでした）";
  const u = res.usage;
  console.log(`  [相談役] 入力 ${u.input_tokens} / キャッシュ読み ${u.cache_read_input_tokens ?? 0} / 出力 ${u.output_tokens}`);
  return res.content.filter(b => b.type === "text").map(b => b.text).join("\n");
}

// Claude Code に1回やってもらう（同じ会話を続ける）。指示は stdin で渡す（長い日本語でも崩れない）
function runCode(prompt) {
  const args = ["-p", "--output-format", "json", "--permission-mode", "acceptEdits"];
  if (sessionId) args.push("--resume", sessionId);
  return new Promise((resolve, reject) => {
    const p = spawn(claudePath(), args, { cwd: root });
    let out = "", err = "";
    p.stdout.on("data", d => out += d);
    p.stderr.on("data", d => err += d);
    p.on("error", reject);
    p.on("close", () => {
      try {
        const j = JSON.parse(out);
        sessionId = j.session_id;
        const denied = j.permission_denials?.length ? `\n（許可がなくて使えなかった道具：${j.permission_denials.map(d => d.tool_name).join(", ")}）` : "";
        console.log(`  [Claude Code] $${(j.total_cost_usd ?? 0).toFixed(3)}`);
        resolve((j.result ?? "") + denied);
      } catch { reject(new Error("Claude Code の返事が読めません:\n" + out + err)); }
    });
    p.stdin.end(prompt);
  });
}

const rl = createInterface({ input: process.stdin, output: process.stdout });
const goal = process.argv.slice(2).join(" ") || await rl.question("やりたいこと: ");
let commits = "";
try { commits = execSync("git log --oneline -15", { cwd: root, encoding: "utf8" }); } catch {}
write("ユーザー", goal);
let reply = await consult(`目標: ${goal}\n\n最近のコミット:\n${commits}\nClaude Code への最初の指示を書いてください。`);

for (;;) {
  write("相談役", reply);
  const done = reply.trimEnd().endsWith("【完了】");
  const a = (await rl.question(done
    ? "\n終わりました。Enter=やめる / 文字=続けて相談役に伝える > "
    : "\nEnter=Claude Code に送る / 文字=相談役に伝えて書き直し / q=やめる > ")).trim();
  if (a === "q" || (done && a === "")) break;
  if (a !== "") { write("ユーザー", a); reply = await consult(`ユーザーから: ${a}`); continue; }
  const report = await runCode(reply);
  write("Claude Code", report);
  reply = await consult(`Claude Code の報告:\n${report}`);
}
rl.close();
console.log(`\nログ: ${log}`);
