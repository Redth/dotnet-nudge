from __future__ import annotations

import hashlib
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


ROOT = Path(__file__).resolve().parents[2]
PLUGIN = ROOT / "plugins" / "dotnet-nudge"
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
        self.assertFalse((plugin_dir / "mcp.json").exists())
        self.assertFalse((plugin_dir / ".mcp.json").exists())

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
        self.fixture = self.cwd / "hook"
        self.fixture.write_text(
            "#!/usr/bin/env python3\n"
            "import json, os, sys\n"
            "payload = sys.stdin.buffer.read()\n"
            "with open(os.environ['FIXTURE_CAPTURE'], 'wb') as stream:\n"
            "    stream.write(payload)\n"
            "with open(os.environ['FIXTURE_ARGS'], 'w', encoding='utf-8') as stream:\n"
            "    json.dump([os.path.basename(sys.argv[0]), *sys.argv[1:]], stream)\n"
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

    def assert_fixture_passthrough(self, result: subprocess.CompletedProcess, host: str, event: str) -> None:
        self.assertEqual(result.returncode, 23, result.stderr.decode(errors="replace"))
        self.assertEqual(result.stdout, self.stdout.encode())
        self.assertEqual(result.stderr, self.stderr.encode())
        self.assertEqual(self.capture.read_bytes(), self.input)
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

    def test_bash_resolves_a_versioned_engine_from_user_data(self) -> None:
        versioned_engine_dir = self.state / "engine" / "1.2.3" / expected_rid()
        versioned_engine_dir.mkdir(parents=True)
        (self.state / "engine" / "current").write_text(
            f"1.2.3/{expected_rid()}\n",
            encoding="utf-8",
        )
        (versioned_engine_dir / "dotnet-nudge").symlink_to(sys.executable)
        env = self.base_env()
        env.pop("NUDGE_ENGINE_PATH")
        result = self.run_bash("copilot", "session-start", env)
        self.assert_fixture_passthrough(result, "copilot", "session-start")


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
