"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const fsp = require("node:fs/promises");
const path = require("node:path");
const crypto = require("node:crypto");
const { gzipSync } = require("node:zlib");
const { spawn, spawnSync } = require("node:child_process");
const { Readable, Writable } = require("node:stream");
const { pipeline } = require("node:stream/promises");
const runtime = require("../../plugins/dotnet-nudge/scripts/run.cjs");
const launcher = path.resolve(__dirname, "../../plugins/dotnet-nudge/scripts/run.cjs");
const releaseBase = "https://github.com/Redth/dotnet-nudge/releases";

async function fixture(t) {
    const parent = process.env.NUDGE_TEST_ROOT || path.resolve(__dirname, "../../.artifacts/plugin-tests");
    await fsp.mkdir(parent, { recursive: true });
    const root = await fsp.mkdtemp(path.join(parent, "runtime with spaces-"));
    t.after(() => fsp.rm(root, { recursive: true, force: true }));
    const env = { ...process.env, NUDGE_STATE_DIR: root,
        XDG_DATA_HOME: path.join(root, "fallback"), LOCALAPPDATA: path.join(root, "fallback"),
        PATH: path.dirname(process.execPath) };
    for (const name of ["NUDGE_ENGINE_PATH", "NUDGE_PLUGIN_DATA", "COPILOT_PLUGIN_DATA", "CLAUDE_PLUGIN_DATA"]) {
        delete env[name];
    }
    return { root, env };
}

function release(bytes = Buffer.from("fixture engine; never executed\n")) {
    const rid = runtime.currentRid();
    const archive = gzipSync(bytes);
    const hash = buffer => crypto.createHash("sha256").update(buffer).digest("hex");
    const manifest = {
        schemaVersion: 1, engine: "dotnet-nudge", status: "published", version: "1.2.3",
        artifacts: [{
            rid, format: "gzip", url: `${releaseBase}/download/v1.2.3/dotnet-nudge-1.2.3-${rid}.gz`,
            sha256: hash(archive), executableSha256: hash(bytes)
        }]
    };
    const requests = [];
    const fetcher = async url => {
        requests.push(url);
        const body = url.endsWith("runtime-manifest.json") ? JSON.stringify(manifest) : archive;
        const response = new Response(body);
        Object.defineProperty(response, "url", { value: url });
        return response;
    };
    return { bytes, archive, manifest, fetcher, requests, rid };
}

test("MCP and hooks share explicit, installed and PATH engine lookup", async t => {
    const { root, env } = await fixture(t);
    assert.equal(runtime.resolveEngine(env), null);
    assert.equal(runtime.resolveEngine({ ...env, NUDGE_ENGINE_PATH: process.execPath }), process.execPath);
    assert.throws(() => runtime.resolveEngine({ ...env, NUDGE_ENGINE_PATH: path.join(root, "missing") }),
        /NUDGE_ENGINE_PATH/);
    const candidate = path.join(root, process.platform === "win32" ? "dotnet-nudge.exe" : "dotnet-nudge");
    await fsp.writeFile(candidate, "fixture", { mode: 0o755 });
    assert.equal(runtime.resolveEngine({ ...env, PATH: root }), candidate);
    const published = release();
    const installed = await runtime.installRelease(root, published.manifest, published.rid, published.fetcher);
    assert.equal(runtime.resolveEngine(env), installed);
    const pluginData = path.join(root, "host state");
    assert.equal(runtime.dataDir({ ...env, NUDGE_PLUGIN_DATA: pluginData }), root);
    const { NUDGE_STATE_DIR, ...withoutOverride } = env;
    assert.equal(runtime.dataDir({ ...withoutOverride, NUDGE_PLUGIN_DATA: pluginData }), pluginData);
    assert.equal(runtime.resolveEngine({ ...withoutOverride, NUDGE_PLUGIN_DATA: root }), installed);
});

test("first use verifies both hashes, installs atomically and reuses the runtime offline", async t => {
    const { root, env } = await fixture(t);
    const published = release();
    const installed = await runtime.ensureEngine(env, published.fetcher);
    assert.deepEqual(await fsp.readFile(installed), published.bytes);
    assert.equal(runtime.resolveEngine(env), installed);
    assert.equal(published.requests.length, 2);
    const second = await runtime.ensureEngine(env, () => { throw new Error("Must not download again"); });
    assert.equal(second, installed);
    assert.equal(fs.existsSync(path.join(root, "engine/.setup-lock")), false);
    assert.deepEqual(await fsp.readdir(path.dirname(installed)),
        [path.basename(installed), "runtime.json"].sort());
});

