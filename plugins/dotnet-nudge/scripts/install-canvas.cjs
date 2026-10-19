#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");

const files = ["extension.mjs", "provider.mjs", "model.mjs", "index.html",
    "dashboard.mjs", "dashboard.css", "dotnet-logo.svg", "OpenSans.ttf", "OFL.txt"];

function install(args) {
    let extensions;
    if (args.length === 1 && args[0] === "--user") {
        extensions = path.join(process.env.COPILOT_HOME || path.join(os.homedir(), ".copilot"), "extensions");
    } else if (args.length === 2 && ["--project", "--session"].includes(args[0]) && path.isAbsolute(args[1])) {
        const root = fs.realpathSync(args[1]);
        if (!fs.statSync(root).isDirectory()) throw new Error("The target root must be a directory.");
        extensions = args[0] === "--session" ? path.join(root, "extensions") :
            path.join(root, ".github", "extensions");
    } else {
        throw new Error("Choose an explicit scope: install-canvas.cjs --user | --project ABSOLUTE_PROJECT_ROOT | --session ABSOLUTE_SDK_SESSION_WORKSPACE");
    }
    const destination = path.join(extensions, "nudge-dashboard");
    if (fs.existsSync(destination)) {
        throw new Error(`Canvas already exists at ${destination}. Review it before replacing it; nothing was overwritten.`);
    }
    fs.mkdirSync(extensions, { recursive: true });
    fs.mkdirSync(destination);
    for (const file of files) fs.copyFileSync(path.join(__dirname, "..", "canvas", file),
        path.join(destination, file), fs.constants.COPYFILE_EXCL);
    process.stdout.write(`Installed Nudge canvas at ${destination}.\nRestart Copilot or reload extensions. A canvas-capable host and package-report-capable engine are required.\n`);
}

if (require.main === module) {
    try { install(process.argv.slice(2)); }
    catch (error) { process.stderr.write(`dotnet-nudge: ${error.message}\n`); process.exitCode = 1; }
}
module.exports = { install };
