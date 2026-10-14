#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const fsp = require("node:fs/promises");
const path = require("node:path");
const os = require("node:os");
const crypto = require("node:crypto");
const { spawn } = require("node:child_process");
const { Transform, Readable } = require("node:stream");
const { pipeline } = require("node:stream/promises");
const { createGunzip } = require("node:zlib");

const pluginRoot = path.resolve(__dirname, "..");
const repository = "Redth/dotnet-nudge";
const releaseBase = `https://github.com/${repository}/releases`;
const supportedRids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];
const versionPattern = /^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$/;
const digestPattern = /^[a-f0-9]{64}$/;
const setupDiagnostic = "dotnet-nudge: runtime setup or update is required. MCP will automatically download and verify a compatible engine; hooks never download or run a missing or outdated managed engine.";

function currentRid() {
    const system = { darwin: "osx", linux: "linux", win32: "win" }[process.platform];
    const rid = `${system}-${process.arch}`;
    if (!supportedRids.includes(rid)) {
        throw new Error(`Unsupported platform: ${process.platform} ${process.arch}.`);
    }
    return rid;
}

function defaultDataDir(env = process.env) {
    if (process.platform === "win32") {
        if (!env.LOCALAPPDATA) throw new Error("LOCALAPPDATA is not set; set NUDGE_STATE_DIR.");
        return path.join(env.LOCALAPPDATA, "dotnet-nudge");
    }
    if (env.XDG_DATA_HOME) return path.join(env.XDG_DATA_HOME, "dotnet-nudge");
    return process.platform === "darwin"
        ? path.join(os.homedir(), "Library", "Application Support", "dotnet-nudge")
        : path.join(os.homedir(), ".local", "share", "dotnet-nudge");
}

function dataDir(env = process.env) {
    return path.resolve(env.NUDGE_STATE_DIR || env.NUDGE_PLUGIN_DATA ||
        env.COPILOT_PLUGIN_DATA || env.CLAUDE_PLUGIN_DATA || defaultDataDir(env));
}

function regularPath(target, directory = false) {
    try {
        const info = fs.lstatSync(target);
        return !info.isSymbolicLink() && (directory ? info.isDirectory() : info.isFile());
    } catch (error) {
        if (error.code === "ENOENT" || error.code === "ENOTDIR") return false;
        throw error;
    }
}

function exists(target) {
    try {
        fs.lstatSync(target);
        return true;
    } catch (error) {
        if (error.code === "ENOENT" || error.code === "ENOTDIR") return false;
        throw error;
    }
}

function executable(target) {
    try {
        if (!fs.statSync(target).isFile()) return false;
        fs.accessSync(target, process.platform === "win32" ? fs.constants.F_OK : fs.constants.X_OK);
        return true;
    } catch (error) {
        if (["ENOENT", "ENOTDIR", "EACCES"].includes(error.code)) return false;
        throw error;
    }
}

function installedEngine(root) {
    const engineRoot = path.join(root, "engine");
    const pointer = path.join(engineRoot, "current");
    if (!regularPath(engineRoot, true) || !regularPath(pointer)) return null;
    const relative = fs.readFileSync(pointer, "utf8").trim();
    const parts = relative.split("/");
    if (parts.length !== 2 || !versionPattern.test(parts[0]) || parts[1] !== currentRid()) return null;
    const versionDir = path.join(engineRoot, parts[0]);
    const installDir = path.join(versionDir, parts[1]);
    const candidate = path.join(installDir, process.platform === "win32" ? "dotnet-nudge.exe" : "dotnet-nudge");
    if (!regularPath(versionDir, true) || !regularPath(installDir, true) ||
        !regularPath(candidate) || !executable(candidate)) return null;
    return candidate;
}

function resolveEngineSelection(env = process.env) {
    if (env.NUDGE_ENGINE_PATH) {
        const explicit = path.resolve(env.NUDGE_ENGINE_PATH);
        if (!executable(explicit)) throw new Error(`NUDGE_ENGINE_PATH is not an executable file: ${explicit}`);
        return { executable: explicit };
    }
    const name = process.platform === "win32" ? "dotnet-nudge.exe" : "dotnet-nudge";
    const bundled = path.join(pluginRoot, "bin", name);
    if (regularPath(bundled) && executable(bundled)) return { executable: bundled };
    const roots = [dataDir(env), defaultDataDir(env),
        env.COPILOT_PLUGIN_DATA, env.CLAUDE_PLUGIN_DATA].filter(Boolean);
    for (const root of new Set(roots)) {
        const installed = installedEngine(path.resolve(root));
        if (installed) return {
            executable: installed, root: path.resolve(root),
            version: path.basename(path.dirname(path.dirname(installed)))
        };
    }
    for (const directory of (env.PATH || "").split(path.delimiter).filter(Boolean)) {
        const candidate = path.join(directory, name);
        if (path.isAbsolute(candidate) && executable(candidate)) return { executable: candidate };
    }
    return null;
}

