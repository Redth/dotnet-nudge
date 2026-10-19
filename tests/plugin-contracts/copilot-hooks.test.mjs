import assert from "node:assert/strict";
import { test } from "node:test";
import { createServer } from "node:http";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const sdk = process.env.NUDGE_COPILOT_SDK_ROOT;
const runtime = process.env.NUDGE_COPILOT_RUNTIME;

test("Copilot host plugin handoff and reload execute command hooks alongside SDK callbacks", {
    skip: !sdk || !runtime ? "Set NUDGE_COPILOT_SDK_ROOT and NUDGE_COPILOT_RUNTIME for real-runtime acceptance." : false,
    timeout: 60000
}, async () => {
    const artifacts = resolve(".artifacts");
    await mkdir(artifacts, { recursive: true });
    const root = await mkdtemp(join(artifacts, "copilot-hooks-"));
    const plugin = join(root, "plugin");
    const workspace = join(root, "workspace");
    const log = join(root, "commands.jsonl");
    const events = ["sessionStart", "userPromptSubmitted", "postToolUse", "sessionEnd"];
    const observedContext = [];
    let requests = 0;
    const server = createServer(async (request, response) => {
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        const body = JSON.parse(Buffer.concat(chunks));
        observedContext.push(JSON.stringify(body.messages));
        const first = ++requests === 1;
        response.writeHead(200, { "Content-Type": "application/json" });
        response.end(JSON.stringify({
            id: `fixture-${requests}`, object: "chat.completion", model: "gpt-6-sol",
            choices: [{ index: 0, finish_reason: first ? "tool_calls" : "stop",
                message: first ? { role: "assistant", content: null, tool_calls: [{
                    id: "fixture-tool", type: "function",
                    function: { name: "fixture_probe", arguments: "{}" }
                }] } : { role: "assistant", content: "Fixture complete." } }],
            usage: { prompt_tokens: 10, completion_tokens: 10, total_tokens: 20 }
        }));
    });
    let client, session;
    try {
        await mkdir(plugin);
        await mkdir(workspace);
        const spy = join(plugin, "spy.cjs");
        await writeFile(spy, `
const fs = require("node:fs");
const input = JSON.parse(fs.readFileSync(0, "utf8"));
fs.appendFileSync(process.env.HOOK_TEST_LOG, JSON.stringify({
    event: process.argv[2], sessionId: input.sessionId, cwd: input.cwd
}) + "\\n");
process.stdout.write(JSON.stringify({ additionalContext: "NUDGE_HOOK_TEST_" + process.argv[2] }));
`);
        await writeFile(join(plugin, "plugin.json"), JSON.stringify({
            name: "nudge-hook-contract", version: "1.0.0", hooks: "hooks.json"
        }));
        await writeFile(join(plugin, "hooks.json"), JSON.stringify({
            version: 1, hooks: Object.fromEntries(events.map(event => [event, [{
                type: "command", bash: `"${process.execPath}" "${spy}" ${event}`,
                powershell: `& '${process.execPath.replaceAll("'", "''")}' '${spy.replaceAll("'", "''")}' ${event}`,
                env: { HOOK_TEST_LOG: log }, timeoutSec: 5
            }]]))
        }));
        await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
        const { CopilotClient } = await import(pathToFileURL(join(sdk, "index.js")).href);
        client = new CopilotClient({
            connection: { kind: "stdio", path: runtime }, baseDirectory: join(root, "home"),
            workingDirectory: workspace, logLevel: "error"
        });
        await client.start();
        const callbacks = [];
        const callback = name => () => { callbacks.push(name); };
        session = await client.createSession({
            model: "gpt-6-sol", workingDirectory: workspace, requestExtensions: false,
            enableConfigDiscovery: true, enableFileHooks: false, enableSessionTelemetry: false,
            availableTools: ["fixture_probe"],
            provider: { type: "openai", wireApi: "completions",
                baseUrl: `http://127.0.0.1:${server.address().port}/v1` },
            onPermissionRequest: () => ({ kind: "approved" }),
            tools: [{ name: "fixture_probe", description: "Return deterministic fixture data.",
                parameters: { type: "object", properties: {} }, handler: async () => "fixture value" }],
            hooks: { onSessionStart: callback("sessionStart"), onSessionEnd: callback("sessionEnd"),
                onUserPromptSubmitted: callback("userPromptSubmitted"), onPostToolUse: callback("postToolUse") }
        });
        await session.rpc.options.update({ installedPlugins: [{
            name: "nudge-hook-contract", marketplace: "", version: "1.0.0",
            installed_at: new Date().toISOString(), enabled: true, cache_path: plugin
        }] });
        await client.rpc.sessions.reloadPluginHooks({ sessionId: session.sessionId, deferRepoHooks: true });
        await session.sendAndWait({ prompt: "Run the fixture probe, then stop." }, 30000);
        await session.disconnect();
        const commands = (await readFile(log, "utf8")).trim().split("\n").map(JSON.parse);
        assert.deepEqual(commands.map(item => item.event).sort(), [...events].sort());
        assert.deepEqual(callbacks.sort(), [...events].sort());
        assert.ok(commands.every(item => item.sessionId === session.sessionId && item.cwd === workspace));
        assert.equal(requests, 2);
        assert.match(observedContext[0], /NUDGE_HOOK_TEST_sessionStart/);
        assert.doesNotMatch(observedContext[0], /NUDGE_HOOK_TEST_postToolUse/);
        assert.match(observedContext[1], /NUDGE_HOOK_TEST_postToolUse/);
    } finally {
        if (session) {
            await session.disconnect();
            await client.deleteSession(session.sessionId);
        }
        if (client) await client.stop();
        server.closeAllConnections();
        await new Promise(resolve => server.close(resolve));
        await rm(root, { recursive: true, force: true });
    }
});
