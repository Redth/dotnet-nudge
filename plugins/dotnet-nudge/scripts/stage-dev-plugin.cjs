#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const path = require("node:path");

function stage(args) {
    if (args.length !== 4 || args[0] !== "--output" || args[2] !== "--engine" ||
        !path.isAbsolute(args[1]) || !path.isAbsolute(args[3])) {
        throw new Error("Use stage-dev-plugin.cjs --output ABSOLUTE_NEW_DIRECTORY --engine ABSOLUTE_ENGINE");
    }
    const source = path.resolve(__dirname, "..");
    const destination = path.resolve(args[1]);
    const engine = fs.realpathSync(args[3]);
    if (!fs.statSync(engine).isFile()) throw new Error("The dev engine must be an existing executable.");
    fs.accessSync(engine, process.platform === "win32" ? fs.constants.F_OK : fs.constants.X_OK);
    if (destination === source || destination.startsWith(source + path.sep))
        throw new Error("Stage the dev plugin outside its source directory.");
    if (fs.existsSync(destination)) throw new Error("The dev output already exists; nothing was overwritten.");
    fs.mkdirSync(destination, { recursive: true });
    for (const directory of ["canvas", "skills", "scripts", "hooks", "com.github.copilot"])
        fs.cpSync(path.join(source, directory), path.join(destination, directory), { recursive: true, errorOnExist: true });
    fs.copyFileSync(path.join(source, "runtime-manifest.json"), path.join(destination, "runtime-manifest.json"));
    const original = JSON.parse(fs.readFileSync(path.join(source, "plugin.json"), "utf8"));
    const { $schema, extensions, ...metadata } = original;
    const manifest = { ...metadata, name: "dotnet-nudge-dev", version: `${original.version}-dev`,
        description: "Local-only dotnet-nudge acceptance copy; never changes the installed production plugin.",
        extensions: "extensions", mcpServers: ".mcp.json", hooks: "com.github.copilot/hooks/hooks.json" };
    fs.writeFileSync(path.join(destination, "plugin.json"), JSON.stringify(manifest, null, 2) + "\n");
    const state = path.join(destination, "state");
    const config = { mcpServers: { "dotnet-nudge-dev": {
        type: "stdio", command: process.execPath,
        args: [path.join(destination, "scripts", "run.cjs"), "mcp"],
        env: { NUDGE_ENGINE_PATH: engine, NUDGE_STATE_DIR: state, NUDGE_AUTO_SETUP: "0" }
    } } };
    fs.writeFileSync(path.join(destination, ".mcp.json"), JSON.stringify(config, null, 2) + "\n");
    const launcher = path.join(destination, "scripts", "run.cjs");
    const content = fs.readFileSync(launcher, "utf8");
    const prologue = '#!/usr/bin/env node\n"use strict";\n';
    if (!content.startsWith(prologue)) throw new Error("The dev launcher header changed; environment binding needs review.");
    const bindings = Object.entries(config.mcpServers["dotnet-nudge-dev"].env)
        .map(([key, value]) => `process.env.${key} = ${JSON.stringify(value)};`).join("\n");
    fs.writeFileSync(launcher, prologue + bindings + "\n" + content.slice(prologue.length));
    const entryDirectory = path.join(destination, "extensions", "nudge-dashboard");
    fs.mkdirSync(entryDirectory, { recursive: true });
    fs.writeFileSync(path.join(entryDirectory, "extension.mjs"), [
        'import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";',
        'import { createDashboard } from "../../canvas/provider.mjs";',
        "let session;",
        'const { canvas } = createDashboard({ createCanvas, CanvasError, getSession: () => session, displayName: "dotNudge" });',
        "session = await joinSession({ canvases: [canvas] });", ""
    ].join("\n"));
    process.stdout.write(`Staged isolated dev plugin: ${destination}\n` +
        `Run copilot --plugin-dir "${destination}" from the test workspace. No global install or engine download is needed.\n`);
    return { destination, engine, state };
}

if (require.main === module) {
    try { stage(process.argv.slice(2)); }
    catch (error) { process.stderr.write(`dotnet-nudge: ${error.message}\n`); process.exitCode = 1; }
}
module.exports = { stage };
