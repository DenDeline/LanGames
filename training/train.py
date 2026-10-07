#!/usr/bin/env python3
"""Train and verify a fixed-shape right-paddle policy from exact-engine JSONL.

The test split is deliberately unavailable to `fit`; inspect it only with an
explicit `evaluate` command after gameplay selection has frozen a candidate.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import onnx
import onnxruntime as ort
import torch
from torch import nn
from torch.nn import functional as F


FEATURE_NAMES = [
    "left_y", "right_y", "ball_x", "ball_y", "ball_vx", "ball_vy",
    "left_score_normalized", "right_score_normalized", "right_contact_y",
    "right_contact_time",
]
CLASS_TO_AXIS = [-1, 0, 1]
INPUT_NAME = "observation"
OUTPUT_NAME = "logits"
FEATURE_COUNT = 10
CLASS_COUNT = 3
SCHEMA_VERSION = 1
CADENCE_TICKS = 9


@dataclass(frozen=True)
class SplitData:
    observations: np.ndarray
    labels: np.ndarray
    provenance: list[dict[str, Any]]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_manifest(directory: Path) -> dict[str, Any]:
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    expected = {
        "observationVersion": SCHEMA_VERSION,
        "featureNames": FEATURE_NAMES,
        "inputName": INPUT_NAME,
        "outputName": OUTPUT_NAME,
        "classToAxis": CLASS_TO_AXIS,
        "inferenceCadenceTicks": CADENCE_TICKS,
    }
    for key, value in expected.items():
        if manifest.get(key) != value:
            raise ValueError(f"{directory}: incompatible {key}: {manifest.get(key)!r}")
    splits = {entry["name"]: entry for entry in manifest["splits"]}
    if set(splits) != {"train", "validation", "test"}:
        raise ValueError(f"{directory}: expected train/validation/test split metadata")
    for name, entry in splits.items():
        ids = entry["matchIds"]
        seeds = entry["matchSeeds"]
        if len(ids) != entry["matchCount"] or len(seeds) != len(ids):
            raise ValueError(f"{directory}: inconsistent {name} match metadata")
        if len(set(ids)) != len(ids) or len(set(seeds)) != len(seeds):
            raise ValueError(f"{directory}: duplicated {name} match ID or seed")
    all_seeds = [seed for entry in splits.values() for seed in entry["matchSeeds"]]
    if len(set(all_seeds)) != len(all_seeds):
        raise ValueError(f"{directory}: match seed crosses split boundaries")
    return manifest


def load_split(directories: list[Path], split: str) -> SplitData:
    features: list[list[float]] = []
    labels: list[int] = []
    provenance: list[dict[str, Any]] = []
    for directory in directories:
        manifest = load_manifest(directory)
        info = next(entry for entry in manifest["splits"] if entry["name"] == split)
        match_ids = set(info["matchIds"])
        path = directory / f"{split}.jsonl"
        observed_matches: set[str] = set()
        count = 0
        with path.open("r", encoding="utf-8") as stream:
            for number, line in enumerate(stream, 1):
                row = json.loads(line)
                match_id = row["matchId"]
                if match_id not in match_ids:
                    raise ValueError(f"{path}:{number}: row is outside {split} matches")
                if int(row["tick"]) % CADENCE_TICKS:
                    raise ValueError(f"{path}:{number}: row is outside model decision cadence")
                observation = row["observation"]
                label = row["teacherClass"]
                if len(observation) != FEATURE_COUNT or label not in (0, 1, 2):
                    raise ValueError(f"{path}:{number}: bad observation or action label")
                if row["behaviorAxis"] not in CLASS_TO_AXIS:
                    raise ValueError(f"{path}:{number}: illegal behavior axis")
                if not all(math.isfinite(value) for value in observation):
                    raise ValueError(f"{path}:{number}: nonfinite observation")
                features.append(observation)
                labels.append(label)
                observed_matches.add(match_id)
                count += 1
        if count != info["rowCount"] or observed_matches != match_ids:
            raise ValueError(f"{path}: rows or match coverage differ from manifest")
        provenance.append({
            "directory": str(directory.resolve()),
            "manifestSha256": sha256(directory / "manifest.json"),
            "jsonlSha256": sha256(path),
            "masterSeed": manifest["masterSeed"],
            "behavior": manifest["behavior"],
            "studentModelSha256": manifest.get("studentModelSha256"),
            "split": split,
            "matchCount": info["matchCount"],
            "rowCount": count,
            "matchSeeds": info["matchSeeds"],
        })
    if not features:
        raise ValueError(f"No {split} observations")
    return SplitData(np.asarray(features, dtype=np.float32),
                     np.asarray(labels, dtype=np.int64), provenance)


def check_split_isolation(train: SplitData, validation: SplitData) -> None:
    train_seed_list = [seed for source in train.provenance for seed in source["matchSeeds"]]
    validation_seed_list = [seed for source in validation.provenance
                            for seed in source["matchSeeds"]]
    train_seeds = set(train_seed_list)
    validation_seeds = set(validation_seed_list)
    if len(train_seeds) != len(train_seed_list):
        raise ValueError("A training match seed is repeated across data sources")
    if len(validation_seeds) != len(validation_seed_list):
        raise ValueError("A validation match seed is repeated across data sources")
    overlap = train_seeds & validation_seeds
    if overlap:
        raise ValueError(f"Training/validation match seed overlap: {sorted(overlap)[:3]}")
    train_sources = {source["directory"] for source in train.provenance}
    validation_sources = {source["directory"] for source in validation.provenance}
    for source in train.provenance + validation.provenance:
        directory = Path(source["directory"])
        manifest = load_manifest(directory)
        test_seeds = set(next(entry["matchSeeds"] for entry in manifest["splits"]
                              if entry["name"] == "test"))
        if test_seeds & (train_seeds | validation_seeds):
            raise ValueError(f"{directory}: held-out test seed overlaps fitting data")
    if len(train_sources) != len(train.provenance):
        raise ValueError("A training source was listed more than once")
    if len(validation_sources) != len(validation.provenance):
        raise ValueError("A validation source was listed more than once")


class BotMLP(nn.Module):
    def __init__(self, hidden: int, mean: np.ndarray, scale: np.ndarray):
        super().__init__()
        self.register_buffer("mean", torch.as_tensor(mean, dtype=torch.float32).reshape(1, 10))
        self.register_buffer("scale", torch.as_tensor(scale, dtype=torch.float32).reshape(1, 10))
        self.network = nn.Sequential(
            nn.Linear(FEATURE_COUNT, hidden), nn.ReLU(),
            nn.Linear(hidden, hidden), nn.ReLU(),
            nn.Linear(hidden, CLASS_COUNT),
        )

    def forward(self, observation: torch.Tensor) -> torch.Tensor:
        return self.network((observation - self.mean) / self.scale)


def seed_all(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed % (2**32))
    torch.manual_seed(seed)
    torch.set_num_threads(1)
    torch.use_deterministic_algorithms(True)


def classes_from_logits(logits: np.ndarray) -> np.ndarray:
    """Match OnnxStudentPolicy's class-1-initialized strict-greater tie break."""
    if logits.ndim != 2 or logits.shape[1] != CLASS_COUNT or not np.isfinite(logits).all():
        raise ValueError("Expected finite [N,3] logits")
    chosen = np.ones(logits.shape[0], dtype=np.int64)
    best = logits[:, 1].copy()
    for candidate in range(CLASS_COUNT):
        better = logits[:, candidate] > best
        chosen[better] = candidate
        best[better] = logits[better, candidate]
    return chosen


