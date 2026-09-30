#!/usr/bin/env python3
"""SmashSync desktop launcher for training and live AI play."""

from __future__ import annotations

import os
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from smash_ai_env import BridgeClient, BridgeOffline, ProtocolError

TOOLS_DIR = Path(__file__).resolve().parent
ROOT_DIR = TOOLS_DIR.parent
RYUJINX_EXE = ROOT_DIR / "Ryujinx.exe"
TRAIN_SCRIPT = TOOLS_DIR / "smash_ai_train.py"
PLAY_SCRIPT = TOOLS_DIR / "smash_ai_play.py"
INSTALL_ML_BAT = ROOT_DIR / "Install-SmashAI-ML.bat"
DEFAULT_MODELS_DIR = ROOT_DIR / "SmashAI-models"


class SmashAiLauncher(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("SmashSync AI")
        self.geometry("920x680")
        self.minsize(820, 600)

        self.worker: subprocess.Popen | None = None
        self.ryujinx: subprocess.Popen | None = None
        self.log_queue: queue.Queue[str] = queue.Queue()
        self.worker_kind: str | None = None

        self.port_var = tk.StringVar(value="24872")
        self.device_var = tk.StringVar(value="auto")
        self.model_dir_var = tk.StringVar(value=str(DEFAULT_MODELS_DIR / "fighter"))
        self.resume_var = tk.StringVar()
        self.updates_var = tk.StringVar(value="0")
        self.rollout_var = tk.StringVar(value="1024")
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

        ttk.Label(top, text="SmashSync AI", font=("Segoe UI", 18, "bold")).pack(side="left")
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

        log_frame = ttk.LabelFrame(self, text="Output", padding=6)
        log_frame.pack(fill="both", expand=False, padx=10, pady=(0, 10))
        self.log = tk.Text(log_frame, height=12, wrap="word", state="disabled")
        self.log.pack(side="left", fill="both", expand=True)
        scroll = ttk.Scrollbar(log_frame, command=self.log.yview)
        scroll.pack(side="right", fill="y")
        self.log.configure(yscrollcommand=scroll.set)

    def _row(self, parent, row: int, label: str, variable: tk.StringVar, width=45):
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", padx=(0, 10), pady=5)
        entry = ttk.Entry(parent, textvariable=variable, width=width)
        entry.grid(row=row, column=1, sticky="ew", pady=5)
        return entry

    def _build_train_tab(self, parent) -> None:
        parent.columnconfigure(1, weight=1)

        ttk.Label(
            parent,
            text=(
                "One-click workflow: Start Training launches Ryujinx if needed, then waits. "
                "In SSBU, enter Training Mode, choose the character to train as P1, choose "
                "an active CPU opponent and stage, and enter the match. Training begins automatically."
            ),
            wraplength=820,
            justify="left",
        ).grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 12))

        self._row(parent, 1, "Model folder", self.model_dir_var)
        ttk.Button(parent, text="Browse", command=self._browse_model_dir).grid(row=1, column=2, padx=(8, 0))

        self._row(parent, 2, "Resume checkpoint (optional)", self.resume_var)
        ttk.Button(parent, text="Browse", command=self._browse_resume).grid(row=2, column=2, padx=(8, 0))

        ttk.Label(parent, text="Device").grid(row=3, column=0, sticky="w", pady=5)
        ttk.Combobox(
            parent,
            textvariable=self.device_var,
            values=("auto", "directml", "cpu", "cuda"),
            state="readonly",
            width=15,
        ).grid(row=3, column=1, sticky="w", pady=5)

        self._row(parent, 4, "PPO updates (0 = until stopped)", self.updates_var, width=18)
        self._row(parent, 5, "Rollout steps per update", self.rollout_var, width=18)
        self._row(parent, 6, "Episode virtual-frame limit", self.episode_frames_var, width=18)

        button_frame = ttk.Frame(parent)
        button_frame.grid(row=7, column=0, columnspan=3, sticky="w", pady=(14, 4))
        ttk.Button(button_frame, text="Start / Resume Training", command=self._start_training).pack(side="left")
        ttk.Button(button_frame, text="Stop Training", command=self._stop_worker).pack(side="left", padx=8)
        ttk.Button(button_frame, text="Open Model Folder", command=self._open_model_folder).pack(side="left")

        ttk.Label(
            parent,
            text=(
                "The trainer saves latest.pt after each PPO update and periodic snapshot_XXXXXX.pt files. "
                "Those checkpoint files are the shareable model weights used by Play with AI."
            ),
            wraplength=820,
            justify="left",
        ).grid(row=8, column=0, columnspan=3, sticky="w", pady=(12, 0))

    def _build_play_tab(self, parent) -> None:
        parent.columnconfigure(1, weight=1)
        ttk.Label(
            parent,
            text=(
                "Choose a trained .pt model and click Start Play with AI. Ryujinx launches if needed. "
                "Navigate Smash normally, select both characters, and start a local match. "
                "The AI waits through menus and takes over the selected player only after the match begins."
            ),
            wraplength=820,
            justify="left",
        ).grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 12))

        self._row(parent, 1, "Model weights", self.play_model_var)
        ttk.Button(parent, text="Browse", command=self._browse_play_model).grid(row=1, column=2, padx=(8, 0))

        ttk.Label(parent, text="AI controls").grid(row=2, column=0, sticky="w", pady=5)
        ttk.Combobox(
            parent,
            textvariable=self.play_player_var,
            values=("P1", "P2", "P3"),
            state="readonly",
            width=10,
        ).grid(row=2, column=1, sticky="w", pady=5)

        ttk.Label(parent, text="Inference device").grid(row=3, column=0, sticky="w", pady=5)
        ttk.Combobox(
            parent,
            textvariable=self.play_device_var,
            values=("auto", "directml", "cpu", "cuda"),
            state="readonly",
            width=15,
        ).grid(row=3, column=1, sticky="w", pady=5)

        button_frame = ttk.Frame(parent)
        button_frame.grid(row=4, column=0, columnspan=3, sticky="w", pady=(14, 4))
        ttk.Button(button_frame, text="Start Play with AI", command=self._start_play).pack(side="left")
        ttk.Button(button_frame, text="Stop AI Control", command=self._stop_worker).pack(side="left", padx=8)

        ttk.Label(
            parent,
            text=(
                "For P2/P3 live play, keep that Ryujinx controller slot configured so SSBU lets the player join "
                "character select. The AI process does not override that slot until in_match=True, so you can use "
                "the keyboard/controller to pick its character first."
            ),
            wraplength=820,
            justify="left",
        ).grid(row=5, column=0, columnspan=3, sticky="w", pady=(12, 0))

    def _build_setup_tab(self, parent) -> None:
        parent.columnconfigure(1, weight=1)

        ttk.Label(parent, text="Bridge UDP port").grid(row=0, column=0, sticky="w", pady=5)
        ttk.Entry(parent, textvariable=self.port_var, width=12).grid(row=0, column=1, sticky="w", pady=5)

        buttons = ttk.Frame(parent)
        buttons.grid(row=1, column=0, columnspan=3, sticky="w", pady=(10, 8))
        ttk.Button(buttons, text="Launch Ryujinx", command=self._launch_ryujinx).pack(side="left")
        ttk.Button(buttons, text="Check Bridge / Exporter", command=self._check_bridge).pack(side="left", padx=8)
        ttk.Button(buttons, text="Install ML Dependencies", command=self._install_dependencies).pack(side="left")

        ttk.Label(
            parent,
            text=(
                "AMD/Intel GPU training uses DirectML when torch-directml is installed. "
                "The first dependency install can take several minutes. The launcher itself does not require PyTorch."
            ),
            wraplength=820,
            justify="left",
        ).grid(row=2, column=0, columnspan=3, sticky="w", pady=(8, 0))

    def _append_log(self, text: str) -> None:
        self.log.configure(state="normal")
        self.log.insert("end", text.rstrip() + "\n")
        self.log.see("end")
        self.log.configure(state="disabled")

    def _drain_log(self) -> None:
        try:
            while True:
                self._append_log(self.log_queue.get_nowait())
        except queue.Empty:
            pass

        if self.worker is not None and self.worker.poll() is not None:
            code = self.worker.returncode
            kind = self.worker_kind or "worker"
            self.log_queue.put(f"{kind} exited with code {code}")
            self.worker = None
            self.worker_kind = None
            self.status_var.set("Ready")

        self.after(100, self._drain_log)

    def _parse_port(self) -> int:
        try:
            port = int(self.port_var.get())
        except ValueError:
            raise ValueError("Bridge port must be a number.")
        if not 1024 <= port <= 65535:
            raise ValueError("Bridge port must be between 1024 and 65535.")
        return port

    def _bridge_online(self) -> bool:
        try:
            with BridgeClient(port=self._parse_port(), timeout=0.25, retries=1) as bridge:
                bridge.ping()
            return True
        except Exception:
            return False

    def _launch_ryujinx(self) -> None:
        if not RYUJINX_EXE.exists():
            messagebox.showerror("Ryujinx missing", f"Could not find:\n{RYUJINX_EXE}")
            return

        if self._bridge_online():
            self.log_queue.put("Ryujinx bridge is already online; not launching another instance.")
            return

        env = os.environ.copy()
        env["SMASH_AI_PORT"] = str(self._parse_port())
        try:
            self.ryujinx = subprocess.Popen([str(RYUJINX_EXE)], cwd=str(ROOT_DIR), env=env)
            self.log_queue.put(f"Launched Ryujinx with SMASH_AI_PORT={env['SMASH_AI_PORT']}")
        except Exception as exc:
            messagebox.showerror("Launch failed", str(exc))

    def _start_process(self, command: list[str], kind: str) -> None:
        if self.worker is not None and self.worker.poll() is None:
            messagebox.showwarning("Already running", "Stop the current training/play process first.")
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

        threading.Thread(target=reader, args=(self.worker,), daemon=True).start()

    def _start_training(self) -> None:
        try:
            port = self._parse_port()
            model_dir = Path(self.model_dir_var.get()).expanduser()
            if not str(model_dir):
                raise ValueError("Choose a model folder.")
            int(self.updates_var.get())
            int(self.rollout_var.get())
            int(self.episode_frames_var.get())
        except Exception as exc:
            messagebox.showerror("Invalid training settings", str(exc))
            return

        self._launch_ryujinx()

        command = [
            sys.executable,
            str(TRAIN_SCRIPT),
            "--model-dir",
            str(model_dir),
            "--port",
            str(port),
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
            "Training started. In SSBU choose Training Mode, trainee=P1, an active CPU opponent, "
            "your stage, then enter the match. The trainer will attach automatically."
        )
        self._start_process(command, "Training")

    def _start_play(self) -> None:
        model = Path(self.play_model_var.get().strip())
        if not model.exists():
            messagebox.showerror("Model missing", "Choose an existing .pt checkpoint.")
            return
        try:
            port = self._parse_port()
        except Exception as exc:
            messagebox.showerror("Invalid port", str(exc))
            return

        self._launch_ryujinx()
        player = {"P1": 0, "P2": 1, "P3": 2}[self.play_player_var.get()]
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
            f"Play mode started. Select characters normally; AI will take over P{player + 1} when the match begins."
        )
        self._start_process(command, "Play with AI")

    def _stop_worker(self) -> None:
        if self.worker is not None and self.worker.poll() is None:
            self.worker.terminate()
            try:
                self.worker.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.worker.kill()

        # Clear all overrides and release exact-step gate even if the child was
        # terminated before its Python finally block ran.
        try:
            with BridgeClient(port=self._parse_port(), timeout=0.25, retries=1) as bridge:
                for player in range(3):
                    bridge.clear(player)
                try:
                    bridge.set_frame_gate(False)
                except ProtocolError:
                    pass
        except Exception:
            pass

        self.worker = None
        self.worker_kind = None
        self.status_var.set("Ready")
        self.log_queue.put("AI process stopped; controller overrides cleared.")

    def _check_bridge(self) -> None:
        try:
            with BridgeClient(port=self._parse_port(), timeout=0.5, retries=2) as bridge:
                bridge.ping()
                self.log_queue.put("bridge: online")
                try:
                    obs = bridge.observe()
                    self.log_queue.put(
                        f"exporter: protocol={obs.protocol_version} size={obs.total_size} "
                        f"in_match={obs.in_match} fighters={obs.fighter_count}"
                    )
                except Exception as exc:
                    self.log_queue.put(f"exporter: bridge online, observation not ready ({exc})")
        except Exception as exc:
            self.log_queue.put(f"bridge check failed: {exc}")

    def _install_dependencies(self) -> None:
        if not INSTALL_ML_BAT.exists():
            messagebox.showerror("Installer missing", str(INSTALL_ML_BAT))
            return
        subprocess.Popen(["cmd", "/c", "start", "", str(INSTALL_ML_BAT)], cwd=str(ROOT_DIR))

    def _browse_model_dir(self) -> None:
        path = filedialog.askdirectory(initialdir=str(DEFAULT_MODELS_DIR))
        if path:
            self.model_dir_var.set(path)

    def _browse_resume(self) -> None:
        path = filedialog.askopenfilename(
            filetypes=[("SmashSync checkpoint", "*.pt"), ("All files", "*.*")]
        )
        if path:
            self.resume_var.set(path)
            if not self.play_model_var.get():
                self.play_model_var.set(path)

    def _browse_play_model(self) -> None:
        path = filedialog.askopenfilename(
            filetypes=[("SmashSync checkpoint", "*.pt"), ("All files", "*.*")]
        )
        if path:
            self.play_model_var.set(path)

    def _open_model_folder(self) -> None:
        path = Path(self.model_dir_var.get())
        path.mkdir(parents=True, exist_ok=True)
        if os.name == "nt":
            os.startfile(path)  # type: ignore[attr-defined]

    def _on_close(self) -> None:
        self._stop_worker()
        self.destroy()


if __name__ == "__main__":
    app = SmashAiLauncher()
    app.mainloop()
