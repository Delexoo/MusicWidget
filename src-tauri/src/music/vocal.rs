//! Local vocal remover: one-time model/tool ensure, then per-track processing.
//! Uses Python + audio-separator (ONNX MDX) when available; caches under tools/.

use crate::music::download::download_file;
use crate::music::library::LibraryPaths;
use crate::windows::cmd::HiddenConsole;
use serde::Serialize;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

const MODEL_NAME: &str = "UVR-MDX-NET-Inst_HQ_3.onnx";
const MODEL_URL: &str = "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/UVR-MDX-NET-Inst_HQ_3.onnx";
const INSTRUMENTAL_MARKER: &str = "mdx-instrumental";

/// Writes only the Instrumental stem. A center-cancel mix is not accepted:
/// that trick leaves every vocal that is not a perfect mono center.
const SEPARATE_SCRIPT: &str = r#"
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
from audio_separator.separator import Separator

def main() -> None:
    input_path, output_dir, model_dir, model_name, target_path = sys.argv[1:6]
    output_dir = str(Path(output_dir))
    target = Path(target_path)
    separator = Separator(
        model_file_dir=model_dir,
        output_dir=output_dir,
        output_format="WAV",
        output_single_stem="Instrumental",
        sample_rate=44100,
        mdx_params={
            "hop_length": 1024,
            "segment_size": 256,
            "overlap": 0.25,
            "batch_size": 1,
            "enable_denoise": False,
        },
    )
    separator.load_model(model_filename=model_name)
    files = separator.separate(
        input_path,
        custom_output_names={"Instrumental": "mw_instrumental"},
    )
    produced = None
    for name in files or []:
        path = Path(name)
        if not path.is_file():
            path = Path(output_dir) / name
        if path.is_file():
            produced = path
    if produced is None:
        raise SystemExit("Vocal model finished without an instrumental file")
    if produced.resolve() != target.resolve():
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            target.unlink()
        produced.replace(target)
    data, _sr = sf.read(str(target), always_2d=True, frames=48000)
    if data.ndim != 2 or data.shape[1] < 2 or data.shape[0] < 1000:
        raise SystemExit("Instrumental file was empty")
    left = data[:, 0]
    right = data[:, 1]
    if float(np.std(left)) > 1e-4 and float(np.std(right)) > 1e-4:
        corr = float(np.corrcoef(left, right)[0, 1])
        if np.isfinite(corr) and corr < -0.98:
            raise SystemExit("Vocal removal collapsed to a center-cancel mix")
    print("MUSICWIDGET_OK", flush=True)

if __name__ == "__main__":
    main()
"#;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct VocalToolsStatus {
    pub ready: bool,
    pub model: bool,
    pub python: bool,
    pub separator: bool,
    pub tools_dir: String,
    pub message: String,
}

fn vocal_dir(paths: &LibraryPaths) -> PathBuf {
    PathBuf::from(&paths.tools_dir).join("vocal")
}

fn model_path(paths: &LibraryPaths) -> PathBuf {
    vocal_dir(paths).join("models").join(MODEL_NAME)
}

fn venv_python(paths: &LibraryPaths) -> PathBuf {
    vocal_dir(paths).join("venv").join("Scripts").join("python.exe")
}

fn find_system_python() -> Option<PathBuf> {
    for name in ["py", "python", "python3"] {
        let mut cmd = Command::new(name);
        if name == "py" {
            cmd.args(["-3", "-c", "import sys; print(sys.executable)"]);
        } else {
            cmd.args(["-c", "import sys; print(sys.executable)"]);
        }
        cmd.stdout(Stdio::piped())
            .stderr(Stdio::null())
            .hide_console();
        if let Ok(out) = cmd.output() {
            if out.status.success() {
                let path = String::from_utf8_lossy(&out.stdout).trim().to_string();
                if !path.is_empty() && Path::new(&path).exists() {
                    return Some(PathBuf::from(path));
                }
            }
        }
    }
    None
}

fn separator_import_ok(python: &Path) -> bool {
    let mut cmd = Command::new(python);
    cmd.args(["-c", "import audio_separator.separator"])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .hide_console();
    cmd.status().map(|s| s.success()).unwrap_or(false)
}

