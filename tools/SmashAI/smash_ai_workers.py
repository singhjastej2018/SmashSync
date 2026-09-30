#!/usr/bin/env python3
"""Windows worker management helpers for SmashSync parallel training."""

from __future__ import annotations

import ctypes
import json
import os
import shutil
import subprocess
import time
from pathlib import Path
from typing import Callable, List, Optional

from smash_ai_env import BridgeClient, BridgeOffline, ObservationUnavailable

SSBU_TITLE_ID = "01006A800016E000"


def _copy_file_worker_safe(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)

    # Installed title/firmware content under NAND Contents is immutable during
    # normal play. Hard-link large files when possible so 5 worker roots do not
    # multiply disk usage. Saves/config/profiles/mods are copied normally.
    parts = [part.lower() for part in source.parts]
    immutable_contents = (
        "bis" in parts
        and "contents" in parts
        and source.stat().st_size >= 4 * 1024 * 1024
    )

    if immutable_contents:
        try:
            os.link(source, destination)
            return
        except OSError:
            pass

    shutil.copy2(source, destination)


def clone_portable(
    source: Path,
    destination: Path,
    *,
    progress: Optional[Callable[[str], None]] = None,
) -> None:
    """Create an isolated Ryujinx root from the working portable folder."""
    source = source.resolve()
    destination = destination.resolve()

    if not source.is_dir():
        raise FileNotFoundError(f"portable folder not found: {source}")
    if destination.exists():
        return

    if progress:
        progress(f"creating worker data: {destination.name}")

    destination.mkdir(parents=True, exist_ok=True)
    for root, dirs, files in os.walk(source):
        root_path = Path(root)
        relative = root_path.relative_to(source)
        target_root = destination / relative
        target_root.mkdir(parents=True, exist_ok=True)

        # Avoid recursively copying logs and screenshots into every worker.
        dirs[:] = [
            name
            for name in dirs
            if name.lower() not in {"logs", "screenshots"}
        ]

        for name in files:
            source_file = root_path / name
            target_file = target_root / name
            _copy_file_worker_safe(source_file, target_file)

    marker = {
        "source": str(source),
        "created_unix": time.time(),
        "format": 1,
    }
    (destination / ".smashai-worker.json").write_text(
        json.dumps(marker, indent=2),
        encoding="utf-8",
    )


def sync_runtime_files(source: Path, destination: Path) -> None:
    """Refresh small config/mod files without rebuilding the whole worker root."""
    source = source.resolve()
    destination = destination.resolve()

    config = source / "Config.json"
    if config.exists():
        shutil.copy2(config, destination / "Config.json")

    source_mod = source / "mods" / "contents" / SSBU_TITLE_ID
    destination_mod = destination / "mods" / "contents" / SSBU_TITLE_ID
    if source_mod.exists():
        shutil.copytree(
            source_mod,
            destination_mod,
            dirs_exist_ok=True,
            copy_function=shutil.copy2,
        )

    source_profiles = source / "profiles"
    destination_profiles = destination / "profiles"
    if source_profiles.exists():
        shutil.copytree(
            source_profiles,
            destination_profiles,
            dirs_exist_ok=True,
            copy_function=shutil.copy2,
        )


def prepare_worker_roots(
    root_dir: Path,
    worker_count: int,
    *,
    progress: Optional[Callable[[str], None]] = None,
) -> List[Path]:
    if worker_count < 1:
        raise ValueError("worker_count must be at least 1")

    source = (root_dir / "portable").resolve()
    if not source.is_dir():
        raise FileNotFoundError(
            f"working portable folder not found beside Ryujinx.exe: {source}"
        )

    roots = [source]
    workers_root = root_dir / "SmashAI-workers"
    workers_root.mkdir(parents=True, exist_ok=True)

    for index in range(1, worker_count):
        destination = workers_root / f"worker-{index + 1}"
        marker = destination / ".smashai-worker.json"
        if destination.exists() and not marker.exists():
            if progress:
                progress(f"worker-{index + 1}: removing incomplete worker data")
            shutil.rmtree(destination, ignore_errors=True)

        if not destination.exists():
            clone_portable(source, destination, progress=progress)
        else:
            if progress:
                progress(f"worker-{index + 1}: existing data root reused")

        sync_runtime_files(source, destination)
        roots.append(destination.resolve())

    return roots


def launch_worker(
    ryujinx_exe: Path,
    data_root: Path,
    port: int,
) -> subprocess.Popen:
    env = os.environ.copy()
    env["SMASH_AI_PORT"] = str(port)

    command = [
        str(ryujinx_exe),
        "--root-data-dir",
        str(data_root),
    ]
    return subprocess.Popen(command, cwd=str(ryujinx_exe.parent), env=env)


def wait_until_match(
    port: int,
    timeout: float,
    *,
    stop_check: Optional[Callable[[], bool]] = None,
):
    deadline = time.monotonic() + timeout
    bridge = BridgeClient(port=port, timeout=0.4, retries=1)
    try:
        while time.monotonic() < deadline:
            if stop_check is not None and stop_check():
                return None
            try:
                bridge.ping()
                observation = bridge.observe()
                if observation.in_match:
                    return observation
            except (BridgeOffline, ObservationUnavailable, OSError):
                pass
            time.sleep(0.25)
        return None
    finally:
        bridge.close()


def _windows_for_pid(pid: int) -> List[int]:
    if os.name != "nt":
        return []

    user32 = ctypes.windll.user32
    handles: List[int] = []
    enum_proc_type = ctypes.WINFUNCTYPE(
        ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p
    )

    @enum_proc_type
    def callback(hwnd, _lparam):
        process_id = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(
            ctypes.c_void_p(hwnd), ctypes.byref(process_id)
        )
        if process_id.value == pid:
            # Ignore invisible helper windows.
            if user32.IsWindow(ctypes.c_void_p(hwnd)):
                handles.append(int(hwnd))
        return True

    user32.EnumWindows(callback, 0)
    return handles


def set_process_windows_visible(pid: int, visible: bool) -> int:
    if os.name != "nt":
        return 0

    user32 = ctypes.windll.user32
    # SW_HIDE=0, SW_SHOW=5, SW_RESTORE=9.
    command = 9 if visible else 0
    count = 0
    for hwnd in _windows_for_pid(pid):
        if user32.ShowWindow(ctypes.c_void_p(hwnd), command):
            count += 1
        else:
            # ShowWindow returns the previous visibility, so a false return is
            # not necessarily failure. Count valid handles anyway.
            count += 1
    return count


def terminate_processes(processes: List[subprocess.Popen]) -> None:
    for process in processes:
        if process.poll() is None:
            try:
                process.terminate()
            except OSError:
                pass
    for process in processes:
        if process.poll() is None:
            try:
                process.wait(timeout=3)
            except Exception:
                try:
                    process.kill()
                except OSError:
                    pass
