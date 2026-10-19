import { randomBytes } from "node:crypto";
import { createServer } from "node:http";
import { open, readFile, realpath } from "node:fs/promises";
import { isAbsolute, join, relative, resolve, sep } from "node:path";
import { DashboardError, DashboardModel } from "./model.mjs";

const objectSchema = properties => ({ type: "object", properties, additionalProperties: false });
const version = { type: "integer", minimum: 0 };
const report = { type: "object" };
const reportInputs = { report, reportPath: { type: "string", minLength: 1 },
    status: { type: ["object", "null"] }, statusPath: { type: "string", minLength: 1 } };
const reportChoice = [
    { required: ["report"], not: { required: ["reportPath"] } },
    { required: ["reportPath"], not: { required: ["report"] } }
];
const stateInput = { ...objectSchema({ version, ...reportInputs }),
    required: ["version"], oneOf: reportChoice };

async function readSessionJson(session, path) {
    if (typeof path !== "string" || isAbsolute(path))
        throw new DashboardError("Report paths must be relative to this session, under files/.");
    const root = await realpath(join(session.workspacePath, "files"));
    const file = await realpath(resolve(session.workspacePath, path));
    const scope = relative(root, file);
    if (!scope || scope === ".." || scope.startsWith(`..${sep}`) || isAbsolute(scope))
        throw new DashboardError("Report files must stay inside this session's files/ directory.");
    const handle = await open(file, "r");
    try {
        const info = await handle.stat();
        if (!info.isFile() || info.size > 8 * 1024 * 1024)
            throw new DashboardError("Report input must be a regular JSON file of at most 8 MiB.");
        const text = await handle.readFile("utf8");
        if (Buffer.byteLength(text) > 8 * 1024 * 1024)
            throw new DashboardError("Report input exceeds 8 MiB; regenerate a bounded report.");
        return JSON.parse(text);
    } finally { await handle.close(); }
}

async function resolveReportInput(session, input) {
    if ((input.report === undefined) === (input.reportPath === undefined))
        throw new DashboardError("Provide exactly one package report object or session reportPath.");
    if (input.status !== undefined && input.statusPath !== undefined)
        throw new DashboardError("Provide a status object or statusPath, not both.");
    return {
        report: input.reportPath === undefined ? input.report : await readSessionJson(session, input.reportPath),
        status: input.statusPath === undefined ? input.status : await readSessionJson(session, input.statusPath)
    };
}