function resolveEngine(env = process.env) {
    return resolveEngineSelection(env)?.executable ?? null;
}

function minimumEngineVersion() {
    const manifest = JSON.parse(fs.readFileSync(path.join(pluginRoot, "runtime-manifest.json"), "utf8"));
    if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(manifest.minimumVersion)) {
        throw new Error("Plugin minimum engine version must be an exact stable version.");
    }
    return manifest.minimumVersion;
}

function meetsMinimum(version, minimum) {
    const match = version.match(/^(\d+)\.(\d+)\.(\d+)(-[^+]+)?(?:\+.*)?$/);
    if (!match) throw new Error(`Invalid managed engine version: ${version}`);
    const required = minimum.split(".");
    for (let index = 0; index < 3; index++) {
        const actual = BigInt(match[index + 1]);
        const expected = BigInt(required[index]);
        if (actual !== expected) return actual > expected;
    }
    return !match[4];
}

function compatibleEngine(selection, minimum) {
    return selection && (!selection.version || meetsMinimum(selection.version, minimum))
        ? selection.executable : null;
}

function validateManifest(manifest, rid) {
    if (!supportedRids.includes(rid) || manifest.schemaVersion !== 1 || manifest.engine !== "dotnet-nudge" ||
        manifest.status !== "published" || !versionPattern.test(manifest.version) ||
        !Array.isArray(manifest.artifacts)) {
        throw new Error("No valid published dotnet-nudge release manifest is available.");
    }
    const matches = manifest.artifacts.filter(artifact => artifact.rid === rid);
    if (matches.length !== 1) throw new Error(`Release must contain exactly one engine for ${rid}.`);
    const artifact = matches[0];
    const name = `dotnet-nudge-${manifest.version}-${rid}.gz`;
    const expectedUrl = `${releaseBase}/download/v${manifest.version}/${name}`;
    if (artifact.format !== "gzip" || artifact.url !== expectedUrl ||
        !digestPattern.test(artifact.sha256) || !digestPattern.test(artifact.executableSha256)) {
        throw new Error(`Release artifact identity or SHA-256 is invalid for ${rid}.`);
    }
    return artifact;
}

function boundedStream(limit, digest) {
    let size = 0;
    return new Transform({
        transform(chunk, encoding, callback) {
            size += chunk.length;
            if (size > limit) return callback(new Error("Runtime download exceeds the supported size."));
            if (digest) digest.update(chunk);
            callback(null, chunk);
        }
    });
}

async function fetchRelease(url, fetcher = fetch) {
    const response = await fetcher(url, { signal: AbortSignal.timeout(120_000) });
    if (!response.ok) {
        throw new Error(`Release download failed (HTTP ${response.status}). Publish the first engine release or set NUDGE_ENGINE_PATH to a local engine.`);
    }
    if (!response.url.startsWith("https://")) throw new Error("Release download redirected to an insecure URL.");
    return response;
}

async function publishedManifest(fetcher = fetch) {
    const bundled = JSON.parse(await fsp.readFile(path.join(pluginRoot, "runtime-manifest.json"), "utf8"));
    if (bundled.status === "published") return bundled;
    if (bundled.status !== "release-channel" ||
        bundled.releaseManifestUrl !== `${releaseBase}/latest/download/runtime-manifest.json`) {
        throw new Error("Plugin runtime release channel is invalid.");
    }
    const response = await fetchRelease(`${releaseBase}/latest/download/runtime-manifest.json`, fetcher);
    const chunks = [];
    const collector = new Transform({
        transform(chunk, encoding, callback) { chunks.push(chunk); callback(); }
    });
    await pipeline(Readable.fromWeb(response.body), boundedStream(1024 * 1024), collector);
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}

async function safeDirectory(target) {
    await fsp.mkdir(target, { recursive: true });
    if (!regularPath(target, true)) throw new Error(`Runtime directory must not be a link: ${target}`);
}