fn run_pip(python: &Path, args: &[&str]) -> Result<(), String> {
    let mut cmd = Command::new(python);
    cmd.arg("-m").arg("pip").args(args);
    cmd.stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .hide_console();
    let out = cmd
        .output()
        .map_err(|e| format!("pip failed to start: {e}"))?;
    if out.status.success() {
        return Ok(());
    }
    let err = String::from_utf8_lossy(&out.stderr);
    let combined = if err.trim().is_empty() {
        String::from_utf8_lossy(&out.stdout).into_owned()
    } else {
        err.into_owned()
    };
    Err(tail_message(&combined, 500))
}

fn tail_message(text: &str, max_chars: usize) -> String {
    text.chars()
        .rev()
        .take(max_chars)
        .collect::<String>()
        .chars()
        .rev()
        .collect()
}

/// Install audio-separator for MDX ONNX. On Windows, `diffq-fixed` often fails to
/// build (Cython / bitpack.pyx); that package is only needed for Demucs quantized
/// models, so we install without it and drop a tiny import stub.
fn install_separator(python: &Path) -> Result<(), String> {
    let _ = run_pip(python, &["install", "--upgrade", "pip", "setuptools", "wheel"]);

    if run_pip(python, &["install", "--upgrade", "audio-separator[cpu]"]).is_ok()
        && separator_import_ok(python)
    {
        return Ok(());
    }

    // Skip the broken Windows native build; MDX Inst HQ only needs ONNX + torch.
    run_pip(python, &["install", "--upgrade", "audio-separator", "--no-deps"])?;
    run_pip(
        python,
        &[
            "install",
            "--upgrade",
            "onnxruntime>=1.17",
            "torch>=2.3",
            "numpy>=2",
            "librosa>=0.10",
            "soundfile>=0.12",
            "scipy>=1.13",
            "pydub>=0.25",
            "tqdm",
            "requests>=2",
            "resampy>=0.4",
            "beartype>=0.18.5,<0.19.0",
            "pyyaml",
            "ml_collections",
            "einops>=0.7",
            "six>=1.16",
            "packaging",
            "samplerate==0.1.0",
            "julius>=0.2",
            "rotary-embedding-torch>=0.6.1,<0.7.0",
            "audioop-lts>=0.2.1",
            "onnx-weekly",
            "onnx2torch-py313>=1.6",
            "audioread",
        ],
    )?;

    // Provide `import diffq` so Demucs helper modules can import; MDX does not call it.
    let stub = r#"
import site, pathlib
pkgs = [pathlib.Path(p) for p in site.getsitepackages() if p.replace("\\", "/").endswith("site-packages")]
root = (pkgs[0] if pkgs else pathlib.Path(site.getsitepackages()[0])) / "diffq"
root.mkdir(parents=True, exist_ok=True)
(root / "__init__.py").write_text(
    '"""Stub: quantized Demucs needs real diffq; MDX ONNX does not."""\n'
    "class DiffQuantizer: pass\n"
    "class UniformQuantizer: pass\n"
    "def restore_quantized_state(*a, **k):\n"
    "    raise RuntimeError('diffq unavailable')\n",
    encoding="utf-8",
)
"#;
    let mut stub_cmd = Command::new(python);
    stub_cmd
        .args(["-c", stub])
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .hide_console();
    let stub_out = stub_cmd
        .output()
        .map_err(|e| format!("Could not write diffq stub: {e}"))?;
    if !stub_out.status.success() {
        return Err(format!(
            "Could not write diffq stub: {}",
            String::from_utf8_lossy(&stub_out.stderr)
        ));
    }

    if separator_import_ok(python) {
        return Ok(());
    }
    Err(
        "Vocal remover packages installed but import still failed. Try Python 3.12 from python.org, delete %APPDATA%\\Mochi\\tools\\vocal, then retry."
            .into(),
    )
}