def classification_metrics(labels: np.ndarray, logits: np.ndarray) -> dict[str, Any]:
    predicted = classes_from_logits(logits)
    confusion = np.zeros((CLASS_COUNT, CLASS_COUNT), dtype=np.int64)
    np.add.at(confusion, (labels, predicted), 1)
    support = confusion.sum(axis=1)
    predicted_count = confusion.sum(axis=0)
    correct = np.diag(confusion)
    recall = np.divide(correct, support, out=np.zeros(3, dtype=float), where=support != 0)
    precision = np.divide(correct, predicted_count, out=np.zeros(3, dtype=float),
                          where=predicted_count != 0)
    f1 = np.divide(2 * precision * recall, precision + recall,
                   out=np.zeros(3, dtype=float), where=precision + recall != 0)
    loss = F.cross_entropy(torch.from_numpy(logits), torch.from_numpy(labels)).item()
    return {
        "rowCount": int(len(labels)),
        "classSupport": support.tolist(),
        "confusionTrueRowsPredictedColumns": confusion.tolist(),
        "accuracy": float(correct.sum() / len(labels)),
        "perClassPrecision": precision.tolist(),
        "perClassRecall": recall.tolist(),
        "perClassF1": f1.tolist(),
        "macroRecall": float(recall.mean()),
        "macroF1": float(f1.mean()),
        "crossEntropy": float(loss),
    }


