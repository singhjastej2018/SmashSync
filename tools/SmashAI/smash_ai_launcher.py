#!/usr/bin/env python3
"""SmashSync desktop launcher for training and live AI play."""

from __future__ import annotations

import os
import queue
import random
import subprocess
import sys
import threading
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from smash_ai_env import BridgeClient, ProtocolError
from smash_ai_workers import (
    launch_worker,
    prepare_worker_roots,
    set_process_windows_visible,
    terminate_processes,
    wait_until_match,
)

TOOLS_DIR = Path(__file__).resolve().parent
ROOT_DIR = TOOLS_DIR.parent
RYUJINX_EXE = ROOT_DIR / "Ryujinx.exe"
TRAIN_SCRIPT = TOOLS_DIR / "smash_ai_train.py"
TRAIN_MULTI_SCRIPT = TOOLS_DIR / "smash_ai_train_multi.py"
PLAY_SCRIPT = TOOLS_DIR / "smash_ai_play.py"
INSTALL_ML_BAT = ROOT_DIR / "Install-SmashAI-ML.bat"
DEFAULT_MODELS_DIR = ROOT_DIR / "SmashAI-models"


class SmashAiLauncher(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("SmashSync AI")
        self.geometry("980x760")
        self.minsize(860, 650)

        self.worker: subprocess.Popen | None = None
        self.worker_kind: str | None = None
        self.log_queue: queue.Queue[str] = queue.Queue()
        self.ui_queue: queue.Queue[object] = queue.Queue()

        # Ryujinx processes launched by this UI, keyed by bridge port.
        self.ryujinx_processes: dict[int, subprocess.Popen] = {}
        self.monitored_port: int | None = None
        self.parallel_prepare_thread: threading.Thread | None = None
        self.closing = False

        self.port_var = tk.StringVar(value="24872")
        self.worker_count_var = tk.StringVar(value="1")
        self.auto_hide_workers_var = tk.BooleanVar(value=True)

        self.device_var = tk.StringVar(value="auto")
        self.model_dir_var = tk.StringVar(value=str(DEFAULT_MODELS_DIR / "fighter"))
        self.resume_var = tk.StringVar()
        self.updates_var = tk.StringVar(value="0")
        self.rollout_var = tk.StringVar(value="2048")
        self.episode_frames_var = tk.StringVar(value="3600")

        self.play_model_var = tk.StringVar()
        self.play_player_var = tk.StringVar(value="P2")
        self.play_device_var = tk.StringVar(value="auto")
        self.status_var = tk.StringVar(value="Ready")

        self._build_ui()
        self.after(100, self._drain_log)
        self.protocol("WM_DELETE_WINDOW", self._on_close)

    def _build_ui(self) -> None:
        top = ttk.Frame(self, padding=10)
        top.pack(fill="x")

        ttk.Label(
            top,
            text="SmashSync AI",
            font=("Segoe UI", 18, "bold"),
        ).pack(side="left")
        ttk.Label(top, textvariable=self.status_var).pack(side="right")

        notebook = ttk.Notebook(self)
        notebook.pack(fill="both", expand=True, padx=10, pady=(0, 10))

        train_tab = ttk.Frame(notebook, padding=14)
        play_tab = ttk.Frame(notebook, padding=14)
        setup_tab = ttk.Frame(notebook, padding=14)
        notebook.add(train_tab, text="Train")
        notebook.add(play_tab, text="Play with AI")
        notebook.add(setup_tab, text="Setup / Status")

        self._build_train_tab(train_tab)
        self._build_play_tab(play_tab)
        self._build_setup_tab(setup_tab)

        log_frame = ttk.LabelFrame(self, text="Live training / runtime output", padding=6)
        log_frame.pack(fill="both", expand=False, padx=10, pady=(0, 10))
        self.log = tk.Text(log_frame, height=14, wrap="word", state="disabled")
        self.log.pack(side="left", fill="both", expand=True)
        scroll = ttk.Scrollbar(log_frame, command=self.log.yview)
        scroll.pack(side="right", fill="y")
        self.log.configure(yscrollcommand=scroll.set)

    def _row(
        self,
        parent,
        row: int,
        label: str,
        variable: tk.StringVar,
        width: int = 45,
    ):
        ttk.Label(parent, text=label).grid(
            row=row, column=0, sticky="w", padx=(0, 10), pady=5
        )
        entry = ttk.Entry(parent, textvariable=variable, width=width)
        entry.grid(row=row, column=1, sticky="ew", pady=5)
        return entry

    def _build_train_tab(self, parent) -> None:
        parent.columnconfigure(1, weight=1)

        ttk.Label(
            parent,
            text=(
                "Start Training launches the requested Ryujinx workers and waits. "
                "For each worker, enter SSBU Training Mode, keep the same trainee as P1, "
                "choose an active CPU and stage, then enter the match. Each ready worker "
                "can auto-hide. One central PPO learner combines experience from all workers."
            ),
            wraplength=880,
            justify="left",
        ).grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 12))

        self._row(parent, 1, "Model folder", self.model_dir_var)
        ttk.Button(
            parent, text="Browse", command=self._browse_model_dir
        ).grid(row=1, column=2, padx=(8, 0))

        self._row(parent, 2, "Resume checkpoint (optional)", self.resume_var)
        ttk.Button(
            parent, text="Browse", command=self._browse_resume
        ).grid(row=2, column=2, padx=(8, 0))

        ttk.Label(parent, text="Device").grid(
            row=3, column=0, sticky="w", pady=5
        )
        ttk.Combobox(
            parent,
            textvariable=self.device_var,
            values=("auto", "directml", "cpu", "cuda"),
            state="readonly",
            width=15,
        ).grid(row=3, column=1, sticky="w", pady=5)

        self._row(
            parent,
            4,
            "Parallel Ryujinx workers",
            self.worker_count_var,
            width=18,
        )
        ttk.Label(
            parent,
            text="Start with 2-3; raise toward 5 if aggregate simulated FPS keeps increasing.",
        ).grid(row=4, column=2, sticky="w", padx=(8, 0))

        self._row(
            parent,
            5,
            "PPO updates (0 = until stopped)",
            self.updates_var,
            width=18,
        )
        self._row(
            parent,
            6,
            "Total rollout transitions/update",
            self.rollout_var,
            width=18,
        )
        self._row(
            parent,
            7,
            "Episode virtual-frame limit",
            self.episode_frames_var,
            width=18,
        )

        ttk.Checkbutton(
            parent,
            text="Auto-hide each worker window after it reaches the Training match",
            variable=self.auto_hide_workers_var,
        ).grid(row=8, column=0, columnspan=3, sticky="w", pady=(8, 2))

        button_frame = ttk.Frame(parent)
        button_frame.grid(
            row=9, column=0, columnspan=3, sticky="w", pady=(14, 4)
        )
        ttk.Button(
            button_frame,
            text="Start / Resume Training",
            command=self._start_training,
        ).pack(side="left")
        ttk.Button(
            button_frame,
            text="Stop Training",
            command=self._stop_worker,
        ).pack(side="left", padx=8)
        ttk.Button(
            button_frame,
            text="Show Random Worker",
            command=self._show_random_worker,
        ).pack(side="left")
        ttk.Button(
            button_frame,
            text="Hide Worker Windows",
            command=self._hide_all_worker_windows,
        ).pack(side="left", padx=8)
        ttk.Button(
            button_frame,
            text="Open Model Folder",
            command=self._open_model_folder,
        ).pack(side="left")

        ttk.Label(
            parent,
            text=(
                "Training automatically uses exact frame stepping, unbounded host pacing, "
                "and muted audio. The output below reports sim_fps/speed, rolling win rate, "
                "reward, PPO losses, and checkpoint saves. latest.pt resumes training; "
                "model.pt is the shareable play model."
            ),
            wraplength=880,
            justify="left",
        ).grid(row=10, column=0, columnspan=3, sticky="w", pady=(12, 0))

    def _build_play_tab(self, parent) -> None:
        parent.columnconfigure(1, weight=1)
        ttk.Label(
            parent,
            text=(
                "Choose a trained model and click Start Play with AI. Ryujinx launches if "
                "needed. Navigate Smash normally, select both characters/stage, and start "
                "a match. The AI takes over the selected player only after the match begins."
            ),
            wraplength=880,
            justify="left",
        ).grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 12))

        self._row(parent, 1, "Model weights", self.play_model_var)
        ttk.Button(
            parent, text="Browse", command=self._browse_play_model
        ).grid(row=1, column=2, padx=(8, 0))

        ttk.Label(parent, text="AI controls").grid(
            row=2, column=0, sticky="w", pady=5
        )
        ttk.Combobox(
            parent,
            textvariable=self.play_player_var,
            values=("P1", "P2", "P3"),
            state="readonly",
            width=10,
        ).grid(row=2, column=1, sticky="w", pady=5)

        ttk.Label(parent, text="Inference device").grid(
            row=3, column=0, sticky="w", pady=5
        )
        ttk.Combobox(
            parent,
            textvariable=self.play_device_var,
            values=("auto", "directml", "cpu", "cuda"),
            state="readonly",
            width=15,
        ).grid(row=3, column=1, sticky="w", pady=5)

        button_frame = ttk.Frame(parent)
        button_frame.grid(
            row=4, column=0, columnspan=3, sticky="w", pady=(14, 4)
        )
        ttk.Button(
            button_frame,
            text="Start Play with AI",
            command=self._start_play,
        ).pack(side="left")
        ttk.Button(
            button_frame,
            text="Stop AI Control",
            command=self._stop_worker,
        ).pack(side="left", padx=8)

        ttk.Label(
            parent,
            text=(
                "For P2/P3 live play, keep that Ryujinx controller slot configured so "
                "the player can join character select. Use your keyboard/controller to "
                "choose the AI's character; the neural policy takes over once in_match=True."
            ),
            wraplength=880,
            justify="left",
        ).grid(row=5, column=0, columnspan=3, sticky="w", pady=(12, 0))

    def _build_setup_tab(self, parent) -> None:
        parent.columnconfigure(1, weight=1)

        ttk.Label(parent, text="Base bridge UDP port").grid(
            row=0, column=0, sticky="w", pady=5
        )
        ttk.Entry(
            parent, textvariable=self.port_var, width=12
        ).grid(row=0, column=1, sticky="w", pady=5)

        buttons = ttk.Frame(parent)
        buttons.grid(
            row=1, column=0, columnspan=3, sticky="w", pady=(10, 8)
        )
        ttk.Button(
            buttons, text="Launch Ryujinx", command=self._launch_ryujinx
        ).pack(side="left")
        ttk.Button(
            buttons,
            text="Check Bridge / Exporter",
            command=self._check_bridge,
        ).pack(side="left", padx=8)
        ttk.Button(
            buttons,
            text="Install ML Dependencies",
            command=self._install_dependencies,
        ).pack(side="left")
        ttk.Button(
            buttons,
            text="Check ML Device",
            command=self._check_ml_device,
        ).pack(side="left", padx=8)
        ttk.Button(
            buttons,
            text="Close Launched Ryujinx Workers",
            command=self._close_launched_ryujinx,
        ).pack(side="left")

        ttk.Label(
            parent,
            text=(
                "For parallel training, worker 1 uses your normal portable folder. Extra "
                "workers get isolated data roots under SmashAI-workers. Large immutable NAND "
                "content is hard-linked when possible to avoid multiplying disk usage."
            ),
            wraplength=880,
            justify="left",
        ).grid(row=2, column=0, columnspan=3, sticky="w", pady=(8, 0))

    def _append_log(self, text: str) -> None:
        self.log.configure(state="normal")
        self.log.insert("end", text.rstrip() + "\n")
        self.log.see("end")
        self.log.configure(state="disabled")

        if text.startswith("update="):
            # Keep a compact live summary visible even when the output box is long.
            self.status_var.set(text[:150])

    def _drain_log(self) -> None:
        try:
            while True:
                self._append_log(self.log_queue.get_nowait())
        except queue.Empty:
            pass

        try:
            while True:
                callback = self.ui_queue.get_nowait()
                callback()
        except queue.Empty:
            pass

        if self.worker is not None and self.worker.poll() is not None:
            code = self.worker.returncode
            kind = self.worker_kind or "worker"
            self.log_queue.put(f"{kind} exited with code {code}")
            self.worker = None
            self.worker_kind = None
            self.status_var.set("Ready")

        if not self.closing:
            self.after(100, self._drain_log)

    def _parse_port(self) -> int:
        try:
            port = int(self.port_var.get())
        except ValueError:
            raise ValueError("Bridge port must be a number.")
        if not 1024 <= port <= 65535:
            raise ValueError("Bridge port must be between 1024 and 65535.")
        return port

    def _parse_worker_count(self) -> int:
        try:
            count = int(self.worker_count_var.get())
        except ValueError:
            raise ValueError("Parallel worker count must be a number.")
        if not 1 <= count <= 8:
            raise ValueError("Parallel worker count must be between 1 and 8.")
        return count

    def _ports_for_workers(self, count: int) -> list[int]:
        base = self._parse_port()
        ports = [base + index for index in range(count)]
        if ports[-1] > 65535:
            raise ValueError("Worker bridge ports exceed 65535.")
        return ports

    def _bridge_online_port(self, port: int) -> bool:
        try:
            with BridgeClient(
                port=port, timeout=0.25, retries=1
            ) as bridge:
                bridge.ping()
            return True
        except Exception:
            return False

    def _launch_ryujinx(self) -> None:
        if not RYUJINX_EXE.exists():
            messagebox.showerror(
                "Ryujinx missing", f"Could not find:\n{RYUJINX_EXE}"
            )
            return

        port = self._parse_port()
        if self._bridge_online_port(port):
            self.log_queue.put(
                "Ryujinx bridge is already online; not launching another instance."
            )
            return

        env = os.environ.copy()
        env["SMASH_AI_PORT"] = str(port)
        try:
            process = subprocess.Popen(
                [str(RYUJINX_EXE)],
                cwd=str(ROOT_DIR),
                env=env,
            )
            self.ryujinx_processes[port] = process
            self.log_queue.put(
                f"Launched Ryujinx worker 1 on UDP {port}"
            )
        except Exception as exc:
            messagebox.showerror("Launch failed", str(exc))

    def _prepare_and_launch_workers(
        self,
        count: int,
        on_ready,
    ) -> None:
        if self.parallel_prepare_thread is not None and self.parallel_prepare_thread.is_alive():
            messagebox.showwarning(
                "Workers are being prepared",
                "Wait for the current worker preparation to finish.",
            )
            return

        ports = self._ports_for_workers(count)
        auto_hide = bool(self.auto_hide_workers_var.get())

        def task() -> None:
            try:
                roots = prepare_worker_roots(
                    ROOT_DIR,
                    count,
                    progress=lambda line: self.log_queue.put(line),
                )

                for index, (port, data_root) in enumerate(
                    zip(ports, roots), start=1
                ):
                    if self.closing:
                        return
                    if self._bridge_online_port(port):
                        self.log_queue.put(
                            f"worker {index}: bridge already online on UDP {port}"
                        )
                        continue

                    process = launch_worker(
                        RYUJINX_EXE,
                        data_root,
                        port,
                    )
                    self.ryujinx_processes[port] = process
                    self.log_queue.put(
                        f"worker {index}: launched PID {process.pid} on UDP {port}"
                    )

                    if auto_hide:
                        threading.Thread(
                            target=self._auto_hide_worker_when_ready,
                            args=(index, port, process),
                            daemon=True,
                        ).start()

                self.ui_queue.put(on_ready)
            except Exception as exc:
                self.log_queue.put(f"worker preparation failed: {exc}")
                error_text = str(exc)
                self.ui_queue.put(
                    lambda error_text=error_text: messagebox.showerror(
                        "Parallel worker setup failed", error_text
                    )
                )
                self.ui_queue.put(lambda: self.status_var.set("Ready"))

        self.status_var.set(f"Preparing {count} Ryujinx worker(s)...")
        self.parallel_prepare_thread = threading.Thread(
            target=task,
            name="SmashAI.WorkerPrepare",
            daemon=True,
        )
        self.parallel_prepare_thread.start()

    def _auto_hide_worker_when_ready(
        self,
        index: int,
        port: int,
        process: subprocess.Popen,
    ) -> None:
        observation = wait_until_match(
            port,
            timeout=900.0,
            stop_check=lambda: self.closing or process.poll() is not None,
        )
        if (
            observation is not None
            and not self.closing
        ):
            set_process_windows_visible(process.pid, False)
            self.log_queue.put(
                f"worker {index}: Training match detected on UDP {port}; window hidden"
            )

    def _start_process(self, command: list[str], kind: str) -> None:
        if self.worker is not None and self.worker.poll() is None:
            messagebox.showwarning(
                "Already running",
                "Stop the current training/play process first.",
            )
            return

        try:
            self.worker = subprocess.Popen(
                command,
                cwd=str(ROOT_DIR),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
            )
            self.worker_kind = kind
            self.status_var.set(kind)
        except Exception as exc:
            messagebox.showerror("Could not start", str(exc))
            return

        def reader(process: subprocess.Popen):
            assert process.stdout is not None
            for line in process.stdout:
                self.log_queue.put(line.rstrip())

        threading.Thread(
            target=reader,
            args=(self.worker,),
            daemon=True,
        ).start()

    def _start_training(self) -> None:
        try:
            count = self._parse_worker_count()
            ports = self._ports_for_workers(count)
            model_dir = Path(self.model_dir_var.get()).expanduser()
            if not str(model_dir):
                raise ValueError("Choose a model folder.")
            int(self.updates_var.get())
            int(self.rollout_var.get())
            int(self.episode_frames_var.get())
        except Exception as exc:
            messagebox.showerror(
                "Invalid training settings", str(exc)
            )
            return

        if self.worker is not None and self.worker.poll() is None:
            messagebox.showwarning(
                "Already running",
                "Stop the current training/play process first.",
            )
            return

        def start_trainer() -> None:
            if count == 1:
                command = [
                    sys.executable,
                    str(TRAIN_SCRIPT),
                    "--model-dir",
                    str(model_dir),
                    "--port",
                    str(ports[0]),
                    "--device",
                    self.device_var.get(),
                    "--updates",
                    self.updates_var.get(),
                    "--rollout-steps",
                    self.rollout_var.get(),
                    "--max-episode-frames",
                    self.episode_frames_var.get(),
                    "--player",
                    "0",
                    "--fighter-slot",
                    "0",
                ]
            else:
                command = [
                    sys.executable,
                    str(TRAIN_MULTI_SCRIPT),
                    "--model-dir",
                    str(model_dir),
                    "--ports",
                    ",".join(map(str, ports)),
                    "--device",
                    self.device_var.get(),
                    "--updates",
                    self.updates_var.get(),
                    "--rollout-steps",
                    self.rollout_var.get(),
                    "--max-episode-frames",
                    self.episode_frames_var.get(),
                    "--player",
                    "0",
                    "--fighter-slot",
                    "0",
                ]

            resume = self.resume_var.get().strip()
            if resume:
                command.extend(["--resume", resume])

            self.log_queue.put(
                f"Training starting with {count} worker(s). "
                "Enter Training Mode on each visible Ryujinx window. "
                "Keep the same trainee as P1; CPU/stage can vary by worker."
            )
            self._start_process(
                command,
                "Training" if count == 1 else f"Parallel training ({count})",
            )

        self._prepare_and_launch_workers(count, start_trainer)

    def _start_play(self) -> None:
        model = Path(self.play_model_var.get().strip())
        if not model.exists():
            messagebox.showerror(
                "Model missing",
                "Choose an existing .pt checkpoint.",
            )
            return
        try:
            port = self._parse_port()
        except Exception as exc:
            messagebox.showerror("Invalid port", str(exc))
            return

        self._launch_ryujinx()
        player = {
            "P1": 0,
            "P2": 1,
            "P3": 2,
        }[self.play_player_var.get()]
        command = [
            sys.executable,
            str(PLAY_SCRIPT),
            "--model",
            str(model),
            "--port",
            str(port),
            "--player",
            str(player),
            "--fighter-slot",
            str(player),
            "--device",
            self.play_device_var.get(),
        ]
        self.log_queue.put(
            f"Play mode started. Select characters normally; "
            f"AI will take over P{player + 1} when the match begins."
        )
        self._start_process(command, "Play with AI")

    def _clear_ports(self) -> None:
        try:
            count = self._parse_worker_count()
            ports = self._ports_for_workers(count)
        except Exception:
            ports = [self._parse_port()]

        for port in ports:
            try:
                with BridgeClient(
                    port=port, timeout=0.25, retries=1
                ) as bridge:
                    for player in range(3):
                        bridge.clear(player)
                    try:
                        bridge.set_frame_gate(False)
                    except ProtocolError:
                        pass
                    try:
                        bridge.set_fast_mode(False)
                    except ProtocolError:
                        pass
            except Exception:
                pass

    def _stop_worker(self) -> None:
        if self.worker is not None and self.worker.poll() is None:
            self.worker.terminate()
            try:
                self.worker.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.worker.kill()

        self._clear_ports()
        self.worker = None
        self.worker_kind = None
        self.status_var.set("Ready")
        self.log_queue.put(
            "AI process stopped; controller overrides, frame gates, and fast mode cleared."
        )

    def _show_random_worker(self) -> None:
        live = [
            (port, process)
            for port, process in self.ryujinx_processes.items()
            if process.poll() is None
        ]
        if not live:
            self.log_queue.put(
                "No Ryujinx worker launched by this UI is available to show."
            )
            return

        # Hide any previously monitored worker first.
        if self.monitored_port is not None:
            previous = self.ryujinx_processes.get(self.monitored_port)
            if previous is not None and previous.poll() is None:
                set_process_windows_visible(previous.pid, False)

        port, process = random.choice(live)
        set_process_windows_visible(process.pid, True)
        self.monitored_port = port
        self.log_queue.put(
            f"Showing worker on UDP {port}. It remains in accelerated training mode."
        )

    def _hide_all_worker_windows(self) -> None:
        count = 0
        for process in self.ryujinx_processes.values():
            if process.poll() is None:
                count += set_process_windows_visible(
                    process.pid, False
                )
        self.monitored_port = None
        self.log_queue.put(
            f"Requested hide for {len(self.ryujinx_processes)} launched worker process(es)."
        )

    def _close_launched_ryujinx(self) -> None:
        processes = list(self.ryujinx_processes.values())
        self._clear_ports()
        terminate_processes(processes)
        self.ryujinx_processes.clear()
        self.monitored_port = None
        self.log_queue.put("Closed Ryujinx workers launched by SmashSync.")

    def _check_bridge(self) -> None:
        try:
            with BridgeClient(
                port=self._parse_port(),
                timeout=0.5,
                retries=2,
            ) as bridge:
                bridge.ping()
                self.log_queue.put("bridge: online")
                try:
                    obs = bridge.observe()
                    self.log_queue.put(
                        f"exporter: protocol={obs.protocol_version} "
                        f"size={obs.total_size} in_match={obs.in_match} "
                        f"fighters={obs.fighter_count}"
                    )
                except Exception as exc:
                    self.log_queue.put(
                        f"exporter: bridge online, observation not ready ({exc})"
                    )
        except Exception as exc:
            self.log_queue.put(
                f"bridge check failed: {exc}"
            )

    def _check_ml_device(self) -> None:
        command = [
            sys.executable,
            str(TRAIN_SCRIPT),
            "--model-dir",
            str(DEFAULT_MODELS_DIR / "_device_check"),
            "--device",
            self.device_var.get(),
            "--device-check",
        ]
        try:
            result = subprocess.run(
                command,
                cwd=str(ROOT_DIR),
                capture_output=True,
                text=True,
                timeout=30,
            )
            output = (
                result.stdout + result.stderr
            ).strip()
            self.log_queue.put(
                output
                or f"ML device check exited with code {result.returncode}"
            )
        except Exception as exc:
            self.log_queue.put(
                f"ML device check failed: {exc}"
            )

    def _install_dependencies(self) -> None:
        if not INSTALL_ML_BAT.exists():
            messagebox.showerror(
                "Installer missing", str(INSTALL_ML_BAT)
            )
            return
        subprocess.Popen(
            ["cmd", "/c", "start", "", str(INSTALL_ML_BAT)],
            cwd=str(ROOT_DIR),
        )

    def _browse_model_dir(self) -> None:
        path = filedialog.askdirectory(
            initialdir=str(DEFAULT_MODELS_DIR)
        )
        if path:
            self.model_dir_var.set(path)

    def _browse_resume(self) -> None:
        path = filedialog.askopenfilename(
            filetypes=[
                ("SmashSync checkpoint", "*.pt"),
                ("All files", "*.*"),
            ]
        )
        if path:
            self.resume_var.set(path)
            if not self.play_model_var.get():
                self.play_model_var.set(path)

    def _browse_play_model(self) -> None:
        path = filedialog.askopenfilename(
            filetypes=[
                ("SmashSync checkpoint", "*.pt"),
                ("All files", "*.*"),
            ]
        )
        if path:
            self.play_model_var.set(path)

    def _open_model_folder(self) -> None:
        path = Path(self.model_dir_var.get())
        path.mkdir(parents=True, exist_ok=True)
        if os.name == "nt":
            os.startfile(path)  # type: ignore[attr-defined]

    def _on_close(self) -> None:
        self.closing = True
        self._stop_worker()
        self._close_launched_ryujinx()
        self.destroy()


if __name__ == "__main__":
    app = SmashAiLauncher()
    app.mainloop()