export function createDashboard({ createCanvas, CanvasError, getSession, displayName = "dotNudge" }) {
    const panels = new Map();
    const openingPanels = new Map();
    const models = new Map();
    const assets = new Map([
        ["", ["index.html", "text/html; charset=utf-8"]],
        ["dashboard.css", ["dashboard.css", "text/css; charset=utf-8"]],
        ["dashboard.mjs", ["dashboard.mjs", "text/javascript; charset=utf-8"]],
        ["dotnet-logo.svg", ["dotnet-logo.svg", "image/svg+xml"]],
        ["OpenSans.ttf", ["OpenSans.ttf", "font/ttf"]]
    ]);
    const pendingModels = new Map();

    function panel(ctx) {
        const entry = panels.get(ctx.instanceId);
        if (!entry || entry.sessionId !== ctx.sessionId)
            throw new CanvasError("dashboard_not_open", "Open this dashboard in the current session first.");
        return entry;
    }

    async function act(entry, action, input = {}) {
        const model = entry.model;
        if (action === "get_model") {
            const state = model.snapshot();
            if (!input.summary) return state;
            return { version: state.version, workspace: state.workspace,
                requestKinds: state.requestKinds,
                report: { workspaceId: state.report.workspaceId, generatedAt: state.report.generatedAt,
                    complete: state.report.complete, packageCount: state.report.packages.length,
                    coverage: state.report.coverage },
                sdks: state.status?.sdks ?? null,
                agentTools: state.status?.agentTools ?? null,
                selection: model.selection(), requests: state.requests };
        }
        if (action === "set_selection") return model.select(input);
        if (action === "request") return model.request(input, options => getSession().send({
            ...options, prompt: options.prompt +
                `\nOriginating canvas instanceId: ${entry.instanceId}. Request status must be returned to that instance.`
        }));
        if (action === "set_report") {
            const replacement = await resolveReportInput(getSession(), input);
            return model.change(input.version, state => {
                if (Date.parse(replacement.report?.generatedAt) < Date.parse(state.report.generatedAt))
                    throw new DashboardError("An older report cannot replace newer evidence.", 409);
                model.replaceReport(state, replacement.report, replacement.status);
            });
        }
        if (action === "set_details") return model.change(input.version, state => {
            const row = state.report.packages.find(p => p.id === input.id);
            if (!row || row.revision !== input.revision || typeof input.text !== "string" ||
                input.text.length > 20000 || !Array.isArray(input.links) ||
                !(row.candidates.some(c => c.version === input.targetVersion && c.source === input.targetSource) ||
                    row.candidates.length === 0 && input.targetVersion === null && input.targetSource === null) ||
                input.links.length > 12 || input.links.some(link =>
                    typeof link.label !== "string" || !safeUrl(link.url)))
                throw new DashboardError("Details must match the current scope and use HTTP(S) evidence links.");
            state.details[row.id] = { revision: row.revision, targetVersion: input.targetVersion,
                targetSource: input.targetSource, text: input.text, links: input.links };
        });
        if (action === "set_request_status") return model.change(input.version, state => {
            const request = state.requests.find(r => r.id === input.requestId);
            if (!request || !["working", "completed", "failed"].includes(input.status) ||
                typeof input.message !== "string" || input.message.length > 4000)
                throw new DashboardError("A known request, status and short result message are required.");
            if (["completed", "failed"].includes(request.status))
                throw new DashboardError("That request already has a terminal result.", 409);
            request.status = input.status;
            request.message = input.message;
        });
        throw new DashboardError("Unknown dashboard action.", 404);
    }

    async function http(entry, req, res) {
        const origin = `http://127.0.0.1:${entry.server.address().port}`;
        res.setHeader("Cache-Control", "no-store");
        res.setHeader("X-Content-Type-Options", "nosniff");
        res.setHeader("Referrer-Policy", "no-referrer");
        res.setHeader("Content-Security-Policy",
            "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self'; font-src 'self'; base-uri 'none'; form-action 'none'");
        if (req.headers.host !== new URL(origin).host)
            throw new DashboardError("Invalid local host.", 403);
        const url = new URL(req.url, origin);
        const prefix = `/${entry.token}/`;
        if (!url.pathname.startsWith(prefix))
            throw new DashboardError("Unknown dashboard endpoint.", 404);
        const route = url.pathname.slice(prefix.length);
        if (req.method === "GET" && assets.has(route)) {
            const [file, mime] = assets.get(route);
            res.setHeader("Content-Type", mime);
            res.end(await readFile(new URL(file, import.meta.url)));
            return;
        }
        if (req.method === "GET" && route === "model") {
            res.setHeader("Content-Type", "application/json");
            res.end(JSON.stringify(entry.model.snapshot()));
            return;
        }
        if (req.method === "GET" && route === "events") {
            res.writeHead(200, { "Content-Type": "text/event-stream", Connection: "keep-alive" });
            res.write("data: changed\n\n");
            const notify = () => res.write("data: changed\n\n");
            entry.model.listeners.add(notify);
            entry.streams.add(res);
            const heartbeat = setInterval(() => res.write(": keepalive\n\n"), 20000);
            req.on("close", () => {
                clearInterval(heartbeat);
                entry.model.listeners.delete(notify);
                entry.streams.delete(res);
            });
            return;
        }
        if (req.method !== "POST" || !["set_selection", "request"].includes(route))
            throw new DashboardError("Unknown dashboard endpoint.", 404);
        if (req.headers.origin !== origin ||
            req.headers["content-type"]?.split(";")[0] !== "application/json")
            throw new DashboardError("A same-origin JSON request is required.", 403);
        let text = "";
        for await (const chunk of req) {
            text += chunk.toString("utf8");
            if (Buffer.byteLength(text) > 1024 * 1024)
                throw new DashboardError("Dashboard request exceeds 1 MiB.", 413);
        }
        let input;
        try { input = JSON.parse(text); }
        catch { throw new DashboardError("Invalid request JSON."); }
        const result = await act(entry, route, input);
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify(result));
    }

    const invoke = action => async ctx => {
        try { return await act(panel(ctx), action, ctx.input); }
        catch (error) {
            throw new CanvasError(error.status === 409 ? "dashboard_conflict" : "dashboard_error", error.message);
        }
    };
    const actions = [
        { name: "get_model", description: "Read evidence, selection, requests and current optimistic version. Use summary:true to omit the full package graph.",
            inputSchema: objectSchema({ summary: { type: "boolean" } }), handler: invoke("get_model") },
        { name: "set_report", description: "Replace with a fresh package report object or session-relative reportPath and optional status/statusPath; invalidate stale selections.",
            inputSchema: stateInput, handler: invoke("set_report") },
        { name: "set_selection", description: "Set exact revision/version/source selections. Planning only, not update approval.",
            inputSchema: { ...objectSchema({ version, changes: { type: "array", items: {
                ...objectSchema({ id: { type: "string" }, revision: { type: "string" },
                    version: { type: "string" }, source: { type: "string" }, selected: { type: "boolean" } }),
                required: ["id", "revision", "version", "source", "selected"]
            } } }), required: ["version", "changes"] }, handler: invoke("set_selection") },
        { name: "set_details", description: "Attach verified plain-text release/advisory details and evidence links to an exact package revision.",
            inputSchema: { ...objectSchema({ version, id: { type: "string" }, revision: { type: "string" },
                targetVersion: { type: ["string", "null"] }, targetSource: { type: ["string", "null"] },
                text: { type: "string" }, links: { type: "array", items: { type: "object" } } }),
                required: ["version", "id", "revision", "targetVersion", "targetSource", "text", "links"] }, handler: invoke("set_details") },
        { name: "set_request_status", description: "Report progress/result of a user dashboard request; completed is not proof of package edits.",
            inputSchema: { ...objectSchema({ version, requestId: { type: "string" },
                status: { enum: ["working", "completed", "failed"] }, message: { type: "string" } }),
                required: ["version", "requestId", "status", "message"] }, handler: invoke("set_request_status") }
    ];
    const canvas = createCanvas({
        id: "nudge-dashboard", displayName,
        description: "Review NuGet upgrades, defer packages and send exact selections to the agent; inspect SDK and workload findings.",
        inputSchema: { ...objectSchema({ workspace: { type: "string", minLength: 1 }, ...reportInputs }),
            required: ["workspace"], oneOf: reportChoice },
        actions,
        open: async ctx => {
            const session = getSession();
            if (ctx.host?.capabilities?.canvases === false)
                throw new CanvasError("dashboard_unavailable", "This host cannot render a canvas; use native questions.");
            if (!session?.workspacePath || !ctx.input || !isAbsolute(ctx.input.workspace))
                throw new CanvasError("dashboard_input", "An absolute workspace and session artifact directory are required.");
            const workspace = await realpath(ctx.input.workspace);
            if (ctx.session?.workingDirectory) {
                const root = await realpath(ctx.session.workingDirectory);
                const scope = relative(root, workspace);
                if (scope === ".." || scope.startsWith(`..${sep}`) || isAbsolute(scope))
                    throw new CanvasError("dashboard_scope", "Dashboard workspace is outside this session.");
            }
            const key = `${ctx.sessionId}:${workspace}`;
            if (!models.has(key)) {
                if (!pendingModels.has(key)) pendingModels.set(key, (async () => {
                    const input = await resolveReportInput(session, ctx.input);
                    return DashboardModel.open(join(session.workspacePath, "files", "nudge-dashboard"),
                        workspace, input.report, input.status);
                })().finally(() => pendingModels.delete(key)));
                models.set(key, await pendingModels.get(key));
            }
            let entry = panels.get(ctx.instanceId) ?? await openingPanels.get(ctx.instanceId);
            if (entry && entry.model !== models.get(key))
                throw new CanvasError("dashboard_scope", "A panel cannot switch workspace; open a new instance.");
            if (!entry) {
                if (!openingPanels.has(ctx.instanceId)) openingPanels.set(ctx.instanceId, (async () => {
                    const next = { sessionId: ctx.sessionId, instanceId: ctx.instanceId,
                        model: models.get(key), token: randomBytes(24).toString("hex"), streams: new Set() };
                    next.server = createServer((req, res) => {
                        http(next, req, res).catch(error => {
                            if (res.headersSent) { res.destroy(error); return; }
                            res.writeHead(error.status ?? 500, { "Content-Type": "application/json" });
                            res.end(JSON.stringify({ error: error.message }));
                        });
                    });
                    await new Promise((resolve, reject) => {
                        next.server.once("error", reject);
                        next.server.listen(0, "127.0.0.1", resolve);
                    });
                    panels.set(ctx.instanceId, next);
                    return next;
                })().finally(() => openingPanels.delete(ctx.instanceId)));
                entry = await openingPanels.get(ctx.instanceId);
                if (entry.model !== models.get(key))
                    throw new CanvasError("dashboard_scope", "A panel cannot switch workspace; open a new instance.");
            }
            return { title: displayName, url: `http://127.0.0.1:${entry.server.address().port}/${entry.token}/` };
        },
        onClose: async ctx => {
            const entry = panels.get(ctx.instanceId) ?? await openingPanels.get(ctx.instanceId);
            if (!entry) return;
            panels.delete(ctx.instanceId);
            for (const stream of entry.streams) stream.end();
            await new Promise(resolve => entry.server.close(resolve));
        }
    });
    return { canvas };
}

function safeUrl(value) {
    try {
        const url = new URL(value);
        return ["http:", "https:"].includes(url.protocol) && !url.username && !url.password;
    } catch { return false; }
}