def near_contact_metrics(data: SplitData, logits: np.ndarray) -> dict[str, Any] | None:
    """Rally-critical slice: approaching right contact within 0.2 seconds."""
    selected = (data.observations[:, 4] > 0) & (data.observations[:, 9] <= 0.2)
    if not np.any(selected):
        return None
    return classification_metrics(data.labels[selected], logits[selected])


def predict(model: BotMLP, observations: np.ndarray) -> np.ndarray:
    model.eval()
    output = []
    with torch.inference_mode():
        for start in range(0, len(observations), 4096):
            batch = torch.from_numpy(observations[start:start + 4096])
            output.append(model(batch).numpy())
    return np.concatenate(output, axis=0)


def model_from_checkpoint(path: Path) -> BotMLP:
    checkpoint = torch.load(path, map_location="cpu", weights_only=True)
    model = BotMLP(int(checkpoint["hidden"]), checkpoint["mean"], checkpoint["scale"])
    model.load_state_dict(checkpoint["stateDict"])
    model.eval()
    return model


def verify_onnx(model: BotMLP, model_path: Path, observations: np.ndarray) -> dict[str, Any]:
    onnx.checker.check_model(str(model_path))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(model_path), sess_options=options,
                                   providers=["CPUExecutionProvider"])
    inputs, outputs = session.get_inputs(), session.get_outputs()
    if (len(inputs) != 1 or inputs[0].name != INPUT_NAME or
            inputs[0].shape != [1, 10] or inputs[0].type != "tensor(float)"):
        raise ValueError(f"ONNX input must be fixed {INPUT_NAME}[1,10]: {inputs}")
    if (len(outputs) != 1 or outputs[0].name != OUTPUT_NAME or
            outputs[0].shape != [1, 3] or outputs[0].type != "tensor(float)"):
        raise ValueError(f"ONNX output must be fixed {OUTPUT_NAME}[1,3]: {outputs}")
    torch_logits = predict(model, observations)
    onnx_logits = np.empty_like(torch_logits)
    for index, observation in enumerate(observations):
        onnx_logits[index] = session.run([OUTPUT_NAME],
                                         {INPUT_NAME: observation.reshape(1, 10)})[0][0]
    absolute = np.abs(torch_logits - onnx_logits)
    tolerance = 1e-4 + 1e-4 * np.abs(torch_logits)
    if not np.all(absolute <= tolerance):
        raise ValueError(f"ONNX/PyTorch logit mismatch: max absolute {absolute.max():.8g}")
    torch_actions = classes_from_logits(torch_logits)
    onnx_actions = classes_from_logits(onnx_logits)
    mismatches = int(np.count_nonzero(torch_actions != onnx_actions))
    if mismatches:
        raise ValueError(f"ONNX/PyTorch action mismatch on {mismatches} observations")
    return {
        "checkedObservations": len(observations),
        "maxAbsoluteLogitError": float(absolute.max()),
        "meanAbsoluteLogitError": float(absolute.mean()),
        "actionMismatches": mismatches,
        "absoluteTolerance": 1e-4,
        "relativeTolerance": 1e-4,
        "onnxSha256": sha256(model_path),
    }


