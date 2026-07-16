from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
WRAPPER = ROOT / "scripts/sign-ota-v1-bridge-local.sh"
CONVERGENCE_WRAPPER = ROOT / "scripts/sign-ota-v1-convergence-local.sh"
APPROVED_ORIGIN = "https://github.com/MinaH153/SuavoAgent.git"
APPROVED_PYTHON = next(
    (
        str(path)
        for path in (Path("/opt/homebrew/bin/python3"), Path("/usr/local/bin/python3"))
        if path.is_file() and os.access(path, os.X_OK)
    ),
    None,
)


def git(repository: Path, *arguments: str) -> str:
    return subprocess.check_output(
        ("git", "-C", str(repository), *arguments), text=True
    ).strip()


class LocalBridgeSigningWrapperTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.cli_marker = self.root / "reviewed-cli-ran"
        self.convergence_cli_marker = self.root / "reviewed-convergence-cli-ran"
        self.loader_marker = self.root / "hostile-loader-environment-reached-python"
        self.remote = self.root / "remote.git"
        subprocess.run(("git", "init", "--bare", "-q", str(self.remote)), check=True)
        self.fixture_origin = self.remote.resolve().as_uri()
        self.repository = self.root / "repository"
        self.scripts = self.repository / "scripts"
        self.scripts.mkdir(parents=True)
        fixture_wrapper = WRAPPER.read_text(encoding="utf-8").replace(
            APPROVED_ORIGIN, self.fixture_origin
        )
        (self.scripts / WRAPPER.name).write_text(fixture_wrapper, encoding="utf-8")
        (self.scripts / WRAPPER.name).chmod(0o755)

        nested_scripts = self.repository / "nested" / "scripts"
        nested_scripts.mkdir(parents=True)
        shutil.copy2(self.scripts / WRAPPER.name, nested_scripts / WRAPPER.name)

        fixture_cli = (ROOT / "scripts/v1_bridge_cli.py").read_text(
            encoding="utf-8"
        ).replace(APPROVED_ORIGIN, self.fixture_origin)
        (self.scripts / "v1_bridge_cli.py").write_text(
            fixture_cli, encoding="utf-8"
        )
        fixture_convergence_wrapper = CONVERGENCE_WRAPPER.read_text(
            encoding="utf-8"
        ).replace(APPROVED_ORIGIN, self.fixture_origin)
        (self.scripts / CONVERGENCE_WRAPPER.name).write_text(
            fixture_convergence_wrapper, encoding="utf-8"
        )
        (self.scripts / CONVERGENCE_WRAPPER.name).chmod(0o755)
        fixture_convergence_cli = (
            ROOT / "scripts/v1_bridge_convergence_cli.py"
        ).read_text(encoding="utf-8").replace(APPROVED_ORIGIN, self.fixture_origin)
        (self.scripts / "v1_bridge_convergence_cli.py").write_text(
            fixture_convergence_cli, encoding="utf-8"
        )
        (self.scripts / "ota_update_trust_roots.py").write_text(
            "class TrustRegistryError(ValueError):\n    pass\n",
            encoding="utf-8",
        )
        for module in (
            "v1_bridge_handoff.py",
            "v1_bridge_file_io.py",
            "v1_bridge_crypto.py",
        ):
            (self.scripts / module).write_text("# reviewed fixture module\n", encoding="utf-8")
        (self.scripts / "v1_bridge_release.py").write_text(
            f"""from pathlib import Path
import os
import sys

if os.environ.get("DYLD_INSERT_LIBRARIES") or os.environ.get("DYLD_LIBRARY_PATH"):
    Path({str(self.loader_marker)!r}).write_text("loader environment survived", encoding="utf-8")

class BridgeError(ValueError):
    pass

DEFAULT_REGISTRY = Path("registry.json")
ROOT = Path(".")

def local_sign(arguments):
    Path({str(self.cli_marker)!r}).write_text("\\n".join(sys.argv), encoding="utf-8")

def assert_bridge_source(*arguments):
    pass

def assert_normal_release(*arguments):
    pass

def finalize_response(*arguments):
    pass

def prepare_request(*arguments):
    pass

def validate_final(*arguments):
    pass
""",
            encoding="utf-8",
        )
        (self.scripts / "v1_bridge_convergence.py").write_text(
            f"""from pathlib import Path

def sign_convergence_claim(arguments):
    Path({str(self.convergence_cli_marker)!r}).write_text("ran", encoding="utf-8")
""",
            encoding="utf-8",
        )
        shutil.copy2(
            ROOT / "scripts/ecdsa_der_to_p1363.py",
            self.scripts / "ecdsa_der_to_p1363.py",
        )
        shutil.copy2(
            ROOT / "scripts/v1_bridge_source_guard.py",
            self.scripts / "v1_bridge_source_guard.py",
        )
        security = self.repository / "security"
        security.mkdir()
        (security / "ota-update-trust-roots.json").write_text("{}\n", encoding="utf-8")
        (security / "ota-v1-bridge-convergence-evidence.json").write_text(
            "{}\n", encoding="utf-8"
        )
        (security / "ota-fleet-inventory-snapshot.json").write_text(
            "{}\n", encoding="utf-8"
        )
        (security / "ota-fleet-inventory-snapshot.sig").write_bytes(b"fixture\n")
        (self.repository / ".gitignore").write_text("ignored-stage/\n", encoding="utf-8")

        subprocess.run(("git", "init", "-q", str(self.repository)), check=True)
        git(self.repository, "config", "user.email", "bridge-test@example.invalid")
        git(self.repository, "config", "user.name", "Bridge Test")
        git(self.repository, "remote", "add", "origin", self.fixture_origin)
        git(self.repository, "add", ".")
        git(self.repository, "commit", "-qm", "bridge wrapper fixture")
        subprocess.run(
            (
                "git",
                "-C",
                str(self.repository),
                "push",
                "-q",
                "origin",
                "HEAD:refs/heads/main",
            ),
            check=True,
        )
        self.source_sha = git(self.repository, "rev-parse", "HEAD")

        self.stage = self.root / "stage"
        self.stage.mkdir()
        (self.stage / "bridge-signing-request.json").write_text(
            '{"sourceSha":"' + self.source_sha + '"}\n', encoding="utf-8"
        )
        self.key = self.root / "v1.pem"
        self.key.write_text("test key path only\n", encoding="utf-8")
        self.descriptor = self.root / "bridge-handoff-descriptor.json"
        self.descriptor.write_text("{}\n", encoding="utf-8")
        self.descriptor_signature = self.root / "bridge-handoff-descriptor.sig"
        self.descriptor_signature.write_bytes(b"fixture\n")
        self.response_json = self.root / "response.json"
        self.response_b64 = self.root / "response.b64"
        self.convergence_output = self.root / "convergence-output"
        self.convergence_output.mkdir()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    @property
    def wrapper(self) -> Path:
        return self.scripts / WRAPPER.name

    @property
    def convergence_wrapper(self) -> Path:
        return self.scripts / CONVERGENCE_WRAPPER.name

    def invoke(
        self,
        *,
        wrapper: Path | None = None,
        stage: Path | None = None,
        key: Path | None = None,
        response_json: Path | None = None,
        response_b64: Path | None = None,
        extra_environment: dict[str, str] | None = None,
    ) -> subprocess.CompletedProcess[str]:
        environment = dict(os.environ)
        environment["V1_BRIDGE_TEST_CLI_MARKER"] = str(self.cli_marker)
        if extra_environment:
            environment.update(extra_environment)
        return subprocess.run(
            (
                str(wrapper or self.wrapper),
                str(key or self.key),
                str(stage or self.stage),
                str(self.descriptor),
                str(self.descriptor_signature),
                str(response_json or self.response_json),
                str(response_b64 or self.response_b64),
            ),
            cwd=self.repository,
            env=environment,
            text=True,
            capture_output=True,
            check=False,
        )

    def assert_reviewed_cli_not_started(self) -> None:
        self.assertFalse(self.cli_marker.exists())

    def invoke_convergence(
        self, *, extra_environment: dict[str, str] | None = None
    ) -> subprocess.CompletedProcess[str]:
        environment = dict(os.environ)
        if extra_environment:
            environment.update(extra_environment)
        return subprocess.run(
            (
                str(self.convergence_wrapper),
                str(self.key),
                str(self.convergence_output),
                "v9.9.9",
                self.source_sha,
            ),
            cwd=self.repository,
            env=environment,
            text=True,
            capture_output=True,
            check=False,
        )

    def test_clean_approved_checkout_uses_isolated_runpy_launcher(self) -> None:
        result = self.invoke()

        self.assertEqual(0, result.returncode, result.stderr)
        arguments = self.cli_marker.read_text(encoding="utf-8").splitlines()
        self.assertEqual(str((self.scripts / "v1_bridge_cli.py").resolve()), arguments[0])
        self.assertEqual("local-sign", arguments[1])
        self.assertEqual("--key", arguments[2])
        self.assertEqual(str(self.key.resolve()), arguments[3])

        wrapper = WRAPPER.read_text(encoding="utf-8")
        interpreter = '"$python_bin" -I -S -B -c'
        self.assertIn(interpreter, wrapper)
        self.assertTrue(wrapper.startswith("#!/bin/bash -p\n"))
        self.assertIn("unset BASH_ENV ENV CDPATH", wrapper)
        self.assertIn("export PATH=/usr/bin:/bin:/usr/sbin:/sbin", wrapper)
        self.assertIn(
            "exec /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin "
            "HOME=/nonexistent LC_ALL=C \\\n"
            "  SUAVO_V1_BRIDGE_ISOLATED_BOOTSTRAP="
            "v1-bridge-clean-isolated-v1 \\\n"
            "  " + interpreter,
            wrapper,
        )
        self.assertLess(
            wrapper.index("for-each-ref --format='%(refname)' refs/replace/"),
            wrapper.index(interpreter),
        )
        self.assertIn("import runpy", wrapper)
        self.assertIn("if [os.path.realpath(entry) if entry else \"\" for entry in sys.path] != stdlib_paths", wrapper)
        self.assertIn("sys.path[:] = [*stdlib_paths, scripts_path]", wrapper)
        self.assertIn('runpy.run_path(cli_path, run_name="__main__")', wrapper)
        self.assertIn("GIT_NO_REPLACE_OBJECTS=1", wrapper)
        self.assertIn("hash-object --no-filters", wrapper)

    def test_assume_unchanged_cannot_hide_modified_imported_module(self) -> None:
        release_module = self.scripts / "v1_bridge_release.py"
        release_module.write_text(
            release_module.read_text(encoding="utf-8")
            + "\nraise RuntimeError('modified module must never import')\n",
            encoding="utf-8",
        )
        git(
            self.repository,
            "update-index",
            "--assume-unchanged",
            "scripts/v1_bridge_release.py",
        )
        self.assertEqual(
            "", git(self.repository, "status", "--porcelain", "--untracked-files=all")
        )

        result = self.invoke()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("differs from its exact HEAD blob", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_bash_env_cannot_run_before_either_historic_key_gate(self) -> None:
        marker = self.root / "bash-env-payload-ran"
        payload = self.root / "hostile-bash-env.sh"
        payload.write_text(
            f"""printf sourced > {str(marker)!r}
if [[ -n "${{1:-}}" && -r "$1" ]]; then
  /bin/cat -- "$1" >> {str(marker)!r}
fi
""",
            encoding="utf-8",
        )

        environment = {"BASH_ENV": str(payload), "ENV": str(payload)}
        bridge = self.invoke(extra_environment=environment)
        convergence = self.invoke_convergence(extra_environment=environment)

        self.assertEqual(0, bridge.returncode, bridge.stderr)
        self.assertEqual(0, convergence.returncode, convergence.stderr)
        self.assertFalse(marker.exists())

    def test_loader_environment_is_absent_from_probe_and_both_clis(self) -> None:
        environment = {
            "DYLD_INSERT_LIBRARIES": "/usr/lib/libSystem.B.dylib",
            "DYLD_LIBRARY_PATH": str(self.root / "hostile-dyld-search"),
            "PYTHONPATH": str(self.root / "hostile-python-search"),
        }

        bridge = self.invoke(extra_environment=environment)
        convergence = self.invoke_convergence(extra_environment=environment)

        self.assertEqual(0, bridge.returncode, bridge.stderr)
        self.assertEqual(0, convergence.returncode, convergence.stderr)
        self.assertFalse(self.loader_marker.exists())

    def test_fsmonitor_and_clean_filters_cannot_execute_before_key_gate(self) -> None:
        fsmonitor_marker = self.root / "fsmonitor-ran"
        fsmonitor = self.root / "hostile-fsmonitor.sh"
        fsmonitor.write_text(
            f"#!/bin/sh\nprintf executed > {str(fsmonitor_marker)!r}\nexit 0\n",
            encoding="utf-8",
        )
        fsmonitor.chmod(0o755)
        filter_marker = self.root / "clean-filter-ran"
        (self.repository / ".git/info/attributes").write_text(
            "* filter=hostile\n", encoding="utf-8"
        )
        git(self.repository, "config", "--local", "core.fsmonitor", str(fsmonitor))
        git(
            self.repository,
            "config",
            "--local",
            "filter.hostile.clean",
            f"/bin/sh -c 'printf executed > {filter_marker}; /bin/cat'",
        )

        bridge = self.invoke()
        convergence = self.invoke_convergence()

        self.assertEqual(0, bridge.returncode, bridge.stderr)
        self.assertEqual(0, convergence.returncode, convergence.stderr)
        self.assertFalse(fsmonitor_marker.exists())
        self.assertFalse(filter_marker.exists())

    def test_replace_ref_attack_is_rejected_by_both_ceremonies(self) -> None:
        for relative in (
            "scripts/v1_bridge_release.py",
            "scripts/v1_bridge_convergence.py",
        ):
            path = self.repository / relative
            path.write_text(
                path.read_text(encoding="utf-8")
                + "\nraise RuntimeError('replacement commit must never import')\n",
                encoding="utf-8",
            )
        git(self.repository, "add", "scripts/v1_bridge_release.py", "scripts/v1_bridge_convergence.py")
        git(self.repository, "commit", "-qm", "malicious replacement commit")
        replacement_sha = git(self.repository, "rev-parse", "HEAD")
        branch = git(self.repository, "symbolic-ref", "--short", "HEAD")
        git(self.repository, "replace", self.source_sha, replacement_sha)
        git(
            self.repository,
            "update-ref",
            f"refs/heads/{branch}",
            self.source_sha,
            replacement_sha,
        )
        self.assertEqual(self.source_sha, git(self.repository, "rev-parse", "HEAD"))
        self.assertIn(
            "replacement commit must never import",
            git(self.repository, "show", "HEAD:scripts/v1_bridge_release.py"),
        )

        bridge = self.invoke()
        convergence = self.invoke_convergence()

        self.assertNotEqual(0, bridge.returncode)
        self.assertNotEqual(0, convergence.returncode)
        self.assertIn("Git replacement refs are forbidden", bridge.stderr)
        self.assertIn("Git replacement refs are forbidden", convergence.stderr)
        self.assert_reviewed_cli_not_started()
        self.assertFalse(self.convergence_cli_marker.exists())

    def test_wrapper_executable_mode_is_part_of_reviewed_source(self) -> None:
        for wrapper in (self.wrapper, self.convergence_wrapper):
            with self.subTest(wrapper=wrapper):
                self.assertTrue(os.access(wrapper, os.X_OK))
                self.assertTrue(
                    git(self.repository, "ls-files", "-s", str(wrapper.relative_to(self.repository))).startswith(
                        "100755 "
                    )
                )

    def test_clean_unpushed_commit_cannot_authorize_v1_key_access(self) -> None:
        release_module = self.scripts / "v1_bridge_release.py"
        release_module.write_text(
            release_module.read_text(encoding="utf-8")
            + "\nraise RuntimeError('unreviewed commit must never import')\n",
            encoding="utf-8",
        )
        git(self.repository, "add", "scripts/v1_bridge_release.py")
        git(self.repository, "commit", "-qm", "unreviewed local signing change")

        result = self.invoke()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("must equal the staged request source SHA", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_git_transport_rewrite_is_rejected_before_remote_authority(self) -> None:
        evil_origin = (self.root / "rewritten-remote.git").resolve().as_uri()
        git(
            self.repository,
            "config",
            "--local",
            f"url.{evil_origin}.insteadOf",
            self.fixture_origin,
        )

        result = self.invoke()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("Git transport rewrites are forbidden", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_untracked_sitecustomize_is_rejected_before_python(self) -> None:
        (self.scripts / "sitecustomize.py").write_text(
            "raise RuntimeError('must never execute')\n", encoding="utf-8"
        )

        result = self.invoke()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("completely clean, including untracked files", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_ignored_python_shadow_is_rejected_before_python(self) -> None:
        (self.repository / ".git/info/exclude").write_text(
            "scripts/argparse.py\n", encoding="utf-8"
        )
        (self.scripts / "argparse.py").write_text(
            "raise RuntimeError('must never execute')\n", encoding="utf-8"
        )
        self.assertEqual("", git(self.repository, "status", "--porcelain", "--untracked-files=all"))

        result = self.invoke()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("ignored untracked files", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_wrong_origin_is_rejected_before_python(self) -> None:
        git(
            self.repository,
            "remote",
            "set-url",
            "origin",
            "https://github.com/MinaH153/SuavoAgent.git.evil",
        )

        result = self.invoke()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("origin is not MinaH153/SuavoAgent", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_script_below_a_nested_directory_is_rejected_before_python(self) -> None:
        nested_wrapper = self.repository / "nested" / "scripts" / WRAPPER.name

        result = self.invoke(wrapper=nested_wrapper)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("exact repository root", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_ignored_stage_inside_repository_is_rejected_before_python(self) -> None:
        ignored_stage = self.repository / "ignored-stage"
        ignored_stage.mkdir()
        self.assertEqual("", git(self.repository, "status", "--porcelain", "--untracked-files=all"))

        result = self.invoke(stage=ignored_stage)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("stage directory must be outside", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_ignored_key_inside_repository_is_rejected_before_reviewed_cli(self) -> None:
        ignored = self.repository / "ignored-stage"
        ignored.mkdir()
        inside_key = ignored / "v1.pem"
        inside_key.write_text("must not be read\n", encoding="utf-8")
        self.assertEqual("", git(self.repository, "status", "--porcelain", "--untracked-files=all"))

        result = self.invoke(key=inside_key)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("key must be outside", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_both_response_outputs_must_resolve_outside_repository(self) -> None:
        ignored = self.repository / "ignored-stage"
        ignored.mkdir()
        inside_json = ignored / "response.json"
        inside_b64 = ignored / "response.b64"

        for response_json, response_b64 in (
            (inside_json, self.response_b64),
            (self.response_json, inside_b64),
        ):
            with self.subTest(response_json=response_json, response_b64=response_b64):
                result = self.invoke(
                    response_json=response_json,
                    response_b64=response_b64,
                )
                self.assertNotEqual(0, result.returncode)
                self.assertIn("response outputs must be outside", result.stderr)
                self.assert_reviewed_cli_not_started()

    def test_pythonpath_sitecustomize_cannot_run_before_reviewed_cli(self) -> None:
        evil = self.root / "evil-pythonpath"
        evil.mkdir()
        theft_marker = self.root / "sitecustomize-ran"
        cli_marker = self.root / "reviewed-cli-ran"
        (evil / "sitecustomize.py").write_text(
            """import os
from pathlib import Path
Path(os.environ["V1_BRIDGE_TEST_THEFT_MARKER"]).write_text("executed", encoding="utf-8")
""",
            encoding="utf-8",
        )

        result = self.invoke(
            extra_environment={
                "PYTHONPATH": str(evil),
                "V1_BRIDGE_TEST_THEFT_MARKER": str(theft_marker),
                "V1_BRIDGE_TEST_CLI_MARKER": str(cli_marker),
            },
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(cli_marker.is_file())
        self.assertFalse(theft_marker.exists())
        cli_arguments = cli_marker.read_text(encoding="utf-8").splitlines()
        self.assertEqual(str((self.scripts / "v1_bridge_cli.py").resolve()), cli_arguments[0])
        self.assertEqual("local-sign", cli_arguments[1])

    def test_path_shadowed_python_cannot_run(self) -> None:
        shadow_bin = self.root / "shadow-bin"
        shadow_bin.mkdir()
        theft_marker = self.root / "path-shadowed-python-ran"
        shadow_python = shadow_bin / "python3"
        shadow_python.write_text(
            f"#!/bin/sh\nprintf executed > '{theft_marker}'\nexit 99\n",
            encoding="utf-8",
        )
        shadow_python.chmod(0o755)
        shadow_git = shadow_bin / "git"
        shadow_git.write_text(
            f"#!/bin/sh\nprintf executed > '{theft_marker}'\nexit 99\n",
            encoding="utf-8",
        )
        shadow_git.chmod(0o755)

        result = self.invoke(
            extra_environment={
                "PATH": str(shadow_bin) + os.pathsep + os.environ["PATH"],
            }
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(self.cli_marker.is_file())
        self.assertFalse(theft_marker.exists())

    def test_forged_isolated_sys_path_cannot_import_reviewed_cli(self) -> None:
        if APPROVED_PYTHON is None:
            self.skipTest("no approved fixed-path Python 3.10+ is installed")
        evil = self.root / "extra-import-root"
        evil.mkdir()
        launcher = """import os
import runpy
import sys
scripts_path = os.path.realpath(sys.argv[1])
extra_path = os.path.realpath(sys.argv[2])
base = os.path.realpath(sys.base_prefix)
major, minor = sys.version_info[:2]
stdlib_paths = [
    os.path.join(base, "lib", f"python{major}{minor}.zip"),
    os.path.join(base, "lib", f"python{major}.{minor}"),
    os.path.join(base, "lib", f"python{major}.{minor}", "lib-dynload"),
]
cli_path = scripts_path + "/v1_bridge_cli.py"
sys.path[:] = [*stdlib_paths, extra_path, scripts_path]
sys.argv = [cli_path, *sys.argv[3:]]
runpy.run_path(cli_path, run_name="__main__")
"""
        environment = dict(os.environ)
        environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
        environment[
            "SUAVO_V1_BRIDGE_ISOLATED_BOOTSTRAP"
        ] = "v1-bridge-clean-isolated-v1"
        environment["V1_BRIDGE_TEST_CLI_MARKER"] = str(self.cli_marker)

        result = subprocess.run(
            (
                APPROVED_PYTHON,
                "-I",
                "-S",
                "-B",
                "-c",
                launcher,
                str(self.scripts.resolve()),
                str(evil.resolve()),
                "local-sign",
                "--key",
                str(self.key),
                "--stage-dir",
                str(self.stage),
                "--source-root",
                str(self.repository),
                "--response-json",
                str(self.response_json),
                "--response-b64",
                str(self.response_b64),
            ),
            cwd=self.repository,
            env=environment,
            text=True,
            capture_output=True,
            check=False,
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("refusing historic v1 key access", result.stderr)
        self.assert_reviewed_cli_not_started()

    def test_real_python_source_validation_never_executes_git_hooks(self) -> None:
        if APPROVED_PYTHON is None:
            self.skipTest("no approved fixed-path Python 3.10+ is installed")
        marker = self.root / "real-source-guard-hook-ran"
        hook = self.root / "real-source-guard-fsmonitor.sh"
        hook.write_text(
            f"#!/bin/sh\nprintf executed > {str(marker)!r}\nexit 0\n",
            encoding="utf-8",
        )
        hook.chmod(0o755)
        (self.repository / ".git/info/attributes").write_text(
            "* filter=hostile\n", encoding="utf-8"
        )
        git(self.repository, "config", "--local", "core.fsmonitor", str(hook))
        git(
            self.repository,
            "config",
            "--local",
            "filter.hostile.clean",
            f"/bin/sh -c 'printf executed > {marker}; /bin/cat'",
        )
        source_sha = git(self.repository, "rev-parse", "HEAD")
        launcher = """import sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
from v1_bridge_release import _validate_local_source
_validate_local_source(Path(sys.argv[2]), sys.argv[3])
"""

        result = subprocess.run(
            (
                APPROVED_PYTHON,
                "-I",
                "-S",
                "-B",
                "-c",
                launcher,
                str((ROOT / "scripts").resolve()),
                str(self.repository.resolve()),
                source_sha,
            ),
            text=True,
            capture_output=True,
            check=False,
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("origin is not MinaH153/SuavoAgent", result.stderr)
        self.assertFalse(marker.exists())

    def test_recreated_tokens_with_hostile_path_cannot_reach_git_openssl_or_key(self) -> None:
        shadow_bin = self.root / "direct-shadow-bin"
        shadow_bin.mkdir()
        trap_marker = self.root / "direct-bootstrap-trap-ran"
        for executable in ("git", "openssl"):
            trap = shadow_bin / executable
            trap.write_text(
                f"#!/bin/sh\nprintf executed >> '{trap_marker}'\nexit 99\n",
                encoding="utf-8",
            )
            trap.chmod(0o755)

        cases = (
            (
                ROOT / "scripts/v1_bridge_cli.py",
                "SUAVO_V1_BRIDGE_ISOLATED_BOOTSTRAP",
                "v1-bridge-clean-isolated-v1",
                (
                    "local-sign",
                    "--key",
                    str(self.key),
                    "--stage-dir",
                    str(self.stage),
                    "--response-json",
                    str(self.response_json),
                    "--response-b64",
                    str(self.response_b64),
                ),
            ),
            (
                ROOT / "scripts/v1_bridge_convergence_cli.py",
                "SUAVO_V1_CONVERGENCE_ISOLATED_BOOTSTRAP",
                "v1-convergence-clean-isolated-v1",
                (),
            ),
        )
        for entrypoint, token_name, token_value, arguments in cases:
            environment = dict(os.environ)
            environment[token_name] = token_value
            environment["PATH"] = str(shadow_bin)
            with self.subTest(entrypoint=entrypoint):
                result = subprocess.run(
                    (sys.executable, "-I", "-S", "-B", str(entrypoint), *arguments),
                    env=environment,
                    text=True,
                    capture_output=True,
                    check=False,
                )
                self.assertNotEqual(0, result.returncode)
                self.assertIn("refusing historic v1 key access", result.stderr)
                self.assertFalse(trap_marker.exists())

        release_source = (ROOT / "scripts/v1_bridge_release.py").read_text(
            encoding="utf-8"
        )
        trust_source = (ROOT / "scripts/ota_update_trust_roots.py").read_text(
            encoding="utf-8"
        )
        crypto_source = (ROOT / "scripts/v1_bridge_crypto.py").read_text(encoding="utf-8")
        cli_source = (ROOT / "scripts/v1_bridge_cli.py").read_text(encoding="utf-8")
        self.assertNotRegex(release_source, r'\(["\'](?:git|openssl)["\']')
        self.assertNotIn('"openssl",', trust_source)
        self.assertIn('"/usr/bin/git"', cli_source)
        self.assertIn('"/usr/bin/openssl"', crypto_source)
        self.assertIn('"/usr/bin/openssl"', trust_source)

    def test_convergence_signer_sanitizes_subprocess_path(self) -> None:
        wrapper = (ROOT / "scripts/sign-ota-v1-convergence-local.sh").read_text(
            encoding="utf-8"
        )
        self.assertTrue(wrapper.startswith("#!/bin/bash -p\n"))
        self.assertIn(
            "exec /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin "
            "HOME=/nonexistent LC_ALL=C \\\n"
            "  SUAVO_V1_CONVERGENCE_ISOLATED_BOOTSTRAP="
            "v1-convergence-clean-isolated-v1 \\\n"
            '  "$python_bin" -I -S -B -c',
            wrapper,
        )
        self.assertIn("unset BASH_ENV ENV CDPATH", wrapper)
        self.assertIn("export PATH=/usr/bin:/bin:/usr/sbin:/sbin", wrapper)
        self.assertIn("sys.path[:] = [*stdlib_paths, scripts_path]", wrapper)
        self.assertIn("GIT_NO_REPLACE_OBJECTS=1", wrapper)
        self.assertIn("-c core.fsmonitor=false", wrapper)
        self.assertIn("hash-object --no-filters", wrapper)
        self.assertNotIn("diff-files", wrapper)


if __name__ == "__main__":
    unittest.main()
