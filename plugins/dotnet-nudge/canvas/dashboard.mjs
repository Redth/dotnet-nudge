const $ = id => document.getElementById(id);
const h = value => String(value ?? "").replace(/[&<>"']/g, char =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]);
const names = { package: "NuGet packages", sdk: "SDKs", workload: "Workloads", runtime: "Runtimes",
    toolchain: "Host tools", "agent-tools": "Skills & tools" };
const inCategory = (finding, category) => finding.category === category ||
    category === "sdk" && finding.category === "sdk-trial";
const versionArrow = '<svg class="icon version-arrow" viewBox="0 0 16 16" aria-hidden="true"><path d="M2.5 8h10m-4-4 4 4-4 4"/></svg>';
let model, area = "package", detailId, busy = false, visible = [];
let renderedDetailId;
let picker;
const expandedDetails = new Map();
const expandedCategories = new Map();
let renderedArea;
const pending = () => model?.requests.some(r => ["sending", "submitted", "working"].includes(r.status));
const canRequest = kind => model?.requestKinds ? model.requestKinds.includes(kind) : kind !== "helper";
const target = row => row.candidates.filter(c => c.kind !== "preview").at(-1) ?? row.candidates.at(-1);
const choice = row => model.choices[row.id] ?? { ...target(row), revision: row.revision, selected: false };
const selected = () => model.report.packages.filter(row => model.choices[row.id]?.selected);
const severity = row => Math.max(-1, ...row.advisories.map(a => a.severity));
export function versionDelta(current, target) {
    const parse = value => /^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([\w.-]+))?(?:\+([\w.-]+))?$/.exec(value);
    const from = parse(current), to = parse(target);
    if (!from || !to) return { kind: "unknown", segments: [{ value: target, changed: false, component: "unknown" }] };
    const components = ["major", "minor", "patch", "patch"];
    const segments = [];
    let kind;
    for (let index = 1; index <= 4; index++) {
        if (to[index] === undefined) continue;
        if (index > 1) segments.push({ value: ".", changed: false, component: "separator" });
        const changed = Number(from[index] ?? "0") !== Number(to[index]);
        if (changed && !kind) kind = components[index - 1];
        segments.push({ value: to[index], changed, component: components[index - 1] });
    }
    if (to[5]) segments.push({ value: `-${to[5]}`, changed: from[5] !== to[5], component: "preview" });
    if (from[5] !== to[5] && !kind) kind = to[5] ? "preview" : "stable";
    if (to[6]) segments.push({ value: `+${to[6]}`, changed: from[6] !== to[6], component: "metadata" });
    if (from[6] !== to[6] && !kind) kind = "metadata";
    return { kind: kind ?? "unchanged", segments };
}
function versionMarkup(current, target) {
    const delta = versionDelta(current, target);
    const label = { major: "Major version change", minor: "Minor version change", patch: "Patch version change",
        preview: "Prerelease change", stable: "Stable release", metadata: "Build metadata change",
        unchanged: "Same version", unknown: "Version" }[delta.kind];
    return `<code class="version-value" title="${label}">${delta.segments.map(segment =>
        `<span class="version-part ${segment.changed ? `changed-${segment.component}` : ""}">${h(segment.value)}</span>`).join("")}</code>`;
}
const filename = path => path.replaceAll("\\", "/").split("/").at(-1);
let projectLabels = new Map(), labeledReport;
function updateProjectLabels() {
    if (labeledReport === model.report) return;
    const projects = [...new Set(model.report.packages.map(row => row.project))];
    projectLabels = new Map(projects.map(path => {
        const parts = path.replaceAll("\\", "/").split("/");
        for (let depth = 1; depth <= parts.length; depth++) {
            const suffix = parts.slice(-depth).join("/");
            if (!projects.some(other => other !== path &&
                other.replaceAll("\\", "/").split("/").slice(-depth).join("/") === suffix))
                return [path, suffix.replace(/\.(cs|fs|vb)proj$/i, "")];
        }
        return [path, path];
    }));
    labeledReport = model.report;
}
const projectLabel = path => projectLabels.get(path) ?? filename(path);
const scopeLabel = row => [projectLabel(row.project), row.framework, row.runtimeIdentifier].filter(Boolean).join(" / ");
function packageLabel(name) {
    const split = name.lastIndexOf(".");
    if (split < 0) return `<span class="package-leaf">${h(name)}</span>`;
    return `<span class="package-prefix">${h(name.slice(0, split + 1)).replaceAll(".", ".<wbr>")}</span><span class="package-leaf">${h(name.slice(split + 1))}</span>`;
}
function sdkScopeLabel(scope) {
    const path = scope.replaceAll("\\", "/");
    const root = model.workspace.replaceAll("\\", "/");
    if (path.startsWith(root + "/")) return path.slice(root.length + 1);
    const directory = path.slice(0, path.lastIndexOf("/"));
    if (directory && root.startsWith(directory + "/")) return `${filename(path)} (inherited)`;
    return scope;
}
function packageSignals(row) {
    return (row.advisories.length ? `<span class="badge security">${h(["Low", "Moderate", "High", "Critical"][severity(row)] ?? "")} advisory</span>` : "") +
        (row.deprecations.length ? '<span class="badge">Deprecated</span>' : "") +
        (row.status !== "complete" ? '<span class="check-indicator" title="Some checks are incomplete; open details for evidence." aria-label="Incomplete checks"><svg class="icon" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="5.5"/><path d="M8 7v4M8 4.5v.5"/></svg></span>' : "");
}
const link = (label, value) => {
    try {
        const url = new URL(value);
        return ["https:", "http:"].includes(url.protocol) && !url.username && !url.password
            ? `<a href="${h(url.href)}" target="_blank" rel="noopener noreferrer">${h(label)}</a>` : "";
    } catch { return ""; }
};

async function load() {
    const response = await fetch("model");
    if (!response.ok) throw new Error("Cannot read the dashboard. Reopen it from chat.");
    const next = await response.json();
    if (!model || next.version >= model.version) model = next;
    render();
}
function fail(error) { $("error").textContent = error.message; $("error").hidden = false; }
async function post(action, input) {
    if (busy) return;
    const focused = document.activeElement?.id;
    busy = true;
    $("error").hidden = true;
    render();
    try {
        const response = await fetch(action, { method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ ...input, version: model.version }) });
        const body = await response.json();
        if (!response.ok) throw new Error(body.error ?? "The dashboard request failed.");
        if (body.version >= model.version) model = body;
    } catch (error) {
        fail(error);
        try { await load(); } catch (reloadError) { fail(reloadError); }
    } finally { busy = false; render(); if (focused) $(focused)?.focus({ preventScroll: true }); }
}
function changes(rows, select, securityOnly = false) {
    return rows.flatMap(row => {
        const candidate = securityOnly
            ? row.candidates.find(c => c.version === row.minimalKnownFix) ?? target(row)
            : choice(row).version ? choice(row) : target(row);
        return candidate ? [{ id: row.id, revision: row.revision, version: candidate.version,
            source: candidate.source, selected: select && (!securityOnly || row.advisories.length > 0) }] : [];
    });
}
function filteredRows() {
    const search = $("search").value.toLowerCase();
    const filter = $("filter").value;
    return model.report.packages.filter(row =>
        `${row.packageId} ${row.project} ${row.framework}`.toLowerCase().includes(search) &&
        (filter === "all" ||
        filter === "attention" && (row.candidates.length || row.advisories.length || row.deprecations.length || row.status !== "complete") ||
        filter === "security" && row.advisories.length ||
        filter === "direct" && row.direct || filter === "transitive" && !row.direct ||
        filter === "selected" && model.choices[row.id]?.selected)).sort((a, b) => {
            const sort = $("sort").value;
            const difference = sort === "security" ? severity(b) - severity(a) :
                sort === "project" ? a.project.localeCompare(b.project) :
                sort === "kind" ? (target(a)?.kind ?? "").localeCompare(target(b)?.kind ?? "") : 0;
            return difference || a.packageId.localeCompare(b.packageId) || a.project.localeCompare(b.project) ||
                a.framework.localeCompare(b.framework) || (a.runtimeIdentifier ?? "").localeCompare(b.runtimeIdentifier ?? "");
        });
}
function render() {
    if (!model) return;
    const previousDetails = $("details");
    if (renderedDetailId && previousDetails) expandedDetails.set(renderedDetailId, new Set(
        [...previousDetails.querySelectorAll("details[open]")].map(item => item.dataset.section)));
    updateProjectLabels();
    const focused = document.activeElement?.id;
    const total = model.report.packages.length;
    const vulnerabilities = model.report.packages.filter(row => row.advisories.length).length;
    $("notice").textContent = `${total} package entries${vulnerabilities ? ` / ${vulnerabilities} with advisories` : ""}`;
    $("coverage-indicator").hidden = model.report.complete;
    $("coverage").querySelector("summary").textContent = model.report.complete ? "About this report" : "Checks incomplete";
    $("coverage-content").innerHTML = `<p>${model.report.complete ? "Mapped metadata checks completed." :
        "Some packages could not be fully checked. Missing evidence does not mean they are up to date."}</p>
        <p>Workspace: ${h(model.workspace)}</p><p>Checked ${h(new Date(model.report.generatedAt).toLocaleString())}.
        Each row is one project / framework / runtime scope. Targets retain the latest patch, minor and major per source, plus known minimal fixes.
        Major versions may need migrations; static metadata is not restore/build validation.</p>` +
        model.report.coverage.map(item => `<p>${h(item.check)}: ${h(item.detail)}</p>`).join("");
    $("package-area").hidden = area !== "package";
    $("package-controls").hidden = area !== "package";
    $("category-area").hidden = area === "package";
    $("category-actions").hidden = area === "package";
    $("package-actions").hidden = area !== "package";
    for (const button of $("areas").querySelectorAll("button")) {
        if (button.dataset.area === area) button.setAttribute("aria-current", "page");
        else button.removeAttribute("aria-current");
    }
    visible = filteredRows();
    const groups = new Map();
    for (const row of visible) {
        const key = $("group").value === "project" ? row.project : $("group").value === "package" ? row.packageId : "";
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(row);
    }
    $("packages").setAttribute("aria-busy", String(busy));
    $("packages").innerHTML = visible.length ? [...groups].map(([name, rows]) => `<section class="group">
      ${name ? `<h3 title="${h(name)}">${h($("group").value === "project" ? projectLabel(name) : name)}</h3>` : ""}
      <div class="table-scroll"><table><thead><tr><th scope="col">
      <input type="checkbox" id="pick-group-${h(rows[0].id)}" data-select-group="${h(name)}" aria-label="Select updates in ${h(name || "the visible report")}" ${busy || !rows.some(row => row.candidates.length) ? "disabled" : ""}></th>
      <th scope="col">Package</th><th scope="col">Version</th></tr></thead>
      <tbody>${rows.map(row => renderRow(row) + (detailId === row.id ?
        '<tr class="package-details-row"><td colspan="3"><section id="details" class="package-details" aria-labelledby="details-title"></section></td></tr>' : "")).join("")}</tbody></table></div></section>`).join("") :
        `<div class="empty"><h3>${total ? "No matching package scopes" :
        model.report.complete ? "No NuGet dependencies" : "No restored packages to display"}</h3>
        <p>${total ? "Try a different search or view." : model.report.complete ?
        "The restored projects contain no NuGet packages." : "Open View options for report coverage."}</p></div>`;
    for (const checkbox of $("packages").querySelectorAll("[data-select-group]")) {
        const rows = groups.get(checkbox.dataset.selectGroup).filter(row => row.candidates.length);
        const picked = rows.filter(row => model.choices[row.id]?.selected).length;
        checkbox.checked = rows.length > 0 && picked === rows.length;
        checkbox.indeterminate = picked > 0 && picked < rows.length;
    }
    const count = selected().length;
    $("selection-count").textContent = count ? `${count} selected` : "Nothing selected";
    const hidden = selected().filter(row => !visible.some(v => v.id === row.id)).length;
    $("selection-note").textContent = `${hidden} hidden by filters`;
    $("selection-note").hidden = hidden === 0;
    $("plan").disabled = busy || pending() || count === 0 || !canRequest("plan");
    $("plan").textContent = pending() ? "Waiting for agent..." : "Plan selected updates";
    $("refresh").disabled = busy || pending() || !canRequest("refresh");
    for (const id of ["select-visible", "select-security", "clear"]) $(id).disabled = busy || total === 0;
    renderDetails();
    renderCategory();
    renderVersionPicker();
    $("activity-disclosure").hidden = model.requests.length === 0;
    const latest = model.requests.at(-1);
    if (latest) {
        $("activity-title").textContent = {
            sending: "Sending to agent...", submitted: "Sent to agent", working: "Agent working",
            completed: "Agent response ready", failed: "Agent request failed"
        }[latest.status] ?? "Agent activity";
        if (latest.status === "failed") $("activity-disclosure").open = true;
    }
    $("activity").innerHTML = model.requests.slice().reverse().map(request =>
        `<div class="request"><strong>${h({ plan: "Update plan", explain: "Package details", refresh: "Refresh", category: "Maintenance review", helper: "Helper review" }[request.kind])}
        - ${h(request.status)}</strong><p>${h(request.message ?? "Continue in chat.")}</p></div>`).join("");
    if (focused) $(focused)?.focus({ preventScroll: true });
}
function renderRow(row) {
    const currentChoice = choice(row);
    const disabled = busy || !row.candidates.length;
    const packageGroup = $("group").value === "package";
    const context = $("group").value === "workspace" ? scopeLabel(row) :
        [row.framework, row.runtimeIdentifier].filter(Boolean).join(" / ");
    return `<tr data-selected="${Boolean(currentChoice.selected)}"><td><input type="checkbox" id="pick-${h(row.id)}"
        data-pick="${h(row.id)}" aria-label="Select ${h(row.packageId)} in ${h(row.project)}, ${h(row.framework)}${row.runtimeIdentifier ? `, ${h(row.runtimeIdentifier)}` : ""}"
        ${currentChoice.selected ? "checked" : ""} ${disabled ? "disabled" : ""}></td>
        <td><div class="package-identity"><button class="package-name" id="details-${h(row.id)}" type="button" data-details="${h(row.id)}" title="${h(row.packageId)}"
        aria-label="Review ${h(row.packageId)} in ${h(row.project)}, ${h(row.framework)}${row.runtimeIdentifier ? `, ${h(row.runtimeIdentifier)}` : ""}"
        aria-expanded="${detailId === row.id}" ${detailId === row.id ? 'aria-controls="details"' : ""}>${packageGroup ? h(projectLabel(row.project)) : packageLabel(row.packageId)}
        <svg class="icon row-reveal" viewBox="0 0 16 16" aria-hidden="true"><path d="m6 4 4 4-4 4"/></svg></button>
        <span class="scope" title="${h(row.project)} / ${h(row.framework)} ${h(row.runtimeIdentifier)}">${h(context)}</span>
        ${packageSignals(row)}</div></td>
        <td><div class="version-change" role="group" aria-label="${h(row.current)} to ${h(currentChoice.version ?? "no known target")}">
        <span class="version-from"><code class="muted">${h(row.current)}</code>${versionArrow}</span>
        ${row.candidates.length > 1 ? `<button class="version-picker" id="target-${h(row.id)}" type="button" role="combobox"
        aria-label="Target version for ${h(row.packageId)} in ${h(row.project)}, ${h(row.framework)}; ${h(currentChoice.version)}, ${h(currentChoice.source)}"
        aria-haspopup="listbox" aria-expanded="${picker?.id === row.id}" aria-controls="version-options"
        data-version-picker="${h(row.id)}" ${busy ? "disabled" : ""}>${versionMarkup(row.current, currentChoice.version)}
        <svg class="icon picker-chevron" viewBox="0 0 16 16" aria-hidden="true"><path d="m5 6 3 3 3-3"/></svg></button>` :
        row.candidates.length ? versionMarkup(row.current, currentChoice.version) :
        `<span class="muted">${row.advisories.length ? "No fix yet" : "No target"}</span>`}</div></td></tr>`;
}
function closeVersionPicker() {
    if (!picker) return;
    const id = picker.id;
    picker = undefined;
    const menu = $("version-options");
    if (menu.matches(":popover-open")) menu.hidePopover();
    const trigger = $(`target-${id}`);
    trigger?.setAttribute("aria-expanded", "false");
    trigger?.removeAttribute("aria-activedescendant");
}
function openVersionPicker(id) {
    const row = model.report.packages.find(item => item.id === id);
    if (!row || row.candidates.length < 2 || busy) return;
    closeVersionPicker();
    const current = choice(row);
    picker = { id, revision: row.revision, index: Math.max(0, row.candidates.findIndex(candidate =>
        candidate.version === current.version && candidate.source === current.source)) };
    renderVersionPicker();
}
function renderVersionPicker() {
    if (!picker) return;
    const row = model.report.packages.find(item => item.id === picker.id);
    const trigger = $(`target-${picker.id}`);
    if (!row || row.revision !== picker.revision) {
        closeVersionPicker();
        fail(new Error("Package evidence changed. Reopen the picker before choosing a target."));
        return;
    }
    if (!trigger) { closeVersionPicker(); return; }
    const menu = $("version-options"), current = choice(row);
    const sources = new Set(row.candidates.map(candidate => candidate.source)).size > 1;
    picker.index = Math.min(picker.index, row.candidates.length - 1);
    menu.innerHTML = row.candidates.map((candidate, index) => {
        const selected = current.version === candidate.version && current.source === candidate.source;
        const delta = versionDelta(row.current, candidate.version);
        return `<div class="version-option ${picker.index === index ? "active" : ""}" role="option"
            id="version-option-${index}" aria-selected="${selected}" aria-label="${h(candidate.version)}, ${h(candidate.source)}, ${h(delta.kind)} change"
            data-version-index="${index}">${versionMarkup(row.current, candidate.version)}
            ${sources ? `<span class="option-source">${h(candidate.source)}</span>` : ""}
            ${selected ? '<svg class="icon option-check" viewBox="0 0 16 16" aria-hidden="true"><path d="m3 8 3 3 7-7"/></svg>' : ""}</div>`;
    }).join("");
    trigger.setAttribute("aria-expanded", "true");
    trigger.setAttribute("aria-activedescendant", `version-option-${picker.index}`);
    if (!menu.matches(":popover-open")) menu.showPopover();
    const rect = trigger.getBoundingClientRect();
    const width = Math.min(Math.max(180, rect.width), innerWidth - 16);
    const below = innerHeight - rect.bottom - 8;
    const height = Math.min(240, Math.max(below, rect.top - 8));
    menu.style.width = `${width}px`;
    menu.style.maxHeight = `${height}px`;
    menu.style.left = `${Math.max(8, Math.min(rect.left, innerWidth - width - 8))}px`;
    menu.style.top = `${below >= Math.min(240, menu.scrollHeight) ? rect.bottom + 4 :
        Math.max(8, rect.top - Math.min(menu.scrollHeight, height) - 4)}px`;
    menu.querySelector(".active")?.scrollIntoView({ block: "nearest" });
}
function commitVersionPicker() {
    if (!picker) return;
    const row = model.report.packages.find(item => item.id === picker.id);
    if (!row || row.revision !== picker.revision) {
        closeVersionPicker();
        fail(new Error("Package evidence changed. Reopen the picker before choosing a target."));
        return;
    }
    const candidate = row.candidates[picker.index], selected = choice(row).selected;
    closeVersionPicker();
    post("set_selection", { changes: [{ id: row.id, revision: row.revision, version: candidate.version,
        source: candidate.source, selected }] });
}
function renderDetails() {
    if (!$("details")) return;
    const row = model.report.packages.find(item => item.id === detailId);
    if (!row) return;
    const c = choice(row), candidate = row.candidates.find(item => item.version === c.version && item.source === c.source);
    const savedDetails = model.details[row.id];
    const details = savedDetails?.targetVersion === (candidate?.version ?? null) &&
        savedDetails?.targetSource === (candidate?.source ?? null) ? savedDetails : null;
    const expanded = expandedDetails.get(row.id) ?? new Set();
    const section = (id, title, content) => `<details data-section="${id}" ${expanded.has(id) ? "open" : ""}>
        <summary>${h(title)}</summary><div class="detail-content">${content}</div></details>`;
    renderedDetailId = row.id;
    $("details").innerHTML = `<button type="button" class="icon-button" id="close-details" aria-label="Close package details" title="Close package details">
      <svg class="icon" viewBox="0 0 16 16" aria-hidden="true"><path d="m4 4 8 8M12 4l-8 8"/></svg></button>
      <h2 id="details-title" tabindex="-1">${h(row.packageId)}</h2><p title="${h(row.project)}">${h(scopeLabel(row))}</p>
      <p class="detail-version version-change" role="group" aria-label="${h(row.current)} to ${h(candidate?.version ?? "no target")}">
      <span class="version-from"><code>${h(row.current)}</code>${versionArrow}</span><code>${h(candidate?.version ?? "no target")}</code></p>
      ${row.centrallyManaged ? "<p>Shared version: other projects may be affected.</p>" : ""}
      ${!row.direct ? "<p>Transitive: update through a direct parent.</p>" : ""}
      <button id="explain" type="button" ${busy || pending() || !canRequest("explain") ? "disabled" : ""}>Ask agent about this update</button>
      ${section("changes", "What's changed", details ?
        `<p class="evidence-text">${h(details.text)}</p><ul>${details.links.map(item => `<li>${link(item.label, item.url)}</li>`).join("")}</ul>` :
        "<p>Release notes have not been loaded. Ask the agent to investigate.</p>")}
      ${section("security", row.advisories.length ? `Security (${row.advisories.length} ${row.advisories.length === 1 ? "advisory" : "advisories"})` : "Security",
        `<p>${row.minimalKnownFix ? `Known fixed candidate: ${h(row.minimalKnownFix)}. Compatibility still needs validation.` :
        row.advisories.length ? "No known fix from the available evidence." :
        "No matching advisory was reported. Incomplete checks are not a clean bill of health."}</p>
        <ul>${row.advisories.map(a => `<li>${link("Read advisory", a.url)} - ${h(["low", "moderate", "high", "critical"][a.severity] ?? "unknown")} (${h(a.auditSource)})</li>`).join("")}</ul>`)}
      ${section("scope", "Scope and impact", `<p>Project: ${h(row.project)}</p><p>Framework: ${h(row.framework)}
        ${row.runtimeIdentifier ? ` / ${h(row.runtimeIdentifier)}` : ""}</p><p>Source: ${h(candidate?.source ?? "not identified")}</p>
        <p>${row.direct ? "Direct dependency." : "Transitive dependency; plan a direct-parent update, not a new direct reference."}</p>
        ${row.centrallyManaged ? "<p>Central version changes may affect unchecked projects. Those require separate approval.</p>" : ""}
        ${row.paths.length ? `<ul>${row.paths.map(path => `<li>${h(path.packageIds.join(" > "))}
        ${path.parentCandidate ? `; parent candidate ${h(path.directParent)} ${h(path.parentCandidate)} from ${h(path.parentSource)}` :
        "; no parent update established"}${path.diagnostic ? `. ${h(path.diagnostic)}` : ""}</li>`).join("")}</ul>` : ""}`)}
      ${row.deprecations.length ? section("deprecation", "Why deprecated?", row.deprecations.map(d =>
        `<p>${h(d.message ?? d.reasons.join(", "))}</p>${d.alternatePackageId ?
        `<p>Publisher suggests ${h(d.alternatePackageId)}; compatibility and source approval are unverified.</p>` : ""}`).join("")) : ""}
      ${section("evidence", "Links and technical evidence", `<p>${h(candidate?.diagnostic ?? "Candidate compatibility has not been established.")}</p>
        <p>${link("Publisher project / source site", candidate?.projectUrl) || "No publisher project link supplied."}</p>
        ${candidate?.description ? `<h3>Package description (not release notes)</h3><p>${h(candidate.description)}</p>` : ""}
        ${row.diagnostics.map(d => `<p>${h(d)}</p>`).join("")}`)}`;
}
const categoryFindings = () => (model.status?.findings ?? []).filter(item => inCategory(item.finding, area));
const categoryRequestKind = () => ["sdk", "agent-tools"].includes(area) || !categoryFindings().length ? "refresh" : "category";
const rowLabel = (title, context = "") => `<span class="summary-label"><span class="row-title">${h(title)}</span>
    ${context ? `<span class="row-context">${h(context)}</span>` : ""}</span>`;