def export_onnx(model: BotMLP, path: Path, validation: np.ndarray) -> dict[str, Any]:
    model.eval()
    torch.onnx.export(
        model, (torch.zeros(1, FEATURE_COUNT, dtype=torch.float32),), str(path),
        input_names=[INPUT_NAME], output_names=[OUTPUT_NAME], opset_version=18,
        dynamo=True, dynamic_shapes=None, external_data=False,
    )
    return verify_onnx(model, path, validation)


def fit(args: argparse.Namespace) -> None:
    seed_all(args.seed)
    train_dirs = [path.resolve() for path in args.data]
    val_dirs = [path.resolve() for path in (args.validation_data or args.data)]
    train = load_split(train_dirs, "train")
    validation = load_split(val_dirs, "validation")
    check_split_isolation(train, validation)
    if args.hidden < 4 or args.epochs < 1 or args.batch_size < 1:
        raise ValueError("hidden, epochs, and batch size must be positive")
    if not 0 <= args.class_weight_power <= 1:
        raise ValueError("class-weight-power must be in [0,1]")
    if args.init_checkpoint:
        model = model_from_checkpoint(args.init_checkpoint)
        if model.network[0].out_features != args.hidden:
            raise ValueError("Warm-start checkpoint and hidden width differ")
    else:
        mean = train.observations.mean(axis=0)
        scale = np.maximum(train.observations.std(axis=0), 0.05)
        model = BotMLP(args.hidden, mean, scale)
    counts = np.bincount(train.labels, minlength=3)
    if np.any(counts == 0):
        raise ValueError(f"Training data is missing an action class: {counts.tolist()}")
    weights = np.power(counts.mean() / counts, args.class_weight_power)
    weights /= weights.mean()
    weight_tensor = torch.tensor(weights, dtype=torch.float32)
    train_x = torch.from_numpy(train.observations)
    train_y = torch.from_numpy(train.labels)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.learning_rate,
                                  weight_decay=args.weight_decay)
    best_score = (-1.0, -1.0, float("-inf"))
    best_state: dict[str, torch.Tensor] | None = None
    best_epoch = 0
    history = []
    for epoch in range(1, args.epochs + 1):
        model.train()
        order = torch.randperm(len(train_y))
        loss_sum = 0.0
        for indices in order.split(args.batch_size):
            optimizer.zero_grad(set_to_none=True)
            logits = model(train_x[indices])
            loss = F.cross_entropy(logits, train_y[indices], weight=weight_tensor)
            loss.backward()
            optimizer.step()
            loss_sum += loss.item() * len(indices)
        val_metrics = classification_metrics(validation.labels,
                                             predict(model, validation.observations))
        score = (val_metrics["macroF1"], val_metrics["macroRecall"],
                 -val_metrics["crossEntropy"])
        if score > best_score:
            best_score = score
            best_epoch = epoch
            best_state = {key: value.detach().clone()
                          for key, value in model.state_dict().items()}
        history.append({
            "epoch": epoch,
            "trainingWeightedCrossEntropy": loss_sum / len(train_y),
            "validationMacroF1": val_metrics["macroF1"],
            "validationMacroRecall": val_metrics["macroRecall"],
            "validationAccuracy": val_metrics["accuracy"],
            "validationCrossEntropy": val_metrics["crossEntropy"],
        })
        print(f"epoch {epoch:03d}: validation macro-F1={score[0]:.4f} "
              f"macro-recall={score[1]:.4f} accuracy={val_metrics['accuracy']:.4f}",
              flush=True)
        if epoch - best_epoch >= args.patience:
            break
    assert best_state is not None
    model.load_state_dict(best_state)
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        raise FileExistsError(f"Training output directory must be empty: {output}")
    output.mkdir(parents=True, exist_ok=True)
    checkpoint_path = output / "checkpoint.pt"
    torch.save({
        "stateDict": best_state,
        "hidden": args.hidden,
        "mean": model.mean.detach().clone().reshape(-1),
        "scale": model.scale.detach().clone().reshape(-1),
        "schemaVersion": SCHEMA_VERSION,
        "bestEpoch": best_epoch,
    }, checkpoint_path)
    onnx_path = output / "policy.onnx"
    parity = export_onnx(model, onnx_path, validation.observations)
    metrics = {
        "schemaVersion": SCHEMA_VERSION,
        "model": {"layers": [10, args.hidden, args.hidden, 3],
                  "activation": "ReLU", "input": INPUT_NAME, "output": OUTPUT_NAME,
                  "fixedInputShape": [1, 10], "fixedOutputShape": [1, 3]},
        "config": {"seed": args.seed, "epochsRequested": args.epochs,
                   "epochsRun": len(history), "bestEpoch": best_epoch,
                   "selectionMetric": "validation macro-F1, then macro-recall, then CE",
                   "batchSize": args.batch_size, "learningRate": args.learning_rate,
                   "weightDecay": args.weight_decay,
                   "classWeightPower": args.class_weight_power,
                   "classWeights": weights.tolist(),
                   "initCheckpointSha256": sha256(args.init_checkpoint)
                   if args.init_checkpoint else None},
        "trainSources": train.provenance,
        "validationSources": validation.provenance,
        "trainMetrics": classification_metrics(train.labels, predict(model, train.observations)),
        "validationMetrics": classification_metrics(validation.labels,
                                                     predict(model, validation.observations)),
        "trainNearContactMetrics": near_contact_metrics(train,
                                                         predict(model, train.observations)),
        "validationNearContactMetrics": near_contact_metrics(
            validation, predict(model, validation.observations)),
        "history": history,
        "onnxParity": parity,
        "checkpointSha256": sha256(checkpoint_path),
    }
    (output / "metrics.json").write_text(json.dumps(metrics, indent=2) + "\n",
                                          encoding="utf-8")
    print(f"selected epoch {best_epoch}; ONNX {onnx_path}; parity {parity}")


