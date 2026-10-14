from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import platform
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path, PurePosixPath
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
PLUGIN = ROOT / "plugins" / "dotnet-nudge"
SETUP_SPEC = importlib.util.spec_from_file_location(
    "dotnet_nudge_setup_runtime",
    PLUGIN / "scripts" / "setup-runtime.py",
)
if SETUP_SPEC is None or SETUP_SPEC.loader is None:
    raise RuntimeError("Could not load the runtime setup helper for contract tests.")
SETUP_RUNTIME = importlib.util.module_from_spec(SETUP_SPEC)
SETUP_SPEC.loader.exec_module(SETUP_RUNTIME)
HOOK_EVENTS = {
    "sessionStart": "session-start",
    "userPromptSubmitted": "prompt-submit",
    "postToolUse": "post-tool-use",
    "sessionEnd": "session-end",
}
CLAUDE_EVENTS = {
    "SessionStart": ("session-start", "SessionStart"),
    "UserPromptSubmit": ("prompt-submit", "UserPromptSubmit"),
    "PostToolUse": ("post-tool-use", "PostToolUse"),
    "SessionEnd": ("session-end", "SessionEnd"),
}
SETUP_DIAGNOSTIC = (
    "dotnet-nudge: runtime setup is required. Use nudge-maintain for local setup guidance; "
    "no runtime was downloaded or run."
)


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def resolve_contained(root: Path, relative_path: str) -> Path:
    path = PurePosixPath(relative_path)
    if path.is_absolute() or ".." in path.parts or "\\" in relative_path:
        raise AssertionError(f"Unsafe plugin-relative path: {relative_path}")
    resolved_root = root.resolve()
    resolved = (root / Path(*path.parts)).resolve()
    if not resolved.is_relative_to(resolved_root):
        raise AssertionError(f"Path escapes plugin root: {relative_path}")
    return resolved


def expected_rid() -> str:
    system = {"Darwin": "osx", "Linux": "linux", "Windows": "win"}.get(platform.system())
    machine = {
        "x86_64": "x64",
        "AMD64": "x64",
        "aarch64": "arm64",
        "arm64": "arm64",
    }.get(platform.machine())
    if not system or not machine:
        raise RuntimeError(f"Unsupported test host: {platform.system()} {platform.machine()}")
    return f"{system}-{machine}"


def powershell_executable() -> str | None:
    return shutil.which("pwsh") or shutil.which("powershell")