test("simultaneous first-use processes coalesce setup", async t => {
    const { env } = await fixture(t);
    const published = release();
    const engines = await Promise.all([runtime.ensureEngine(env, published.fetcher),
        runtime.ensureEngine(env, published.fetcher)]);
    assert.equal(engines[0], engines[1]);
    assert.equal(published.requests.length, 2);
});

test("bootstrap opt-out and unpublished releases fail explicitly without running anything", async t => {
    const { env, root } = await fixture(t);
    await assert.rejects(runtime.ensureEngine({ ...env, NUDGE_AUTO_SETUP: "0" }), /disabled/);
    const unavailable = async url => {
        const response = new Response("not found", { status: 404 });
        Object.defineProperty(response, "url", { value: url });
        return response;
    };
    await assert.rejects(runtime.ensureEngine(env, unavailable), /HTTP 404.*first engine release/);
    assert.equal(fs.existsSync(path.join(root, "engine/current")), false);
    assert.equal(fs.existsSync(path.join(root, "engine/.setup-lock")), false);
});

test("wrong checksums, release URLs, duplicate RIDs and unsafe versions cannot select an engine", async t => {
    const { root } = await fixture(t);
    for (const mutate of [
        manifest => { manifest.artifacts[0].sha256 = "0".repeat(64); },
        manifest => { manifest.artifacts[0].executableSha256 = "0".repeat(64); },
        manifest => { manifest.artifacts[0].url = "https://example.invalid/engine.gz"; },
        manifest => { manifest.artifacts.push(manifest.artifacts[0]); },
        manifest => { manifest.version = "../outside"; },
        manifest => { manifest.status = "unpublished"; }
    ]) {
        const published = release();
        mutate(published.manifest);
        await assert.rejects(runtime.installRelease(root, published.manifest, published.rid, published.fetcher));
        assert.equal(fs.existsSync(path.join(root, "engine/current")), false);
        const version = path.join(root, "engine/1.2.3");
        if (fs.existsSync(version)) assert.deepEqual(await fsp.readdir(version), []);
    }
});

test("stream bounds accept the exact limit and reject a single excessive byte", async () => {
    const sink = () => new Writable({ write(chunk, encoding, callback) { callback(); } });
    await pipeline(Readable.from([Buffer.alloc(4)]), runtime.boundedStream(4), sink());
    await assert.rejects(pipeline(Readable.from([Buffer.alloc(4), Buffer.alloc(1)]),
        runtime.boundedStream(4), sink()), /exceeds/);
});

test("unsafe engine directories and broken selection links cannot be overwritten", async t => {
    const { root } = await fixture(t);
    const outside = path.join(root, "outside");
    await fsp.mkdir(outside);
    const linkedRoot = path.join(root, "linked state");
    await fsp.mkdir(linkedRoot);
    await fsp.symlink(outside, path.join(linkedRoot, "engine"),
        process.platform === "win32" ? "junction" : "dir");
    const published = release();
    await assert.rejects(runtime.installRelease(linkedRoot, published.manifest, published.rid, published.fetcher),
        /must not be a link/);
    assert.deepEqual(await fsp.readdir(outside), []);
    const state = path.join(root, "safe state");
    await fsp.mkdir(path.join(state, "engine"), { recursive: true });
    try {
        await fsp.symlink(path.join(outside, "missing"), path.join(state, "engine/current"));
    } catch (error) {
        if (error.code !== "EPERM") throw error;
        t.diagnostic("Windows file symlink privilege unavailable");
        return;
    }
    await assert.rejects(runtime.installRelease(state, published.manifest, published.rid, published.fetcher),
        /Unsafe runtime selection/);
});

test("an interrupted selection can recover only an identical verified runtime", async t => {
    const { root } = await fixture(t);
    const published = release();
    const installed = await runtime.installRelease(root, published.manifest, published.rid, published.fetcher);
    await fsp.unlink(path.join(root, "engine/current"));
    const recovered = await runtime.installRelease(root, published.manifest, published.rid,
        () => { throw new Error("Verified existing runtime needs no download"); });
    assert.equal(recovered, installed);
    await fsp.writeFile(installed, "tampered");
    await assert.rejects(runtime.installRelease(root, published.manifest, published.rid, published.fetcher),
        /does not match/);
});

