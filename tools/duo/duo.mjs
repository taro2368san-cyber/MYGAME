// 相談役（Claude API）と Claude Code を交互に話させる。自動で往復し、完了・質問・$5 ごとに止まる
// 使い方: node tools/duo/duo.mjs "やりたいこと"   （ANTHROPIC_API_KEY が要る）
import Anthropic from "@anthropic-ai/sdk";
import { spawn, execSync } from "node:child_process";
import { readFileSync, readdirSync, mkdirSync, appendFileSync, existsSync, renameSync } from "node:fs";
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
- 仕様書に書いていないことは勝手に決めない。決める必要があるときは「ユーザーへの質問：」として書く（そのときだけ ユーザーに止めて聞く）
- Claude Code は 自分でルールのテストを回し、作業ごとに 自分でコミットできる。テストやコミットを ユーザーに頼ませない
- 目標が終わったら、最後の行に【完了】と書く
返事は日本語で、Claude Code にそのまま渡せる文にしてください。`;

// Claude Code に毎回足す決まり（テストとコミットを 自分でやるため）
const CODE_RULES = `あなたは duo（相談役との自動の往復）の中で動いています。ユーザーは 毎回は見ていません。
- テストは自分で回す：Unity が再生中でないことを確かめ、mcp__ai-game-developer__assets-refresh のあと mcp__ai-game-developer__script-execute で GameDataTools.RunRulesTest（NonPublic|Static）を リフレクションで呼ぶ。失敗すると「テスト失敗: …」の例外になる。合格は、script-execute がエラーにならず、console-get-logs を logTypeFilter Error・Exception で見て空で、Log の最後が「ルールのテスト完了」のとき（Log は maxEntries 5 くらいで見る。全部取ると大きすぎる）
- 作業が1つ終わって テストが全部通ったら、自分でコミットする。git add には 自分が変えたファイルだけを名前で書く（git add -A や . は使わない）
- コミットしないもの：ProjectSettings/ProjectSettings.asset、Assets/_Recovery、シーン（*.unity）、盤面の CSV、xlsx、消された _player の画像、.mcp.json
- git push はしない。ユーザーのファイルを消したり 名前を変えたりしない
- コミットのメッセージは日本語で、最後に「Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>」の行を入れる`;
const ALLOWED = ["mcp__ai-game-developer", "Bash(git add:*)", "Bash(git commit:*)", "PowerShell(git add:*)", "PowerShell(git commit:*)"];
const DENIED = ["Bash(git push:*)", "PowerShell(git push:*)"];
const BUDGET_STEP = 5; // ponytail: $5 使うごとに 止めて聞く。もっと細かく見たいなら ここを変える

if (!process.env.ANTHROPIC_API_KEY) {
  console.log("ANTHROPIC_API_KEY がありません。https://platform.claude.com で API キーを作って、PowerShell で\n  $env:ANTHROPIC_API_KEY = \"sk-ant-...\"\nとしてから もう一度動かしてください。");
  process.exit(1);
}
const client = new Anthropic();
const messages = [];
let sessionId = null;
let spent = 0; // 相談役に使ったお金（このあいだの合計）
const log = join(here, "logs", new Date().toISOString().replace(/[:T]/g, "-").slice(0, 16) + ".md");
mkdirSync(dirname(log), { recursive: true });
const write = (who, text) => { console.log(`\n===== ${who} =====\n${text}`); appendFileSync(log, `\n## ${who}\n\n${text}\n`); };

// tools/duo/inbox.md を置くと、次に相談役へ話すときに いっしょに渡す（動かしたまま 回答や指示を足せる）。相談役に届いたら logs へ移す
const inbox = join(here, "inbox.md");
const readInbox = () => existsSync(inbox) ? readFileSync(inbox, "utf8").replace(/^﻿/, "").trim() : "";

