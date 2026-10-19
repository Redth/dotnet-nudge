import assert from "node:assert/strict";
import { test } from "node:test";
import { cp, mkdir, mkdtemp, readFile, realpath, rm, symlink, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { createRequire } from "node:module";
import { execFileSync } from "node:child_process";
import { DashboardModel } from "../../plugins/dotnet-nudge/canvas/model.mjs";
import { createDashboard } from "../../plugins/dotnet-nudge/canvas/provider.mjs";
import { versionDelta } from "../../plugins/dotnet-nudge/canvas/dashboard.mjs";

const require = createRequire(import.meta.url);
const { install } = require("../../plugins/dotnet-nudge/scripts/install-canvas.cjs");
const { stage } = require("../../plugins/dotnet-nudge/scripts/stage-dev-plugin.cjs");

test("version visualization preserves values and emphasizes each changed component", () => {
    const major = versionDelta("9.0.0", "10.0.13");
    assert.equal(major.kind, "major");
    assert.deepEqual(major.segments.filter(part => part.changed).map(part => part.component), ["major", "patch"]);
    assert.equal(major.segments.map(part => part.value).join(""), "10.0.13");
    assert.equal(versionDelta("1.2.3", "1.3.0").kind, "minor");
    assert.equal(versionDelta("1.2.3", "1.2.4").kind, "patch");
    assert.equal(versionDelta("1.2.3.4", "1.2.3.5").kind, "patch");
    assert.equal(versionDelta("11.0.0-rc.1", "11.0.0-rc.2").kind, "preview");
    assert.equal(versionDelta("1.0.0+first", "1.0.0+second").kind, "metadata");
    assert.equal(versionDelta("1.0.0+first", "1.0.0").kind, "metadata");
    const metadata = versionDelta("1.0.0-rc.1+first", "1.0.0-rc.1+second");
    assert.equal(metadata.kind, "metadata");
    assert.deepEqual(metadata.segments.filter(part => part.changed).map(part => part.component), ["metadata"]);
    assert.equal(versionDelta("1.0.0-rc.1", "1.0.0").kind, "stable");
    assert.equal(versionDelta("opaque", "<untrusted>").kind, "unknown");
});

test("package view starts with its toolbar and keeps report metadata inside View options", async () => {
    const html = await readFile(new URL("../../plugins/dotnet-nudge/canvas/index.html", import.meta.url), "utf8");
    assert.match(html, /<section id="package-controls" aria-label="Package controls">\s*<div class="toolbar">/);
    const options = html.slice(html.indexOf('<div id="view-options"'), html.indexOf('<div id="selection-options"'));
    assert.match(options, /id="notice"/);
    assert.match(options, /id="coverage"/);
    assert.match(html, /id="coverage-indicator"[^>]*aria-label="Some checks are incomplete"/);
    assert.doesNotMatch(html, /class="section-heading"|id="packages-title"/);
    const chrome = html.slice(html.indexOf('<header class="canvas-toolbar">'), html.indexOf("</header>"));
    assert.match(chrome, /<nav[^>]+id="areas"/);
    assert.match(chrome, /<\/nav>\s*<button id="refresh"/);
    assert.doesNotMatch(chrome, /<img|<h1|id="workspace"/);
    assert.match(html, /<title>dotNudge<\/title>/);
    assert.ok(html.indexOf('id="plan"') < html.indexOf('<main id="content-scroll">'));
    assert.ok(html.indexOf('id="review-category"') < html.indexOf('<main id="content-scroll">'));
    assert.doesNotMatch(html, /<footer/);
    assert.doesNotMatch(html, /id="visible-count"|<aside/);
    const css = await readFile(new URL("../../plugins/dotnet-nudge/canvas/dashboard.css", import.meta.url), "utf8");
    const script = await readFile(new URL("../../plugins/dotnet-nudge/canvas/dashboard.mjs", import.meta.url), "utf8");
    assert.match(css, /thead \{ position: sticky; top: 0;/);
    assert.match(css, /table \{ border-collapse: separate; border-spacing: 0;/);
    assert.doesNotMatch(css, /th \{ box-shadow:/);
    assert.doesNotMatch(css, /th:last-child, td:last-child \{ display: none/);
    assert.doesNotMatch(script, /<th[^>]*>Status<\/th>|data-target=/);
    assert.match(html, /id="version-options"[^>]+popover="manual"[^>]+role="listbox"/);
});

function report(workspace, revision = "r1") {
    const row = (id, project, vulnerable = false) => ({
        id, revision, project, framework: "net10.0", runtimeIdentifier: null,
        packageId: "Example.Package", current: "1.0.0", direct: true, centrallyManaged: true,
        status: "complete", candidates: [
            { version: "1.0.1", kind: "patch", source: "private", compatibility: "partial",
                projectUrl: "https://example.invalid/project", description: "<script>untrusted</script>" },
            { version: "2.0.0", kind: "major", source: "private", compatibility: "unknown" }
        ],
        advisories: vulnerable ? [{ url: "https://example.invalid/advisory", severity: 3,
            range: "[1.0.0,1.0.1)", auditSource: "private" }] : [],
        minimalKnownFix: vulnerable ? "1.0.1" : null, deprecations: [], paths: [], diagnostics: []
    });
    return { schemaVersion: 1, workspaceId: "fixture", workspace,
        generatedAt: "2026-10-15T10:00:00Z", complete: true, solutions: ["Example.slnx"],
        packages: [row("first", "App/App.csproj", true), row("second", "Tests/Tests.csproj")], coverage: [] };
}
async function fixture(t) {
    const base = resolve(".artifacts");
    await mkdir(base, { recursive: true });
    const root = await mkdtemp(join(base, "dashboard-tests-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const workspace = join(root, "workspace");
    await mkdir(workspace);
    return { root, workspace, report: report(workspace) };
}
const selection = (model, id = "first", selected = true) => ({
    version: model.state.version,
    changes: [{ id, revision: "r1", version: "1.0.1", source: "private", selected }]
});

test("exact choices persist across panels/restarts; stale evidence invalidates them", async t => {
    const f = await fixture(t);
    const directory = join(f.root, "state");
    let model = await DashboardModel.open(directory, f.workspace, f.report);
    await model.select(selection(model));
    model = await DashboardModel.open(directory, f.workspace, f.report);
    assert.equal(model.selection().length, 1);
    assert.equal(model.selection()[0].project, "App/App.csproj");
    await model.select(selection(model, "first", false));
    model = await DashboardModel.open(directory, f.workspace, f.report);
    assert.equal(model.state.choices.first.selected, false);
    await model.select(selection(model));
    await model.change(model.state.version, state => model.replaceReport(state, report(f.workspace, "r2")));
    assert.equal(model.selection().length, 0);
    await assert.rejects(model.select(selection(model)), /scope changed/);
});

test("rejects mismatched targets, source, workspace and concurrent stale writes", async t => {
    const f = await fixture(t);
    const model = await DashboardModel.open(join(f.root, "state"), f.workspace, f.report);
    const input = selection(model);
    input.changes[0].source = "unapproved";
    await assert.rejects(model.select(input), /target and source/);
    await model.select(selection(model));
    await assert.rejects(model.select({ ...selection(model), version: 0 }), /dashboard changed/);
    await assert.rejects(DashboardModel.open(join(f.root, "other"), f.root, f.report), /exact workspace/);
    const first = model.select(selection(model, "first", false));
    const second = model.select(selection(model, "second"));
    await first;
    await assert.rejects(second, /dashboard changed/);
});

test("planning handoff excludes unchecked scopes, records receipt, and does not approve edits", async t => {
    const f = await fixture(t);
    const model = await DashboardModel.open(join(f.root, "state"), f.workspace, f.report);
    await model.select(selection(model));
    let sent;
    await model.request({ version: model.state.version, kind: "plan" }, async options => {
        sent = options;
        // A user can change a future selection while the original request is being admitted.
        await model.select(selection(model, "first", false));
        return "message-id";
    });
    assert.equal(model.selection().length, 0);
    assert.equal(model.state.requests[0].selection.length, 1);
    assert.equal(model.state.requests[0].selection[0].target, "1.0.1");
    assert.equal(model.state.requests[0].status, "submitted");
    assert.equal(model.state.requests[0].messageId, "message-id");
    assert.equal(sent.mode, "immediate");
    assert.match(sent.prompt, /PLAN ONLY, not approval/);
    assert.match(sent.prompt, /Central|centrally managed/);
    assert.doesNotMatch(sent.prompt, /Tests\/Tests.csproj/);
    await assert.rejects(model.request({ version: model.state.version, kind: "refresh" },
        () => assert.fail("Duplicate request must not be sent")), /pending/);
});

test("delivery failure is explicit and persists, without automatically resending", async t => {
    const f = await fixture(t);
    const model = await DashboardModel.open(join(f.root, "state"), f.workspace, f.report);
    await assert.rejects(model.request({ version: 0, kind: "refresh" },
        async () => { throw new Error("Disconnected"); }), /Disconnected/);
    assert.equal(model.state.requests[0].status, "failed");
    assert.match(model.state.requests[0].message, /not confirmed/);
    const loaded = await DashboardModel.open(join(f.root, "state"), f.workspace, f.report);
    assert.equal(loaded.state.requests[0].status, "failed");
});

test("SDK refresh checks actual scan data even with no findings and binds isolated trials separately", async t => {
    const f = await fixture(t);
    const status = { schemaVersion: 1, workspaceId: f.report.workspaceId, findings: [], coverage: [] };
    const model = await DashboardModel.open(join(f.root, "state"), f.workspace, f.report, status);
    assert.ok(model.snapshot().requestKinds.includes("helper"));
    let prompt;
    await model.request({ version: 0, kind: "refresh", category: "sdk" }, async options => {
        prompt = options.prompt;
        return "sdk-refresh";
    });
    assert.match(prompt, /read-only nudge_scan first/);
    assert.match(prompt, /cached status alone does not check SDKs/);
    assert.match(prompt, /requested global.json pin, effective SDK, installed SDKs/);
    await model.change(model.state.version, state => {
        state.requests[0].status = "completed";
        state.status.findings = [{ finding: { id: "preview-trial", revision: "r1", category: "sdk-trial",
            kind: "sdk-preview-trial", current: "10.0.100", candidate: "11.0.100-preview.1",
            scope: { sdkScope: "global.json" } } }];
    });
    await model.request({ version: model.state.version, kind: "category", category: "sdk" }, async () => "sdk-review");
    assert.equal(model.state.requests.at(-1).findings[0].category, "sdk-trial");
    assert.equal(model.state.requests.at(-1).findings[0].candidate, "11.0.100-preview.1");
    await assert.rejects(DashboardModel.open(join(f.root, "wrong"), f.workspace, f.report,
        { ...status, workspaceId: "another-worktree" }), /exact workspace/);
});

test("empty maintenance categories can be checked without replacing NuGet selections", async t => {
    const f = await fixture(t);
    const model = await DashboardModel.open(join(f.root, "state"), f.workspace, f.report,
        { schemaVersion: 1, workspaceId: f.report.workspaceId, findings: [], coverage: [] });
    await model.select(selection(model));
    await assert.rejects(model.request({ version: model.state.version, kind: "refresh", category: "invented" },
        () => assert.fail("Unknown categories must not be sent")), /available maintenance category/);
    for (const category of ["workload", "runtime", "toolchain"]) {
        await model.request({ version: model.state.version, kind: "refresh", category }, async ({ prompt }) => {
            assert.match(prompt, /existing package report can be retained/);
            assert.match(prompt, /Local runtime servicing is not a deployment fix/);
            return `${category}-refresh`;
        });
        assert.equal(model.state.requests.at(-1).category, category);
        assert.deepEqual(model.state.requests.at(-1).findings, []);
        assert.equal(model.selection()[0].target, "1.0.1");
        await model.change(model.state.version, state => { state.requests.at(-1).status = "completed"; });
    }
});

test("helper review binds exact catalog identity/revision without authorizing installation", async t => {
    const f = await fixture(t);
    const resource = { id: "dotnet/skills:dotnet", repository: "dotnet/skills", name: "dotnet",
        version: "0.2.5", revision: "a".repeat(40), url: "https://github.com/dotnet/skills/tree/main/plugins/dotnet",
        description: "C# helpers", capabilities: ["skills", "language-server"] };
    const helpers = { schemaVersion: 1, workspaceId: f.report.workspaceId, coverage: [],
        recommendations: [{ resource, action: "review", revision: "fit-revision",
            reasons: ["C# project"], relevantSkills: [] }] };
    const status = { schemaVersion: 1, workspaceId: f.report.workspaceId, findings: [], coverage: [],
        agentTools: helpers };
    const model = await DashboardModel.open(join(f.root, "state"), f.workspace, f.report, status);
    await assert.rejects(model.request({ version: 0, kind: "helper", id: resource.id, revision: "stale" },
        async () => assert.fail("Stale recommendation must not be sent")), /recommendation changed/);
    let sent;
    await model.request({ version: 0, kind: "helper", id: resource.id, revision: "fit-revision" },
        async options => { sent = options; return "helper-review"; });
    assert.equal(model.state.requests[0].helper.id, resource.id);
    assert.equal(model.state.requests[0].helper.sourceRevision, resource.revision);
    assert.equal(model.state.requests[0].helper.action, "review");
    assert.match(sent.prompt, /Never auto-install, enable, execute catalog commands/);
    assert.match(sent.prompt, /native user approval before install\/update\/remove/);
    await assert.rejects(DashboardModel.open(join(f.root, "wrong"), f.workspace, f.report,
        { ...status, agentTools: { ...helpers, workspaceId: "different-worktree" } }), /retain this workspace/);
});

test("provider shares models, serves protected loopback assets, and routes UI requests to the session", async t => {
    const f = await fixture(t);
    let sent;
    class CanvasError extends Error {
        constructor(code, message) { super(message); this.code = code; }
    }
    const provider = createDashboard({ createCanvas: options => options, CanvasError,
        getSession: () => ({ workspacePath: join(f.root, "session"), send: async options => {
            sent = options; return "admitted";
        } }) });
    const canvas = provider.canvas;
    const ctx = instanceId => ({ sessionId: "session", instanceId,
        host: { capabilities: { canvases: true } }, session: { workingDirectory: f.workspace },
        input: { workspace: f.workspace, report: f.report } });
    const [opened, reopened] = await Promise.all([canvas.open(ctx("one")), canvas.open(ctx("one"))]);
    assert.equal(opened.url, reopened.url);
    const other = await canvas.open(ctx("two"));
    t.after(async () => {
        await canvas.onClose(ctx("one"));
        await canvas.onClose(ctx("two"));
    });
    const action = (instanceId, name, input = {}) =>
        canvas.actions.find(a => a.name === name).handler({ ...ctx(instanceId), input });
    assert.equal(canvas.id, "nudge-dashboard");
    assert.equal(canvas.displayName, "dotNudge");
    assert.equal(opened.title, "dotNudge");
    assert.equal(canvas.actions.length, 5);
    const page = await fetch(opened.url);
    assert.equal(page.status, 200);
    assert.match(await page.text(), /Plan selected updates/);
    assert.match(page.headers.get("content-security-policy"), /script-src 'self'/);
    assert.equal((await fetch(new URL("dotnet-logo.svg", opened.url))).status, 200);
    assert.equal((await fetch(new URL("OpenSans.ttf", opened.url))).status, 200);
    assert.equal((await fetch(new URL("/model", opened.url))).status, 404);
    const route = new URL("set_selection", opened.url);
    const input = { version: 0, changes: [{ id: "first", revision: "r1", version: "1.0.1",
        source: "private", selected: true }] };
    assert.equal((await fetch(route, { method: "POST", headers: {
        Origin: "https://untrusted.invalid", "Content-Type": "application/json"
    }, body: JSON.stringify(input) })).status, 403);
    assert.equal((await fetch(route, { method: "POST", headers: {
        Origin: route.origin, "Content-Type": "application/json"
    }, body: JSON.stringify(input) })).status, 200);
    let shared = await action("two", "get_model");
    assert.equal(shared.choices.first.selected, true);
    const stream = await fetch(new URL("events", other.url), { signal: AbortSignal.timeout(5000) });
    const reader = stream.body.getReader();
    assert.match(new TextDecoder().decode((await reader.read()).value), /changed/);
    await action("one", "set_selection", { version: shared.version, changes: [{
        id: "first", revision: "r1", version: "1.0.1", source: "private", selected: true
    }] });
    assert.match(new TextDecoder().decode((await reader.read()).value), /changed/);
    await reader.cancel();
    shared = await action("two", "get_model");
    const requestRoute = new URL("request", opened.url);
    const request = await fetch(requestRoute, { method: "POST", headers: {
        Origin: requestRoute.origin, "Content-Type": "application/json"
    }, body: JSON.stringify({ version: shared.version, kind: "plan" }) });
    assert.equal(request.status, 200);
    assert.match(sent.prompt, /instanceId: one/);
    let state = await action("one", "get_model");
    await action("one", "set_request_status", { version: state.version,
        requestId: state.requests[0].id, status: "completed", message: "Plan ready; awaiting approval." });
    state = await action("one", "get_model");
    assert.equal(state.requests[0].status, "completed");
    await assert.rejects(action("one", "set_details", { version: state.version,
        id: "first", revision: "r1", targetVersion: "1.0.1", targetSource: "private",
        text: "Unsafe", links: [{ label: "Bad", url: "javascript:alert(1)" }] }));
    await action("one", "set_details", { version: state.version,
        id: "first", revision: "r1", targetVersion: "1.0.1", targetSource: "private",
        text: "Verified release information.",
        links: [{ label: "Source", url: "https://example.invalid/project" }] });
    state = await action("one", "get_model");
    assert.equal(state.details.first.text, "Verified release information.");
    assert.equal(state.details.first.targetVersion, "1.0.1");
    await action("one", "set_selection", { version: state.version, changes: [{
        id: "first", revision: "r1", version: "2.0.0", source: "private", selected: true
    }] });
    state = await action("one", "get_model");
    assert.notEqual(state.choices.first.version, state.details.first.targetVersion);
    await assert.rejects(action("one", "set_report", { version: state.version,
        report: { ...f.report, generatedAt: "2025-01-01T00:00:00Z" } }), /older report/);
    await assert.rejects(canvas.open({ ...ctx("outside"), input: {
        workspace: f.root, report: report(f.root) } }), /outside this session/);
    const restored = createDashboard({ createCanvas: options => options, CanvasError,
        getSession: () => ({ workspacePath: join(f.root, "session") }) }).canvas;
    const restoredOpen = await restored.open(ctx("restored"));
    assert.equal((await (await fetch(new URL("model", restoredOpen.url))).json()).choices.first.selected, true);
    await restored.onClose(ctx("restored"));
});

test("canvas installation is explicit, self-contained, and refuses to overwrite", async t => {
    const f = await fixture(t);
    assert.throws(() => install([]), /explicit scope/);
    install(["--project", f.workspace]);
    const destination = join(f.workspace, ".github", "extensions", "nudge-dashboard");
    assert.match(await readFile(join(destination, "extension.mjs"), "utf8"), /joinSession/);
    assert.match(await readFile(join(destination, "dashboard.css"), "utf8"), /#512bd4/);
    assert.throws(() => install(["--project", f.workspace]), /nothing was overwritten/);
    const sdkWorkspace = join(f.root, "sdk-session");
    await mkdir(sdkWorkspace);
    install(["--session", sdkWorkspace]);
    assert.match(await readFile(join(sdkWorkspace, "extensions", "nudge-dashboard", "extension.mjs"), "utf8"),
        /joinSession/);
    assert.throws(() => install(["--session", sdkWorkspace]), /nothing was overwritten/);
});

test("native canvas can import bounded report artifacts without putting the graph in tool arguments", async t => {
    const f = await fixture(t);
    const sessionRoot = join(f.root, "session");
    await mkdir(join(sessionRoot, "files"), { recursive: true });
    await writeFile(join(sessionRoot, "files", "packages.json"), JSON.stringify(f.report));
    await writeFile(join(sessionRoot, "files", "status.json"), JSON.stringify({
        schemaVersion: 1, workspaceId: f.report.workspaceId, findings: [], coverage: []
    }));
    class CanvasError extends Error {
        constructor(code, message) { super(message); this.code = code; }
    }
    const provider = createDashboard({ createCanvas: options => options, CanvasError,
        displayName: "dotNudge", getSession: () => ({ workspacePath: sessionRoot }) });
    const canvas = provider.canvas;
    const ctx = { sessionId: "artifacts", instanceId: "imported",
        session: { workingDirectory: f.workspace },
        input: { workspace: f.workspace, reportPath: "files/packages.json", statusPath: "files/status.json" } };
    const opened = await canvas.open(ctx);
    t.after(() => canvas.onClose(ctx));
    assert.equal(opened.title, "dotNudge");
    const invoke = (name, input = {}) => canvas.actions.find(action => action.name === name).handler({ ...ctx, input });
    const summary = await invoke("get_model", { summary: true });
    assert.equal(summary.report.packageCount, 2);
    assert.equal(summary.report.packages, undefined);
    assert.deepEqual(summary.selection, []);
    const next = { ...f.report, generatedAt: "2026-10-16T10:00:00Z" };
    await writeFile(join(sessionRoot, "files", "packages.json"), JSON.stringify(next));
    await invoke("set_report", { version: summary.version, reportPath: "files/packages.json" });
    const current = await invoke("get_model");
    assert.equal(current.report.generatedAt, next.generatedAt);
    await writeFile(join(sessionRoot, "outside.json"), JSON.stringify(next));
    await assert.rejects(invoke("set_report", { version: current.version, reportPath: "outside.json" }), /inside this session/);
    await assert.rejects(invoke("set_report", { version: current.version,
        reportPath: join(sessionRoot, "files", "packages.json") }), /relative to this session/);
    await assert.rejects(invoke("set_report", { version: current.version,
        report: next, reportPath: "files/packages.json" }), /exactly one/);
    await assert.rejects(invoke("set_report", { version: current.version, report: next,
        status: {}, statusPath: "files/status.json" }), /not both/);
    if (process.platform !== "win32") {
        await symlink(join(sessionRoot, "outside.json"), join(sessionRoot, "files", "escape.json"));
        await assert.rejects(invoke("set_report", { version: current.version, reportPath: "files/escape.json" }),
            /inside this session/);
    }
    await writeFile(join(sessionRoot, "files", "large.json"), " ".repeat(8 * 1024 * 1024 + 1));
    await assert.rejects(invoke("set_report", { version: current.version, reportPath: "files/large.json" }), /8 MiB/);
});

test("dev plugin staging is local, isolated, explicit and native-extension capable", async t => {
    const f = await fixture(t);
    const destination = join(f.root, "dev-plugin");
    assert.throws(() => stage([]), /ABSOLUTE_NEW_DIRECTORY/);
    stage(["--output", destination, "--engine", process.execPath]);
    const manifest = JSON.parse(await readFile(join(destination, "plugin.json"), "utf8"));
    assert.equal(manifest.name, "dotnet-nudge-dev");
    assert.equal(manifest.extensions, "extensions");
    assert.equal(manifest.mcpServers, ".mcp.json");
    assert.equal(manifest.hooks, "com.github.copilot/hooks/hooks.json");
    assert.equal(manifest.$schema, undefined);
    const config = JSON.parse(await readFile(join(destination, ".mcp.json"), "utf8"));
    const server = config.mcpServers["dotnet-nudge-dev"];
    assert.equal(server.env.NUDGE_AUTO_SETUP, "0");
    assert.equal(server.env.NUDGE_STATE_DIR, join(destination, "state"));
    assert.equal(server.env.NUDGE_ENGINE_PATH, await realpath(process.execPath));
    const bindings = JSON.parse(execFileSync(process.execPath, ["-e",
        `const launcher = require(process.argv[1]);
         process.stdout.write(JSON.stringify({ engine: process.env.NUDGE_ENGINE_PATH,
             state: launcher.dataDir(), setup: process.env.NUDGE_AUTO_SETUP }));`,
        join(destination, "scripts", "run.cjs")], {
        encoding: "utf8", env: { ...process.env, NUDGE_ENGINE_PATH: join(f.root, "unrelated-engine"),
            NUDGE_STATE_DIR: join(f.root, "unrelated-state"), NUDGE_AUTO_SETUP: "1" }
    }));
    assert.equal(bindings.engine, await realpath(process.execPath));
    assert.equal(bindings.state, join(destination, "state"));
    assert.equal(bindings.setup, "0");
    const entry = await readFile(join(destination, "extensions", "nudge-dashboard", "extension.mjs"), "utf8");
    assert.match(entry, /joinSession/);
    assert.match(entry, /dotNudge/);
    assert.match(entry, /"\.\.\/\.\.\/canvas\/provider\.mjs"/);
    assert.throws(() => stage(["--output", destination, "--engine", process.execPath]), /nothing was overwritten/);
});

test("dev plugin staging accepts CRLF source launchers", async t => {
    const f = await fixture(t);
    const source = join(f.root, "source-plugin");
    await cp(new URL("../../plugins/dotnet-nudge/", import.meta.url), source, { recursive: true });
    const launcher = join(source, "scripts", "run.cjs");
    await writeFile(launcher, (await readFile(launcher, "utf8")).replace(/\r?\n/g, "\r\n"));
    const { stage: stageCopiedPlugin } = require(join(source, "scripts", "stage-dev-plugin.cjs"));
    const destination = join(f.root, "staged-crlf-plugin");
    stageCopiedPlugin(["--output", destination, "--engine", process.execPath]);
    const stagedLauncher = join(destination, "scripts", "run.cjs");
    assert.match(await readFile(stagedLauncher, "utf8"), /process\.env\.NUDGE_ENGINE_PATH = /);
    execFileSync(process.execPath, ["--check", stagedLauncher]);
});