class PluginManifestTests(unittest.TestCase):
    def test_marketplace_and_dual_host_manifests(self) -> None:
        marketplace = read_json(ROOT / ".claude-plugin" / "marketplace.json")
        self.assertEqual(set(marketplace), {"name", "owner", "metadata", "plugins"})
        self.assertEqual(marketplace["name"], "dotnet-nudge-marketplace")
        self.assertEqual(marketplace["owner"], {"name": "Redth"})
        self.assertEqual(set(marketplace["metadata"]), {"description", "version"})
        self.assertEqual(len(marketplace["plugins"]), 1)
        entry = marketplace["plugins"][0]
        self.assertEqual(set(entry), {"name", "description", "version", "source"})
        self.assertEqual(entry["name"], "dotnet-nudge")
        self.assertEqual(entry["version"], marketplace["metadata"]["version"])
        self.assertEqual(entry["source"], "./plugins/dotnet-nudge")
        plugin_dir = resolve_contained(ROOT, entry["source"])
        self.assertTrue(plugin_dir.is_dir())

        portable = read_json(plugin_dir / "plugin.json")
        self.assertEqual(
            portable,
            {
                "$schema": "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
                "name": "dotnet-nudge",
                "version": "0.1.0",
                "description": "A .NET maintenance plugin with shared skills and host-specific hooks; engine actions remain explicit.",
                "author": {"name": "Redth"},
                "repository": "https://github.com/Redth/dotnet-nudge",
                "license": "MIT",
                "keywords": ["dotnet", "maintenance", "nuget"],
            },
        )
        self.assertEqual(
            set(portable),
            {
                "$schema",
                "name",
                "version",
                "description",
                "author",
                "repository",
                "license",
                "keywords",
            },
        )

        claude = read_json(plugin_dir / ".claude-plugin" / "plugin.json")
        self.assertEqual(
            claude,
            {
                "name": portable["name"],
                "version": portable["version"],
                "description": portable["description"],
                "author": {"name": "Redth"},
                "repository": portable["repository"],
                "license": portable["license"],
                "keywords": portable["keywords"],
            },
        )
        self.assertNotIn("hooks", claude)
        self.assertNotIn("mcpServers", claude)
        portable_mcp = read_json(plugin_dir / "mcp.json")
        native_mcp = read_json(plugin_dir / ".mcp.json")
        self.assertEqual(portable_mcp["$schema"], "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json")
        for config, placeholder in ((portable_mcp, "${PLUGIN_DATA}"),
                                    (native_mcp, "${CLAUDE_PLUGIN_DATA}")):
            self.assertEqual(set(config["mcpServers"]), {"dotnet-nudge"})
            server = config["mcpServers"]["dotnet-nudge"]
            self.assertEqual(server["type"], "stdio")
            self.assertEqual(server["command"], "dotnet-nudge")
            self.assertEqual(server["args"], ["mcp"])
            self.assertEqual(server["env"], {"NUDGE_PLUGIN_DATA": placeholder})

        runtime = read_json(plugin_dir / "runtime-manifest.json")
        self.assertEqual(runtime["status"], "unpublished")
        self.assertEqual(runtime["artifacts"], [])
        self.assertNotIn("url", runtime)
        self.assertNotIn("sha256", runtime)

        for skill_name in (
            "nudge-status",
            "nudge-maintain",
            "nudge-trial",
            "nudge-preferences",
        ):
            skill_dir = resolve_contained(plugin_dir, f"skills/{skill_name}")
            skill = skill_dir / "SKILL.md"
            self.assertTrue(skill.is_file())
            content = skill.read_text(encoding="utf-8")
            for heading in (
                "## Read-only assessment",
                "## User approval",
                "## Structured decisions",
                "## Supported and not yet implemented",
            ):
                self.assertIn(heading, content)

        status_skill = (plugin_dir / "skills/nudge-status/SKILL.md").read_text(encoding="utf-8")
        for command in (
            "dotnet-nudge version",
            "dotnet-nudge doctor [--workspace PATH] [--state-dir PATH] --json",
            "dotnet-nudge scan --workspace PATH [--state-dir PATH] --json",
            "dotnet-nudge status --workspace PATH [--state-dir PATH] --json",
        ):
            self.assertIn(command, status_skill)

        decision_skill = (plugin_dir / "skills/nudge-preferences/SKILL.md").read_text(encoding="utf-8")
        self.assertIn(
            "snooze|skip-version|disable-category|enable-category",
            decision_skill,
        )
        trial_skill = (plugin_dir / "skills/nudge-trial/SKILL.md").read_text(encoding="utf-8")
        self.assertIn("dotnet-nudge trial root --workspace PATH --json", trial_skill)
        self.assertIn("exact revision", trial_skill)
        self.assertIn("not an OS sandbox", trial_skill)

    def test_one_hook_configuration_and_exact_host_event_mapping(self) -> None:
        copilot = read_json(PLUGIN / "com.github.copilot/hooks/hooks.json")
        self.assertEqual(copilot["version"], 1)
        self.assertEqual(set(copilot["hooks"]), set(HOOK_EVENTS))
        for host_event, event_arg in HOOK_EVENTS.items():
            entries = copilot["hooks"][host_event]
            self.assertEqual(len(entries), 1)
            hook = entries[0]
            self.assertEqual(hook["type"], "command")
            self.assertEqual(
                hook["bash"].split()[-2:],
                ["copilot", event_arg],
            )
            self.assertEqual(
                hook["powershell"].split()[-2:],
                ["copilot", event_arg],
            )
            self.assertIn("${PLUGIN_ROOT}/scripts/hook.sh", hook["bash"])
            self.assertIn("$env:PLUGIN_ROOT/scripts/hook.ps1", hook["powershell"])

        claude = read_json(PLUGIN / "hooks/hooks.json")
        self.assertEqual(set(claude["hooks"]), set(CLAUDE_EVENTS))
        for event, (event_arg, hook_event_name) in CLAUDE_EVENTS.items():
            entries = claude["hooks"][event]
            self.assertEqual(len(entries), 1)
            hooks = entries[0]["hooks"]
            self.assertEqual(len(hooks), 1)
            hook = hooks[0]
            self.assertEqual(hook["type"], "command")
            self.assertEqual(
                hook["command"].split()[-2:],
                ["claude", event_arg],
            )
            self.assertIn("${CLAUDE_PLUGIN_ROOT}/scripts/hook.sh", hook["command"])
            self.assertEqual(hook_event_name, event)

        self.assertFalse((PLUGIN / "hooks.json").exists())
        self.assertFalse((PLUGIN / "com.github.copilot/hooks.json").exists())

    def test_marketplace_source_rejects_escape_paths(self) -> None:
        for path in ("../outside", "/absolute/plugin", "plugins/../../outside", "plugins\\outside"):
            with self.subTest(path=path):
                with self.assertRaises(AssertionError):
                    resolve_contained(ROOT, path)


class LauncherTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="dotnet-nudge contracts ")
        self.addCleanup(self.temp.cleanup)
        self.temp_root = Path(self.temp.name)
        self.plugin_copy = self.temp_root / "plugin root with spaces"
        shutil.copytree(PLUGIN, self.plugin_copy)
        self.cwd = self.temp_root / "working directory with spaces"
        self.cwd.mkdir()
        self.state = self.temp_root / "state directory with spaces"
        self.capture = self.temp_root / "captured stdin.json"
        self.args_capture = self.temp_root / "captured args.json"
        self.state_capture = self.temp_root / "captured state directory.txt"
        self.fixture = self.cwd / "hook"
        self.fixture.write_text(
            "#!/usr/bin/env python3\n"
            "import json, os, sys\n"
            "payload = sys.stdin.buffer.read()\n"
            "with open(os.environ['FIXTURE_CAPTURE'], 'wb') as stream:\n"
            "    stream.write(payload)\n"
            "with open(os.environ['FIXTURE_ARGS'], 'w', encoding='utf-8') as stream:\n"
            "    json.dump([os.path.basename(sys.argv[0]), *sys.argv[1:]], stream)\n"
            "with open(os.environ['FIXTURE_STATE_CAPTURE'], 'w', encoding='utf-8') as stream:\n"
            "    stream.write(os.environ.get('NUDGE_STATE_DIR', ''))\n"
            "sys.stdout.buffer.write(os.environ['FIXTURE_STDOUT'].encode('utf-8'))\n"
            "sys.stderr.buffer.write(os.environ['FIXTURE_STDERR'].encode('utf-8'))\n"
            "raise SystemExit(int(os.environ['FIXTURE_EXIT']))\n",
            encoding="utf-8",
        )
        self.fixture.chmod(0o755)
        self.input = b'{"event":"payload with spaces","text":"caf\xc3\xa9"}\n'
        self.stdout = '{"fixture":"stdout stays unchanged"}\n'
        self.stderr = "fixture diagnostic on stderr\n"

    def base_env(self) -> dict[str, str]:
        env = os.environ.copy()
        env.update(
            {
                "NUDGE_ENGINE_PATH": sys.executable,
                "NUDGE_STATE_DIR": str(self.state),
                "FIXTURE_CAPTURE": str(self.capture),
                "FIXTURE_ARGS": str(self.args_capture),
                "FIXTURE_STATE_CAPTURE": str(self.state_capture),
                "FIXTURE_PYTHON": sys.executable,
                "FIXTURE_SCRIPT": str(self.fixture),
                "FIXTURE_STDOUT": self.stdout,
                "FIXTURE_STDERR": self.stderr,
                "FIXTURE_EXIT": "23",
            }
        )
        for name in ("COPILOT_PLUGIN_DATA", "CLAUDE_PLUGIN_DATA"):
            env.pop(name, None)
        return env

    def run_bash(self, host: str, event: str, env: dict[str, str]) -> subprocess.CompletedProcess:
        bash = shutil.which("bash")
        if not bash:
            self.skipTest("Bash is unavailable on this host.")
        script = self.plugin_copy / "scripts" / "hook.sh"
        return subprocess.run(
            [bash, str(script), host, event],
            input=self.input,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=self.cwd,
            env=env,
            check=False,
        )

    def run_powershell(
        self, host: str, event: str, env: dict[str, str]
    ) -> subprocess.CompletedProcess:
        powershell = powershell_executable()
        if not powershell:
            self.skipTest("PowerShell is unavailable on this host.")
        script = self.plugin_copy / "scripts" / "hook.ps1"
        return subprocess.run(
            [
                powershell,
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-File",
                str(script),
                host,
                event,
            ],
            input=self.input,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=self.cwd,
            env=env,
            check=False,
        )

    def assert_fixture_passthrough(
        self,
        result: subprocess.CompletedProcess,
        host: str,
        event: str,
        state_dir: Path | None = None,
    ) -> None:
        self.assertEqual(result.returncode, 23, result.stderr.decode(errors="replace"))
        self.assertEqual(result.stdout, self.stdout.encode())
        self.assertEqual(result.stderr, self.stderr.encode())
        self.assertEqual(self.capture.read_bytes(), self.input)
        self.assertEqual(self.state_capture.read_text(encoding="utf-8"), str(state_dir or self.state))
        self.assertEqual(
            json.loads(self.args_capture.read_text(encoding="utf-8")),
            ["hook", "--host", host, "--event", event],
        )

    def test_bash_launcher_preserves_arguments_stdin_stdout_stderr_and_exit_status(self) -> None:
        engine = self.temp_root / "engine executable with spaces"
        engine.symlink_to(sys.executable)
        env = self.base_env()
        env["NUDGE_ENGINE_PATH"] = str(engine)
        result = self.run_bash("copilot", "post-tool-use", env)
        self.assert_fixture_passthrough(result, "copilot", "post-tool-use")

    def test_powershell_launcher_preserves_arguments_stdin_stdout_stderr_and_exit_status(self) -> None:
        result = self.run_powershell("claude", "prompt-submit", self.base_env())
        self.assert_fixture_passthrough(result, "claude", "prompt-submit")

    def test_plugin_data_directory_is_passed_to_the_engine(self) -> None:
        launchers = (
            (self.run_bash, "copilot", "COPILOT_PLUGIN_DATA"),
            (self.run_bash, "claude", "CLAUDE_PLUGIN_DATA"),
            (self.run_powershell, "copilot", "COPILOT_PLUGIN_DATA"),
            (self.run_powershell, "claude", "CLAUDE_PLUGIN_DATA"),
        )
        for launcher, host, data_variable in launchers:
            with self.subTest(launcher=launcher.__name__, host=host, data_variable=data_variable):
                selected_data = self.temp_root / f"{data_variable.lower()} state"
                env = self.base_env()
                env.pop("NUDGE_STATE_DIR")
                env[data_variable] = str(selected_data)
                result = launcher(host, "session-start", env)
                self.assert_fixture_passthrough(
                    result,
                    host,
                    "session-start",
                    selected_data,
                )

    def test_explicit_state_directory_overrides_plugin_data_for_the_engine(self) -> None:
        for launcher, host in (
            (self.run_bash, "copilot"),
            (self.run_powershell, "claude"),
        ):
            with self.subTest(launcher=launcher.__name__, host=host):
                selected_data = self.temp_root / "explicit override state"
                env = self.base_env()
                env["NUDGE_STATE_DIR"] = str(selected_data)
                env["COPILOT_PLUGIN_DATA"] = str(self.temp_root / "different plugin state")
                result = launcher(host, "session-start", env)
                self.assert_fixture_passthrough(
                    result,
                    host,
                    "session-start",
                    selected_data,
                )

    def test_missing_engine_emits_one_host_correct_notice_and_uses_data_state(self) -> None:
        for launcher, host, event, expected in (
            (
                self.run_bash,
                "copilot",
                "session-start",
                {"additionalContext": SETUP_DIAGNOSTIC},
            ),
            (
                self.run_powershell,
                "claude",
                "session-start",
                {
                    "hookSpecificOutput": {
                        "hookEventName": "SessionStart",
                        "additionalContext": SETUP_DIAGNOSTIC,
                    }
                },
            ),
        ):
            with self.subTest(host=host, launcher=launcher.__name__):
                state = self.temp_root / f"once-only state {host}"
                env = os.environ.copy()
                env.pop("NUDGE_ENGINE_PATH", None)
                env.pop("COPILOT_PLUGIN_DATA", None)
                env.pop("CLAUDE_PLUGIN_DATA", None)
                env["NUDGE_STATE_DIR"] = str(state)
                first = launcher(host, event, env)
                second = launcher(host, event, env)
                self.assertEqual(first.returncode, 0, first.stderr.decode(errors="replace"))
                self.assertEqual(second.returncode, 0, second.stderr.decode(errors="replace"))
                self.assertEqual(json.loads(first.stdout), expected)
                self.assertEqual(first.stderr, b"")
                self.assertEqual(second.stdout, b"")
                self.assertTrue((state / ".setup-required-notice-shown").is_file())
                self.assertFalse(
                    (self.plugin_copy / ".setup-required-notice-shown").exists(),
                    "Setup state must not be written into the plugin cache.",
                )

    def test_invalid_state_directory_reports_error_without_hook_stdout(self) -> None:
        for launcher, host in (
            (self.run_bash, "copilot"),
            (self.run_powershell, "claude"),
        ):
            with self.subTest(launcher=launcher.__name__, host=host):
                invalid_state = self.temp_root / "state path is a file"
                invalid_state.write_text("not a directory", encoding="utf-8")
                env = self.base_env()
                env.pop("NUDGE_ENGINE_PATH")
                env["NUDGE_STATE_DIR"] = str(invalid_state)
                result = launcher(host, "session-start", env)
                self.assertEqual(result.returncode, 0)
                self.assertEqual(result.stdout, b"")
                self.assertIn(b"unable to persist the one-time setup notice", result.stderr)

    @unittest.skipIf(os.name == "nt", "Bash is not the native Windows launcher")
    def test_bash_resolves_a_versioned_engine_from_user_data(self) -> None:
        versioned_engine_dir = self.state / "engine" / "1.2.3" / expected_rid()
        versioned_engine_dir.mkdir(parents=True)
        (self.state / "engine" / "current").write_text(
            f"1.2.3/{expected_rid()}\n",
            encoding="utf-8",
        )
        engine = versioned_engine_dir / "dotnet-nudge"
        engine.write_text(
            '#!/bin/sh\n[ "$1" = hook ] || exit 88\nshift\n'
            'exec "$FIXTURE_PYTHON" "$FIXTURE_SCRIPT" "$@"\n',
            encoding="utf-8",
        )
        engine.chmod(0o755)
        env = self.base_env()
        env.pop("NUDGE_ENGINE_PATH")
        result = self.run_bash("copilot", "session-start", env)
        self.assert_fixture_passthrough(result, "copilot", "session-start")

    @unittest.skipIf(os.name == "nt", "Bash is not the native Windows launcher")
    def test_bash_rejects_installed_engine_leaf_symlink(self) -> None:
        versioned_engine_dir = self.state / "engine" / "1.2.3" / expected_rid()
        versioned_engine_dir.mkdir(parents=True)
        (self.state / "engine" / "current").write_text(
            f"1.2.3/{expected_rid()}\n",
            encoding="utf-8",
        )
        (versioned_engine_dir / "dotnet-nudge").symlink_to(self.fixture)
        env = self.base_env()
        env.pop("NUDGE_ENGINE_PATH")
        result = self.run_bash("copilot", "session-start", env)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout), {"additionalContext": SETUP_DIAGNOSTIC})
        self.assertEqual(result.stderr, b"")
        self.assertFalse(self.capture.exists(), "A symlinked engine must not be executed.")

    @unittest.skipIf(os.name == "nt", "Bash is not the native Windows launcher")
    def test_bash_rejects_installed_engine_directory_symlink(self) -> None:
        engine_root = self.state / "engine"
        engine_root.mkdir(parents=True)
        external_version = self.temp_root / "external version"
        external_engine_dir = external_version / expected_rid()
        external_engine_dir.mkdir(parents=True)
        external_engine = external_engine_dir / "dotnet-nudge"
        shutil.copy2(self.fixture, external_engine)
        external_engine.chmod(0o755)
        (engine_root / "1.2.3").symlink_to(external_version, target_is_directory=True)
        (engine_root / "current").write_text(
            f"1.2.3/{expected_rid()}\n",
            encoding="utf-8",
        )
        env = self.base_env()
        env.pop("NUDGE_ENGINE_PATH")
        result = self.run_bash("copilot", "session-start", env)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout), {"additionalContext": SETUP_DIAGNOSTIC})
        self.assertEqual(result.stderr, b"")
        self.assertFalse(self.capture.exists(), "An engine behind a directory symlink must not run.")

    @unittest.skipUnless(os.name == "nt", "PowerShell is the native Windows launcher")
    def test_powershell_resolves_a_versioned_engine_from_user_data(self) -> None:
        versioned_engine_dir = self.state / "engine" / "1.2.3" / expected_rid()
        versioned_engine_dir.mkdir(parents=True)
        (self.state / "engine" / "current").write_text(
            f"1.2.3/{expected_rid()}\n",
            encoding="utf-8",
        )
        shutil.copy2(sys.executable, versioned_engine_dir / "dotnet-nudge.exe")
        env = self.base_env()
        env.pop("NUDGE_ENGINE_PATH")
        result = self.run_powershell("copilot", "session-start", env)
        self.assert_fixture_passthrough(result, "copilot", "session-start")

    @unittest.skipUnless(os.name == "nt", "PowerShell reparse-point checks require Windows")
    def test_powershell_rejects_installed_engine_leaf_symlink(self) -> None:
        versioned_engine_dir = self.state / "engine" / "1.2.3" / expected_rid()
        versioned_engine_dir.mkdir(parents=True)
        (self.state / "engine" / "current").write_text(
            f"1.2.3/{expected_rid()}\n",
            encoding="utf-8",
        )
        try:
            (versioned_engine_dir / "dotnet-nudge.exe").symlink_to(sys.executable)
        except OSError as error:
            self.skipTest(f"Windows symlink privilege is unavailable: {error}")
        env = self.base_env()
        env.pop("NUDGE_ENGINE_PATH")
        result = self.run_powershell("copilot", "session-start", env)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout), {"additionalContext": SETUP_DIAGNOSTIC})
        self.assertEqual(result.stderr, b"")
        self.assertFalse(self.capture.exists(), "A symlinked engine must not be executed.")

    @unittest.skipUnless(os.name == "nt", "PowerShell reparse-point checks require Windows")
    def test_powershell_rejects_installed_engine_directory_junction(self) -> None:
        engine_root = self.state / "engine"
        engine_root.mkdir(parents=True)
        external_version = self.temp_root / "external version"
        external_engine_dir = external_version / expected_rid()
        external_engine_dir.mkdir(parents=True)
        shutil.copy2(sys.executable, external_engine_dir / "dotnet-nudge.exe")
        version_link = engine_root / "1.2.3"
        powershell = powershell_executable()
        if not powershell:
            self.fail("PowerShell is required to create a Windows directory junction.")
        env = os.environ.copy()
        env["NUDGE_JUNCTION_LINK"] = str(version_link)
        env["NUDGE_JUNCTION_TARGET"] = str(external_version)
        result = subprocess.run(
            [
                powershell,
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "New-Item -ItemType Junction -Path $env:NUDGE_JUNCTION_LINK "
                "-Target $env:NUDGE_JUNCTION_TARGET | Out-Null",
            ],
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertEqual(
            result.returncode,
            0,
            result.stdout.decode(errors="replace") + result.stderr.decode(errors="replace"),
        )
        self.assertTrue(version_link.is_dir(), "PowerShell did not create the junction.")
        (engine_root / "current").write_text(
            f"1.2.3/{expected_rid()}\n",
            encoding="utf-8",
        )
        env = self.base_env()
        env.pop("NUDGE_ENGINE_PATH")
        result = self.run_powershell("copilot", "session-start", env)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout), {"additionalContext": SETUP_DIAGNOSTIC})
        self.assertEqual(result.stderr, b"")
        self.assertFalse(self.capture.exists(), "An engine behind a junction must not run.")

    def test_current_rid_maps_each_advertised_platform_and_architecture(self) -> None:
        for system, machine, expected in (
            ("Linux", "x86_64", "linux-x64"),
            ("Linux", "aarch64", "linux-arm64"),
            ("Darwin", "x86_64", "osx-x64"),
            ("Darwin", "arm64", "osx-arm64"),
            ("Windows", "AMD64", "win-x64"),
            ("Windows", "ARM64", "win-arm64"),
        ):
            with self.subTest(system=system, machine=machine):
                with (
                    patch.object(SETUP_RUNTIME.platform, "system", return_value=system),
                    patch.object(SETUP_RUNTIME.platform, "machine", return_value=machine),
                ):
                    self.assertEqual(SETUP_RUNTIME.current_rid(), expected)


class RuntimeSetupTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="dotnet-nudge setup ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.archive = self.root / "release archive with spaces.zip"
        self.state = self.root / "user data with spaces"
        self.rid = expected_rid()
        self.exe_name = "dotnet-nudge.exe" if self.rid.startswith("win-") else "dotnet-nudge"
        self.executable_bytes = b"fixture engine bytes; installer never executes this file\n"

    def make_zip(
        self,
        member: str = "dotnet-nudge",
        content: bytes | None = None,
        extra_files: dict[str, bytes] | None = None,
    ) -> None:
        with zipfile.ZipFile(self.archive, "w") as package:
            package.writestr(member, content if content is not None else self.executable_bytes)
            for path, contents in (extra_files or {}).items():
                package.writestr(path, contents)

    def make_tar(self, member: str = "dotnet-nudge", extra_files: dict[str, bytes] | None = None) -> None:
        with tarfile.open(self.archive, "w:gz") as package:
            info = tarfile.TarInfo(member)
            info.size = len(self.executable_bytes)
            package.addfile(info, io_bytes(self.executable_bytes))
            for path, contents in (extra_files or {}).items():
                info = tarfile.TarInfo(path)
                info.size = len(contents)
                package.addfile(info, io_bytes(contents))

    def setup_args(self) -> list[str]:
        digest = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        return [
            sys.executable,
            str(PLUGIN / "scripts/setup-runtime.py"),
            "--archive",
            str(self.archive),
            "--version",
            "1.2.3",
            "--rid",
            self.rid,
            "--sha256",
            digest,
            "--state-dir",
            str(self.state),
        ]

    def run_setup(self, *extra: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [*self.setup_args(), *extra],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )

    def test_checksum_and_install_location_are_pinned(self) -> None:
        self.make_zip(self.exe_name)
        result = self.run_setup()
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        installed_dir = self.state / "engine" / "1.2.3" / self.rid
        installed = installed_dir / self.exe_name
        self.assertEqual(installed.read_bytes(), self.executable_bytes)
        self.assertTrue(installed.resolve().is_relative_to(self.state.resolve()))
        metadata = read_json(installed_dir / "runtime.json")
        self.assertEqual(metadata["engine"], "dotnet-nudge")
        self.assertEqual(metadata["version"], "1.2.3")
        self.assertEqual(metadata["rid"], self.rid)
        self.assertEqual(metadata["sha256"], hashlib.sha256(self.archive.read_bytes()).hexdigest())
        self.assertIn(self.exe_name, metadata["files"])
        self.assertEqual(
            (self.state / "engine" / "current").read_text(encoding="utf-8"),
            f"1.2.3/{self.rid}\n",
        )
        if not self.rid.startswith("win-"):
            self.assertTrue(installed.stat().st_mode & stat.S_IXUSR)

    def test_tar_archive_is_supported(self) -> None:
        self.archive = self.root / "local engine.tar.gz"
        dependency = "runtimes/native/libe_sqlite3.so"
        self.make_tar(self.exe_name, {dependency: b"native sqlite fixture"})
        result = self.run_setup()
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        installed_dir = self.state / "engine" / "1.2.3" / self.rid
        self.assertEqual((installed_dir / self.exe_name).read_bytes(), self.executable_bytes)
        self.assertEqual((installed_dir / dependency).read_bytes(), b"native sqlite fixture")
        self.assertEqual(
            (self.state / "engine" / "current").read_text(encoding="utf-8"),
            f"1.2.3/{self.rid}\n",
        )

    def test_bad_checksum_and_traversal_are_rejected_without_install(self) -> None:
        self.make_zip(self.exe_name)
        bad_checksum = self.run_setup("--sha256", "0" * 64)
        self.assertNotEqual(bad_checksum.returncode, 0)
        self.assertFalse((self.state / "engine" / "1.2.3" / self.rid / self.exe_name).exists())

        self.make_zip(f"../{self.exe_name}")
        traversal = self.run_setup()
        self.assertNotEqual(traversal.returncode, 0)
        self.assertIn(b"unsafe path", traversal.stderr)
        self.assertFalse((self.root / self.exe_name).exists())
        self.assertFalse((self.state / "engine" / "1.2.3" / self.rid / self.exe_name).exists())

    def test_wrong_executable_identity_and_rid_are_rejected(self) -> None:
        wrong_name = "dotnet-nudge" if self.exe_name.endswith(".exe") else "other-engine"
        self.make_zip(wrong_name)
        wrong_identity = self.run_setup()
        self.assertNotEqual(wrong_identity.returncode, 0)
        self.assertFalse((self.state / "engine" / "1.2.3" / self.rid / self.exe_name).exists())

        self.make_zip(self.exe_name)
        wrong_rid = "win-x64" if not self.rid.startswith("win-") else "linux-x64"
        wrong_platform = self.run_setup("--rid", wrong_rid)
        self.assertNotEqual(wrong_platform.returncode, 0)
        self.assertFalse((self.state / "engine" / "1.2.3" / self.rid / self.exe_name).exists())

    def test_zip_archive_preserves_native_runtime_files(self) -> None:
        dependency = f"runtimes/{self.rid}/native/libe_sqlite3.dylib"
        self.make_zip(self.exe_name, extra_files={dependency: b"native sqlite fixture"})
        result = self.run_setup()
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        installed_dir = self.state / "engine" / "1.2.3" / self.rid
        self.assertEqual((installed_dir / dependency).read_bytes(), b"native sqlite fixture")

    def test_archive_rejects_links_and_windows_device_names(self) -> None:
        with tarfile.open(self.archive, "w:gz") as package:
            link = tarfile.TarInfo(self.exe_name)
            link.type = tarfile.SYMTYPE
            link.linkname = "../outside"
            package.addfile(link)
        symlink = self.run_setup()
        self.assertNotEqual(symlink.returncode, 0)
        self.assertFalse((self.state / "engine" / "1.2.3" / self.rid / self.exe_name).exists())

        self.make_zip(self.exe_name, extra_files={"NUL.txt": b"device"})
        device_name = self.run_setup()
        self.assertNotEqual(device_name.returncode, 0)
        self.assertFalse((self.state / "engine" / "1.2.3" / self.rid / self.exe_name).exists())


def io_bytes(value: bytes):
    from io import BytesIO

    return BytesIO(value)


if __name__ == "__main__":
    unittest.main()