pub fn vocal_tools_status(paths: &LibraryPaths) -> VocalToolsStatus {
    let model = model_path(paths).exists();
    let venv = venv_python(paths);
    let python = if venv.exists() {
        Some(venv)
    } else {
        find_system_python()
    };
    let has_python = python.is_some();
    let separator = python
        .as_ref()
        .map(|p| separator_import_ok(p))
        .unwrap_or(false);
    let ready = model && separator;
    VocalToolsStatus {
        ready,
        model,
        python: has_python,
        separator,
        tools_dir: vocal_dir(paths).to_string_lossy().into_owned(),
        message: if ready {
            "Vocal remover installed".into()
        } else if !has_python {
            "Python 3 is required once to install the vocal remover".into()
        } else if !separator {
            "Vocal remover package not installed yet".into()
        } else {
            "Model not downloaded yet".into()
        },
    }
}

fn emit_progress(
    on_progress: &dyn Fn(String, Option<f32>, String),
    phase: &str,
    percent: Option<f32>,
    message: &str,
) {
    on_progress(phase.to_string(), percent, message.to_string());
}

fn format_left(seconds: u32) -> String {
    let seconds = seconds.max(1);
    if seconds < 600 {
        return format!("About {seconds} sec left");
    }
    let mins = seconds.div_ceil(60);
    if mins == 1 {
        "About 1 min left".to_string()
    } else {
        format!("About {mins} min left")
    }
}

fn sit_tight(step: &str, seconds: u32) -> String {
    format!("Sit tight. MusicWidget is {step}. {}", format_left(seconds))
}

fn emit_step(
    on_progress: &dyn Fn(String, Option<f32>, String),
    last: &mut String,
    phase: &str,
    percent: Option<f32>,
    step: &str,
    seconds: u32,
) {
    let message = sit_tight(step, seconds);
    if message == *last {
        return;
    }
    *last = message.clone();
    emit_progress(on_progress, phase, percent, &message);
}

/// tqdm lines look like `12%|█ | 3/20 [00:08<02:32, 1.5it/s]`.
fn parse_tqdm_remaining(line: &str) -> Option<u32> {
    let rest = line.rsplit('<').next()?;
    let token = rest
        .split([',', ']'])
        .next()?
        .trim();
    if token.is_empty() || token.contains('?') {
        return None;
    }
    let parts: Vec<&str> = token.split(':').collect();
    let nums = parts
        .iter()
        .map(|part| part.parse::<u32>().ok())
        .collect::<Option<Vec<_>>>()?;
    match nums.as_slice() {
        [secs] => Some(*secs),
        [mins, secs] => Some(mins * 60 + secs),
        [hours, mins, secs] => Some(hours * 3600 + mins * 60 + secs),
        _ => None,
    }
}

pub fn ensure_vocal_tools(
    paths: &LibraryPaths,
    on_progress: &dyn Fn(String, Option<f32>, String),
) -> Result<VocalToolsStatus, String> {
    let root = vocal_dir(paths);
    let models = root.join("models");
    fs::create_dir_all(&models).map_err(|e| e.to_string())?;

    let mut last_message = String::new();
    emit_step(
        &on_progress,
        &mut last_message,
        "prepare",
        Some(5.0),
        "getting ready",
        20,
    );

    let system_py =
        find_system_python().ok_or_else(|| {
            "Python 3 was not found. Install Python from python.org (check Add to PATH), then try again.".to_string()
        })?;

    let venv_py = venv_python(paths);
    if !venv_py.exists() {
        emit_step(
            &on_progress,
            &mut last_message,
            "venv",
            Some(15.0),
            "setting up",
            60,
        );
        let venv_dir = root.join("venv");
        let mut cmd = Command::new(&system_py);
        cmd.args(["-m", "venv", venv_dir.to_string_lossy().as_ref()])
            .stdout(Stdio::null())
            .stderr(Stdio::piped())
            .hide_console();
        let out = cmd
            .output()
            .map_err(|e| format!("Could not create Python venv: {e}"))?;
        if !out.status.success() {
            return Err(format!(
                "Could not create Python venv: {}",
                String::from_utf8_lossy(&out.stderr)
            ));
        }
    }

    if !separator_import_ok(&venv_py) {
        emit_step(
            &on_progress,
            &mut last_message,
            "pip",
            Some(35.0),
            "installing tools",
            180,
        );
        install_separator(&venv_py).map_err(|e| format!("Could not install vocal remover: {e}"))?;
    }

    let model = model_path(paths);
    if !model.exists() {
        emit_step(
            &on_progress,
            &mut last_message,
            "model",
            Some(70.0),
            "downloading the model",
            60,
        );
        let tmp = model.with_extension("onnx.partial");
        let _ = fs::remove_file(&tmp);
        download_file(MODEL_URL, &tmp).map_err(|e| format!("Model download failed: {e}"))?;
        let _ = fs::remove_file(&model);
        fs::rename(&tmp, &model).map_err(|e| e.to_string())?;
    }

    // Tools being installed is not the song being finished.
    Ok(vocal_tools_status(paths))
}