async function selectEngine(engineRoot, version, rid) {
    const pointer = path.join(engineRoot, "current");
    if (exists(pointer) && !regularPath(pointer)) throw new Error(`Unsafe runtime selection: ${pointer}`);
    const temporary = path.join(engineRoot, `.current-${crypto.randomUUID()}`);
    try {
        await fsp.writeFile(temporary, `${version}/${rid}\n`, { flag: "wx" });
        await fsp.rename(temporary, pointer);
    } finally {
        await fsp.rm(temporary, { force: true });
    }
}

async function installRelease(root, manifest, rid, fetcher = fetch) {
    const artifact = validateManifest(manifest, rid);
    await fsp.mkdir(root, { recursive: true });
    root = await fsp.realpath(root);
    const engineRoot = path.join(root, "engine");
    await safeDirectory(engineRoot);
    const versionDir = path.join(engineRoot, manifest.version);
    await safeDirectory(versionDir);
    const installDir = path.join(versionDir, rid);
    const pointer = path.join(engineRoot, "current");
    if (exists(pointer) && !regularPath(pointer)) {
        throw new Error(`Unsafe runtime selection: ${pointer}`);
    }
    const name = rid.startsWith("win-") ? "dotnet-nudge.exe" : "dotnet-nudge";
    if (exists(installDir)) {
        const candidate = path.join(installDir, name);
        const metadataPath = path.join(installDir, "runtime.json");
        if (!regularPath(installDir, true) || !regularPath(candidate) || !regularPath(metadataPath)) {
            throw new Error(`Unsafe runtime installation: ${installDir}`);
        }
        const metadata = JSON.parse(await fsp.readFile(metadataPath, "utf8"));
        const digest = crypto.createHash("sha256");
        await pipeline(fs.createReadStream(candidate), boundedStream(1024 * 1024 * 1024, digest),
            new Transform({ transform(chunk, encoding, callback) { callback(); } }));
        if (metadata.version !== manifest.version || metadata.rid !== rid ||
            metadata.sha256 !== artifact.sha256 || digest.digest("hex") !== artifact.executableSha256) {
            throw new Error(`Existing runtime does not match the verified release: ${installDir}`);
        }
        await selectEngine(engineRoot, manifest.version, rid);
        return candidate;
    }
    const staging = await fsp.mkdtemp(path.join(versionDir, ".setup-"));
    const archive = path.join(staging, "engine.gz");
    const candidate = path.join(staging, name);
    try {
        const response = await fetchRelease(artifact.url, fetcher);
        const compressedDigest = crypto.createHash("sha256");
        await pipeline(Readable.fromWeb(response.body), boundedStream(512 * 1024 * 1024, compressedDigest),
            fs.createWriteStream(archive, { flags: "wx" }));
        if (compressedDigest.digest("hex") !== artifact.sha256) throw new Error("Runtime archive SHA-256 mismatch.");
        const engineDigest = crypto.createHash("sha256");
        await pipeline(fs.createReadStream(archive), createGunzip(), boundedStream(1024 * 1024 * 1024, engineDigest),
            fs.createWriteStream(candidate, { flags: "wx", mode: 0o755 }));
        if ((await fsp.stat(candidate)).size === 0 || engineDigest.digest("hex") !== artifact.executableSha256) {
            throw new Error("Runtime executable SHA-256 mismatch or empty executable.");
        }
        await fsp.unlink(archive);
        await fsp.writeFile(path.join(staging, "runtime.json"), JSON.stringify({
            engine: "dotnet-nudge", version: manifest.version, rid,
            sha256: artifact.sha256, executableSha256: artifact.executableSha256, executable: name, files: [name]
        }, null, 2) + "\n", { flag: "wx" });
        await fsp.rename(staging, installDir);
        await selectEngine(engineRoot, manifest.version, rid);
        return path.join(installDir, name);
    } finally {
        await fsp.rm(staging, { recursive: true, force: true });
    }
}

