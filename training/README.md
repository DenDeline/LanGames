# Hard policy training

Use Python 3.12.14 from the Codex workspace runtime on the development macOS
ARM64 host. Python 3.10 or newer is required, but the pinned Python ONNX Runtime
1.30.0 wheel used here supports Python 3.11 or newer. The repository's system
`python3` is 3.9 and must not be used. Install only into a virtual environment:

```bash
/Users/dendeline/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/bin/python3 \
  -m venv .artifacts/venv-step6-312
.artifacts/venv-step6-312/bin/python -m pip install -r training/requirements-lock.txt
```

The `fit` command reads only `train.jsonl` and `validation.jsonl`; it checks the
version 1 feature order, class mapping, 9-tick decision cadence, manifest row
counts, and whole-match seed isolation. It never reads test labels. A model is a
small `10 → hidden → hidden → 3` ReLU MLP with training-set mean and scale folded
into its ONNX graph. Class 0/1/2 maps to up/stay/down. Checkpoint selection uses
validation macro F1, then macro recall and cross entropy, since most training
labels are `stay`. The default loss is unweighted cross entropy; optional
`--class-weight-power 0.5` applies inverse-square-root class weighting when
development metrics and gameplay justify it.

```bash
.artifacts/venv-step6-312/bin/python training/train.py fit \
  --data .artifacts/bot-data --validation-data .artifacts/bot-data \
  --output .artifacts/bot-train-base-v1 --seed 20261014
```

The output directory contains `checkpoint.pt`, `policy.onnx`, and
`metrics.json`. The latter includes source file SHA-256 hashes, source match
seeds, class confusion matrices with true classes in rows, per-class recall,
precision, F1, macro F1, and an approaching-right-contact slice (at most 0.2
seconds to contact). The export is a fixed `[1,10]` float32 `observation` input
and fixed `[1,3]` float32 `logits` output. The current PyTorch
`torch.onnx.export(..., dynamo=True)` path is used without dynamic shapes. ONNX
Runtime and PyTorch logits and selected actions are checked on every validation
observation before a fit succeeds.

For one student-rollout relabeling iteration, run the exact-engine generator
with the base ONNX model as behavior. These states receive fresh teacher
labels and preserve the generator's whole-match train/validation/test split:

```bash
dotnet run --project tools/LanPong.TrainingData -c Release -- generate \
  --output .artifacts/bot-student-rollout-v1 --seed 20261013 \
  --train 48 --validation 12 --test 12 --gate-matches 100 \
  --behavior student \
  --student-model .artifacts/bot-train-base-v1/policy.onnx
.artifacts/venv-step6-312/bin/python training/train.py fit \
  --data .artifacts/bot-data --data .artifacts/bot-student-rollout-v1 \
  --validation-data .artifacts/bot-data \
  --validation-data .artifacts/bot-student-rollout-v1 \
  --init-checkpoint .artifacts/bot-train-base-v1/checkpoint.pt \
  --output .artifacts/bot-train-dagger-v1 --seed 20261015
```

Use the .NET `evaluate-model` command on separate development seeds to choose
the model by actual gameplay against Simple. Once selected and frozen, test
split classification/parity can be reported explicitly:

```bash
.artifacts/venv-step6-312/bin/python training/train.py evaluate \
  --data .artifacts/bot-data --split test \
  --checkpoint .artifacts/bot-train-dagger-v1/checkpoint.pt \
  --onnx .artifacts/bot-train-dagger-v1/policy.onnx \
  --output .artifacts/bot-train-dagger-test-metrics.json
```

These commands run from the repository root. On this macOS host, importing
ONNX Runtime may create a `:memory:.ses` telemetry file in the current
directory. Running the Python commands from `.artifacts` with adjusted relative
paths keeps that incidental file inside the ignored artifact directory.

The [PyTorch ONNX exporter documentation](https://docs.pytorch.org/docs/2.14/onnx.html)
specifies `dynamo=True` and says omitted dynamic shapes keep example input
dimensions fixed. Direct dependencies are in `requirements.txt`; the resolved
and tested environment is pinned in `requirements-lock.txt`.