def evaluate(args: argparse.Namespace) -> None:
    seed_all(0)
    data = load_split([path.resolve() for path in args.data], args.split)
    model = model_from_checkpoint(args.checkpoint)
    parity = verify_onnx(model, args.onnx, data.observations)
    metrics = classification_metrics(data.labels, predict(model, data.observations))
    near_metrics = near_contact_metrics(data, predict(model, data.observations))
    result = {"split": args.split, "sources": data.provenance,
              "classification": metrics, "nearContactClassification": near_metrics,
              "onnxParity": parity}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"split": args.split, "macroF1": metrics["macroF1"],
                      "macroRecall": metrics["macroRecall"], "parity": parity}, indent=2))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    fit_parser = commands.add_parser("fit", help="Fit from train split; select by validation only")
    fit_parser.add_argument("--data", type=Path, action="append", required=True,
                            help="Training data directory; repeat for DAgger data")
    fit_parser.add_argument("--validation-data", type=Path, action="append",
                            help="Validation source override; defaults to --data sources")
    fit_parser.add_argument("--output", type=Path, required=True)
    fit_parser.add_argument("--init-checkpoint", type=Path)
    fit_parser.add_argument("--seed", type=int, default=20261014)
    fit_parser.add_argument("--hidden", type=int, default=64)
    fit_parser.add_argument("--epochs", type=int, default=60)
    fit_parser.add_argument("--patience", type=int, default=12)
    fit_parser.add_argument("--batch-size", type=int, default=512)
    fit_parser.add_argument("--learning-rate", type=float, default=0.001)
    fit_parser.add_argument("--weight-decay", type=float, default=0.0001)
    fit_parser.add_argument("--class-weight-power", type=float, default=0.0,
                            help="0=unweighted CE, 0.5=mild inverse-sqrt class weighting")
    fit_parser.set_defaults(run=fit)
    eval_parser = commands.add_parser("evaluate", help="Explicit post-selection split metrics and parity")
    eval_parser.add_argument("--data", type=Path, action="append", required=True)
    eval_parser.add_argument("--split", choices=["validation", "test"], required=True)
    eval_parser.add_argument("--checkpoint", type=Path, required=True)
    eval_parser.add_argument("--onnx", type=Path, required=True)
    eval_parser.add_argument("--output", type=Path, required=True)
    eval_parser.set_defaults(run=evaluate)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