function categoryDisclosure(id, summary, content) {
    return `<details class="maintenance-row" data-category-section="${h(id)}" ${expandedCategories.get(area)?.has(id) ? "open" : ""}>
        <summary><svg class="icon disclosure-chevron" viewBox="0 0 16 16" aria-hidden="true"><path d="m6 4 4 4-4 4"/></svg>
        ${summary}</summary><div class="maintenance-detail">${content}</div></details>`;
}
function versionPair(current, candidate) {
    return `<span class="version-change" role="group" aria-label="${h(current ?? "Not observed")} to ${h(candidate ?? "No candidate")}">
        ${current ? `<span class="version-from"><code class="muted">${h(current)}</code>${versionArrow}</span>` : ""}
        ${candidate ? versionMarkup(current, candidate) : '<span class="muted">No candidate</span>'}</span>`;
}
function findingEvidence(finding, suppression) {
    return `<p>${h(finding.assessment)} evidence${suppression ? `; reminder ${h(suppression)}` : ""}.
        Scope: ${h(finding.scope.project ?? finding.scope.sdkScope ?? "Workspace")}${finding.scope.framework ? ` / ${h(finding.scope.framework)}` : ""}.</p>
        <ul>${(finding.evidence ?? []).map(item => `<li>${link(item.type.replaceAll("-", " "), item.uri) ||
            `${h(item.type.replaceAll("-", " "))}: <code>${h(item.uri)}</code>`}</li>`).join("")}</ul>`;
}
function categoryCoverage(coverage) {
    const incomplete = !coverage.length || coverage.some(item => ["partial", "blocked", "unknown", "stale"].includes(item.assessment));
    return categoryDisclosure("coverage", rowLabel("Check details") +
        (incomplete ? '<span class="badge">Incomplete</span>' : ""),
        `${model.status?.scannedAt ? `<p>Checked ${h(new Date(model.status.scannedAt).toLocaleString())}.</p>` : ""}
        ${coverage.length ? coverage.map(item => `<p>${h(item.detail)}</p>`).join("") : "<p>No check coverage was recorded.</p>"}`);
}
function renderCategory() {
    if (renderedArea && renderedArea !== "package") expandedCategories.set(renderedArea, new Set(
        [...$("category-findings").querySelectorAll("details[open][data-category-section]")].map(item => item.dataset.categorySection)));
    renderedArea = area;
    if (area === "package") return;
    $("category-title").textContent = names[area];
    const findings = categoryFindings();
    $("review-category").textContent = area === "sdk" ? model.status?.sdks ? "Review SDKs with agent" : "Check SDKs with agent" :
        area === "agent-tools" ? "Review helper setup with agent" : `${findings.length ? "Review" : "Check"} ${names[area].toLowerCase()} with agent`;
    $("review-category").disabled = busy || pending() || !canRequest(categoryRequestKind());
    if (area === "sdk") { renderSdks(findings); return; }
    if (area === "agent-tools") { renderHelpers(); return; }
    const titles = {
        "workload-set-update": "Workload set update", "workload-set-difference": "Workload set mismatch",
        "workload-manifest-difference": "Workload manifest mismatch", "workload-missing": "Missing workload",
        "local-runtime-security-servicing": "Local runtime security update",
        "deployment-runtime-security-servicing": "Deployment runtime security update",
        "host-tool-incompatibility": "Host tool mismatch"
    };
    const notes = {
        workload: "Workload advice uses supplied offline inventory, not live discovery.",
        runtime: "Local runtime updates do not update deployed apps.",
        toolchain: "Host compatibility uses supplied inventory; process-local changes need a separate plan."
    };
    const coverage = (model.status?.coverage ?? []).filter(item =>
        ({ workload: /^(workload|toolchain-evidence|global-json|snapshot|scan)/,
            runtime: /^(runtime|dotnet-probe|snapshot|scan)/,
            toolchain: /^(host-|toolchain-evidence|workload-set|snapshot|scan)/ }[area]).test(item.check));
    $("category-findings").innerHTML = findings.length ? findings.map(({ finding, suppression }) =>
        categoryDisclosure(finding.id, rowLabel(titles[finding.kind] ?? finding.kind.replaceAll("-", " "),
            finding.scope.project ?? finding.scope.sdkScope ?? "Workspace") + versionPair(finding.current, finding.candidate),
            `<p>${notes[area]}</p>${findingEvidence(finding, suppression)}`)).join("") :
        `<div class="category-empty"><h3>No ${h(names[area].toLowerCase())} recommendations</h3>
        <p>${notes[area]} Review check details before assuming everything is current.</p></div>`;
    $("category-findings").innerHTML += categoryCoverage(coverage);
}
function renderHelpers() {
    const report = model.status?.agentTools;
    if (!report) {
        $("category-findings").innerHTML = '<div class="category-empty"><h3>Helpers have not been checked</h3><p>Ask the agent to review project fit and the installed setup.</p></div>';
        return;
    }
    const labels = { review: "Check setup", install: "Suggested", update: "Update available",
        installed: "Installed", disabled: "Disabled", alternative: "Alternative", "review-removal": "Review fit" };
    const actionable = report.recommendations.filter(item => ["review", "install", "update", "review-removal"].includes(item.action));
    const other = report.recommendations.filter(item => !actionable.includes(item));
    const renderHelper = item => categoryDisclosure(item.resource.id,
      rowLabel(item.resource.name, (item.reasons[0] ?? item.resource.description)
        .replace(/^[^:]+\.(?:csproj|fsproj|vbproj|props|razor|json):\s*/, "")
        .replace(" (declarations only; MSBuild conditions/imports were not executed).", ".")) +
      `<span class="badge">${h(item.assessment === "stale" ? "Cached catalog" :
            ["unknown", "blocked"].includes(item.assessment) ? "Check catalog" : labels[item.action] ?? "Review")}</span>`,
      `<p>${link(item.resource.repository, item.resource.url)}${item.resource.capabilities.length ?
        ` / ${h(item.resource.capabilities.map(value => ({ skills: "Skills", "language-server": "LSP", "mcp-server": "MCP" }[value] ?? value)).join(", "))}` : ""}</p>
      ${item.action === "update" ? `<p class="version-change"><code>${h(item.installedVersion ?? "Previous revision")}</code>
        ${item.installedVersion === item.resource.version ? '<span class="badge">Source changed</span>' :
            `${versionArrow}<code>${h(item.resource.version ?? "new revision")}</code>`}</p>` : ""}
        <p>${h(item.resource.description)}</p><ul>${item.reasons.map(reason => `<li>${h(reason)}</li>`).join("")}</ul>
        ${item.relevantSkills.length ? `<h3>Example skill usage</h3><ul>${item.relevantSkills.map(skill =>
            `<li>${link(skill.name, skill.url)}: ${h(skill.description)}</li>`).join("")}</ul>` : ""}
        <p>Version ${h(item.resource.version ?? "not declared")}. Source revision <code>${h(item.resource.revision || "no longer listed")}</code>.</p>
        <p>Relevance is not a trust or compatibility rating. Installation and activation need separate approval.</p>
      <button type="button" data-helper-id="${h(item.resource.id)}" data-helper-revision="${h(item.revision)}"
        ${busy || pending() || !model.requestKinds?.includes("helper") ? "disabled" : ""}
        ${!model.requestKinds?.includes("helper") ? 'title="Individual helper review is unavailable in this provider. Reopen after the host has admitted and loaded the current extension."' : ""}>Review with agent</button>`);
    $("category-findings").innerHTML = (actionable.length
        ? actionable.slice(0, 6).map(renderHelper).join("")
        : '<div class="category-empty"><h3>No new helper recommendations</h3><p>Review coverage before assuming the setup is complete.</p></div>') +
        (actionable.length > 6 || other.length ? categoryDisclosure("other-helpers", rowLabel("Other matching helpers"),
            [...actionable.slice(6), ...other].map(renderHelper).join("")) : "") +
        categoryDisclosure("coverage", rowLabel("Sources & installed inventory"),
        `<p>Installed inventory is separate from project relevance. Unverified helpers are not assumed missing.</p>
        <p>Catalog metadata is cached for 24 hours and reassessed at maintenance boundaries. No project context is sent to the repositories.</p>
        ${(report.sources ?? []).map(source => `<p>${h(source.repository)}: ${h(source.freshness)};
            ${source.retrievedAt ? `checked ${h(new Date(source.retrievedAt).toLocaleString())}` : "retrieval time unknown"}.</p>`).join("")}
        <p>${report.inventory ? `Host inventory: ${h(report.inventory.host)}, ${h(new Date(report.inventory.observedAt).toLocaleString())}.` :
            "Installed helpers have not been verified. Suggestions do not mean they are missing."}</p>
        ${report.coverage.map(item => `<p>${h(item.detail)}</p>`).join("")}`);
}
function renderSdks(findings) {
    const overview = model.status?.sdks;
    const coverage = (model.status?.coverage ?? []).filter(item =>
        /^(sdk|effective-sdk|installed-sdks|dotnet-probe|snapshot|scan)/.test(item.check));
    const rootSdk = overview?.effectiveSdks?.["."];
    const preview = version => version?.includes("-") ? '<span class="badge">Preview</span>' : "";
    const observed = Object.entries(overview?.effectiveSdks ?? {});
    const sdkTitles = {
        "sdk-servicing": "Servicing update",
        "sdk-major-trial": "Try a newer SDK",
        "sdk-preview-trial": "Try a preview SDK"
    };
    const stale = coverage.some(item => item.assessment === "stale");
    const gaps = coverage.filter(item => ["partial", "blocked", "unknown", "stale"].includes(item.assessment));
    $("category-findings").innerHTML = (overview ? `<table class="inventory-table"><thead><tr><th scope="col">SDK selection</th><th scope="col">Version</th></tr></thead><tbody>
        ${overview.declarations.length ? overview.declarations.map(pin =>
            `<tr><td>Requested SDK<span class="scope" title="${h(pin.scope)}">${h(sdkScopeLabel(pin.scope))}</span></td>
            <td><code>${h(pin.version ?? "No version specified")}</code> ${preview(pin.version)}</td></tr>`).join("") :
            '<tr><td>Requested SDK</td><td class="muted">No workspace pin</td></tr>'}
        <tr><td>Effective SDK<span class="scope">Workspace root</span></td><td><code>${h(rootSdk ?? "Not observed")}</code> ${preview(rootSdk)}</td></tr>
        </tbody></table>${stale ? '<p class="category-note">Cached information; a current check is needed.</p>' : ""}
        ${!rootSdk ? '<p class="category-note">Local SDK resolution was not established.</p>' : ""}
        ${categoryDisclosure("installed", rowLabel("Installed SDKs & resolution"), `<h3>Installed locally</h3>
            <pre><code>${h(overview.installedSdks?.trim() || "Installed SDKs were not established.")}</code></pre>
            ${observed.length ? `<h3>Effective by execution scope</h3><ul>${observed.map(([scope, sdk]) =>
                `<li><code>${h(sdk)}</code> - ${h(scope === "." ? "Workspace root" : scope)}</li>`).join("")}</ul>` : ""}
            ${overview.declarations.map(pin => `<p>${h(pin.scope)}: roll-forward ${h(pin.rollForward ?? "default")},
            preview policy ${h(typeof pin.allowPrerelease !== "boolean" ? "host-dependent" : pin.allowPrerelease ? "allowed" : "disabled")}.</p>`).join("")}`)}` :
        `<div class="category-empty"><h3>${model.status?.scannedAt ? "SDK inventory is not in this snapshot" :
            "SDKs have not been checked"}</h3><p>${model.status?.scannedAt ? "Refresh to read SDK inventory." :
            "Ask the agent to read the SDK pin, local selection and available releases."}</p></div>`) +
        findings.map(({ finding, suppression }) => {
            const security = finding.evidence?.some(item => item.type === "official-security-release");
            const trial = finding.category === "sdk-trial";
            return categoryDisclosure(finding.id, rowLabel(security ? "Security servicing update" :
                sdkTitles[finding.kind] ?? "SDK advice") + versionPair(finding.current, finding.candidate),
                `<p>${trial ? "Separate SDK-only trial; keep the production pin and target frameworks unchanged." :
                    "Same feature band as the requested SDK; check local resolution before changing the pin."}</p>
                ${security ? "<p>The upgrade path includes a security release; applicability to this app has not been established.</p>" : ""}
                ${findingEvidence(finding, suppression)}`);
        }).join("") +
        (overview && !findings.length ? `<p class="category-note">${gaps.length || !coverage.length ?
            "SDK update checks need attention; no update recommendation was established." :
            "No newer same-band SDK was identified in the available metadata."}</p>` : "") +
        categoryCoverage(coverage);
}
async function startDashboard() {
    $("search").addEventListener("input", render);
    for (const id of ["group", "filter", "sort"]) $(id).addEventListener("change", render);
    for (const [buttonId, panelId] of [["view-toggle", "view-options"], ["selection-toggle", "selection-options"]]) {
        $(buttonId).addEventListener("click", () => {
            const opening = $(panelId).hidden;
            for (const [otherButton, otherPanel] of [["view-toggle", "view-options"], ["selection-toggle", "selection-options"]]) {
                $(otherPanel).hidden = otherPanel === panelId ? !opening : true;
                $(otherButton).setAttribute("aria-expanded", String(otherPanel === panelId && opening));
            }
        });
    }
    $("areas").addEventListener("click", event => {
        const button = event.target.closest("[data-area]");
        if (button) { closeVersionPicker(); area = button.dataset.area; render(); $("content-scroll").scrollTop = 0; }
    });
    $("select-visible").addEventListener("click", () => post("set_selection", { changes: changes(visible, true) }));
    $("select-security").addEventListener("click", () => post("set_selection", { changes: changes(model.report.packages, true, true) }));
    $("clear").addEventListener("click", () => post("set_selection", { changes: changes(model.report.packages, false) }));
    $("refresh").addEventListener("click", () => post("request", { kind: "refresh" }));
    $("plan").addEventListener("click", () => post("request", { kind: "plan" }));
    $("review-category").addEventListener("click", () => post("request", {
        kind: categoryRequestKind(), category: area
    }));
    $("category-findings").addEventListener("click", event => {
        const button = event.target.closest("[data-helper-id]");
        if (button) post("request", { kind: "helper", id: button.dataset.helperId,
            revision: button.dataset.helperRevision });
    });
    $("packages").addEventListener("change", event => {
        if (event.target.hasAttribute("data-select-group")) {
            const key = event.target.dataset.selectGroup;
            const rows = visible.filter(row => $("group").value === "project" ? row.project === key :
                $("group").value === "package" ? row.packageId === key : true);
            post("set_selection", { changes: changes(rows, event.target.checked) });
            return;
        }
        const row = model.report.packages.find(item => item.id === event.target.dataset.pick);
        if (!row) return;
        const current = choice(row);
        post("set_selection", { changes: [{ id: row.id, revision: row.revision, version: current.version,
            source: current.source, selected: event.target.checked }] });
    });
    $("packages").addEventListener("click", event => {
        const versionButton = event.target.closest("[data-version-picker]");
        if (versionButton) {
            if (picker?.id === versionButton.dataset.versionPicker) closeVersionPicker();
            else openVersionPicker(versionButton.dataset.versionPicker);
            return;
        }
        const button = event.target.closest("[data-details]");
        if (button) {
            const prior = detailId;
            detailId = prior === button.dataset.details ? undefined : button.dataset.details;
            render();
            if (detailId) $("details-title")?.focus();
            else $(`details-${prior}`)?.focus();
        } else if (event.target.closest("#close-details")) {
            const prior = detailId; detailId = undefined; render(); $(`details-${prior}`)?.focus();
        } else if (event.target.closest("#explain")) post("request", { kind: "explain", id: detailId });
    });
    $("packages").addEventListener("keydown", event => {
        const trigger = event.target.closest("[data-version-picker]");
        if (!trigger) return;
        const keys = ["ArrowDown", "ArrowUp", "Home", "End", "Enter", " ", "Escape", "Tab"];
        if (!keys.includes(event.key)) return;
        if (event.key === "Tab") { closeVersionPicker(); return; }
        event.preventDefault();
        if (event.key === "Escape") { closeVersionPicker(); return; }
        const wasOpen = picker?.id === trigger.dataset.versionPicker;
        if (!wasOpen) openVersionPicker(trigger.dataset.versionPicker);
        if (!picker) return;
        const row = model.report.packages.find(item => item.id === picker.id);
        if (wasOpen && (event.key === "Enter" || event.key === " ")) { commitVersionPicker(); return; }
        if (event.key === "Home") picker.index = 0;
        else if (event.key === "End") picker.index = row.candidates.length - 1;
        else if (wasOpen && event.key === "ArrowDown") picker.index = Math.min(row.candidates.length - 1, picker.index + 1);
        else if (wasOpen && event.key === "ArrowUp") picker.index = Math.max(0, picker.index - 1);
        renderVersionPicker();
    });
    $("version-options").addEventListener("pointerdown", event => event.preventDefault());
    $("version-options").addEventListener("click", event => {
        const option = event.target.closest("[data-version-index]");
        if (option && picker) { picker.index = Number(option.dataset.versionIndex); commitVersionPicker(); }
    });
    document.addEventListener("pointerdown", event => {
        if (!event.target.closest("#version-options") && !event.target.closest("[data-version-picker]")) closeVersionPicker();
    });
    $("content-scroll").addEventListener("scroll", closeVersionPicker, { passive: true });
    window.addEventListener("resize", closeVersionPicker);
    await load().catch(fail);
    const events = new EventSource("events");
    events.onmessage = () => load().catch(fail);
    events.onerror = () => fail(new Error("Live updates disconnected. The view may be stale; reopen the dashboard if it does not reconnect."));
    events.onopen = () => { $("error").hidden = true; };
    window.addEventListener("pagehide", () => events.close());
}
if (typeof document !== "undefined") await startDashboard();