test("installed symlinks, foreign RIDs and selection traversal are never executed", async t => {
    const { root } = await fixture(t);
    const published = release();
    const installed = await runtime.installRelease(root, published.manifest, published.rid, published.fetcher);
    const pointer = path.join(root, "engine/current");
    await fsp.writeFile(pointer, "../outside\n");
    assert.equal(runtime.installedEngine(root), null);
    const foreign = published.rid === "linux-x64" ? "osx-arm64" : "linux-x64";
    await fsp.writeFile(pointer, `1.2.3/${foreign}\n`);
    assert.equal(runtime.installedEngine(root), null);
    await fsp.writeFile(pointer, `1.2.3/${published.rid}\n`);
    await fsp.unlink(installed);
    try {
        await fsp.symlink(process.execPath, installed);
    } catch (error) {
        if (error.code !== "EPERM") throw error;
        t.diagnostic("Windows file symlink privilege unavailable");
        return;
    }
    assert.equal(runtime.installedEngine(root), null);
    await assert.rejects(runtime.installRelease(root, published.manifest, published.rid, published.fetcher),
        /Unsafe runtime/);
});

test("CLI launcher preserves arguments, streams, state and nonzero child exit", async t => {
    const { root, env } = await fixture(t);
    const script = path.join(root, "engine fixture.cjs");
    await fsp.writeFile(script, `
        process.stdin.on("data", chunk => process.stdout.write(chunk));
        process.stdin.on("end", () => {
            console.error(JSON.stringify({ args: process.argv.slice(2), state: process.env.NUDGE_STATE_DIR }));
            process.exitCode = 23;
        });
    `);
    const result = spawnSync(process.execPath, [launcher, script, "argument with spaces"], {
        env: { ...env, NUDGE_ENGINE_PATH: process.execPath }, input: "unmodified stdin\n", encoding: "utf8"
    });
    assert.equal(result.status, 23, result.stderr);
    assert.equal(result.stdout, "unmodified stdin\n");
    assert.deepEqual(JSON.parse(result.stderr), { args: ["argument with spaces"], state: root });
});

test("a supplied real self-contained engine initializes MCP through the plugin launcher", {
    skip: !(process.env.NUDGE_INTEGRATION_ENGINE_PATH || process.env.NUDGE_ENGINE_PATH)
}, async t => {
    const { root, env } = await fixture(t);
    const source = process.env.NUDGE_INTEGRATION_ENGINE_PATH || process.env.NUDGE_ENGINE_PATH;
    const published = release(await fsp.readFile(source));
    await runtime.ensureEngine(env, published.fetcher);
    const child = spawn(process.execPath, [launcher, "mcp"], {
        env: { ...env, DOTNET_ROOT: path.join(root, "no shared dotnet runtime"), NUDGE_AUTO_SETUP: "0" },
        stdio: ["pipe", "pipe", "pipe"]
    });
    t.after(() => { if (child.exitCode === null) child.kill(); });
    let stdout = "";
    let stderr = "";
    const pending = new Map();
    child.stderr.on("data", chunk => { stderr += chunk; });
    child.on("error", error => {
        for (const request of pending.values()) request.reject(error);
    });
    child.on("exit", code => {
        for (const request of pending.values()) request.reject(new Error(`MCP exited ${code}: ${stderr}`));
    });
    child.stdout.on("data", chunk => {
        stdout += chunk;
        let newline;
        while ((newline = stdout.indexOf("\n")) >= 0) {
            const line = stdout.slice(0, newline);
            stdout = stdout.slice(newline + 1);
            const message = JSON.parse(line);
            const request = pending.get(message.id);
            if (request) {
                pending.delete(message.id);
                request.resolve(message);
            }
        }
    });
    function request(id, method, params = {}) {
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                pending.delete(id);
                reject(new Error(`MCP ${method} timed out: ${stderr}`));
            }, 30_000);
            pending.set(id, {
                resolve: value => { clearTimeout(timer); resolve(value); },
                reject: error => { clearTimeout(timer); reject(error); }
            });
            child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
        });
    }
    const initialized = await request(1, "initialize", {
        protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "runtime-contract", version: "1" }
    });
    assert.ok(initialized.result?.capabilities?.tools, stdout + stderr);
    assert.equal(initialized.error, undefined);
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
    const tools = await request(2, "tools/list");
    assert.equal(tools.error, undefined);
    const names = tools.result.tools.map(tool => tool.name);
    assert.ok(names.includes("nudge_status"), JSON.stringify(names));
    assert.ok(names.includes("nudge_scan"), JSON.stringify(names));
    const stopped = new Promise(resolve => child.once("exit", resolve));
    child.stdin.end();
    await stopped;
});