async function consult(userText) {
  const extra = readInbox();
  if (extra) { write("ユーザー（inbox）", extra); userText = `ユーザーからの回答・指示（これを最優先にして、次の指示を決め直す）:\n${extra}\n\n${userText}`; }
  messages.push({ role: "user", content: userText });
  let res;
  try {
    res = await client.beta.messages.stream({
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
  } catch (e) {
    if (e.status === 401) { console.log("\nAPI キーがちがいます（401）。https://platform.claude.com で作ったキーを\n  $env:ANTHROPIC_API_KEY = \"sk-ant-...\"\nに入れ直してから、もう一度動かしてください。inbox.md は そのまま残っています。"); process.exit(1); }
    throw e;
  }
  if (extra) renameSync(inbox, log.replace(/\.md$/, `-inbox-${Date.now()}.md`));
  messages.push({ role: "assistant", content: res.content });
  if (res.stop_reason === "refusal") return "（相談役が答えられませんでした）";
  // ponytail: Opus 5.5 の定価で計算（入力 $4・1時間キャッシュ書き $8・キャッシュ読み $0.2・出力 $20 / 100万トークン）。正しい額は Console で
  const u = res.usage;
  spent += (u.input_tokens * 4 + (u.cache_creation_input_tokens ?? 0) * 8 + (u.cache_read_input_tokens ?? 0) * 0.2 + u.output_tokens * 20) / 1e6;
  console.log(`  [相談役] 今回まで およそ $${spent.toFixed(2)}（キャッシュ読み ${u.cache_read_input_tokens ?? 0} トークン）`);
  return res.content.filter(b => b.type === "text").map(b => b.text).join("\n");
}

// 道具を使うたびに 1行出す（止まって見えないように）
function showTool(t) {
  const i = t.input ?? {};
  const what = i.file_path ?? i.command ?? i.pattern ?? i.methodName ?? i.description ?? "";
  console.log(`  [Claude Code] ${t.name.replace("mcp__ai-game-developer__", "Unity:")} ${String(what).split("\n")[0].slice(0, 90)}`);
}

// Claude Code に1回やってもらう（同じ会話を続ける）。指示は stdin で渡す（長い日本語でも崩れない）
function runCode(prompt) {
  const args = ["-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "acceptEdits",
    "--append-system-prompt", CODE_RULES, "--allowedTools", ...ALLOWED, "--disallowedTools", ...DENIED];
  if (sessionId) args.push("--resume", sessionId);
  return new Promise((resolve, reject) => {
    // API キーは渡さない：Claude Code は いつものログインで動かし、API のお金は相談役だけに使う
    const env = { ...process.env };
    delete env.ANTHROPIC_API_KEY;
    const p = spawn(claudePath(), args, { cwd: root, env });
    const started = Date.now();
    console.log("\n  [Claude Code] 作業中…");
    let buf = "", err = "", result = null;
    p.stdout.on("data", d => {
      buf += d;
      let n;
      while ((n = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, n); buf = buf.slice(n + 1);
        let j; try { j = JSON.parse(line); } catch { continue; }
        if (j.type === "assistant") for (const c of j.message?.content ?? []) if (c.type === "tool_use") showTool(c);
        if (j.type === "result") result = j;
      }
    });
    p.stderr.on("data", d => err += d);
    p.on("error", reject);
    p.on("close", () => {
      if (!result) return reject(new Error("Claude Code の返事が読めません:\n" + buf + err));
      sessionId = result.session_id;
      console.log(`  [Claude Code] 終わり（${Math.round((Date.now() - started) / 60000)}分）`);
      const denied = result.permission_denials?.length ? `\n（許可がなくて使えなかった道具：${result.permission_denials.map(d => d.tool_name).join(", ")}）` : "";
      resolve((result.result ?? "") + denied);
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

// 自動で往復する。止まるのは【完了】・ユーザーへの質問・$5 使うごと だけ（Ctrl+C でいつでもやめられる）
let nextStop = BUDGET_STEP;
for (;;) {
  write("相談役", reply);
  const done = reply.trimEnd().endsWith("【完了】");
  const asks = reply.includes("ユーザーへの質問");
  const broke = spent >= nextStop;
  if (done || asks || broke) {
    if (broke) nextStop = Math.floor(spent / BUDGET_STEP) * BUDGET_STEP + BUDGET_STEP;
    const why = done ? "終わりました" : asks ? "相談役から質問があります" : `相談役に およそ $${spent.toFixed(2)} 使いました`;
    const a = (await rl.question(`\n${why}。Enter=${done ? "やめる" : "そのまま続ける"} / 文字=相談役に伝える / q=やめる > `)).trim();
    if (a === "q" || (done && a === "")) break;
    if (a !== "") { write("ユーザー", a); reply = await consult(`ユーザーから: ${a}`); continue; }
  }
  const report = await runCode(reply);
  write("Claude Code", report);
  reply = await consult(`Claude Code の報告:\n${report}`);
}
rl.close();
console.log(`\nログ: ${log}`);