async function ensureEngine(env = process.env, fetcher = fetch) {
    const minimum = minimumEngineVersion();
    const existing = resolveEngineSelection(env);
    const compatible = compatibleEngine(existing, minimum);
    if (compatible) return compatible;
    if (env.NUDGE_AUTO_SETUP === "0") {
        throw new Error(`Automatic runtime setup is disabled. This plugin requires managed engine ${minimum} or newer; install a verified engine or set NUDGE_ENGINE_PATH.`);
    }
    const root = path.resolve(env.NUDGE_STATE_DIR || existing?.root || defaultDataDir(env));
    await fsp.mkdir(root, { recursive: true });
    const engineRoot = path.join(root, "engine");
    await safeDirectory(engineRoot);
    const lock = path.join(engineRoot, ".setup-lock");
    let handle;
    const deadline = Date.now() + 120_000;
    while (!handle) {
        try {
            handle = await fsp.open(lock, "wx");
        } catch (error) {
            if (error.code !== "EEXIST") throw error;
            if (!regularPath(lock)) throw new Error(`Unsafe runtime setup lock: ${lock}`);
            const installed = compatibleEngine(resolveEngineSelection(env), minimum);
            if (installed) return installed;
            if (Date.now() >= deadline) {
                throw new Error(`Runtime setup is already locked: ${lock}. If no setup process is running, remove that lock and retry.`);
            }
            await new Promise(resolve => setTimeout(resolve, 200));
        }
    }
    try {
        await handle.writeFile(`${process.pid}\n`);
        const installed = compatibleEngine(resolveEngineSelection(env), minimum);
        if (installed) return installed;
        console.error(`dotnet-nudge: downloading a verified ${currentRid()} engine from ${repository}.`);
        const manifest = await publishedManifest(fetcher);
        validateManifest(manifest, currentRid());
        if (!meetsMinimum(manifest.version, minimum)) {
            throw new Error(`Published engine ${manifest.version} is older than this plugin's minimum ${minimum}.`);
        }
        return await installRelease(root, manifest, currentRid(), fetcher);
    } finally {
        await handle.close();
        await fsp.unlink(lock);
    }
}

async function missingHook(host, event, root) {
    let payload;
    if (host === "copilot" && ["session-start", "post-tool-use"].includes(event)) {
        payload = { additionalContext: setupDiagnostic };
    } else if (host === "claude" && event !== "session-end") {
        const names = { "session-start": "SessionStart", "prompt-submit": "UserPromptSubmit", "post-tool-use": "PostToolUse" };
        payload = { hookSpecificOutput: { hookEventName: names[event], additionalContext: setupDiagnostic } };
    }
    if (!payload) return;
    const marker = path.join(root, ".setup-required-notice-shown");
    try {
        await fsp.mkdir(root, { recursive: true });
        const notice = await fsp.open(marker, "wx");
        await notice.close();
    } catch (error) {
        if (error.code === "EEXIST" && regularPath(marker)) return;
        console.error(`dotnet-nudge: unable to persist the one-time setup notice in '${root}': ${error.message}. Set NUDGE_STATE_DIR to writable storage.`);
        return;
    }
    console.log(JSON.stringify(payload));
}

async function main(args) {
    if (Number(process.versions.node.split(".")[0]) < 22) {
        throw new Error("Node.js 22 or newer is required by the plugin launcher.");
    }
    const hook = args[0] === "hook";
    if (hook && (args.length !== 5 || args[1] !== "--host" || args[3] !== "--event" ||
        !["copilot", "claude"].includes(args[2]) ||
        !["session-start", "prompt-submit", "post-tool-use", "session-end"].includes(args[4]))) {
        throw new Error("Invalid hook host or event.");
    }
    const root = dataDir();
    const engine = hook
        ? compatibleEngine(resolveEngineSelection(), minimumEngineVersion())
        : await ensureEngine();
    if (!engine) {
        await missingHook(args[2], args[4], root);
        return;
    }
    const child = spawn(engine, args, {
        stdio: "inherit",
        env: { ...process.env, NUDGE_STATE_DIR: root,
            DOTNET_BUNDLE_EXTRACT_BASE_DIR: path.join(root, "bundle-cache") }
    });
    for (const signal of ["SIGINT", "SIGTERM"]) {
        process.on(signal, () => child.kill(signal));
    }
    child.on("error", error => {
        console.error(`dotnet-nudge: engine could not start: ${error.message}`);
        process.exitCode = 1;
    });
    child.on("exit", (code, signal) => {
        process.exitCode = code ?? (128 + (os.constants.signals[signal] || 1));
    });
}

module.exports = { currentRid, dataDir, defaultDataDir, installedEngine, resolveEngine,
    validateManifest, publishedManifest, installRelease, ensureEngine, boundedStream, setupDiagnostic };

if (require.main === module) {
    main(process.argv.slice(2)).catch(error => {
        console.error(`dotnet-nudge: ${error.message}`);
        process.exitCode = 1;
    });
}