fn instrumental_path_for(original: &Path) -> PathBuf {
    let stem = original
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("track");
    let parent = original.parent().unwrap_or_else(|| Path::new("."));
    parent.join(format!("{stem}.instrumental.wav"))
}

fn original_sidecar(instrumental: &Path) -> PathBuf {
    instrumental.with_extension("wav.original.txt")
}

fn instrumental_file_for(file_path: &str) -> PathBuf {
    let path = PathBuf::from(file_path);
    if file_path.contains(".instrumental.") {
        path
    } else {
        instrumental_path_for(&path)
    }
}

/// Sidecar first line marks a model instrumental. Center-cancel files have no marker.
fn trusted_instrumental(path: &Path) -> bool {
    if !path.is_file() {
        return false;
    }
    let Ok(text) = fs::read_to_string(original_sidecar(path)) else {
        return false;
    };
    text.lines()
        .next()
        .map(|line| line.trim() == INSTRUMENTAL_MARKER)
        .unwrap_or(false)
}

fn original_from_sidecar(sidecar: &Path, fallback: &str) -> String {
    let Ok(text) = fs::read_to_string(sidecar) else {
        return fallback.to_string();
    };
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line == INSTRUMENTAL_MARKER {
            continue;
        }
        return line.to_string();
    }
    fallback.to_string()
}

fn write_instrumental_sidecar(instrumental: &Path, original: &Path) -> Result<(), String> {
    let body = format!(
        "{INSTRUMENTAL_MARKER}\n{}",
        original.to_string_lossy()
    );
    fs::write(original_sidecar(instrumental), body).map_err(|e| e.to_string())
}

/// Prefer a real instrumental stem when present; otherwise the given path.
pub fn resolve_playback_path(file_path: &str) -> String {
    let inst = instrumental_file_for(file_path);
    if trusted_instrumental(&inst) {
        return inst.to_string_lossy().into_owned();
    }
    file_path.to_string()
}

pub fn has_instrumental(file_path: &str) -> bool {
    trusted_instrumental(&instrumental_file_for(file_path))
}

