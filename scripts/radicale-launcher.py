"""Launcher shim for Radicale on Windows (integration test harness).

Radicale's startup probe `pathutils.path_supports_symlink` catches only
PermissionError, but on Windows without the symlink privilege (any non-admin,
non-developer-mode session) `os.symlink` raises OSError (WinError 1314,
ERROR_PRIVILEGE_NOT_HELD), which escapes the probe and crashes server startup.
Symlink support is an optional storage optimization, so the correct degraded
answer is simply "no". This shim wraps the probe to fail soft, then hands off
to Radicale's normal CLI entry point (arguments pass through unchanged).

Used by scripts/start-test-servers.ps1; harmless on systems where the
privilege is held (the original probe result is returned).
"""

import radicale.pathutils as pathutils

_original_probe = pathutils.path_supports_symlink


def _path_supports_symlink_failsoft(path):
    try:
        return _original_probe(path)
    except OSError:
        return False


pathutils.path_supports_symlink = _path_supports_symlink_failsoft

from radicale.__main__ import run  # noqa: E402  (import after patch is the point)

run()