pub fn remove_vocals(
    paths: &LibraryPaths,
    file_path: &str,
    on_progress: &dyn Fn(String, Option<f32>, String),
) -> Result<String, String> {
    ensure_vocal_tools(paths, on_progress)?;

    let input = PathBuf::from(file_path);
    if !input.exists() {
        return Err("Track file not found".into());
    }
    let out = instrumental_path_for(&input);
    if trusted_instrumental(&out) {
        return Ok(out.to_string_lossy().into_owned());
    }
    // Drop a previous center-cancel file so playback cannot keep the vocals.
    let _ = fs::remove_file(&out);
    let _ = fs::remove_file(original_sidecar(&out));

    let python = venv_python(paths);
    if !python.exists() {
        return Err("Vocal remover environment missing".into());
    }
    let model = model_path(paths);
    let model_dir = model
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .to_path_buf();
    // Keep the in-progress stem out of the playlist folder so it is not listed as a song.
    let work_dir = vocal_dir(paths).join("work");
    fs::create_dir_all(&work_dir).map_err(|e| e.to_string())?;
    let scratch = work_dir.join("mw_instrumental.wav");
    let _ = fs::remove_file(&scratch);

    let mut last_message = String::new();
    emit_step(
        &on_progress,
        &mut last_message,
        "separate",
        Some(40.0),
        "converting the instrumental",
        180,
    );

    let script_path = vocal_dir(paths).join("separate_instrumental.py");
    fs::write(&script_path, SEPARATE_SCRIPT).map_err(|e| e.to_string())?;

    let mut cmd = Command::new(&python);
    cmd.arg(&script_path)
        .arg(&input)
        .arg(&work_dir)
        .arg(&model_dir)
        .arg(MODEL_NAME)
        .arg(&out)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .env("PYTHONUNBUFFERED", "1")
        .env("PYTHONIOENCODING", "utf-8")
        .hide_console();

    let mut child = cmd
        .spawn()
        .map_err(|e| format!("Vocal remover failed to start: {e}"))?;
    let stdout_pipe = child.stdout.take();
    let mut stderr = child
        .stderr
        .take()
        .ok_or_else(|| "Vocal remover produced no status".to_string())?;
    let stdout_thread = std::thread::spawn(move || {
        let mut collected = String::new();
        if let Some(mut pipe) = stdout_pipe {
            let _ = pipe.read_to_string(&mut collected);
        }
        collected
    });

    let mut carry = Vec::<u8>::new();
    let mut chunk = [0u8; 1024];
    let mut stderr_tail = Vec::<u8>::new();
    loop {
        let n = stderr
            .read(&mut chunk)
            .map_err(|e| format!("Vocal remover status failed: {e}"))?;
        if n == 0 {
            break;
        }
        stderr_tail.extend_from_slice(&chunk[..n]);
        if stderr_tail.len() > 8_000 {
            let drop_n = stderr_tail.len() - 8_000;
            stderr_tail.drain(..drop_n);
        }
        carry.extend_from_slice(&chunk[..n]);
        while let Some(pos) = carry
            .iter()
            .position(|byte| *byte == b'\r' || *byte == b'\n')
        {
            let line = String::from_utf8_lossy(&carry[..pos]).into_owned();
            carry.drain(..=pos);
            if let Some(seconds) = parse_tqdm_remaining(&line) {
                emit_step(
                    &on_progress,
                    &mut last_message,
                    "separate",
                    None,
                    "converting the instrumental",
                    seconds,
                );
            }
        }
    }
    if let Some(seconds) = parse_tqdm_remaining(&String::from_utf8_lossy(&carry)) {
        emit_step(
            &on_progress,
            &mut last_message,
            "separate",
            None,
            "converting the instrumental",
            seconds,
        );
    }

    let status = child
        .wait()
        .map_err(|e| format!("Vocal remover failed to finish: {e}"))?;
    let stdout = stdout_thread.join().unwrap_or_default();
    let stderr = String::from_utf8_lossy(&stderr_tail);
    let _ = fs::remove_file(&scratch);
    if !status.success() || !stdout.contains("MUSICWIDGET_OK") || !out.is_file() {
        let _ = fs::remove_file(&out);
        let detail = tail_message(&format!("{stderr}\n{stdout}"), 700);
        return Err(format!("Vocal remover failed: {detail}"));
    }

    write_instrumental_sidecar(&out, &input)?;
    if !trusted_instrumental(&out) {
        return Err("Vocal remover finished without a saved instrumental".into());
    }

    emit_progress(&on_progress, "done", Some(100.0), "Vocal remover ready");
    Ok(out.to_string_lossy().into_owned())
}

pub fn restore_original_vocals(file_path: &str) -> Result<String, String> {
    let path = PathBuf::from(file_path);
    let inst = if file_path.contains(".instrumental.") {
        path.clone()
    } else {
        instrumental_path_for(&path)
    };
    if !inst.exists() {
        return Err("No instrumental stem to restore from".into());
    }
    let sidecar = original_sidecar(&inst);
    let original = if sidecar.exists() {
        original_from_sidecar(&sidecar, file_path)
    } else {
        file_path
            .replace(".instrumental.wav", "")
            .trim_end_matches('.')
            .to_string()
    };
    let _ = fs::remove_file(&inst);
    let _ = fs::remove_file(&sidecar);
    Ok(original)
}
